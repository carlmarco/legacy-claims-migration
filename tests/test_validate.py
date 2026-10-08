"""The generic validator on the dental fixture: a correct migration passes, and a single wrong value
is caught by the checksum and located by the diff."""
from etl import db, pipeline, validate
from etl.mapping import Mapping
from tests.test_pipeline import mapping_path, q  # noqa: F401  (fixture)

ORACLE = "dental_oracle"


def build_oracle():
    with db.connect() as con:
        con.execute(f"DROP SCHEMA IF EXISTS {ORACLE} CASCADE")
        con.execute(f"CREATE SCHEMA {ORACLE}")
        for t in ("patient", "appointment", "appointment_procedure"):
            con.execute(f"CREATE TABLE {ORACLE}.{t} AS TABLE dental.{t} WITH NO DATA")
        # Expected state, written by hand from the fixture's legacy rows.
        con.execute(f"""INSERT INTO {ORACLE}.patient (patient_number, last_name, birth_date, gender, zip)
                        VALUES ('P001', 'Okafor-Lee', '1961-03-14', 'F', '02139')""")
        con.execute(f"""INSERT INTO {ORACLE}.appointment (appointment_number, patient_number, appointment_date)
                        VALUES (500, 'P001', '2020-06-01')""")
        con.execute(f"""INSERT INTO {ORACLE}.appointment_procedure (appointment_number, slot, procedure_code, fee)
                        VALUES (500, 1, 'D0120', 45.00), (500, 2, 'D1110', 95.00)""")


def report(path):
    with db.connect() as con:
        return validate.run(con, Mapping(path), ORACLE)


def test_correct_migration_matches_oracle(mapping_path):  # noqa: F811
    pipeline.run(mapping_path, "naive", fresh=True)
    build_oracle()
    r = report(mapping_path)
    assert all(c["match"] for c in r["checksums"].values())
    assert all((d["missing_in_target"], d["extra_in_target"], d["differing_rows"]) == (0, 0, 0)
               for d in r["diff"].values())
    assert set(r["fk_orphans_in_target"].values()) == {0}
    assert r["counts"]["patient"]["quarantined"] == {"BAD_VALUE": 1}


def test_single_wrong_value_is_caught_and_located(mapping_path):  # noqa: F811
    pipeline.run(mapping_path, "naive", fresh=True)
    build_oracle()
    with db.connect() as con:
        con.execute("UPDATE dental.appointment_procedure SET fee = 96.00 WHERE slot = 2")
    r = report(mapping_path)
    assert not r["checksums"]["appointment_procedure"]["match"]
    assert r["checksums"]["patient"]["match"]
    d = r["diff"]["appointment_procedure"]
    assert d["differing_rows"] == 1 and d["differing_by_column"] == {"fee": 1}


def test_checksum_is_order_independent_and_counts_duplicates():
    with db.connect() as con:
        schema = con.execute("SELECT 'pg_temp'").fetchone()[0]

        def cs(rows):
            con.execute("DROP TABLE IF EXISTS pg_temp.t")
            con.execute("CREATE TEMP TABLE t (x text, y text)")
            for r in rows:
                con.execute("INSERT INTO t VALUES (%s, %s)", r)
            return validate.checksum(con, schema, "t", ["x", "y"])

        assert cs([("1", "p"), ("2", None), ("3", "q")]) == cs([("3", "q"), ("1", "p"), ("2", None)])
        assert cs([("1", "p"), ("1", "p")]) != cs([("1", "p")])          # duplicates count (XOR would cancel)
        assert cs([("2", None)]) != cs([("2", "\\N")])                    # NULL vs the text '\N'
        assert cs([("2", None)]) != cs([("2", "NULL")])                   # NULL vs the text 'NULL'
        assert cs([("a|b", "c")]) != cs([("a", "b|c")])                   # delimiter inside a value
        assert cs([("a,b", "c")]) != cs([("a", "b,c")])


def test_zero_padded_codes_excluded_from_diff_but_scored():
    m = Mapping("datasets/synpuf/mapping.yaml")
    assert validate.code_columns(m, "claim_line") == ["dx"]
    assert "dx" not in validate.compare_columns(m, "claim_line")
    assert "payment_amount" in validate.compare_columns(m, "claim_line")
