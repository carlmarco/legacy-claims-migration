"""Set-based merge from staging into the target, generated from the mapping (D6, D14).

Per entity, in parent-before-child order:
  1. survivorship  DISTINCT ON (key) ... ORDER BY key, <order>; field rules as window aggregates
  2. orphans       rows whose parent key is missing from the target -> quarantine (ORPHAN_FK)
  3. upsert        INSERT ... ON CONFLICT (key) DO UPDATE ... WHERE row IS DISTINCT FROM excluded
A re-run over unchanged data writes zero target rows.
"""
import time

import psycopg
from psycopg import sql

from .mapping import LINEAGE_COLS, Mapping
from .stage import quarantine_table, stage_table

FIELD_RULES = {"max": "max", "min": "min"}


def _ids(cols: list[str]) -> sql.Composable:
    return sql.SQL(", ").join(map(sql.Identifier, cols))


def merge_entity(con: psycopg.Connection, m: Mapping, entity: str, run_id: int) -> dict:
    e = m.entities[entity]
    target = sql.Identifier(m.target_schema, entity)
    stage = stage_table(m, entity)
    surv = sql.Identifier(f"surv__{entity}")
    key = _ids(e.key)
    stats: dict = {"staged": con.execute(sql.SQL("SELECT count(*) FROM {}").format(stage)).fetchone()[0]}
    t = time.perf_counter()

    # 1. Survivorship. Ties on the order expression break on the higher legacy REC_ID, deterministically.
    rules = e.survivorship.get("fields", {})
    select = []
    for c in e.output:
        if c in rules:
            fn = sql.SQL(FIELD_RULES[rules[c]])
            select.append(sql.SQL("{}({}) OVER (PARTITION BY {}) AS {}").format(
                fn, sql.Identifier(c), key, sql.Identifier(c)))
        else:
            select.append(sql.Identifier(c))
    order = sql.SQL(e.survivorship.get("order", "_version DESC"))
    con.execute(sql.SQL("DROP TABLE IF EXISTS {}").format(surv))
    follow = e.survivorship.get("follow_parent")
    if follow:
        # Child rows belong to a *version* of their parent. Keep only children emitted by the parent's
        # surviving legacy row, so a slot the newest version dropped can't be revived from an older one.
        pe = m.entities[follow]
        pcols = e.references[follow]
        pkey = _ids(pe.key)
        win = sql.SQL("""SELECT DISTINCT ON ({pkey}) {pkey}, _src_table, _src_rec_id FROM {pstage}
                         ORDER BY {pkey}, {porder}, _src_rec_id DESC""").format(
            pkey=pkey, pstage=stage_table(m, follow),
            porder=sql.SQL(pe.survivorship.get("order", "_version DESC")))
        on = sql.SQL(" AND ").join(
            [sql.SQL("w.{} = c.{}").format(sql.Identifier(pk), sql.Identifier(ck)) for pk, ck in zip(pe.key, pcols)]
            + [sql.SQL("w._src_table = c._src_table"), sql.SQL("w._src_rec_id = c._src_rec_id")])
        con.execute(sql.SQL("""CREATE TEMP TABLE {surv} AS
            SELECT DISTINCT ON ({key}) {select} FROM {stage} c JOIN ({win}) w ON {on}
            ORDER BY {key}, {order}, c._src_rec_id DESC""").format(
            surv=surv, key=sql.SQL(", ").join(sql.SQL("c.{}").format(sql.Identifier(k)) for k in e.key),
            select=sql.SQL(", ").join(sql.SQL("c.{}").format(sql.Identifier(c)) for c in e.output)
            if not rules else sql.SQL(", ").join(select),
            stage=stage, win=win, on=on, order=order))
    else:
        con.execute(sql.SQL("""CREATE TEMP TABLE {surv} AS
            SELECT DISTINCT ON ({key}) {select} FROM {stage}
            ORDER BY {key}, {order}, _src_rec_id DESC""").format(
            surv=surv, key=key, select=sql.SQL(", ").join(select), stage=stage, order=order))
    stats["survivors"] = con.execute(sql.SQL("SELECT count(*) FROM {}").format(surv)).fetchone()[0]
    stats["t_survivorship"], t = round(time.perf_counter() - t, 2), time.perf_counter()
    stats["duplicates_resolved"] = stats["staged"] - stats["survivors"]

    # 2. Orphans: parent key absent from the (already merged) parent target table.
    stats["orphans"] = 0
    for parent, child_cols in e.references.items():
        parent_key = m.entities[parent].key
        join = sql.SQL(" AND ").join(
            sql.SQL("p.{} = s.{}").format(sql.Identifier(pk), sql.Identifier(ck))
            for pk, ck in zip(parent_key, child_cols))
        missing = sql.SQL("NOT EXISTS (SELECT 1 FROM {} p WHERE {})").format(
            sql.Identifier(m.target_schema, parent), join)
        con.execute(sql.SQL("""
            INSERT INTO etl.quarantine (dataset, entity, src_table, src_rec_id, reason, detail, raw, run_id)
            SELECT %s, %s, s._src_table, s._src_rec_id, 'ORPHAN_FK', %s, to_jsonb(s), %s
            FROM {surv} s WHERE {missing}
            ON CONFLICT DO NOTHING""").format(surv=surv, missing=missing),
            (m.name, entity, f"no {parent} for ({', '.join(child_cols)})", run_id))
        n = con.execute(sql.SQL("DELETE FROM {} s WHERE {}").format(surv, missing)).rowcount
        stats["orphans"] += n
        stats[f"orphans_{parent}"] = n

    stats["t_orphans"], t = round(time.perf_counter() - t, 2), time.perf_counter()

    # 3. Upsert. Only rows whose data actually changed are written (lineage excluded from the comparison).
    data_cols = [c for c in e.output if c not in e.key and c not in LINEAGE_COLS]
    set_cols = [c for c in e.output if c not in e.key]
    distinct = sql.SQL("({}) IS DISTINCT FROM ({})").format(
        sql.SQL(", ").join(sql.SQL("t.{}").format(sql.Identifier(c)) for c in data_cols),
        sql.SQL(", ").join(sql.SQL("EXCLUDED.{}").format(sql.Identifier(c)) for c in data_cols))
    upsert = sql.SQL("""
        WITH w AS (
            INSERT INTO {target} AS t ({cols}) SELECT {cols} FROM {surv}
            ON CONFLICT ({key}) DO UPDATE SET {sets} WHERE {distinct}
            RETURNING (xmax = 0) AS inserted)
        SELECT count(*) FILTER (WHERE inserted), count(*) FILTER (WHERE NOT inserted) FROM w""").format(
        target=target, cols=_ids(e.output), surv=surv, key=key,
        sets=sql.SQL(", ").join(sql.SQL("{c} = EXCLUDED.{c}").format(c=sql.Identifier(c)) for c in set_cols),
        distinct=distinct if data_cols else sql.SQL("false"))
    stats["inserted"], stats["updated"] = con.execute(upsert).fetchone()
    stats["unchanged"] = stats["survivors"] - stats["orphans"] - stats["inserted"] - stats["updated"]
    stats["t_upsert"], t = round(time.perf_counter() - t, 2), time.perf_counter()

    if follow:
        # Replace the child set of every parent present in this run: delete target children the
        # surviving parent version no longer has.
        pcols = e.references[follow]
        same_parent = sql.SQL(" AND ").join(
            sql.SQL("p.{} = t.{}").format(sql.Identifier(pk), sql.Identifier(ck))
            for pk, ck in zip(m.entities[follow].key, pcols))
        same_key = sql.SQL(" AND ").join(sql.SQL("s.{0} = t.{0}").format(sql.Identifier(k)) for k in e.key)
        stats["deleted"] = con.execute(sql.SQL("""
            DELETE FROM {target} t
            WHERE EXISTS (SELECT 1 FROM {pstage} p WHERE {same_parent})
              AND NOT EXISTS (SELECT 1 FROM {surv} s WHERE {same_key})""").format(
            target=target, pstage=stage_table(m, follow), same_parent=same_parent, surv=surv,
            same_key=same_key)).rowcount
        stats["t_child_delete"] = round(time.perf_counter() - t, 2)
    con.execute(sql.SQL("DROP TABLE {}").format(surv))
    return stats


