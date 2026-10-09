"""End-to-end runs of the pipeline (staging -> survivorship -> orphans -> upsert) on the dental fixture."""
import shutil
import sqlite3
from pathlib import Path

import psycopg
import pytest

from etl import db, pipeline

FIXTURE = Path(__file__).parent / "fixtures" / "dental"

LEGACY = """
CREATE TABLE PATIENT_TBL (ROWID_ INTEGER, PT_NUM TEXT, LNAME TEXT, BDATE TEXT, GENDER TEXT,
                          ZIP INTEGER, DELETED TEXT, MODIFIED TEXT);
INSERT INTO PATIENT_TBL VALUES
  (1, 'P001', 'Okafor',   '03/14/1961', 'f', '02139', 'F', '2019-01-01'),
  (2, 'p001 ', 'Okafor-Lee', '03/14/1961', 'F', '02139', 'F', '2021-05-05'),  -- newer version, ID variant
  (3, 'P002', 'Lind',     '13/40/1970', 'm', 10001,   'F', '2019-01-02'),     -- bad date -> quarantine
  (4, 'P003', 'Gone',     '01/01/1950', 'm', 94110,   'T', '2019-01-04');     -- soft-deleted
CREATE TABLE APPT_TBL (ROWID_ INTEGER, APPT_NO TEXT, PT_NUM TEXT, APPT_DT TEXT,
                       PROC1 TEXT, FEE1 TEXT, PROC2 TEXT, FEE2 TEXT, PROC3 TEXT, FEE3 TEXT,
                       PROC4 TEXT, FEE4 TEXT, DELETED TEXT, MODIFIED TEXT);
INSERT INTO APPT_TBL VALUES
  (1, '500', 'P001', '06/01/2020', 'D0120', '$45.00', 'D1110', '95', NULL, '0.00', '', NULL, 'F', '2020-06-01'),
  (2, '500', 'P001', '06/01/2020', 'D0120', '$45.00', 'D1110', '95', NULL, '0.00', '', NULL, 'F', '2020-06-01'),
  (3, '501', 'P002', '06/02/2020', 'D0120', '45',     NULL,   NULL, NULL, NULL,   NULL, NULL, 'F', '2020-06-02');
"""


@pytest.fixture
def mapping_path(tmp_path):
    try:
        db.connect().close()
    except psycopg.OperationalError:
        pytest.skip("Postgres not reachable (docker compose up -d)")
    for f in ("mapping.yaml", "target.sql"):
        shutil.copy(FIXTURE / f, tmp_path / f)
    con = sqlite3.connect(tmp_path / "legacy.sqlite")
    con.executescript(LEGACY)
    con.commit()
    con.close()
    return str(tmp_path / "mapping.yaml")


def q(sql, *params):
    with db.connect() as con:
        return con.execute(sql, params).fetchall()


def test_fresh_run(mapping_path):
    s = pipeline.run(mapping_path, "naive", fresh=True)
    m = s["merge"]
    # Two versions of P001 (ID variant normalized); the newer one survives.
    assert m["patient"]["duplicates_resolved"] == 1
    assert q("SELECT patient_number, last_name, zip FROM dental.patient") == [("P001", "Okafor-Lee", "02139")]
    # Exact duplicate appointment row resolved; appointment for quarantined patient P002 is an orphan.
    assert m["appointment"]["duplicates_resolved"] == 1
    assert m["appointment"]["orphans"] == 1
    assert m["appointment_procedure"]["orphans"] == 1  # its procedure row cascades to orphan too
    assert q("SELECT slot, procedure_code, fee FROM dental.appointment_procedure ORDER BY slot") == [
        (1, "D0120", 45), (2, "D1110", 95)]
    reasons = q("SELECT entity, src_rec_id, reason FROM etl.quarantine WHERE dataset = 'dental' ORDER BY 1, 2")
    assert reasons == [("appointment", 3, "ORPHAN_FK"), ("appointment_procedure", 3, "ORPHAN_FK"),
                       ("patient", 3, "BAD_VALUE")]
    # Lineage points at the winning legacy row.
    assert q("SELECT _src_table, _src_rec_id FROM dental.patient") == [("PATIENT_TBL", 2)]


def test_rerun_is_a_no_op(mapping_path):
    pipeline.run(mapping_path, "naive", fresh=True)
    s = pipeline.run(mapping_path, "naive")
    for entity, st in s["merge"].items():
        assert (st["inserted"], st["updated"]) == (0, 0), entity


