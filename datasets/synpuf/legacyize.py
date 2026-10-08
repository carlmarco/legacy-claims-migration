"""Degrade clean SynPUF into a realistic legacy SQLite database, logging every injected defect.

    python -m datasets.synpuf.legacyize [--pct 25] [--out data/legacy.sqlite] [--truth data/legacy_truth.sqlite]

Outputs:
  legacy.sqlite        what the migrator sees
  legacy_truth.sqlite  ground-truth defect log; read only by the validator, never by the migrator

Every random decision comes from rng_for(seed, table, natural key), so a given row gets the same
damage regardless of processing order, worker count, or subset size.
Defect IDs (A1..E3) match the M1 defect list; see DECISIONS.md.
"""
import argparse
import os
import sqlite3
import time
from datetime import date, datetime, timedelta
from decimal import Decimal
from pathlib import Path

import yaml

from etl.sampling import keep, rng_for, unit
from etl.sources import iter_zip_csv

from . import files as F

HERE = Path(__file__).parent

# ---------------------------------------------------------------------------------------------
# Legacy table layouts: (legacy column, source column, kind). Kind drives both the SQLite
# declared type and which defects apply. Source column None = derived.
# ---------------------------------------------------------------------------------------------
FLAG_COLS = [(f"FLG_{s[3:]}", s, "flag") for s in F.CHRONIC_FLAGS]
MONEY_BENE = [
    (f"AMT_{part}_{kind}", f"{src}_{part}", "money")
    for part in ("IP", "OP", "CAR")
    for kind, src in (("REIMB", "MEDREIMB"), ("BENRES", "BENRES"), ("PPPYMT", "PPPYMT"))
]
MBR_MSTR = [
    ("MBR_ID", "DESYNPUF_ID", "bene_id"),
    ("YR", None, "int"),
    ("DOB", "BENE_BIRTH_DT", "dob"),
    ("DOD", "BENE_DEATH_DT", "dod"),
    ("SEX", "BENE_SEX_IDENT_CD", "sex"),
    ("RACE", "BENE_RACE_CD", "plain"),
    ("ESRD", "BENE_ESRD_IND", "esrd"),
    ("ST", "SP_STATE_CODE", "state"),
    ("CNTY", "BENE_COUNTY_CD", "plain"),
    ("MOS_A", "BENE_HI_CVRAGE_TOT_MONS", "count"),
    ("MOS_B", "BENE_SMI_CVRAGE_TOT_MONS", "count"),
    ("MOS_HMO", "BENE_HMO_CVRAGE_TOT_MONS", "count"),
    ("MOS_D", "PLAN_CVRG_MOS_NUM", "count"),
    *FLAG_COLS,
    *MONEY_BENE,
]
CLM_HDR_ALL = [
    ("CLM_TYP", None, "claim_type"),
    ("CLM_ID", "CLM_ID", "plain"),
    ("SEG", "SEGMENT", "plain"),
    ("MBR_ID", "DESYNPUF_ID", "ref_id"),
    ("DOB", None, "dob"),  # A1: denormalized copies of beneficiary demographics
    ("SEX", None, "sex"),
    ("ST", None, "state"),
    ("FROM_DT", "CLM_FROM_DT", "date"),
    ("THRU_DT", "CLM_THRU_DT", "date"),
    ("PRVDR", "PRVDR_NUM", "plain"),
    ("AMT_PMT", "CLM_PMT_AMT", "money"),
    ("AMT_PRMRY_PYR", "NCH_PRMRY_PYR_CLM_PD_AMT", "money"),
    ("AT_NPI", "AT_PHYSN_NPI", "plain"),
    ("OP_NPI", "OP_PHYSN_NPI", "plain"),
    ("OT_NPI", "OT_PHYSN_NPI", "plain"),
    ("ADMIT_DT", "CLM_ADMSN_DT", "date"),
    ("ADMIT_DX", "ADMTNG_ICD9_DGNS_CD", "dx"),
    ("AMT_PASSTHRU", "CLM_PASS_THRU_PER_DIEM_AMT", "money"),
    ("AMT_IP_DED", "NCH_BENE_IP_DDCTBL_AMT", "money"),
    ("AMT_PTA_COINS", "NCH_BENE_PTA_COINSRNC_LBLTY_AM", "money"),
    ("AMT_BLOOD_DED", "NCH_BENE_BLOOD_DDCTBL_LBLTY_AM", "money"),
    ("AMT_PTB_DED", "NCH_BENE_PTB_DDCTBL_AMT", "money"),
    ("AMT_PTB_COINS", "NCH_BENE_PTB_COINSRNC_AMT", "money"),
    ("UTIL_DAYS", "CLM_UTLZTN_DAY_CNT", "count"),
    ("DISCH_DT", "NCH_BENE_DSCHRG_DT", "date"),
    ("DRG", "CLM_DRG_CD", "plain"),
    *[(f"DX{i}", f"ICD9_DGNS_CD_{i}", "dx") for i in range(1, 11)],
    *[(f"PX{i}", f"ICD9_PRCDR_CD_{i}", "px") for i in range(1, 7)],
    *[(f"HCPCS{i}", f"HCPCS_CD_{i}", "plain") for i in range(1, 46)],
]
CAR_LINE_FIELDS = [
    ("NPI", "PRF_PHYSN_NPI", "plain"),
    ("TAX", "TAX_NUM", "plain"),
    ("HCPCS", "HCPCS_CD", "plain"),
    ("PMT", "LINE_NCH_PMT_AMT", "money"),
    ("DED", "LINE_BENE_PTB_DDCTBL_AMT", "money"),
    ("PRPYR", "LINE_BENE_PRMRY_PYR_PD_AMT", "money"),
    ("COINS", "LINE_COINSRNC_AMT", "money"),
    ("ALOWD", "LINE_ALOWD_CHRG_AMT", "money"),
    ("PRCSG", "LINE_PRCSG_IND_CD", "plain"),
    ("DX", "LINE_ICD9_DGNS_CD", "dx"),
]
CAR_CLM = [
    ("CLM_ID", "CLM_ID", "plain"),
    ("MBR_ID", "DESYNPUF_ID", "ref_id"),
    ("FROM_DT", "CLM_FROM_DT", "date"),
    ("THRU_DT", "CLM_THRU_DT", "date"),
    ("DX_LIST", None, "dx_list"),  # A4: ICD9_DGNS_CD_1..8 packed into one delimited string
    *[(f"L{n}_{name}", f"{src}_{n}", kind) for n in range(1, 14) for name, src, kind in CAR_LINE_FIELDS],
]
RX_EVNT = [
    ("PDE_ID", "PDE_ID", "plain"),
    ("MBR_ID", "DESYNPUF_ID", "ref_id"),
    ("SRVC_DT", "SRVC_DT", "date"),
    ("NDC", "PROD_SRVC_ID", "ndc"),
    ("QTY", "QTY_DSPNSD_NUM", "plain"),
    ("DAYS", "DAYS_SUPLY_NUM", "count"),
    ("AMT_PTNT_PAY", "PTNT_PAY_AMT", "money"),
    ("AMT_TOT_COST", "TOT_RX_CST_AMT", "money"),
]
AUDIT_COLS = ["LOAD_TS", "LOAD_BATCH", "DEL_FLG"]
INTEGER_KINDS = {"dx", "px", "ndc", "int"}  # INTEGER affinity: SQLite itself strips leading zeros