def _drop_fks(con: psycopg.Connection, m: Mapping) -> list[tuple[str, str, str]]:
    """Drop the target's FK constraints, returning (table, name, definition) to restore them."""
    fks = con.execute("""
        SELECT c.conrelid::regclass::text, c.conname, pg_get_constraintdef(c.oid)
        FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace
        WHERE c.contype = 'f' AND n.nspname = %s ORDER BY 1, 2""", (m.target_schema,)).fetchall()
    for table, name, _ in fks:
        con.execute(sql.SQL("ALTER TABLE {} DROP CONSTRAINT {}").format(sql.SQL(table), sql.Identifier(name)))
    return fks


def _restore_fks(con: psycopg.Connection, fks: list[tuple[str, str, str]]) -> None:
    """Re-add FKs NOT VALID (instant), then VALIDATE: one set-based check per FK instead of a
    trigger lookup per inserted row. A violation fails the whole merge transaction."""
    for table, name, definition in fks:
        con.execute(sql.SQL("ALTER TABLE {} ADD CONSTRAINT {} {} NOT VALID").format(
            sql.SQL(table), sql.Identifier(name), sql.SQL(definition)))
    for table, name, _ in fks:
        con.execute(sql.SQL("ALTER TABLE {} VALIDATE CONSTRAINT {}").format(sql.SQL(table), sql.Identifier(name)))


def merge_all(con: psycopg.Connection, m: Mapping, run_id: int, defer_fks: bool = False) -> dict:
    """Merge every entity in one transaction (the caller commits, or rolls back for a dry run).

    defer_fks: drop the target's FKs for the duration of the merge and re-validate them set-based
    at the end. Redundant per-row checks are skipped (the merge already quarantines orphans), but
    ALTER TABLE holds ACCESS EXCLUSIVE locks until commit: for cutover loads, not live targets.
    """
    fks = _drop_fks(con, m) if defer_fks else []
    # Quarantine reflects the latest run: clear this dataset's rows, then re-add from this run.
    con.execute("DELETE FROM etl.quarantine WHERE dataset = %s", (m.name,))
    con.execute(sql.SQL("""
        INSERT INTO etl.quarantine (dataset, entity, src_table, src_rec_id, reason, detail, raw, run_id)
        SELECT %s, entity, src_table, src_rec_id, reason, min(detail), (array_agg(raw))[1], %s
        FROM {} GROUP BY entity, src_table, src_rec_id, reason
        ON CONFLICT DO NOTHING""").format(quarantine_table(m)), (m.name, run_id))
    out = {}
    for entity in m.load_order():
        t = time.perf_counter()
        out[entity] = merge_entity(con, m, entity, run_id)
        out[entity]["seconds"] = round(time.perf_counter() - t, 2)
    if fks:
        t = time.perf_counter()
        _restore_fks(con, fks)
        out["_fk_validate"] = {"constraints": len(fks), "seconds": round(time.perf_counter() - t, 2)}
    return out
