"""Build the *expected* target from the clean SynPUF zips and load it into schema synpuf_oracle.

Independence is the point: this reads the clean CSVs (not the legacy DB) and uses deliberately simple,
separate parsing (no engine converters), so a bug in the engine can't hide by also being in the oracle.

Expected-state rules (what a perfect migration of the legacy DB would produce):
  - same subset (pct, seed) as the legacy-izer; beneficiaries removed by E1 are absent, and so are
    their claims and drug events (the migrator must quarantine them as orphans)
  - beneficiary demographics from the latest year; death_date = latest non-null (D11)
  - ICD columns hold the clean code; *_raw / *_resolution are left NULL (scored separately, not diffed)

    python -m datasets.synpuf.oracle [--truth data/legacy_truth.sqlite]
"""
import argparse
import sqlite3
import time
from collections.abc import Iterator
from datetime import date
from decimal import Decimal
from pathlib import Path

import psycopg
from psycopg import sql

from etl import db
from etl.mapping import LINEAGE_COLS, Mapping
from etl.sampling import keep
from etl.sources import iter_zip_csv

from . import files as F

HERE = Path(__file__).parent
ORACLE_SCHEMA = "synpuf_oracle"


def d(v: str) -> date | None:
    return date(int(v[:4]), int(v[4:6]), int(v[6:8])) if v else None


def money(v: str) -> Decimal | None:
    return Decimal(v) if v else None


def i(v: str) -> int | None:
    return int(Decimal(v)) if v else None


def s(v: str) -> str | None:
    return v or None


SEX = {"1": "M", "2": "F"}
FLAG = {"1": True, "2": False}
ESRD = {"0": False, "Y": True}
FLAGS = ["SP_ALZHDMTA", "SP_CHF", "SP_CHRNKIDN", "SP_CNCR", "SP_COPD", "SP_DEPRESSN", "SP_DIABETES",
         "SP_ISCHMCHT", "SP_OSTEOPRS", "SP_RA_OA", "SP_STRKETIA"]
BENE_MONEY = [f"{a}_{p}" for p in ("IP", "OP", "CAR") for a in ("MEDREIMB", "BENRES", "PPPYMT")]


