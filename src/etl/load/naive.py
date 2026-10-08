"""Baseline: one INSERT statement per row, autocommit.

This is what ORM-style code does by default: one network round trip and one durable commit
(WAL flush) per row. It's the number every other strategy is measured against.
"""
from collections.abc import Iterator

import psycopg
from psycopg.types.json import Jsonb

from .. import stage
from ..mapping import Mapping, Record
from . import strategy


@strategy("naive")
def load(dsn: str, m: Mapping, records: Iterator[Record]) -> dict:
    inserts = {e: stage.insert_sql(m, e) for e in m.entities}
    q_insert = stage.quarantine_insert_sql(m)
    counts: dict[str, int] = {}
    with psycopg.connect(dsn, autocommit=True) as con:
        for r in records:
            if r.kind == "row":
                con.execute(inserts[r.entity], r.values)
                counts[r.entity] = counts.get(r.entity, 0) + 1
            else:
                con.execute(q_insert, (r.entity, r.table, r.rec_id, r.reason, r.detail, Jsonb(r.raw)))
                counts["_quarantine"] = counts.get("_quarantine", 0) + 1
    return counts
