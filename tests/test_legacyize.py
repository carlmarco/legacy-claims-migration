import os
import random
import sqlite3
import subprocess
import sys
from pathlib import Path

import pytest

from datasets.synpuf import legacyize as L

ALWAYS = {k: 1.0 for k in ("B1_date_format", "B3_money_format", "B4_dx_dot_format", "B5_ndc_hyphen",
                           "C1_sex", "C3_state", "C5_esrd")}
NEVER = {k: 0.0 for k in ALWAYS}


def test_int_cast_matches_sqlite_integer_affinity():
    con = sqlite3.connect(":memory:")
    con.execute("CREATE TABLE t (v INTEGER)")
    for code in ["0389", "00843", "4580", "V5841", "OTHER", "00247037252"]:
        con.execute("DELETE FROM t")
        con.execute("INSERT INTO t VALUES (?)", (code,))
        assert str(con.execute("SELECT v FROM t").fetchone()[0]) == L.int_cast(code)


@pytest.mark.parametrize("seed", range(20))
def test_dates_keep_their_value(seed):
    v, d = L.corrupt_date("20080412", random.Random(seed), ALWAYS)
    assert d == "B1"
    assert v in {"04/12/2008", "2008-04-12", "4/12/08"}


def test_missing_date_sentinels():
    seen = {L.corrupt_date("", random.Random(s), NEVER, allow_far_future=True) for s in range(200)}
    assert {v for v, _ in seen} == {"", None, "00000000", "N/A", "99991231"}
    assert all((d == "B2") == (v not in ("", None)) for v, d in seen)


@pytest.mark.parametrize("seed", range(30))
def test_money_text_round_trips(seed):
    for clean in ["1200.00", "50.50", "-50.00", "7.10"]:
        v, d = L.corrupt_money(clean, random.Random(seed), ALWAYS)
        parsed = v.strip().replace("$", "").replace(",", "")
        if parsed.startswith("("):
            parsed = "-" + parsed.strip("()")
        assert float(parsed) == float(clean), (clean, v)


def test_zero_money_untouched():
    assert L.corrupt_money("0.00", random.Random(0), ALWAYS) == ("0.00", None)


def test_dx_dot_only_for_text_codes():
    assert L.corrupt_dx("V5841", random.Random(0), ALWAYS) == ("V58.41", "B4_dot")
    assert L.corrupt_dx("E8889", random.Random(0), ALWAYS) == ("E888.9", "B4_dot")
    assert L.corrupt_dx("0389", random.Random(0), ALWAYS) == ("0389", "B4_int_cast")
    assert L.corrupt_dx("4580", random.Random(0), ALWAYS) == ("4580", None)


def test_ndc():
    assert L.corrupt_ndc("00247037252", random.Random(0), ALWAYS) == ("00247-0372-52", "B5_hyphen")
    assert L.corrupt_ndc("00247037252", random.Random(0), NEVER) == ("00247037252", "B5_int_cast")
    assert L.corrupt_ndc("OTHER", random.Random(0), ALWAYS) == ("OTHER", None)


RAW = Path("data/raw")


@pytest.mark.skipif(not os.environ.get("ETL_SLOW") or not RAW.exists(), reason="set ETL_SLOW=1; needs data/raw")
def test_build_is_deterministic_and_truth_matches_legacy(tmp_path):
    def build(tag):
        out, truth = tmp_path / f"{tag}.sqlite", tmp_path / f"{tag}_truth.sqlite"
        subprocess.run([sys.executable, "-m", "datasets.synpuf.legacyize", "--pct", "0.5",
                        "--out", str(out), "--truth", str(truth)], check=True, capture_output=True)
        return out, truth

    def dump(path):
        con = sqlite3.connect(path)
        return [line for line in con.iterdump() if "built_at" not in line]

    (a, at), (b, bt) = build("a"), build("b")
    assert dump(a) == dump(b)
    assert dump(at) == dump(bt)

    legacy, truth = sqlite3.connect(a), sqlite3.connect(at)
    rows = truth.execute("""SELECT defect_id, tbl, rec_id, col, legacy_value FROM defect
                            WHERE rec_id IS NOT NULL AND col NOT LIKE '%[%' AND col NOT LIKE '%,%'
                              AND defect_id NOT IN ('D1', 'E3')""").fetchall()
    assert rows
    for defect_id, tbl, rec_id, col, logged in rows:
        actual = legacy.execute(f"SELECT {col} FROM {tbl} WHERE REC_ID = ?", (rec_id,)).fetchone()[0]
        assert str(actual) == str(logged), (defect_id, tbl, rec_id, col)
