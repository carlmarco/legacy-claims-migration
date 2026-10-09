"""Profile the merge against the current staging tables without touching the target:
empty the target inside a transaction, run the full merge, report per-step timings, ROLL BACK.

    python scripts/profile_merge.py [mapping.yaml]
"""
import json
import sys
import time

from psycopg import sql

from etl import db, merge
from etl.mapping import Mapping

import argparse

ap = argparse.ArgumentParser()
ap.add_argument("mapping", nargs="?", default="datasets/synpuf/mapping.yaml")
ap.add_argument("--work-mem", help="SET LOCAL work_mem for the merge transaction, e.g. 256MB")
ap.add_argument("--defer-fks", action="store_true", help="the real implementation: drop FKs, VALIDATE at end")
ap.add_argument("--no-fk-triggers", action="store_true",
                help="experiment only: skip per-row FK trigger checks (session_replication_role=replica)")
args = ap.parse_args()
m = Mapping(args.mapping)
with db.connect() as con:
    if args.work_mem:
        con.execute(sql.SQL("SET LOCAL work_mem = {}").format(sql.Literal(args.work_mem)))
    if args.no_fk_triggers:
        con.execute("SET LOCAL session_replication_role = replica")
    con.execute(sql.SQL("TRUNCATE {} CASCADE").format(
        sql.SQL(", ").join(sql.Identifier(m.target_schema, e) for e in m.entities)))
    t0 = time.perf_counter()
    stats = merge.merge_all(con, m, run_id=None, defer_fks=args.defer_fks)
    total = time.perf_counter() - t0
    con.rollback()
keys = ("seconds", "t_survivorship", "t_orphans", "t_upsert", "t_child_delete")
for e, s in stats.items():
    print(f"{e:18s}", s if e.startswith("_") else {k: s.get(k) for k in keys})
print(f"total merge {total:.1f}s (rolled back)")
