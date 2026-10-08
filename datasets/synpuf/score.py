"""Score the migration per injected defect type, against the legacy-izer's ground-truth log.

For each logged defect we find the legacy row's natural key through the staging lineage
(_src_table, _src_rec_id), then compare the target with the oracle at that key:

  repaired     target value == expected value
  withheld     target value is NULL with a resolution status (code resolver declined to guess)
  wrong        target value != expected value
  excluded     expected absent and target absent (orphans of removed beneficiaries, soft deletes)
  lost         expected present, target absent
  unexpected   target present, expected absent

Requires: a completed (non-dry) migration with its staging tables still in place, and the oracle.

    python -m datasets.synpuf.score [--out results/defect_scores.json]
"""
import argparse
import json
import sqlite3
import time
from pathlib import Path

from psycopg import sql

from etl import db
from etl.mapping import Mapping
from etl.stage import stage_table
from etl.validate import compare_columns, locate

from .oracle import ORACLE_SCHEMA

HERE = Path(__file__).parent
TARGET, ORACLE = "synpuf", ORACLE_SCHEMA
ROW_ENTITY = {"MBR_MSTR": "beneficiary", "CLM_HDR_ALL": "claim", "CAR_CLM": "claim", "RX_EVNT": "drug_event"}
A1_FIELDS = {"DOB": "birth_date", "SEX": "sex", "ST": "state_ssa_code"}


def load_truth(con, truth_path: str) -> None:
    con.execute("DROP TABLE IF EXISTS truth_defect")
    con.execute("""CREATE TEMP TABLE truth_defect (defect_id text, tbl text, rec_id bigint, col text,
                   clean_value text, legacy_value text)""")
    src = sqlite3.connect(truth_path)
    with con.cursor().copy("COPY truth_defect FROM STDIN") as cp:
        for row in src.execute("SELECT defect_id, tbl, rec_id, col, clean_value, legacy_value FROM defect"):
            cp.write_row(tuple(None if v is None else str(v) for v in row))
    con.execute("CREATE INDEX ON truth_defect (tbl, col)")
    con.execute("ANALYZE truth_defect")


def index_staging(con, m: Mapping) -> None:
    for entity in m.entities:
        con.execute(sql.SQL("CREATE INDEX IF NOT EXISTS {} ON {} (_src_table, _src_rec_id)").format(
            sql.Identifier(f"{m.name}__{entity}__lineage"), stage_table(m, entity)))


def _key_join(alias: str, keys: list[str], other: str) -> sql.Composable:
    return sql.SQL(" AND ").join(sql.SQL("{}.{} = {}.{}").format(
        sql.Identifier(alias), sql.Identifier(k), sql.Identifier(other), sql.Identifier(k)) for k in keys)


def _outcomes(expr_match: sql.Composable, k0: sql.Composable) -> sql.Composable:
    # 'excluded' is checked first: a value whose row the mapping drops by rule (e.g. an all-zero
    # carrier line) is never staged, and that's correct exactly when the oracle has no row there either.
    return sql.SQL("""
        CASE WHEN t.{k0} IS NULL AND o.{k0} IS NULL THEN 'excluded'
             WHEN s._src_rec_id IS NULL THEN 'not_staged'
             WHEN t.{k0} IS NULL THEN 'lost'
             WHEN o.{k0} IS NULL THEN 'unexpected'
             ELSE {match} END""").format(k0=k0, match=expr_match)