STRUCTURAL = [
    ("A1", "CLM_HDR_ALL", "DOB,SEX,ST", "beneficiary demographics denormalized onto every claim row"),
    ("A2", "CLM_HDR_ALL", "CLM_TYP", "inpatient and outpatient claims merged into one table"),
    ("A3", "CLM_HDR_ALL,CAR_CLM", "DX*,PX*,HCPCS*,L*_*", "repeating groups stored as wide columns"),
    ("A4", "CAR_CLM", "DX_LIST", "header diagnoses packed into one '|'-delimited string"),
    ("A5", "MBR_MSTR", "YR", "2008 and 2009 snapshots in one table keyed by (MBR_ID, YR)"),
    ("B4", "CLM_HDR_ALL,CAR_CLM", "DX*,PX*,ADMIT_DX,L*_DX", "ICD-9 codes in INTEGER columns"),
    ("B5", "RX_EVNT", "NDC", "NDC in an INTEGER column"),
    ("B7", "*", "MOS_*,UTIL_DAYS,DAYS", "whole numbers sometimes stored as decimal text"),
]


# ---------------------------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------------------------
class Out:
    """Buffered writers for the legacy DB and the truth DB."""

    FLUSH = 5000

    def __init__(self, legacy_path: Path, truth_path: Path):
        for p in (legacy_path, truth_path):
            p.parent.mkdir(parents=True, exist_ok=True)
            if p.exists():
                p.unlink()
        self.legacy = sqlite3.connect(legacy_path)
        self.truth = sqlite3.connect(truth_path)
        for c in (self.legacy, self.truth):
            c.execute("PRAGMA journal_mode=OFF")  # build artifact, rebuilt from scratch on failure
            c.execute("PRAGMA synchronous=OFF")
        self.truth.executescript("""
            CREATE TABLE defect (defect_id TEXT, tbl TEXT, rec_id INTEGER, col TEXT,
                                 clean_value TEXT, legacy_value TEXT);
            CREATE TABLE structural (defect_id TEXT, tbl TEXT, cols TEXT, note TEXT);
            CREATE TABLE run (key TEXT PRIMARY KEY, value TEXT);
        """)
        self.buffers: dict[str, list] = {}
        self.sql: dict[str, str] = {}
        self.next_rec: dict[str, int] = {}
        self.defects: list[tuple] = []
        self.counts: dict[str, int] = {}

    def create(self, table: str, spec: list[tuple]) -> None:
        cols = ["REC_ID INTEGER PRIMARY KEY"]
        cols += [f"{name} {'INTEGER' if kind in INTEGER_KINDS else 'TEXT'}" for name, _, kind in spec]
        cols += [f"{c} TEXT" for c in AUDIT_COLS]
        self.legacy.execute(f"CREATE TABLE {table} ({', '.join(cols)})")
        n = 1 + len(spec) + len(AUDIT_COLS)
        self.sql[table] = f"INSERT INTO {table} VALUES ({','.join('?' * n)})"
        self.buffers[table] = []
        self.next_rec[table] = 1

    def rec_id(self, table: str) -> int:
        r = self.next_rec[table]
        self.next_rec[table] = r + 1
        return r

    def row(self, table: str, values: list) -> None:
        buf = self.buffers[table]
        buf.append(values)
        if len(buf) >= self.FLUSH:
            self.legacy.executemany(self.sql[table], buf)
            buf.clear()

    def defect(self, defect_id: str, table: str, rec_id: int | None, col: str | None,
               clean: object, legacy: object) -> None:
        self.defects.append((defect_id, table, rec_id, col, clean, legacy))
        self.counts[defect_id] = self.counts.get(defect_id, 0) + 1
        if len(self.defects) >= self.FLUSH:
            self.truth.executemany("INSERT INTO defect VALUES (?,?,?,?,?,?)", self.defects)
            self.defects.clear()

    def close(self, run_info: dict) -> None:
        for table, buf in self.buffers.items():
            if buf:
                self.legacy.executemany(self.sql[table], buf)
        if self.defects:
            self.truth.executemany("INSERT INTO defect VALUES (?,?,?,?,?,?)", self.defects)
        self.truth.executemany("INSERT INTO structural VALUES (?,?,?,?)", STRUCTURAL)
        self.truth.executemany("INSERT INTO run VALUES (?,?)", [(k, str(v)) for k, v in run_info.items()])
        self.truth.execute("CREATE INDEX defect_by_id ON defect(defect_id)")
        self.truth.execute("CREATE INDEX defect_by_row ON defect(tbl, rec_id)")
        for c in (self.legacy, self.truth):
            c.commit()
            c.close()


