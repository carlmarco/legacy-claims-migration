"""The engine run against a second, unrelated legacy schema (a toy dental practice system)."""
import shutil
import sqlite3
from datetime import date
from decimal import Decimal
from pathlib import Path

import pytest

from etl.mapping import LINEAGE_COLS, Mapping

FIXTURE = Path(__file__).parent / "fixtures" / "dental"


@pytest.fixture
def mapping(tmp_path):
    shutil.copy(FIXTURE / "mapping.yaml", tmp_path / "mapping.yaml")
    con = sqlite3.connect(tmp_path / "legacy.sqlite")
    con.executescript("""
        CREATE TABLE PATIENT_TBL (ROWID_ INTEGER, PT_NUM TEXT, LNAME TEXT, BDATE TEXT, GENDER TEXT,
                                  ZIP INTEGER, DELETED TEXT, MODIFIED TEXT);
        INSERT INTO PATIENT_TBL VALUES
          (1, 'p001 ', 'Okafor', '03/14/1961', 'f', '02139', 'F', '2019-01-01'),
          (2, 'P002', 'Lind',   '00/00/0000', 'M', 10001,  NULL, '2019-01-02'),
          (3, 'P003', 'Ruiz',   '13/40/1970', 'm', 94110,  'F', '2019-01-03'),
          (4, 'P004', 'Gone',   '01/01/1950', 'm', 94110,  'T', '2019-01-04');
        CREATE TABLE APPT_TBL (ROWID_ INTEGER, APPT_NO TEXT, PT_NUM TEXT, APPT_DT TEXT,
                               PROC1 TEXT, FEE1 TEXT, PROC2 TEXT, FEE2 TEXT, PROC3 TEXT, FEE3 TEXT,
                               PROC4 TEXT, FEE4 TEXT, DELETED TEXT, MODIFIED TEXT);
        INSERT INTO APPT_TBL VALUES
          (1, '500', 'P001', '06/01/2020', 'D0120', '$45.00', 'D1110', '95', NULL, '0.00', '', NULL,
           'F', '2020-06-01');
    """)
    con.commit()
    con.close()
    return Mapping(tmp_path / "mapping.yaml")


def rows(m, entity):
    out = []
    for r in m.transform():
        if r.kind == "row" and r.entity == entity:
            out.append(dict(zip(m.entities[entity].output, r.values)))
    return out


def test_flat_entity_conversions(mapping):
    pts = {p["patient_number"]: p for p in rows(mapping, "patient")}
    assert set(pts) == {"P001", "P002"}  # P003 quarantined, P004 soft-deleted
    assert pts["P001"]["zip"] == "02139"  # INTEGER column dropped the zero; restored
    assert pts["P001"]["birth_date"] == date(1961, 3, 14)
    assert pts["P001"]["gender"] == "F"
    assert pts["P002"]["birth_date"] is None  # null token
    assert pts["P001"]["_src_table"] == "PATIENT_TBL" and pts["P001"]["_version"] == "2019-01-01"


def test_bad_value_is_quarantined_not_nulled(mapping):
    q = [r for r in mapping.transform() if r.kind == "quarantine"]
    assert len(q) == 1
    assert q[0].rec_id == 3 and q[0].reason == "BAD_VALUE" and "birth_date" in q[0].detail
    assert q[0].raw["PT_NUM"] == "P003"


def test_soft_deletes_excluded_and_counted(mapping):
    assert mapping.excluded_count("PATIENT_TBL") == 1


def test_unpivot_keeps_only_real_slots(mapping):
    procs = rows(mapping, "appointment_procedure")
    assert [(p["slot"], p["procedure_code"], p["fee"]) for p in procs] == [
        (1, "D0120", Decimal("45.00")),
        (2, "D1110", Decimal("95.00")),
    ]  # slot 3 is 0.00 with no code; slot 4 is empty


def test_entity_metadata(mapping):
    e = mapping.entities["appointment_procedure"]
    assert e.key == ["appointment_number", "slot"]
    assert e.references == {"appointment": ["appointment_number"]}
    assert e.output[-3:] == LINEAGE_COLS