def score_values(con, m: Mapping) -> dict:
    """Defects tied to one legacy column: compare that column's target value with the oracle's."""
    groups = con.execute("""SELECT DISTINCT tbl, col FROM truth_defect
                            WHERE rec_id IS NOT NULL AND col IS NOT NULL AND defect_id NOT IN ('A1', 'E2')
                            AND col NOT LIKE '%,%'""").fetchall()
    by_target: dict[tuple, list] = {}
    unmapped = []
    for tbl, col in groups:
        loc = locate(m, tbl, col)
        if loc is None:
            unmapped.append(f"{tbl}.{col}")
            continue
        entity, tcol, idx_col, idx = loc
        by_target.setdefault((entity, tcol, idx_col), []).append((tbl, col, idx))

    results: dict[str, dict] = {}
    for (entity, tcol, idx_col), cols in by_target.items():
        e = m.entities[entity]
        con.execute("DROP TABLE IF EXISTS loc")
        con.execute("CREATE TEMP TABLE loc (tbl text, col text, idx int)")
        with con.cursor().copy("COPY loc FROM STDIN") as cp:
            for row in cols:
                cp.write_row(row)
        c = sql.Identifier(tcol)
        res_col = f"{tcol}_resolution"
        withheld = (sql.SQL("WHEN t.{c} IS NULL AND t.{r} IS NOT NULL THEN 'withheld'").format(
            c=c, r=sql.Identifier(res_col)) if res_col in e.output else sql.SQL(""))
        match = sql.SQL("CASE WHEN t.{c} IS NOT DISTINCT FROM o.{c} THEN 'repaired' {w} ELSE 'wrong' END").format(
            c=c, w=withheld)
        stage_on = sql.SQL("s._src_table = d.tbl AND s._src_rec_id = d.rec_id")
        parent_join = sql.SQL("")
        key_expr = {k: sql.SQL("s.{}").format(sql.Identifier(k)) for k in e.key}
        if idx_col:
            stage_on = sql.SQL("{} AND s.{} = l.idx").format(stage_on, sql.Identifier(idx_col))
            follow = e.survivorship.get("follow_parent")
            if follow:
                # If the slot wasn't staged, recover its key from the parent's staging row + slot index.
                parent_join = sql.SQL("LEFT JOIN {} ps ON ps._src_table = d.tbl AND ps._src_rec_id = d.rec_id").format(
                    stage_table(m, follow))
                for pk, ck in zip(m.entities[follow].key, e.references[follow]):
                    key_expr[ck] = sql.SQL("coalesce(s.{}, ps.{})").format(sql.Identifier(ck), sql.Identifier(pk))
                key_expr[idx_col] = sql.SQL("coalesce(s.{}, l.idx)").format(sql.Identifier(idx_col))
        ok = sql.SQL(" AND ").join(sql.SQL("o.{} = {}").format(sql.Identifier(k), key_expr[k]) for k in e.key)
        q = sql.SQL("""
            SELECT d.defect_id, {outcome}, count(*)
            FROM truth_defect d JOIN loc l ON l.tbl = d.tbl AND l.col = d.col
            LEFT JOIN {stage} s ON {stage_on}
            {parent_join}
            LEFT JOIN {target} t ON {tk}
            LEFT JOIN {oracle} o ON {ok}
            WHERE d.defect_id NOT IN ('A1', 'E2')
            GROUP BY 1, 2""").format(
            outcome=_outcomes(match, sql.Identifier(e.key[0])), stage=stage_table(m, entity), stage_on=stage_on,
            parent_join=parent_join, target=sql.Identifier(TARGET, entity), oracle=sql.Identifier(ORACLE, entity),
            tk=_key_join("t", e.key, "s"), ok=ok)
        for defect_id, outcome, n in con.execute(q):
            d = results.setdefault(defect_id, {})
            d[outcome] = d.get(outcome, 0) + n
    if unmapped:
        results["_unmapped_columns"] = unmapped
    return results


