"""Export a small, deterministic sample of the legacy DB (plus the mapping and reference files) for the browser demo.

Beneficiaries are chosen by a keyed hash of the normalized ID (same idea as the main subset), so every claim and drug
event of a chosen beneficiary comes along, including their duplicates, corrections and soft-deleted junk rows. Two
beneficiaries the legacy-izer removed (E1) are added on purpose, so the demo shows orphan quarantine.

    python -m datasets.synpuf.export_demo_sample [--pct 0.3] [--out demo/Etl.Demo/wwwroot/sample]
"""
import argparse
import csv
import re
import shutil
import sqlite3
from pathlib import Path

from etl.sampling import unit

HERE = Path(__file__).parent
TABLES = ["MBR_MSTR", "CLM_HDR_ALL", "CAR_CLM", "RX_EVNT"]


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--pct", type=float, default=0.3)
    ap.add_argument("--legacy", default="data/legacy.sqlite")
    ap.add_argument("--truth", default="data/legacy_truth.sqlite")
    ap.add_argument("--out", default="demo/Etl.Demo/wwwroot/sample")
    args = ap.parse_args()
    out = Path(args.out)
    (out / "tables").mkdir(parents=True, exist_ok=True)
    (out / "icd").mkdir(parents=True, exist_ok=True)

    con = sqlite3.connect(f"file:{args.legacy}?mode=ro", uri=True)
    con.create_function("norm", 1, lambda s: (str(s) if s is not None else "").strip().upper())
    ids = {r[0] for r in con.execute("SELECT DISTINCT norm(MBR_ID) FROM MBR_MSTR")}
    chosen = {i for i in ids if unit(0, "demo", i) * 100 < args.pct}
    removed = [r[0] for r in sqlite3.connect(args.truth).execute(
        "SELECT clean_value FROM defect WHERE defect_id = 'E1' ORDER BY clean_value LIMIT 2")]
    chosen |= set(removed)
    con.execute("CREATE TEMP TABLE pick (id TEXT PRIMARY KEY)")
    con.executemany("INSERT INTO pick VALUES (?)", [(i,) for i in chosen])
    total = 0
    for t in TABLES:
        cur = con.execute(f"SELECT * FROM {t} WHERE norm(MBR_ID) IN (SELECT id FROM pick) ORDER BY REC_ID")
        cols = [d[0] for d in cur.description]
        with open(out / "tables" / f"{t}.csv", "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow(cols)
            n = 0
            for row in cur:
                w.writerow(["" if v is None else v for v in row])
                n += 1
        total += n
        print(f"{t:12s} {n:>6,} rows")

    mapping = (HERE / "mapping.yaml").read_text()
    mapping = re.sub(r"\.\./\.\./data/raw/", "icd/", mapping)
    mapping = mapping.replace("path: ../../data/legacy.sqlite", "path: tables")
    (out / "mapping.yaml").write_text(mapping)
    for f in ("state_codes.yaml", "target.sql"):
        shutil.copy(HERE / f, out / f)
    for z in sorted(Path("data/raw").glob("ICD-9-CM-*.zip")):
        shutil.copy(z, out / "icd" / z.name)
    print(f"{len(chosen)} beneficiaries ({len(removed)} removed on purpose), {total:,} legacy rows -> {out}")


if __name__ == "__main__":
    main()