# ---------------------------------------------------------------------------------------------
# Value-level corruption. Each returns (legacy_value, defect_id | None).
# ---------------------------------------------------------------------------------------------
def parse_ymd(v: str) -> date:
    return datetime.strptime(v, "%Y%m%d").date()


def corrupt_date(v: str, rng, rates: dict, allow_far_future: bool = False):
    if v == "":
        choices = ["", None, "00000000", "N/A"] + (["99991231"] if allow_far_future else [])
        s = rng.choice(choices)
        return s, ("B2" if s not in ("", None) else None)
    if rng.random() >= rates["B1_date_format"]:
        return v, None
    d = parse_ymd(v)
    fmt = rng.randrange(3)
    if fmt == 0:
        return d.strftime("%m/%d/%Y"), "B1"
    if fmt == 1:
        return d.isoformat(), "B1"
    return f"{d.month}/{d.day}/{d.year % 100:02d}", "B1"  # two-digit year: century is lost


def corrupt_money(v: str, rng, rates: dict):
    # Zero amounts are skipped: unused carrier lines are padded with 0.00 and would swamp the log.
    if v == "" or Decimal(v) == 0 or rng.random() >= rates["B3_money_format"]:
        return (v or None), None
    d = Decimal(v)
    if d < 0:
        return f"({-d:.2f})", "B3"
    options = ["dollar", "spaces"] + (["int"] if d == d.to_integral_value() else [])
    pick = rng.choice(options)
    if pick == "dollar":
        return f"${d:,.2f}", "B3"
    if pick == "int":
        return str(int(d)), "B3"
    return f"  {d:.1f} " if d * 10 == (d * 10).to_integral_value() else f" {d:.2f}  ", "B3"