def score_rows(con, m: Mapping) -> dict:
    """Row-level defects: duplicates, reversed dates, stale copies, removed parents, soft deletes."""
    out: dict[str, dict] = {}

    def add(defect_id, rows):
        d = out.setdefault(defect_id, {})
        for outcome, n in rows:
            d[outcome] = d.get(outcome, 0) + n

    # D1 (duplicate load) and E2 (reversed dates): the surviving target row equals the oracle row.
    for defect_id in ("D1", "E2"):
        for tbl, entity in ROW_ENTITY.items():
            e = m.entities[entity]
            cols = compare_columns(m, entity)
            match = sql.SQL("CASE WHEN ({}) IS NOT DISTINCT FROM ({}) THEN 'repaired' ELSE 'wrong' END").format(
                sql.SQL(", ").join(sql.SQL("t.{}").format(sql.Identifier(c)) for c in cols),
                sql.SQL(", ").join(sql.SQL("o.{}").format(sql.Identifier(c)) for c in cols))
            add(defect_id, con.execute(sql.SQL("""
                SELECT {outcome}, count(*) FROM truth_defect d
                LEFT JOIN {stage} s ON s._src_table = d.tbl AND s._src_rec_id = d.rec_id
                LEFT JOIN {target} t ON {tk} LEFT JOIN {oracle} o ON {ok}
                WHERE d.defect_id = %s AND d.tbl = %s GROUP BY 1""").format(
                outcome=_outcomes(match, sql.Identifier(e.key[0])), stage=stage_table(m, entity),
                target=sql.Identifier(TARGET, entity), oracle=sql.Identifier(ORACLE, entity),
                tk=_key_join("t", e.key, "s"), ok=_key_join("o", e.key, "s")), (defect_id, tbl)).fetchall())

    # A1 (stale demographic copy on a claim): the copy is ignored; the beneficiary matches the oracle.
    for legacy_col, tcol in A1_FIELDS.items():
        add("A1", con.execute(sql.SQL("""
            SELECT CASE WHEN tb.beneficiary_id IS NULL AND ob.beneficiary_id IS NULL THEN 'excluded'
                        WHEN tb.{c} IS NOT DISTINCT FROM ob.{c} THEN 'repaired' ELSE 'wrong' END, count(*)
            FROM truth_defect d
            JOIN {stage} s ON s._src_table = d.tbl AND s._src_rec_id = d.rec_id
            LEFT JOIN synpuf.beneficiary tb ON tb.beneficiary_id = s.beneficiary_id
            LEFT JOIN synpuf_oracle.beneficiary ob ON ob.beneficiary_id = s.beneficiary_id
            WHERE d.defect_id = 'A1' AND d.col = %s GROUP BY 1""").format(
            c=sql.Identifier(tcol), stage=stage_table(m, "claim")), (legacy_col,)).fetchall())

    # E1 (beneficiary removed from MBR_MSTR): absent from the target, with all its claims/drug events
    # quarantined rather than loaded.
    add("E1", con.execute("""
        SELECT CASE WHEN EXISTS (SELECT 1 FROM synpuf.beneficiary b WHERE b.beneficiary_id = d.clean_value)
                      OR EXISTS (SELECT 1 FROM synpuf.claim c WHERE c.beneficiary_id = d.clean_value)
                      OR EXISTS (SELECT 1 FROM synpuf.drug_event x WHERE x.beneficiary_id = d.clean_value)
                    THEN 'wrong' ELSE 'excluded' END, count(*)
        FROM truth_defect d WHERE d.defect_id = 'E1' GROUP BY 1""").fetchall())

    # E3 (soft-deleted junk rows with fabricated keys): no target row carries the fabricated key.
    # claim.clm_id alone isn't indexed (the PK leads with claim_type), and with default work_mem
    # Postgres won't hash 1.4M claim ids for an IN (subquery); it re-scans per row. So join the
    # other way: hash the 14k fabricated keys (small side) and scan each target table once.
    add("E3", con.execute("""
        WITH e3 AS (SELECT legacy_value AS v FROM truth_defect WHERE defect_id = 'E3'),
             hit AS (SELECT beneficiary_id AS v FROM synpuf.beneficiary WHERE beneficiary_id IN (SELECT v FROM e3)
                     UNION SELECT clm_id FROM synpuf.claim WHERE clm_id IN (SELECT v FROM e3)
                     UNION SELECT pde_id FROM synpuf.drug_event WHERE pde_id IN (SELECT v FROM e3))
        SELECT CASE WHEN hit.v IS NULL THEN 'excluded' ELSE 'wrong' END, count(*)
        FROM e3 LEFT JOIN hit ON hit.v = e3.v GROUP BY 1""").fetchall())
    return out


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--mapping", default=str(HERE / "mapping.yaml"))
    ap.add_argument("--truth", default="data/legacy_truth.sqlite")
    ap.add_argument("--out", default="results/defect_scores.json")
    args = ap.parse_args()
    m = Mapping(args.mapping)
    t0 = time.perf_counter()
    with db.connect() as con:
        con.execute("SET work_mem = '256MB'")  # session-only: lets the scoring joins hash in memory
        load_truth(con, args.truth)
        index_staging(con, m)
        con.commit()
        scores = {**score_values(con, m), **score_rows(con, m)}
    unmapped = scores.pop("_unmapped_columns", [])
    summary = {}
    for defect_id, outcomes in sorted(scores.items()):
        total = sum(outcomes.values())
        good = outcomes.get("repaired", 0) + outcomes.get("excluded", 0)
        summary[defect_id] = {"total": total, **outcomes,
                              "handled_pct": round(100 * good / total, 3) if total else None}
    report = {"defects": summary, "unmapped_columns": unmapped, "elapsed_s": round(time.perf_counter() - t0, 1)}
    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    Path(args.out).write_text(json.dumps(report, indent=2))
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
