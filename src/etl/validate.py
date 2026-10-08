"""Generic validation suite, driven by the mapping. Compares the target against an oracle schema
(the expected state, built independently per dataset) and checks the target's own integrity.

  counts        target rows and quarantine reasons per entity
  fk            child rows whose parent key is missing (must be 0)
  checksum      order-independent: count + sum of 64-bit row hashes, computed in SQL on both sides
  diff          full keyed comparison: missing / extra / differing rows, mismatch count per column
  codes         columns produced by zero_padded_code: resolution status x outcome vs oracle
Columns from code resolvers are excluded from checksum/diff and scored by `codes` instead, because a
resolver is allowed to withhold a value (NULL + status) and that's an outcome to measure, not a diff.
"""
import psycopg
from psycopg import sql

from .mapping import LINEAGE_COLS, Mapping


def code_columns(m: Mapping, entity: str) -> list[str]:
    """Base names of multi-output code columns (col, col_raw, col_resolution) for an entity."""
    out = []
    for src in m.by_table.values():
        for cs in src:
            if cs.entity != entity:
                continue
            cols = cs.columns + (cs.unpivot.columns if cs.unpivot else [])
            out += [c.name for c in cols if len(c.outputs) > 1 and c.name not in out]
    return out


def compare_columns(m: Mapping, entity: str) -> list[str]:
    skip = set(LINEAGE_COLS)
    for c in code_columns(m, entity):
        skip |= {c, f"{c}_raw", f"{c}_resolution"}
    return [c for c in m.entities[entity].output if c not in skip]


def _q(con: psycopg.Connection, query, params=None):
    return con.execute(query, params).fetchall()


def counts(con: psycopg.Connection, m: Mapping) -> dict:
    out = {}
    for entity in m.entities:
        n = _q(con, sql.SQL("SELECT count(*) FROM {}").format(sql.Identifier(m.target_schema, entity)))[0][0]
        q = dict(_q(con, "SELECT reason, count(*) FROM etl.quarantine WHERE dataset = %s AND entity = %s "
                         "GROUP BY reason", (m.name, entity)))
        out[entity] = {"target_rows": n, "quarantined": q}
    return out


def fk(con: psycopg.Connection, m: Mapping) -> dict:
    out = {}
    for entity, e in m.entities.items():
        for parent, cols in e.references.items():
            join = sql.SQL(" AND ").join(
                sql.SQL("p.{} = c.{}").format(sql.Identifier(pk), sql.Identifier(ck))
                for pk, ck in zip(m.entities[parent].key, cols))
            n = _q(con, sql.SQL("SELECT count(*) FROM {} c WHERE NOT EXISTS (SELECT 1 FROM {} p WHERE {})").format(
                sql.Identifier(m.target_schema, entity), sql.Identifier(m.target_schema, parent), join))[0][0]
            out[f"{entity}->{parent}"] = n
    return out


def checksum(con: psycopg.Connection, schema: str, entity: str, cols: list[str]) -> tuple[int, str]:
    """Order-independent table checksum: (row count, sum of the first 64 bits of md5(row text)).

    Sum (not XOR) so duplicate rows don't cancel out. quote_nullable() makes the row text unambiguous:
    every value is quoted and escaped and NULL is a bare NULL, so neither a literal 'NULL'/'\\N' string
    nor a delimiter inside a value can collide with a different row.
    """
    row_text = sql.SQL("concat_ws(',', {})").format(sql.SQL(", ").join(
        sql.SQL("quote_nullable({}::text)").format(sql.Identifier(c)) for c in cols))
    n, total = _q(con, sql.SQL(
        "SELECT count(*), coalesce(sum(('x' || left(md5({}), 16))::bit(64)::bigint), 0)::text FROM {}").format(
        row_text, sql.Identifier(schema, entity)))[0]
    return n, total


