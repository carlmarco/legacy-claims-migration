"""Compile a dataset's mapping spec (YAML) into row-level transforms, and run them over a source.

The engine is dataset-agnostic: everything about the source and target lives in the spec.
Row-local work happens here (D14): null tokens, converters, unpivot/split, row rules.
Set-based work (dedup, survivorship, FK checks) is declared in the spec but executed in SQL.

Each legacy table is read once and feeds every entity that maps from it.
"""
import sqlite3
from collections.abc import Iterator
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import yaml

from . import convert, reference
from .convert import ConversionError

LINEAGE_COLS = ["_src_table", "_src_rec_id", "_version"]


@dataclass
class Column:
    name: str
    src: str | None
    fn: Any = None
    const: Any = None
    outputs: tuple[str, ...] = ("",)


@dataclass
class Unpivot:
    index: str
    fields: dict[str, str] | None = None  # field -> column template with {n}
    n: int = 0
    split_column: str | None = None
    split_sep: str = "|"
    split_field: str | None = None
    columns: list[Column] = field(default_factory=list)
    keep_if_any: list[str] = field(default_factory=list)


@dataclass
class CompiledSource:
    entity: str
    table: str
    columns: list[Column]
    unpivot: Unpivot | None
    swap_if_reversed: list[tuple[str, str]]
    output: list[str]


@dataclass
class Entity:
    name: str
    key: list[str]
    survivorship: dict
    references: dict[str, list[str]]
    output: list[str]


@dataclass
class Record:
    kind: str  # "row" | "quarantine"
    entity: str
    values: tuple | None = None
    table: str | None = None
    rec_id: int | None = None
    reason: str | None = None
    detail: str | None = None
    raw: dict | None = None


