# etl

A generic engine that migrates a degraded "legacy" SQLite database into a normalized PostgreSQL schema. It includes a validation suite and benchmarks three load strategies (row-by-row, batched COPY, parallel workers).

The source data is CMS DE-SynPUF (synthetic Medicare claims). The pipeline deliberately damages it into a realistic legacy schema, then migrates it back to a clean target.

Status: work in progress. Benchmark numbers will be added here once they're measured.

## Quickstart

```bash
./datasets/synpuf/download.sh        # ~378 MB, CMS Sample 1
docker compose up -d
pip install -e '.[dev]'
```

## Legacy source (built from 25% of DE-SynPUF Sample 1, seed 0)

```bash
python -m datasets.synpuf.profile_source   # native defects in the clean data -> results/native_profile.json
python -m datasets.synpuf.legacyize        # -> data/legacy.sqlite + data/legacy_truth.sqlite
```

| Legacy table | Rows | Built from |
|---|---:|---|
| `MBR_MSTR` | 57,773 | 2008 + 2009 beneficiary summaries, one row per beneficiary per year |
| `CLM_HDR_ALL` | 219,214 | Inpatient + outpatient claims, merged, wide |
| `CAR_CLM` | 1,215,626 | Carrier claims, 13 line groups kept wide |
| `RX_EVNT` | 1,402,142 | Part D drug events |

Build time is 190 s on an Apple M4 (10 cores, 16 GB) using one process, streaming straight from the zips. The legacy DB is 957 MB. There are 3,967,754 logged defects across 20 types; the definitions are in [DECISIONS.md](DECISIONS.md) (D11, D12) and [legacyize.yaml](datasets/synpuf/legacyize.yaml). Two builds are byte-identical.

## Transform (row-level, dry run)

```bash
etl transform --mapping datasets/synpuf/mapping.yaml   # counts + quarantine reasons; writes nothing
```

| Target entity | Rows before dedup |
|---|---:|
| beneficiary / beneficiary_year | 57,481 / 57,481 |
| claim | 1,427,946 |
| claim_diagnosis | 3,246,714 |
| claim_line (carrier) | 2,366,623 |
| claim_hcpcs | 952,013 |
| claim_procedure | 24,418 |
| drug_event | 1,395,271 |

2,880,698 legacy rows become 9,527,947 target rows in 325 s (**29k rows/s, one process, transform only**). 14,057 soft-deleted rows are excluded and 0 rows are quarantined for bad values.
