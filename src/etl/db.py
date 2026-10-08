"""Postgres connection settings. Override with ETL_DSN."""
import os

import psycopg

DSN = os.environ.get("ETL_DSN", "postgresql://etl:etl@localhost:5433/target")


def connect(**kwargs) -> psycopg.Connection:
    return psycopg.connect(DSN, **kwargs)
