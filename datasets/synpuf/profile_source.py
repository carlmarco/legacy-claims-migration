"""Profile the clean SynPUF subset for defects it already has, before we inject any.

Without this baseline, native defects would be miscounted as injected ones and the
validator's repair-rate numbers would be wrong.

    python -m datasets.synpuf.profile_source --pct 25 --out results/native_profile.json
"""
import argparse
import json
import time
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path

from etl.sampling import keep
from etl.sources import iter_zip_csv

from . import files as F

EXAMPLES = 5


def valid_date(s: str) -> bool:
    try:
        datetime.strptime(s, "%Y%m%d")
        return True
    except ValueError:
        return False


def profile_beneficiaries(pct: float, seed: int) -> tuple[dict, set[str]]:
    by_year: dict[int, dict[str, dict]] = {}
    out: dict = {"years": {}}
    for year, path in F.BENEFICIARY.items():
        rows: dict[str, dict] = {}
        dup_ids = 0
        domains: dict[str, Counter] = defaultdict(Counter)
        for r in iter_zip_csv(path):
            bid = r["DESYNPUF_ID"]
            if not keep(bid, pct, seed):
                continue
            if bid in rows:
                dup_ids += 1
            rows[bid] = r
            for c in ["BENE_SEX_IDENT_CD", "BENE_RACE_CD", "BENE_ESRD_IND", *F.CHRONIC_FLAGS[:1]]:
                domains[c][r[c]] += 1
            domains["SP_STATE_CODE_distinct"][r["SP_STATE_CODE"]] += 1
            domains["BIRTH_DT_valid"][valid_date(r["BENE_BIRTH_DT"])] += 1
            domains["DEATH_DT_present"][r["BENE_DEATH_DT"] != ""] += 1
        by_year[year] = rows
        out["years"][year] = {
            "rows": len(rows) + dup_ids,
            "distinct_ids": len(rows),
            "duplicate_ids": dup_ids,
            "domains": {
                k: (len(v) if k.endswith("_distinct") else {str(a): b for a, b in v.most_common()})
                for k, v in domains.items()
            },
        }

    a, b = by_year[2008], by_year[2009]
    both = a.keys() & b.keys()
    conflicts = {}
    for f in F.DEMOGRAPHIC_FIELDS:
        diff = [i for i in both if a[i][f] != b[i][f]]
        conflicts[f] = {
            "count": len(diff),
            "examples": [{"id": i, "2008": a[i][f], "2009": b[i][f]} for i in sorted(diff)[:EXAMPLES]],
        }
    out["overlap"] = {
        "only_2008": len(a.keys() - b.keys()),
        "only_2009": len(b.keys() - a.keys()),
        "both": len(both),
    }
    out["cross_year_conflicts"] = conflicts
    return out, a.keys() | b.keys()


def profile_claims(name: str, paths: list[Path], key_cols: list[str], date_cols: tuple[str, str],
                   benes: set[str], pct: float, seed: int, icd9_lead0: Counter, icd9_all: set[tuple[str, str]],
                   claim_ids: dict[str, set[str]]) -> dict:
    seen: set[tuple] = set()
    rows = dup_keys = orphans = reversed_dates = bad_dates = no_dates = 0
    dup_examples, orphan_examples = [], []
    ids: set[str] = set()
    for path in paths:
        icd_cols = None
        for r in iter_zip_csv(path):
            bid = r["DESYNPUF_ID"]
            if not keep(bid, pct, seed):
                continue
            rows += 1
            key = tuple(r[k] for k in key_cols)
            ids.add(r[key_cols[0]])
            if key in seen:
                dup_keys += 1
                if len(dup_examples) < EXAMPLES:
                    dup_examples.append(key)
            seen.add(key)
            if bid not in benes:
                orphans += 1
                if len(orphan_examples) < EXAMPLES:
                    orphan_examples.append(bid)
            d0, d1 = r[date_cols[0]], r[date_cols[1]] if date_cols[1] else ""
            if not d0 and not d1:
                no_dates += 1
            elif not valid_date(d0) or (d1 and not valid_date(d1)):
                bad_dates += 1
            elif d1 and d1 < d0:
                reversed_dates += 1
            if icd_cols is None:
                icd_cols = F.icd9_columns(list(r.keys()))
            for c in icd_cols:
                v = r[c]
                if v:
                    system = "dx" if "DGNS" in c else "px"  # diagnosis and procedure are separate code systems
                    icd9_all.add((system, v))
                    if v[0] == "0":
                        icd9_lead0[(system, v)] += 1
    claim_ids[name] = ids
    return {
        "rows": rows,
        "duplicate_keys": dup_keys,
        "duplicate_key_examples": dup_examples,
        "orphan_rows": orphans,
        "orphan_examples": orphan_examples,
        "no_dates": no_dates,
        "bad_dates": bad_dates,
        "reversed_dates": reversed_dates,
    }