def int_cast(v: str) -> str:
    """What an INTEGER column does to an all-digit code."""
    return str(int(v)) if v.isdigit() else v


def corrupt_dx(v: str, rng, rates: dict):
    if v == "":
        return None, None
    if v.isdigit():
        return v, ("B4_int_cast" if v[0] == "0" else None)  # SQLite applies the cast on insert
    if rng.random() < rates["B4_dx_dot_format"]:
        split = 4 if v[0] == "E" else 3  # V12.34, E123.4
        if len(v) > split:
            return f"{v[:split]}.{v[split:]}", "B4_dot"
    return v, None


def corrupt_px(v: str, rng, rates: dict):
    if v == "":
        return None, None
    return v, ("B4_int_cast" if v.isdigit() and v[0] == "0" else None)


def corrupt_ndc(v: str, rng, rates: dict):
    if v.isdigit() and len(v) == 11 and rng.random() < rates["B5_ndc_hyphen"]:
        return f"{v[:5]}-{v[5:9]}-{v[9:]}", "B5_hyphen"  # text: hyphenated NDCs keep their zeros
    return v, ("B5_int_cast" if v.isdigit() and v[0] == "0" else None)


def corrupt_count(v: str, rng, rates: dict):
    if v == "":
        return None, None
    return (f"{v}.0" if rng.random() < 0.5 else v), None  # B7 is structural; not logged per value


def corrupt_sex(v: str, rng, rates: dict):
    if rng.random() >= rates["C1_sex"] or v not in ("1", "2"):
        return v, None
    return rng.choice(["M", "male", "Male"] if v == "1" else ["F", "female", "Female"]), "C1"


def corrupt_esrd(v: str, rng, rates: dict):
    if rng.random() >= rates["C5_esrd"]:
        return v, None
    return rng.choice(["N", "n"] if v == "0" else ["y", "1"]), "C5"


def make_corrupt_state(ssa_to_usps: dict[str, str]):
    def corrupt_state(v: str, rng, rates: dict):
        if rng.random() < rates["C3_state"] and v in ssa_to_usps:
            return ssa_to_usps[v], "C3"
        return v, None
    return corrupt_state


def corrupt_plain(v: str, rng, rates: dict):
    return (v if v != "" else None), None


def flag_scheme(seed: int, batch: str, rates: dict) -> str:
    """C2: each load batch uses one encoding for chronic-condition flags."""
    if unit(seed, "C2", batch) >= rates["C2_flag_batches"]:
        return "native"
    return "YN" if unit(seed, "C2-scheme", batch) < 0.5 else "01"


FLAG_MAP = {"YN": {"1": "Y", "2": "N"}, "01": {"1": "1", "2": "0"}}


