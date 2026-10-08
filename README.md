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

## Load strategy benchmark (25% subset → 9,527,947 staged rows → 9,255,375 target rows)

```bash
./scripts/bench_all.sh   # every strategy from an empty target, each validated against the oracle and scored
```

The machine is an Apple M4 (4 performance + 6 efficiency cores, 16 GB). Postgres 16 runs in Docker Desktop (10 vCPU, 8 GB) with default durability settings. Every run starts from an empty target. Raw results, including each run's validation and defect scores, are in [`results/bench/`](results/bench/).

| Strategy | Transform | Load to staging | Extract+load (wall) | Staged rows/s | Merge | **Total** | vs naive | Validated |
|---|---:|---:|---:|---:|---:|---:|---:|:-:|
| `naive`: INSERT per row, autocommit | 284 s | **1,485 s** | 1,769 s | 5,387 | 160 s | **1,929 s** | 1.0× | ✓ |
| `executemany`: pipelined, 20k batches | 236 s | 86 s | 322 s | 29,600 | 150 s | **472 s** | 4.1× | ✓ |
| `batched`: COPY, 20k batches | **207 s** | 17 s | 225 s | 42,367 | 162 s | **386 s** | 5.0× | ✓ |
| `parallel`, 2 workers | — | — | 188 s | 50,720 | 193 s | **380 s** | 5.1× | ✓ |
| `parallel`, 4 workers | — | — | 128 s | 74,315 | 200 s | **328 s** | 5.9× | ✓ |
| `parallel`, 8 workers | — | — | 80 s | 118,578 | **170 s** | **251 s** | 7.7× | ✓ |

"Validated" means all 8 target tables match the oracle by checksum, 0 FK orphans, and 19 of 20 defect types at 100% (the 20th is the ICD-9 leading-zero case; see below).

**Where the bottleneck is, and how it moves:**
1. **naive is bound by round trips and commits.** 84% of its time is the load. Python and Postgres each sit at about 25% CPU, both waiting on a round trip plus a WAL flush per row. On the load path alone that's 6.4k rows/s.
2. **executemany** removes the round trips, which makes the load path 17× faster (111k rows/s). It still executes one statement per row.
3. **COPY** removes the per-statement cost too: 545k rows/s on the load path, so the load is now 8% of extract+load. **The bottleneck has moved to the Python transform**, at about 46k rows/s on one core.
4. **parallel** attacks the transform. Extract+load scales sublinearly: 1.2× at 2 workers, 1.8× at 4, 2.8× at 8. The total worker CPU for the same work rises from 350 s to 579 s. Past 4 workers, processes land on efficiency cores, and they compete with Postgres in the same machine.
5. **At 8 workers the merge is the bottleneck: 68% of the total.** It's single-threaded set-based SQL; `claim_diagnosis` (66 s) and `claim_line` (46 s) dominate. The merge is also 20–40 s slower after parallel loads than after single-process ones. My hypothesis is that rows interleaved by concurrent COPYs lose physical locality for the sort and join; that's unverified. The merge is the next optimization target.

**Run-to-run noise:** each row above is a single run. An earlier full set of runs on the same code, minus a merge fix, measured naive at 2,502 s (+30%), COPY at 447 s (+16%), and 8 workers at 253 s (+1%). The single-process, transform-heavy runs vary the most. The bottleneck story held in both sets. The absolute numbers depend on what else the laptop is doing; a rented-VM run with repeated trials is planned.

Memory stays flat regardless of input size: about 120 MB for single-process batched runs, and at most 95 MB per parallel worker.

## Validation (`etl validate`, `python -m datasets.synpuf.score`)

The expected target is built **independently** from the clean CMS files ([`oracle.py`](datasets/synpuf/oracle.py): separate, deliberately simple parsing, none of the engine's converters) into a sibling schema with the same DDL. The checks:

| Check | Result |
|---|---|
| Row counts, all 8 tables | match the oracle exactly |
| Order-independent checksums: `count` + Σ first 64 bits of `md5(quote_nullable(...))` | 8/8 match |
| Full keyed diff (`FULL JOIN` on natural key, 9.3M rows, ~20 s) | 0 missing / 0 extra / 0 differing |
| FK integrity in target | 0 orphans on all 7 references |
| Injected defects (3,967,754 across 20 types), scored against the truth log | 19/20 types at 100% |

**ICD-9 leading-zero loss (B4_int_cast, 41,456 cases):**
| Outcome | Count |
|---|---:|
| Repaired | 26,505 |
| Withheld as ambiguous or unresolved | 14,454 |
| **Wrong** | **295 (0.7%)** |
| On lines dropped by rule | 202 |

The wrong cases are source codes that aren't valid in any ICD-9 version, so no reference list can fix them. The broader finding: once codes were stored as integers, every numeric code is suspect, not just the damaged ones. Of 223,689 codes the resolver withholds as ambiguous, 94.2% are actually undamaged. Whether to withhold or trust the as-received value is a coverage/precision decision ([DECISIONS.md](DECISIONS.md) D23).

The validator found a real bug on its first run: 3 extra `claim_line` rows out of 2.3M, caused by a filter running before survivorship. The details are in D22.