def profile_drug_events(benes: set[str], pct: float, seed: int) -> dict:
    seen: set[str] = set()
    rows = dup = orphans = bad_dates = 0
    ndc_len: Counter = Counter()
    ndc_lead0 = 0
    for r in iter_zip_csv(F.DRUG_EVENTS):
        bid = r["DESYNPUF_ID"]
        if not keep(bid, pct, seed):
            continue
        rows += 1
        if r["PDE_ID"] in seen:
            dup += 1
        seen.add(r["PDE_ID"])
        orphans += bid not in benes
        bad_dates += not valid_date(r["SRVC_DT"])
        ndc = r["PROD_SRVC_ID"]
        ndc_len["non-numeric" if not ndc.isdigit() else str(len(ndc))] += 1
        ndc_lead0 += ndc.startswith("0")
    return {
        "rows": rows,
        "duplicate_keys": dup,
        "orphan_rows": orphans,
        "bad_dates": bad_dates,
        "ndc_length_distribution": dict(sorted(ndc_len.items())),
        "ndc_with_leading_zero": ndc_lead0,
    }


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--pct", type=float, default=25)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--out", default="results/native_profile.json")
    args = ap.parse_args()

    t0 = time.perf_counter()
    report: dict = {"pct": args.pct, "seed": args.seed}
    report["beneficiary"], benes = profile_beneficiaries(args.pct, args.seed)

    icd9_lead0: Counter = Counter()
    icd9_all: set[tuple[str, str]] = set()
    claim_ids: dict[str, set[str]] = {}
    report["inpatient"] = profile_claims("inpatient", [F.INPATIENT], ["CLM_ID", "SEGMENT"],
                                         ("CLM_FROM_DT", "CLM_THRU_DT"), benes, args.pct, args.seed,
                                         icd9_lead0, icd9_all, claim_ids)
    report["outpatient"] = profile_claims("outpatient", [F.OUTPATIENT], ["CLM_ID", "SEGMENT"],
                                          ("CLM_FROM_DT", "CLM_THRU_DT"), benes, args.pct, args.seed,
                                          icd9_lead0, icd9_all, claim_ids)
    report["carrier"] = profile_claims("carrier", F.CARRIER, ["CLM_ID"],
                                       ("CLM_FROM_DT", "CLM_THRU_DT"), benes, args.pct, args.seed,
                                       icd9_lead0, icd9_all, claim_ids)
    report["claim_id_overlap_across_types"] = {
        "inpatient&outpatient": len(claim_ids["inpatient"] & claim_ids["outpatient"]),
        "inpatient&carrier": len(claim_ids["inpatient"] & claim_ids["carrier"]),
        "outpatient&carrier": len(claim_ids["outpatient"] & claim_ids["carrier"]),
    }
    report["drug_events"] = profile_drug_events(benes, args.pct, args.seed)

    # B4 preview: codes whose integer cast collides with a different real code in the data.
    report["icd9"] = {}
    for system in ("dx", "px"):
        lead0 = {code: n for (s, code), n in icd9_lead0.items() if s == system}
        codes = {code for (s, code) in icd9_all if s == system}
        collisions = sorted(c for c in lead0 if c.lstrip("0") in codes)
        report["icd9"][system] = {
            "distinct_codes": len(codes),
            "distinct_with_leading_zero": len(lead0),
            "occurrences_with_leading_zero": sum(lead0.values()),
            "integer_cast_collisions": len(collisions),
            "collision_occurrences": sum(lead0[c] for c in collisions),
            "collision_examples": [(c, c.lstrip("0")) for c in collisions[:10]],
        }
    report["elapsed_s"] = round(time.perf_counter() - t0, 1)

    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    Path(args.out).write_text(json.dumps(report, indent=2, default=list))
    print(json.dumps(report, indent=2, default=list))


if __name__ == "__main__":
    main()
