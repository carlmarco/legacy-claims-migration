"""Batched loading: buffer rows per entity, flush each buffer as one bulk operation.

Two variants, to separate the two costs that row-by-row pays:
  executemany  removes the per-row network round trip (psycopg pipelines the statements),
               but Postgres still parses/executes one INSERT per row.
  copy         COPY FROM STDIN: one statement per batch, rows streamed in the wire protocol's
               bulk format; no per-row statement overhead at all.
Buffers are bounded (batch_size rows per entity), so memory stays flat regardless of input size.
"""
from collections.abc import Iterator

import psycopg
from psycopg import sql
from psycopg.types.json import Jsonb

from .. import stage
from ..mapping import Mapping, Record
from . import strategy

BATCH_SIZE = 20_000


class Buffers:
    def __init__(self, con: psycopg.Connection, m: Mapping, flush_fn, batch_size: int):
        self.con, self.m, self.flush_fn, self.batch_size = con, m, flush_fn, batch_size
        self.rows: dict[str, list] = {}
        self.counts: dict[str, int] = {}

    def add(self, r: Record) -> None:
        if r.kind == "row":
            key, values = r.entity, r.values
        else:
            key, values = "_quarantine", (r.entity, r.table, r.rec_id, r.reason, r.detail, Jsonb(r.raw))
        buf = self.rows.setdefault(key, [])
        buf.append(values)
        if len(buf) >= self.batch_size:
            self.flush(key)

    def flush(self, key: str) -> None:
        buf = self.rows.get(key)
        if not buf:
            return
        self.flush_fn(self.con, self.m, key, buf)
        self.con.commit()
        self.counts[key] = self.counts.get(key, 0) + len(buf)
        buf.clear()

    def close(self) -> dict:
        for key in list(self.rows):
            self.flush(key)
        return self.counts


def _target(m: Mapping, key: str) -> tuple[sql.Identifier, list[str]]:
    if key == "_quarantine":
        return stage.quarantine_table(m), stage.QUARANTINE_COLS
    return stage.stage_table(m, key), m.entities[key].output


def _flush_copy(con: psycopg.Connection, m: Mapping, key: str, rows: list) -> None:
    table, cols = _target(m, key)
    stmt = sql.SQL("COPY {} ({}) FROM STDIN").format(table, sql.SQL(", ").join(map(sql.Identifier, cols)))
    with con.cursor() as cur, cur.copy(stmt) as copy:
        for row in rows:
            copy.write_row(row)


def _flush_executemany(con: psycopg.Connection, m: Mapping, key: str, rows: list) -> None:
    stmt = stage.quarantine_insert_sql(m) if key == "_quarantine" else stage.insert_sql(m, key)
    with con.cursor() as cur:
        cur.executemany(stmt, rows)


def _run(dsn: str, m: Mapping, records: Iterator[Record], flush_fn, batch_size: int) -> dict:
    with psycopg.connect(dsn) as con:
        bufs = Buffers(con, m, flush_fn, batch_size)
        for r in records:
            bufs.add(r)
        return bufs.close()


@strategy("batched")
def load_copy(dsn: str, m: Mapping, records: Iterator[Record], batch_size: int = BATCH_SIZE) -> dict:
    return _run(dsn, m, records, _flush_copy, batch_size)


@strategy("executemany")
def load_executemany(dsn: str, m: Mapping, records: Iterator[Record], batch_size: int = BATCH_SIZE) -> dict:
    return _run(dsn, m, records, _flush_executemany, batch_size)