class Mapping:
    def __init__(self, path: str | Path):
        path = Path(path)
        self.base = path.parent
        spec = yaml.safe_load(path.read_text())
        self.spec = spec
        self.name = spec["name"]
        self.source_path = self.base / spec["source"]["path"]  # all paths are relative to the spec file
        self.null_tokens = set(spec.get("null_tokens", [""]))
        self.rec_id_col = spec["lineage"]["rec_id"]
        self.version_col = spec["lineage"]["version"]
        self.exclude = spec.get("exclude")

        code_maps = {name: self._code_map(m) for name, m in spec.get("code_maps", {}).items()}
        refs = {name: reference.load(r, self.base) for name, r in spec.get("references", {}).items()}
        self.ctx = convert.Context(code_maps, refs)

        self.entities: dict[str, Entity] = {}
        self.by_table: dict[str, list[CompiledSource]] = {}
        for ename, e in spec["entities"].items():
            compiled = [self._compile_source(ename, s) for s in e["sources"]]
            if any(c.unpivot for c in compiled):
                outs = {tuple(c.output) for c in compiled}
                if len(outs) != 1:
                    raise ValueError(f"entity {ename}: unpivot sources produce different columns: {outs}")
            else:
                # Flat entities: a source may omit columns another source has; they become NULL.
                union = list(dict.fromkeys(col for c in compiled for col in c.output if col not in LINEAGE_COLS))
                for c in compiled:
                    c.output = union + LINEAGE_COLS
            self.entities[ename] = Entity(ename, e["key"], e.get("survivorship", {}),
                                          e.get("references", {}), compiled[0].output)
            for c in compiled:
                self.by_table.setdefault(c.table, []).append(c)

    # -- compile ------------------------------------------------------------------------------
    def _code_map(self, m: dict) -> dict:
        out: dict = {}
        if "file" in m:
            pairs = yaml.safe_load((self.base / m["file"]).read_text())
            pairs = {str(k): str(v) for k, v in pairs.items()}
            if m.get("invert"):
                pairs = {v: k for k, v in pairs.items()}
            out.update(pairs)
            if m.get("include_targets"):
                out.update({v: v for v in pairs.values()})
        for k, v in (m.get("values") or {}).items():
            out[str(k)] = v
        if m.get("case_insensitive"):
            out.update({k.lower(): v for k, v in list(out.items())})
        return out

    def _column(self, name: str, spec: Any) -> Column:
        if isinstance(spec, dict) and "const" in spec:
            return Column(name, None, const=spec["const"])
        src, conv, *rest = spec
        fn = convert.build(conv, rest[0] if rest else None, self.ctx)
        outputs = ("", "_raw", "_resolution") if conv == "zero_padded_code" else ("",)
        return Column(name, src, fn, outputs=outputs)

    def _compile_source(self, entity: str, s: dict) -> CompiledSource:
        columns = [self._column(n, c) for n, c in (s.get("columns") or {}).items()]
        up = None
        if "unpivot" in s:
            u = s["unpivot"]
            up = Unpivot(index=u["index"], fields=u.get("fields"), n=u.get("n", 0),
                         keep_if_any=u.get("keep_if_any", []),
                         columns=[self._column(n, c) for n, c in u["columns"].items()])
            if "split" in u:
                up.split_column = u["split"]["column"]
                up.split_sep = u["split"].get("sep", "|")
                up.split_field = u["split"]["field"]
        output = [f"{c.name}{suf}" for c in columns for suf in c.outputs]
        if up:
            output += [up.index] + [f"{c.name}{suf}" for c in up.columns for suf in c.outputs]
        output += LINEAGE_COLS
        rules = s.get("rules", {})
        return CompiledSource(entity, s["from"], columns, up,
                              [tuple(p) for p in rules.get("swap_if_reversed", [])], output)

    # -- run ----------------------------------------------------------------------------------
    def _null(self, v: Any) -> bool:
        return v is None or (isinstance(v, str) and v.strip() in self.null_tokens)

    def _apply(self, cols: list[Column], lookup, out: dict) -> None:
        for c in cols:
            if c.src is None:
                out[c.name] = c.const
                continue
            v = lookup(c.src)
            if self._null(v):
                for suf in c.outputs:
                    out[f"{c.name}{suf}"] = None
                continue
            try:
                r = c.fn(v)
            except ConversionError as e:
                raise ConversionError(f"{c.name} <- {c.src}: {e}") from None
            if isinstance(r, dict):
                for suf, val in r.items():
                    out[f"{c.name}{suf}"] = val
            else:
                out[c.name] = r

    def transform_row(self, src: CompiledSource, row: dict) -> Iterator[Record]:
        rec_id = row[self.rec_id_col]
        lineage = (src.table, rec_id, row[self.version_col])
        base: dict = {}
        try:
            self._apply(src.columns, row.__getitem__, base)
        except ConversionError as e:
            yield Record("quarantine", src.entity, table=src.table, rec_id=rec_id,
                         reason="BAD_VALUE", detail=str(e), raw=dict(row))
            return
        for a, b in src.swap_if_reversed:
            if base.get(a) and base.get(b) and base[b] < base[a]:
                base[a], base[b] = base[b], base[a]

        if src.unpivot is None:
            yield Record("row", src.entity, tuple(base.get(c) for c in src.output[:-3]) + lineage)
            return

        u = src.unpivot
        if u.split_column is not None:
            raw = row[u.split_column]
            parts = [] if self._null(raw) else str(raw).split(u.split_sep)
            slots = [{u.split_field: p} for p in parts]
        else:
            slots = [{f: row[t.format(n=i)] for f, t in u.fields.items()} for i in range(1, u.n + 1)]
        head = tuple(base[c.name + suf] for c in src.columns for suf in c.outputs)
        for i, slot in enumerate(slots, 1):
            if all(self._null(v) for v in slot.values()):
                continue
            vals: dict = {}
            try:
                self._apply(u.columns, lambda k: slot[k] if k in slot else row[k], vals)
            except ConversionError as e:
                yield Record("quarantine", src.entity, table=src.table, rec_id=rec_id,
                             reason="BAD_VALUE", detail=f"[{u.index}={i}] {e}", raw=dict(row))
                continue
            if u.keep_if_any and not any(vals.get(k) for k in u.keep_if_any):
                continue
            body = tuple(vals[c.name + suf] for c in u.columns for suf in c.outputs)
            yield Record("row", src.entity, head + (i,) + body + lineage)

    def connect(self) -> sqlite3.Connection:
        con = sqlite3.connect(f"file:{self.source_path}?mode=ro", uri=True)
        con.row_factory = sqlite3.Row
        return con

    def excluded_count(self, table: str) -> int:
        if not self.exclude:
            return 0
        with self.connect() as con:
            return con.execute(f"SELECT count(*) FROM {table} WHERE coalesce(({self.exclude}), 0)").fetchone()[0]

    def transform(self, tables: list[str] | None = None, rec_range: tuple[int, int] | None = None,
                  limit: int | None = None) -> Iterator[Record]:
        """Stream every record for the given legacy tables (default: all), optionally one REC_ID range."""
        con = self.connect()
        for table in tables or list(self.by_table):
            # coalesce: a NULL flag must not exclude the row (NOT NULL is NULL in SQL).
            where = [f"NOT coalesce(({self.exclude}), 0)"] if self.exclude else []
            if rec_range:
                where.append(f"{self.rec_id_col} BETWEEN {int(rec_range[0])} AND {int(rec_range[1])}")
            sql = f"SELECT * FROM {table}" + (f" WHERE {' AND '.join(where)}" if where else "")
            sql += f" ORDER BY {self.rec_id_col}" + (f" LIMIT {int(limit)}" if limit else "")
            sources = self.by_table[table]
            for row in con.execute(sql):
                for src in sources:
                    yield from self.transform_row(src, row)
        con.close()