def diff(con: psycopg.Connection, m: Mapping, entity: str, oracle_schema: str, samples: int = 3) -> dict:
    e = m.entities[entity]
    cols = [c for c in compare_columns(m, entity) if c not in e.key]
    on = sql.SQL(" AND ").join(sql.SQL("t.{0} = o.{0}").format(sql.Identifier(k)) for k in e.key)
    t_null = sql.SQL("t.{} IS NULL").format(sql.Identifier(e.key[0]))
    o_null = sql.SQL("o.{} IS NULL").format(sql.Identifier(e.key[0]))
    per_col = [sql.SQL("count(*) FILTER (WHERE NOT {} AND NOT {} AND t.{c} IS DISTINCT FROM o.{c})").format(
        t_null, o_null, c=sql.Identifier(c)) for c in cols]
    row_diff = sql.SQL("({}) IS DISTINCT FROM ({})").format(
        sql.SQL(", ").join(sql.SQL("t.{}").format(sql.Identifier(c)) for c in cols),
        sql.SQL(", ").join(sql.SQL("o.{}").format(sql.Identifier(c)) for c in cols)) if cols else sql.SQL("false")
    frm = sql.SQL("FROM {} t FULL JOIN {} o ON {}").format(
        sql.Identifier(m.target_schema, entity), sql.Identifier(oracle_schema, entity), on)
    r = _q(con, sql.SQL("SELECT count(*) FILTER (WHERE {t_null}), count(*) FILTER (WHERE {o_null}), "
                        "count(*) FILTER (WHERE NOT {t_null} AND NOT {o_null} AND {row_diff}), {per_col} {frm}").format(
        t_null=t_null, o_null=o_null, row_diff=row_diff, per_col=sql.SQL(", ").join(per_col) if per_col else sql.SQL("0"),
        frm=frm))[0]
    out = {"missing_in_target": r[0], "extra_in_target": r[1], "differing_rows": r[2],
           "differing_by_column": {c: n for c, n in zip(cols, r[3:]) if n}}
    if r[0] or r[1] or r[2]:
        keys = sql.SQL(", ").join(sql.SQL("coalesce(t.{0}, o.{0})").format(sql.Identifier(k)) for k in e.key)
        out["examples"] = [list(map(str, x)) for x in _q(con, sql.SQL(
            "SELECT {keys}, {t_null} AS missing, {o_null} AS extra {frm} "
            "WHERE {t_null} OR {o_null} OR {row_diff} LIMIT {n}").format(
            keys=keys, t_null=t_null, o_null=o_null, frm=frm, row_diff=row_diff, n=sql.Literal(samples)))]
    return out


def codes(con: psycopg.Connection, m: Mapping, entity: str, col: str, oracle_schema: str) -> dict:
    """For a resolver column: outcome (correct / withheld / wrong / unexpected) per resolution status."""
    e = m.entities[entity]
    on = sql.SQL(" AND ").join(sql.SQL("t.{0} = o.{0}").format(sql.Identifier(k)) for k in e.key)
    c, res = sql.Identifier(col), sql.Identifier(f"{col}_resolution")
    rows = _q(con, sql.SQL("""
        SELECT coalesce(t.{res}, '(none)'),
               CASE WHEN o.{c} IS NULL AND t.{c} IS NULL THEN 'both_null'
                    WHEN t.{c} = o.{c} THEN 'correct'
                    WHEN t.{c} IS NULL THEN 'withheld'
                    ELSE 'wrong' END,
               count(*)
        FROM {t} t JOIN {o} o ON {on} GROUP BY 1, 2""").format(
        res=res, c=c, t=sql.Identifier(m.target_schema, entity), o=sql.Identifier(oracle_schema, entity), on=on))
    out: dict = {}
    for resolution, outcome, n in rows:
        out.setdefault(resolution, {})[outcome] = n
    return out


def run(con: psycopg.Connection, m: Mapping, oracle_schema: str | None) -> dict:
    report: dict = {"counts": counts(con, m), "fk_orphans_in_target": fk(con, m)}
    if not oracle_schema:
        return report
    report["checksums"], report["diff"], report["codes"] = {}, {}, {}
    for entity in m.entities:
        cols = compare_columns(m, entity)
        t, o = checksum(con, m.target_schema, entity, cols), checksum(con, oracle_schema, entity, cols)
        report["checksums"][entity] = {"target": t, "oracle": o, "match": t == o}
        report["diff"][entity] = diff(con, m, entity, oracle_schema)
        for col in code_columns(m, entity):
            report["codes"][f"{entity}.{col}"] = codes(con, m, entity, col, oracle_schema)
    return report


def locate(m: Mapping, table: str, col: str) -> tuple[str, str, str | None, int | None] | None:
    """Where does legacy (table, column) land in the target?

    Returns (entity, target column, index column, index value) or None if the column isn't mapped.
    Handles flat columns, unpivot templates ("L{n}_PMT" matches "L3_PMT" -> slot 3), and split
    columns ("DX_LIST[2]" -> position 2). Flat entities win over unpivots for the same source column.
    """
    import re

    flat, nested = None, None
    for src in m.by_table.get(table, []):
        for c in src.columns:
            if c.src == col and flat is None and src.unpivot is None:
                flat = (src.entity, c.name, None, None)
        u = src.unpivot
        if u is None or nested:
            continue
        if u.split_column:
            mm = re.fullmatch(rf"{re.escape(u.split_column)}\[(\d+)\]", col)
            if mm:
                target = next(c.name for c in u.columns if c.src == u.split_field)
                nested = (src.entity, target, u.index, int(mm[1]))
            continue
        for field, template in u.fields.items():
            pattern = re.escape(template).replace(re.escape("{n}"), r"(\d+)")
            mm = re.fullmatch(pattern, col)
            if mm:
                target = next((c.name for c in u.columns if c.src == field), None)
                if target:
                    nested = (src.entity, target, u.index, int(mm[1]))
    return flat or nested
