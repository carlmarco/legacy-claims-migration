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

## Load strategy benchmark (25% subset → 9,527,947 staged rows → 8,817,780 target rows)

```bash
etl migrate --mapping datasets/synpuf/mapping.yaml --strategy {naive|executemany|batched|parallel} [--workers N] --fresh
```

Apple M4 (4 performance + 6 efficiency cores, 16 GB). Postgres 16 runs in Docker Desktop (10 vCPU, 8 GB) with default durability settings. Each strategy is one run from an empty target; run-to-run variance is about 5%. Raw results are in [`results/bench/`](results/bench/).

| Strategy | Transform | Load to staging | Extract+load (wall) | Staged rows/s | Merge | **Total** | vs naive |
|---|---:|---:|---:|---:|---:|---:|---:|
| `naive`: INSERT per row, autocommit | 367 s | **1,975 s** | 2,342 s | 4,068 | 160 s | **2,502 s** | 1.0× |
| `executemany`: pipelined, 20k batches | 299 s | 108 s | 408 s | 23,373 | 163 s | **570 s** | 4.4× |
| `batched`: COPY, 20k batches | **283 s** | 19 s | 301 s | 31,609 | 145 s | **447 s** | 5.6× |
| `parallel`, 2 workers | — | — | 204 s | 46,595 | 185 s | **389 s** | 6.4× |
| `parallel`, 4 workers | — | — | 134 s | 71,113 | 148 s | **282 s** | 8.9× |
| `parallel`, 8 workers | — | — | 97 s | 98,704 | **157 s** | **253 s** | 9.9× |

**Where the bottleneck is, and how it moves:**
1. **naive is bound by round trips and commits.** 84% of its time is the load. Python and Postgres each sit at about 25% CPU, both waiting on a round trip plus a WAL flush per row. On the load path alone that's 4.8k rows/s.
2. **executemany** removes the round trips, which makes the load path 18× faster (88k rows/s). It still executes one statement per row.
3. **COPY** removes the per-statement cost too: 504k rows/s on the load path, so the load is now only 6% of extract+load. **The bottleneck has moved to the Python transform**, at about 33k rows/s on one core.
4. **parallel** attacks the transform, and scaling is sublinear: 1.5× at 2 workers, 2.2× at 4, 3.1× at 8. The total worker CPU for the same work rises from 381 s to 655 s. Past 4 workers, processes land on efficiency cores, and they compete with Postgres in the same machine.
5. **At 8 workers the merge is the bottleneck: 62% of the total.** It's single-threaded set-based SQL; `claim_diagnosis` (53 s) and `claim_line` (40 s) dominate. That's the next thing to optimize.

Memory stays flat regardless of input size: about 120 MB for single-process batched runs, and at most 91 MB per parallel worker.

**Correctness is identical across all strategies.** Every run produces the same survivors, orphans and inserted counts. Those counts also reconcile exactly with the clean-source profile:
- 28,972 beneficiaries = 29,027 − 55 removed (E1).
- 1,392,642 claims = 16,713 IP + 196,100 OP + 1,179,829 carrier.
- 1,367,641 drug events.

A re-run over unchanged data writes 0 rows.