def test_source_change_updates_exactly_that_row(mapping_path):
    pipeline.run(mapping_path, "naive", fresh=True)
    con = sqlite3.connect(Path(mapping_path).parent / "legacy.sqlite")
    con.execute("UPDATE APPT_TBL SET FEE2 = '$99.00' WHERE APPT_NO = '500'")
    con.commit()
    con.close()
    s = pipeline.run(mapping_path, "naive")
    assert s["merge"]["appointment_procedure"]["updated"] == 1
    assert s["merge"]["appointment"]["updated"] == 0
    assert q("SELECT fee FROM dental.appointment_procedure WHERE slot = 2") == [(99,)]


def test_dry_run_writes_nothing_to_target(mapping_path):
    s = pipeline.run(mapping_path, "naive", fresh=True, dry_run=True)
    assert s["merge"]["patient"]["inserted"] == 1  # reported...
    assert q("SELECT count(*) FROM dental.patient") == [(0,)]  # ...but rolled back
    assert q("SELECT count(*) FROM etl.quarantine WHERE dataset = 'dental'") == [(0,)]
    assert q("SELECT dry_run, status FROM etl.migration_run WHERE run_id = %s", s["run_id"]) == [(True, "succeeded")]


def test_child_slot_dropped_by_newer_parent_version_is_not_revived(mapping_path):
    """Regression: a newer version whose slot is filtered out (no code, zero fee) must not let the
    older version's slot win survivorship by default. Children follow their parent's version."""
    con = sqlite3.connect(Path(mapping_path).parent / "legacy.sqlite")
    con.execute("""INSERT INTO APPT_TBL VALUES
        (4, '500', 'P001', '06/01/2020', 'D0120', '$45.00', NULL, '0.00', NULL, '0.00', '', NULL,
         'F', '2020-07-01')""")  # newer version of appointment 500: slot 2 removed
    con.commit()
    con.close()
    s = pipeline.run(mapping_path, "naive", fresh=True)
    assert q("SELECT slot FROM dental.appointment_procedure ORDER BY slot") == [(1,)]
    assert q("SELECT _src_rec_id FROM dental.appointment") == [(4,)]
    assert s["merge"]["appointment_procedure"]["inserted"] == 1


def test_rerun_deletes_children_the_parent_no_longer_has(mapping_path):
    pipeline.run(mapping_path, "naive", fresh=True)
    assert len(q("SELECT * FROM dental.appointment_procedure")) == 2
    con = sqlite3.connect(Path(mapping_path).parent / "legacy.sqlite")
    con.execute("UPDATE APPT_TBL SET PROC2 = NULL, FEE2 = '0.00' WHERE APPT_NO = '500'")
    con.commit()
    con.close()
    s = pipeline.run(mapping_path, "naive")
    assert s["merge"]["appointment_procedure"]["deleted"] == 1
    assert q("SELECT slot FROM dental.appointment_procedure") == [(1,)]
    # Child-set replacement is opt-in (follow_parent). An FK alone must never delete.
    assert "deleted" not in s["merge"]["appointment"]


def test_defer_fks_gives_same_result_and_restores_constraints(mapping_path):
    s = pipeline.run(mapping_path, "naive", fresh=True, defer_fks=True)
    assert s["merge"]["_fk_validate"]["constraints"] == 2
    assert q("SELECT patient_number, last_name FROM dental.patient") == [("P001", "Okafor-Lee")]
    assert q("SELECT slot FROM dental.appointment_procedure ORDER BY slot") == [(1,), (2,)]
    fks = q("""SELECT conrelid::regclass::text, convalidated FROM pg_constraint c
               JOIN pg_namespace n ON n.oid = c.connamespace
               WHERE n.nspname = 'dental' AND contype = 'f' ORDER BY 1""")
    assert fks == [("dental.appointment", True), ("dental.appointment_procedure", True)]


def test_defer_fks_still_rejects_a_real_violation(mapping_path):
    """Deferral moves the check, it doesn't remove it: an orphan that slips into the target fails VALIDATE."""
    import psycopg
    from etl import merge
    from etl.mapping import Mapping
    pipeline.run(mapping_path, "naive", fresh=True)
    m = Mapping(mapping_path)
    with db.connect() as con:
        fks = merge._drop_fks(con, m)
        con.execute("INSERT INTO dental.appointment VALUES (999, 'NOPE', NULL, 'T', 1, now())")
        with pytest.raises(psycopg.errors.ForeignKeyViolation):
            merge._restore_fks(con, fks)
        con.rollback()
    assert len(q("SELECT * FROM pg_constraint WHERE conrelid = 'dental.appointment'::regclass AND contype = 'f'")) == 1