# ---------------------------------------------------------------------------------------------
# Builder
# ---------------------------------------------------------------------------------------------
class Legacyizer:
    def __init__(self, cfg: dict, out: Out):
        self.seed = cfg["seed"]
        self.pct = cfg["subset_pct"]
        self.rates = cfg["rates"]
        self.batches = cfg["batches_per_year"]
        self.out = out
        ssa = yaml.safe_load((HERE / "state_codes.yaml").read_text())
        self.ssa_to_usps = {str(k).zfill(2): v for k, v in ssa.items()}
        self.ssa_codes = sorted(self.ssa_to_usps)
        self.fns = {
            "date": corrupt_date,
            "dob": corrupt_date,
            "dod": lambda v, rng, r: corrupt_date(v, rng, r, allow_far_future=True),
            "money": corrupt_money,
            "dx": corrupt_dx,
            "px": corrupt_px,
            "ndc": corrupt_ndc,
            "count": corrupt_count,
            "sex": corrupt_sex,
            "esrd": corrupt_esrd,
            "state": make_corrupt_state(self.ssa_to_usps),
            "plain": corrupt_plain,
        }
        self.demographics: dict[str, dict] = {}  # clean latest-year demographics, for A1 copies
        self.removed: set[str] = set()

    # -- helpers ------------------------------------------------------------------------------
    def apply(self, table: str, rec_id: int, col: str, kind: str, clean: str, rng) -> object:
        legacy, defect_id = self.fns[kind](clean, rng, self.rates)
        if defect_id:
            logged = int_cast(clean) if defect_id.endswith("int_cast") else legacy
            self.out.defect(defect_id, table, rec_id, col, clean, logged)
        return legacy

    @staticmethod
    def id_variant(bid: str, rng) -> str:
        options = [f"{bid} ", f" {bid}", f"{bid}  "]
        if bid.lower() != bid:
            options += [bid.lower(), f"{bid.lower()} "]
        return rng.choice(options)

    def fake_id(self, *parts: object, digits: int = 15) -> str:
        return str(int(unit(self.seed, "fake", *parts) * 10**digits)).zfill(digits)

    @staticmethod
    def del_flag(rng) -> str | None:
        return rng.choice(["N", "", None])

    # -- beneficiaries ------------------------------------------------------------------------
    def build_members(self) -> None:
        o, t = self.out, "MBR_MSTR"
        o.create(t, MBR_MSTR)
        for year, path in F.BENEFICIARY.items():
            for r in iter_zip_csv(path):
                bid = r["DESYNPUF_ID"]
                if not keep(bid, self.pct, self.seed):
                    continue
                self.demographics[bid] = {
                    "DOB": r["BENE_BIRTH_DT"], "SEX": r["BENE_SEX_IDENT_CD"], "ST": r["SP_STATE_CODE"],
                }
                if unit(self.seed, "E1", bid) < self.rates["E1_removed_bene"]:
                    if bid not in self.removed:
                        self.removed.add(bid)
                        o.defect("E1", t, None, "MBR_ID", bid, None)
                    continue
                self.emit_member(r, year)

    def emit_member(self, r: dict, year: int) -> None:
        o, t = self.out, "MBR_MSTR"
        bid = r["DESYNPUF_ID"]
        rng = rng_for(self.seed, t, bid, year)
        rec = o.rec_id(t)
        bucket = int(unit(self.seed, "batch", bid) * self.batches)
        batch = f"MBR-{year}-{bucket:02d}"
        scheme = flag_scheme(self.seed, batch, self.rates)
        values: list = [rec]
        for col, src, kind in MBR_MSTR:
            if kind == "bene_id":
                v = bid
                if year == 2009 and unit(self.seed, "D3", bid) < self.rates["D3_bene_id_variant"]:
                    v = self.id_variant(bid, rng)
                    o.defect("D3", t, rec, col, bid, v)
            elif kind == "int":
                v = year
            elif kind == "flag":
                clean = r[src]
                v = FLAG_MAP[scheme].get(clean, clean) if scheme != "native" else clean
                if v != clean:
                    o.defect("C2", t, rec, col, clean, v)
            else:
                v = self.apply(t, rec, col, kind, r[src], rng)
            values.append(v)
        load_ts = f"{year + 1}-01-{1 + bucket % 28:02d} 02:{rng.randrange(60):02d}:{rng.randrange(60):02d}"
        values += [load_ts, batch, self.del_flag(rng)]
        o.row(t, values)
        if unit(self.seed, "E3", t, bid, year) < self.rates["E3_soft_deleted"]:
            junk = list(values)
            junk[0] = o.rec_id(t)
            junk[1] = self.fake_id(t, bid, year, digits=16)
            junk[-1] = "Y"
            o.row(t, junk)
            o.defect("E3", t, junk[0], None, None, junk[1])

    # -- claims -------------------------------------------------------------------------------
    def build_claims(self, table: str, spec: list[tuple], sources: list[tuple[str, Path]]) -> None:
        o = self.out
        o.create(table, spec)
        for claim_type, path in sources:
            for r in iter_zip_csv(path):
                if keep(r["DESYNPUF_ID"], self.pct, self.seed):
                    self.emit_claim(table, spec, claim_type, r)

    def emit_claim(self, t: str, spec: list[tuple], claim_type: str | None, r: dict) -> None:
        o = self.out
        bid = r["DESYNPUF_ID"]
        key = (r.get("CLM_ID") or r.get("PDE_ID"), r.get("SEGMENT", ""))
        rng = rng_for(self.seed, t, *key)
        rec = o.rec_id(t)

        src = dict(r)
        # E2: reversed service dates
        fd, td = r.get("CLM_FROM_DT") or r.get("SRVC_DT", ""), r.get("CLM_THRU_DT", "")
        if td and fd < td and unit(self.seed, "E2", t, *key) < self.rates["E2_reversed_dates"]:
            src["CLM_FROM_DT"], src["CLM_THRU_DT"] = td, fd
            o.defect("E2", t, rec, "FROM_DT,THRU_DT", f"{fd},{td}", f"{td},{fd}")

        demo = self.demographics.get(bid, {})
        stale_col = None
        if claim_type and rng.random() < self.rates["A1_stale_copy"]:
            stale_col = rng.choice(["DOB", "SEX", "ST"])

        values: list = [rec]
        for col, s, kind in spec:
            if kind == "claim_type":
                v = claim_type
                if rng.random() < self.rates["C4_claim_type"]:
                    v = rng.choice(["I", "INPT", "01"] if claim_type == "IP" else ["O", "OUTPT", "02"])
                    o.defect("C4", t, rec, col, claim_type, v)
            elif kind == "ref_id":
                v = bid
                if rng.random() < self.rates["B6_id_variant"]:
                    v = self.id_variant(bid, rng)
                    o.defect("B6", t, rec, col, bid, v)
            elif kind == "dx_list":
                codes = [r[f"ICD9_DGNS_CD_{i}"] for i in range(1, 9)]
                for i, c in enumerate(codes, 1):
                    if c.isdigit() and c[0] == "0":
                        o.defect("B4_int_cast", t, rec, f"{col}[{i}]", c, int_cast(c))
                v = "|".join(int_cast(c) for c in codes).rstrip("|") or None
            elif s is None:  # A1 denormalized copy
                clean = demo.get(col, "")
                stale = self.stale_value(col, clean, rng) if col == stale_col and clean else None
                v = self.fns[kind](stale or clean, rng, self.rates)[0]  # format noise on copies is not scored
                if stale:
                    o.defect("A1", t, rec, col, clean, v)
            else:
                v = self.apply(t, rec, col, kind, src.get(s, ""), rng)
            values.append(v)

        base = parse_ymd(td or fd) if (td or fd) else date(2010, 12, 31)
        load_dt = datetime.combine(base, datetime.min.time()) + timedelta(
            days=rng.randint(1, 45), seconds=rng.randrange(86400))
        values += [load_dt.isoformat(sep=" "), f"{t[:3]}-{load_dt:%Y%m}", self.del_flag(rng)]

        # D2: an older, wrong version of the row was loaded first; the correct one supersedes it.
        # The superseded row gets the *higher* REC_ID, so survivorship must use LOAD_TS, not REC_ID.
        amount_col = {"CLM_HDR_ALL": "AMT_PMT", "CAR_CLM": "L1_PMT"}.get(t)
        if amount_col and unit(self.seed, "D2", t, *key) < self.rates["D2_correction"]:
            idx = 1 + [c for c, _, _ in spec].index(amount_col)
            if values[idx] is not None:
                old = list(values)
                old[0] = o.rec_id(t)
                clean_amt = src[spec[idx - 1][1]]
                wrong = f"{Decimal(clean_amt) + Decimal('10.00'):.2f}"
                old[idx] = wrong
                old[-3] = (load_dt - timedelta(days=rng.randint(5, 30))).isoformat(sep=" ")
                o.row(t, old)
                o.defect("D2", t, old[0], amount_col, clean_amt, wrong)

        o.row(t, values)

        if unit(self.seed, "D1", t, *key) < self.rates["D1_exact_dup"]:
            dup = list(values)
            dup[0] = o.rec_id(t)
            later = load_dt + timedelta(days=rng.randint(1, 10))
            dup[-3], dup[-2] = later.isoformat(sep=" "), f"{t[:3]}-{later:%Y%m}-RELOAD"
            o.row(t, dup)
            o.defect("D1", t, dup[0], None, rec, None)

        if unit(self.seed, "E3", t, *key) < self.rates["E3_soft_deleted"]:
            junk = list(values)
            junk[0] = o.rec_id(t)
            id_idx = 1 + [c for c, _, _ in spec].index("PDE_ID" if t == "RX_EVNT" else "CLM_ID")
            junk[id_idx] = self.fake_id(t, *key)
            junk[-1] = "Y"
            o.row(t, junk)
            o.defect("E3", t, junk[0], None, None, junk[id_idx])

    def stale_value(self, col: str, clean: str, rng) -> str:
        if col == "SEX":
            return "2" if clean == "1" else "1"
        if col == "ST":
            return rng.choice([c for c in self.ssa_codes if c != clean])
        d = parse_ymd(clean)
        return d.replace(year=d.year + rng.choice([-1, 1]), day=min(d.day, 28)).strftime("%Y%m%d")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", default=str(HERE / "legacyize.yaml"))
    ap.add_argument("--pct", type=float, help="override subset_pct")
    ap.add_argument("--out", default="data/legacy.sqlite")
    ap.add_argument("--truth", default="data/legacy_truth.sqlite")
    args = ap.parse_args()

    cfg = yaml.safe_load(Path(args.config).read_text())
    if args.pct is not None:
        cfg["subset_pct"] = args.pct

    # Build into temp files, then rename: a crashed run never leaves a half-built DB behind.
    out_path, truth_path = Path(args.out), Path(args.truth)
    tmp_out, tmp_truth = out_path.with_suffix(".tmp"), truth_path.with_suffix(".tmp")
    t0 = time.perf_counter()
    out = Out(tmp_out, tmp_truth)
    lz = Legacyizer(cfg, out)

    steps = [
        ("MBR_MSTR", lz.build_members),
        ("CLM_HDR_ALL", lambda: lz.build_claims("CLM_HDR_ALL", CLM_HDR_ALL,
                                                [("IP", F.INPATIENT), ("OP", F.OUTPATIENT)])),
        ("CAR_CLM", lambda: lz.build_claims("CAR_CLM", CAR_CLM, [(None, p) for p in F.CARRIER])),
        ("RX_EVNT", lambda: lz.build_claims("RX_EVNT", RX_EVNT, [(None, F.DRUG_EVENTS)])),
    ]
    for name, step in steps:
        ts = time.perf_counter()
        step()
        print(f"{name:12s} {out.next_rec[name] - 1:>10,} rows  {time.perf_counter() - ts:6.1f}s", flush=True)

    out.close({"seed": cfg["seed"], "subset_pct": cfg["subset_pct"], "rates": cfg["rates"],
               "built_at": datetime.now().isoformat(timespec="seconds")})
    os.replace(tmp_out, out_path)
    os.replace(tmp_truth, truth_path)
    print(f"defects: {dict(sorted(out.counts.items()))}")
    print(f"total {time.perf_counter() - t0:.1f}s  -> {out_path} ({out_path.stat().st_size / 1e6:,.0f} MB), {truth_path}")


if __name__ == "__main__":
    main()