class Oracle:
    def __init__(self, pct: float, seed: int, removed: set[str]):
        self.pct, self.seed, self.removed = pct, seed, removed

    def _rows(self, path: Path) -> Iterator[dict]:
        for r in iter_zip_csv(path):
            b = r["DESYNPUF_ID"]
            if keep(b, self.pct, self.seed) and b not in self.removed:
                yield r

    def beneficiary_year(self) -> Iterator[tuple]:
        for year, path in F.BENEFICIARY.items():
            for r in self._rows(path):
                yield (r["DESYNPUF_ID"], year, ESRD[r["BENE_ESRD_IND"]],
                       i(r["BENE_HI_CVRAGE_TOT_MONS"]), i(r["BENE_SMI_CVRAGE_TOT_MONS"]),
                       i(r["BENE_HMO_CVRAGE_TOT_MONS"]), i(r["PLAN_CVRG_MOS_NUM"]),
                       *[FLAG[r[f]] for f in FLAGS], *[money(r[c]) for c in BENE_MONEY])

    def beneficiary(self) -> Iterator[tuple]:
        latest: dict[str, dict] = {}
        death: dict[str, date] = {}
        for year, path in F.BENEFICIARY.items():  # 2008 then 2009: later year overwrites
            for r in self._rows(path):
                latest[r["DESYNPUF_ID"]] = r
                if r["BENE_DEATH_DT"]:
                    death[r["DESYNPUF_ID"]] = max(d(r["BENE_DEATH_DT"]), death.get(r["DESYNPUF_ID"], date.min))
        for b, r in latest.items():
            yield (b, d(r["BENE_BIRTH_DT"]), death.get(b), SEX[r["BENE_SEX_IDENT_CD"]], s(r["BENE_RACE_CD"]),
                   r["SP_STATE_CODE"], s(r["BENE_COUNTY_CD"]))

    def _institutional(self) -> Iterator[tuple[str, dict]]:
        for claim_type, path in (("inpatient", F.INPATIENT), ("outpatient", F.OUTPATIENT)):
            for r in self._rows(path):
                yield claim_type, r

    def claim(self) -> Iterator[tuple]:
        for t, r in self._institutional():
            g = r.get
            yield (t, r["CLM_ID"], int(r["SEGMENT"]), r["DESYNPUF_ID"], d(r["CLM_FROM_DT"]), d(r["CLM_THRU_DT"]),
                   s(r["PRVDR_NUM"]), money(r["CLM_PMT_AMT"]), money(r["NCH_PRMRY_PYR_CLM_PD_AMT"]),
                   s(r["AT_PHYSN_NPI"]), s(r["OP_PHYSN_NPI"]), s(r["OT_PHYSN_NPI"]),
                   d(g("CLM_ADMSN_DT", "")), d(g("NCH_BENE_DSCHRG_DT", "")),
                   s(r["ADMTNG_ICD9_DGNS_CD"]), None, None, s(g("CLM_DRG_CD", "")),
                   money(g("CLM_PASS_THRU_PER_DIEM_AMT", "")), money(g("NCH_BENE_IP_DDCTBL_AMT", "")),
                   money(g("NCH_BENE_PTA_COINSRNC_LBLTY_AM", "")), money(r["NCH_BENE_BLOOD_DDCTBL_LBLTY_AM"]),
                   money(g("NCH_BENE_PTB_DDCTBL_AMT", "")), money(g("NCH_BENE_PTB_COINSRNC_AMT", "")),
                   i(g("CLM_UTLZTN_DAY_CNT", "")))
        for path in F.CARRIER:
            for r in self._rows(path):
                yield ("carrier", r["CLM_ID"], 1, r["DESYNPUF_ID"], d(r["CLM_FROM_DT"]), d(r["CLM_THRU_DT"]),
                       *[None] * 19)

    def claim_diagnosis(self) -> Iterator[tuple]:
        for t, r in self._institutional():
            for n in range(1, 11):
                if r[f"ICD9_DGNS_CD_{n}"]:
                    yield (t, r["CLM_ID"], int(r["SEGMENT"]), n, r[f"ICD9_DGNS_CD_{n}"], None, None)
        for path in F.CARRIER:
            for r in self._rows(path):
                for n in range(1, 9):
                    if r[f"ICD9_DGNS_CD_{n}"]:
                        yield ("carrier", r["CLM_ID"], 1, n, r[f"ICD9_DGNS_CD_{n}"], None, None)

    def claim_procedure(self) -> Iterator[tuple]:
        for t, r in self._institutional():
            for n in range(1, 7):
                if r[f"ICD9_PRCDR_CD_{n}"]:
                    yield (t, r["CLM_ID"], int(r["SEGMENT"]), n, r[f"ICD9_PRCDR_CD_{n}"], None, None)

    def claim_hcpcs(self) -> Iterator[tuple]:
        for t, r in self._institutional():
            for n in range(1, 46):
                if r[f"HCPCS_CD_{n}"]:
                    yield (t, r["CLM_ID"], int(r["SEGMENT"]), n, r[f"HCPCS_CD_{n}"])

    def claim_line(self) -> Iterator[tuple]:
        amounts = ["LINE_NCH_PMT_AMT", "LINE_BENE_PTB_DDCTBL_AMT", "LINE_BENE_PRMRY_PYR_PD_AMT",
                   "LINE_COINSRNC_AMT", "LINE_ALOWD_CHRG_AMT"]
        for path in F.CARRIER:
            for r in self._rows(path):
                for n in range(1, 14):
                    vals = [money(r[f"{a}_{n}"]) for a in amounts]
                    hcpcs = s(r[f"HCPCS_CD_{n}"])
                    if not hcpcs and not any(vals):
                        continue
                    yield ("carrier", r["CLM_ID"], 1, n, s(r[f"PRF_PHYSN_NPI_{n}"]), s(r[f"TAX_NUM_{n}"]), hcpcs,
                           *vals, s(r[f"LINE_PRCSG_IND_CD_{n}"]), s(r[f"LINE_ICD9_DGNS_CD_{n}"]), None, None)

    def drug_event(self) -> Iterator[tuple]:
        for r in self._rows(F.DRUG_EVENTS):
            ndc = r["PROD_SRVC_ID"]
            yield (r["PDE_ID"], r["DESYNPUF_ID"], d(r["SRVC_DT"]), ndc if ndc.isdigit() else None,
                   Decimal(r["QTY_DSPNSD_NUM"]), i(r["DAYS_SUPLY_NUM"]), money(r["PTNT_PAY_AMT"]),
                   money(r["TOT_RX_CST_AMT"]))


def build(m: Mapping, truth_path: str) -> dict:
    truth = sqlite3.connect(truth_path)
    run = dict(truth.execute("SELECT key, value FROM run"))
    removed = {r[0] for r in truth.execute("SELECT clean_value FROM defect WHERE defect_id = 'E1'")}
    oracle = Oracle(float(run["subset_pct"]), int(run["seed"]), removed)
    counts = {}
    with db.connect() as con:
        con.execute(sql.SQL("DROP SCHEMA IF EXISTS {} CASCADE").format(sql.Identifier(ORACLE_SCHEMA)))
        con.execute(sql.SQL("CREATE SCHEMA {}").format(sql.Identifier(ORACLE_SCHEMA)))
        for entity, e in m.entities.items():
            table = sql.Identifier(ORACLE_SCHEMA, entity)
            con.execute(sql.SQL("CREATE TABLE {} AS TABLE {} WITH NO DATA").format(
                table, sql.Identifier(m.target_schema, entity)))
            cols = [c for c in e.output if c not in LINEAGE_COLS]
            n = 0
            with con.cursor().copy(sql.SQL("COPY {} ({}) FROM STDIN").format(
                    table, sql.SQL(", ").join(map(sql.Identifier, cols)))) as cp:
                for row in getattr(oracle, entity)():
                    if len(row) != len(cols):
                        raise ValueError(f"{entity}: oracle row has {len(row)} values, target has {len(cols)}")
                    cp.write_row(row)
                    n += 1
            con.execute(sql.SQL("ALTER TABLE {} ADD PRIMARY KEY ({})").format(
                table, sql.SQL(", ").join(map(sql.Identifier, e.key))))
            counts[entity] = n
            print(f"{entity:18s} {n:>10,}", flush=True)
        con.commit()
    return counts


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--mapping", default=str(HERE / "mapping.yaml"))
    ap.add_argument("--truth", default="data/legacy_truth.sqlite")
    args = ap.parse_args()
    t0 = time.perf_counter()
    build(Mapping(args.mapping), args.truth)
    print(f"oracle built in {time.perf_counter() - t0:.1f}s")


if __name__ == "__main__":
    main()
