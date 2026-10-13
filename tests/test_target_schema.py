"""Target DDL applies cleanly, is idempotent, and matches what the mapping emits."""
from pathlib import Path

import psycopg
import pytest

from etl import db
from etl.mapping import Mapping
from tests.conftest import requires_data

ROOT = Path(__file__).parent.parent
ENGINE_SQL = ROOT / "src/etl/sql/engine.sql"
TARGET_SQL = ROOT / "datasets/synpuf/target.sql"
SCHEMA = "synpuf_schema_test"


@pytest.fixture(scope="module")
def con():
    try:
        c = db.connect(autocommit=True)
    except psycopg.OperationalError:
        pytest.skip("Postgres not reachable (docker compose up -d)")
    c.execute(f"DROP SCHEMA IF EXISTS {SCHEMA} CASCADE")
    ddl = TARGET_SQL.read_text().replace("synpuf.", f"{SCHEMA}.").replace("SCHEMA IF NOT EXISTS synpuf", f"SCHEMA IF NOT EXISTS {SCHEMA}")
    c.execute(ENGINE_SQL.read_text())
    c.execute(ddl)
    c.execute(ddl)  # idempotent: second apply is a no-op
    yield c
    c.execute(f"DROP SCHEMA {SCHEMA} CASCADE")
    c.close()


@requires_data
def test_target_columns_match_mapping(con):
    m = Mapping(ROOT / "datasets/synpuf/mapping.yaml")
    for name, entity in m.entities.items():
        cols = [r[0] for r in con.execute(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = %s AND table_name = %s "
            "ORDER BY ordinal_position", (SCHEMA, name))]
        assert cols == entity.output, name


@requires_data
def test_primary_keys_match_mapping_keys(con):
    m = Mapping(ROOT / "datasets/synpuf/mapping.yaml")
    for name, entity in m.entities.items():
        pk = [r[0] for r in con.execute("""
            SELECT a.attname FROM pg_index i
            JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
            WHERE i.indrelid = %s::regclass AND i.indisprimary
            ORDER BY array_position(i.indkey::int2[], a.attnum)""", (f"{SCHEMA}.{name}",))]
        assert pk == entity.key, name


def test_resolution_constraint_rejects_null_code_marked_exact(con):
    con.execute(f"""INSERT INTO {SCHEMA}.beneficiary VALUES
        ('00000000000000AA', NULL, NULL, 'M', '1', '05', '000', 'T', 1, now())""")
    con.execute(f"""INSERT INTO {SCHEMA}.claim (claim_type, clm_id, segment, beneficiary_id, _src_table,
        _src_rec_id, _version) VALUES ('carrier', '1', 1, '00000000000000AA', 'T', 1, now())""")
    with pytest.raises(psycopg.errors.CheckViolation):
        con.execute(f"""INSERT INTO {SCHEMA}.claim_diagnosis VALUES
            ('carrier', '1', 1, 1, NULL, '389', 'exact', 'T', 1, now())""")
    con.execute(f"""INSERT INTO {SCHEMA}.claim_diagnosis VALUES
        ('carrier', '1', 1, 1, NULL, '389', 'ambiguous', 'T', 1, now())""")
