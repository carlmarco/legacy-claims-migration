"""Staging tables: UNLOGGED, no constraints, same column types as the target.

Rows land here first; survivorship, FK checks and the upsert into the target run as SQL (D14).
"""
import psycopg
from psycopg import sql

from .mapping import Mapping

STAGE_SCHEMA = "etl_stage"
QUARANTINE_COLS = ["entity", "src_table", "src_rec_id", "reason", "detail", "raw"]


def stage_table(m: Mapping, entity: str) -> sql.Identifier:
    return sql.Identifier(STAGE_SCHEMA, f"{m.name}__{entity}")


def quarantine_table(m: Mapping) -> sql.Identifier:
    return sql.Identifier(STAGE_SCHEMA, f"{m.name}__quarantine")


def ensure_schema(con: psycopg.Connection, m: Mapping, engine_sql: str) -> None:
    """Create engine tables and the target schema. Both scripts are idempotent."""
    con.execute(engine_sql)
    con.execute(m.target_ddl.read_text())
    con.execute(sql.SQL("CREATE SCHEMA IF NOT EXISTS {}").format(sql.Identifier(STAGE_SCHEMA)))


def reset(con: psycopg.Connection, m: Mapping) -> None:
    """(Re)create empty staging tables. CREATE ... AS TABLE ... WITH NO DATA copies types, not constraints."""
    for entity in m.entities:
        t = stage_table(m, entity)
        con.execute(sql.SQL("DROP TABLE IF EXISTS {}").format(t))
        con.execute(sql.SQL("CREATE UNLOGGED TABLE {} AS TABLE {} WITH NO DATA").format(
            t, sql.Identifier(m.target_schema, entity)))
    q = quarantine_table(m)
    con.execute(sql.SQL("DROP TABLE IF EXISTS {}").format(q))
    con.execute(sql.SQL("""CREATE UNLOGGED TABLE {} (entity text, src_table text, src_rec_id bigint,
                           reason text, detail text, raw jsonb)""").format(q))


def insert_sql(m: Mapping, entity: str) -> sql.Composed:
    cols = m.entities[entity].output
    return sql.SQL("INSERT INTO {} ({}) VALUES ({})").format(
        stage_table(m, entity), sql.SQL(", ").join(map(sql.Identifier, cols)),
        sql.SQL(", ").join(sql.Placeholder() * len(cols)))


def quarantine_insert_sql(m: Mapping) -> sql.Composed:
    return sql.SQL("INSERT INTO {} ({}) VALUES ({})").format(
        quarantine_table(m), sql.SQL(", ").join(map(sql.Identifier, QUARANTINE_COLS)),
        sql.SQL(", ").join(sql.Placeholder() * len(QUARANTINE_COLS)))
