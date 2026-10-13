# Results and measurements (detail)

The full record behind the numbers in the [README](../README.md): how each was measured, on what machine, with what caveats. Decisions are in [DECISIONS.md](../DECISIONS.md).

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

Build time is 190 s on an Apple M4 (10 cores, 16 GB) using one process, streaming straight from the zips. The legacy DB is 957 MB. There are 3,967,754 logged defects across 20 types; the definitions are in [DECISIONS.md](../DECISIONS.md) (D11, D12) and [legacyize.yaml](../datasets/synpuf/legacyize.yaml). Two builds are byte-identical.

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

## C# / .NET 10 engine (port in progress, see DECISIONS D27–D28)

```bash
dotnet run -c Release --project src/Etl.Cli -- transform --mapping datasets/synpuf/mapping.yaml --checksum
```

The C# transform produces **output identical to the Python engine's**: an order-independent per-entity checksum over every transformed row (typed canonical encoding → MD5 → summed) matches for all 8 entities, 9,527,947 rows. Same machine, back-to-back single runs, both computing the checksum:

| Engine | Transform time | Rows/s | Peak memory |
|---|---:|---:|---:|
| Python 3.13 | 240 s | ~39.7k | 44 MB |
| C# / .NET 10 (one thread) | **52 s** | **~183k** | 88 MB |

### Schema discovery (`etl discover`)
Profiles an unfamiliar database in one streaming pass. It reports declared vs stored types, null/blank rates, distinct counts, length ranges, value-format patterns, candidate keys, repeating column groups, code domains and near-unique keys. On the legacy DB (2,894,755 rows, 52 s), it flags every injected defect family without being told about any of them. Report: [`results/discovery_legacy.md`](../results/discovery_legacy.md).

### Length validation
String limits are read from the target DDL (`varchar(n)`/`char(n)`). A value that doesn't fit quarantines its row with reason `TOO_LONG` before load, never truncated. On the real data: 0 violations, and parity unchanged.

### Migration, validated end to end (C#)
`etl migrate --strategy batched --fresh` with the C# engine: 9,527,947 rows staged and merged in 231 s. The **Python** validator then checked it against the **Python-built** oracle: 8/8 table checksums match, the full keyed diff is 0 missing / 0 extra / 0 differing, 0 FK orphans, and the per-defect scores are identical to the Python engine's on all 20 defect types. Strategies: `naive` (prepared INSERT per row), `executemany` (NpgsqlBatch), `batched` (binary COPY with per-column typed writers), and `parallel` (N transform workers → bounded `Channel<T>` → K COPY writers, with backpressure wait measured).

### Incremental sync (`etl sync`)
Per legacy table, a `(version, rec_id)` watermark of the last *committed* sync. Each run extracts only `(watermark, high mark]`. Soft-deleted rows become tombstones that delete their target row (children cascade). An upsert version guard prevents regressions. The watermark commits in the merge's transaction.

| Sync over the loaded 9.26M-row target | Rows extracted | Target rows written | Time |
|---|---:|---:|---:|
| First sync (no watermark yet) | 9,527,947 | 0 | 661 s |
| No changes since last sync | 0 | 0 | **0.63 s** |
| Delta since 2010-12-01 | 160,934 | 0 | 15–40 s (see below) |

**A "0 rows written" re-run was still writing.** `ON CONFLICT DO UPDATE … WHERE false` locks every conflicting row, and each lock is WAL-logged. Filtering unchanged rows out *before* the upsert, plus `ANALYZE` on the merge's temp tables (autovacuum never analyzes them), measured over 3 paired runs on the same delta:

| | Untuned | Tuned |
|---|---:|---:|
| WAL written per no-op delta sync | 554 MB (3/3 runs) | **1 MB** (3/3 runs) |
| Merge time | 36–42 s | 15–66 s (median 27 s) |

The WAL result is deterministic. **The time results in this section are not trustworthy:** an unrelated CPU-heavy workload was running on the same machine (load average ~23 on 10 cores; the pure-CPU transform slowed up to 3.5×). Clean, repeated measurements on a quiet, dedicated machine are pending (D33).

Next: PowerShell scripts ([`scripts/`](../scripts/), run on PowerShell 7.7; not yet on Windows), T-SQL notes ([docs/TSQL_NOTES.md](TSQL_NOTES.md)), clean benchmarks on dedicated hardware.
## C# engine benchmark (2026-10-13, repeated trials, every run validated)

Apple M4 (10 cores, 16 GB), PostgreSQL 16 in Docker Desktop, `max_wal_size = 4GB`, durability at defaults. Each run started from an empty target and passed validation against the oracle (8/8 checksums, 0 FK orphans) before it was counted. Raw results: [`results/bench/dotnet-m4-2026-10-13/`](../results/bench/dotnet-m4-2026-10-13/).

| Strategy (C#) | Trials | Extract + load | Merge (SQL) | **Total** | Peak memory |
|---|---:|---:|---:|---:|---:|
| `executemany` (NpgsqlBatch, 20k) | 2 | 256–362 s | 164–196 s | **420–558 s** | 540–570 MB |
| `batched` (binary COPY, 20k) | 2 | 103–104 s | 201–233 s | **305–336 s** | 174–188 MB |
| `parallel`, 2 workers | 1 | 71 s | 203 s | **274 s** | 137 MB |
| `parallel`, 4 workers | 1 | 60 s | 198 s | **258 s** | 204 MB |
| `parallel`, 8 workers | 1 | 63 s | 220 s | **283 s** | 316 MB |

**What it shows:**
- **The merge now dominates** at about 70–80% of total. The C# transform is fast enough that the set-based SQL merge (identical for both engines) is the bottleneck. The 4.6× transform speedup over Python therefore becomes a much smaller end-to-end gain.
- **Parallel loading plateaus at 4 workers.** At 8 workers, producers spent 13.6 s blocked on channel backpressure: the 2 COPY writers became the limit, and the bounded channel kept memory capped instead of growing.
- **`executemany` holds 20,000 parameterized commands per batch**, about 3× the memory of COPY for the same rows.

**Incomplete matrix, stated plainly:** 3 trials per configuration were planned. Mid-run the laptop went to sleep (lid closed), and one merge sat on `WALWrite` for 50 minutes. Shortly before that, an unrelated CPU-heavy process had started on the same machine. The matrix was stopped there. One run that overlapped the other process is excluded, and only the 7 clean runs above are reported. A same-day Python baseline wasn't run. The Python figures below come from an earlier session and aren't directly comparable.

## Load strategy benchmark (25% subset → 9,527,947 staged rows → 9,255,375 target rows)

```bash
./scripts/bench_all.sh   # every strategy from an empty target, each validated against the oracle and scored
```

The machine is an Apple M4 (4 performance + 6 efficiency cores, 16 GB). Postgres 16 runs in Docker Desktop (10 vCPU, 8 GB) with default durability settings. Every run starts from an empty target. Raw results, including each run's validation and defect scores, are in [`results/bench/`](../results/bench/).

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

### Merge optimization (`--defer-fks`, `max_wal_size`)

The merge was profiled per step before anything changed: upsert 62%, survivorship 27%, orphan checks 7%, child-set delete 4%. Each hypothesis was tested with [`scripts/profile_merge.py`](../scripts/profile_merge.py), which runs the merge on the current staging inside a rolled-back transaction. Baseline and candidate runs alternate, 3 pairs each:

| Change | Baseline | Candidate | Verdict |
|---|---:|---:|---|
| `work_mem` 4 MB → 256 MB | 149 s | 163 s | rejected |
| `--defer-fks`: drop FKs → upsert → `ADD … NOT VALID` → `VALIDATE` | 158 s (mean) | **133 s (mean)** | **−16%** (per pair −12% to −20%); kept, opt-in |

The merge already quarantines orphans set-based, so the per-row FK triggers were redundant. `VALIDATE` re-checks every row once, with a join (6–8 s for all 7 FKs). It's opt-in because `ALTER TABLE` locks the target tables until commit. That's for cutover loads, not live databases.

`max_wal_size` was raised from 1 GB to 4 GB: each merge writes several GB of WAL, and the default forced dozens of checkpoints per merge (it's 1–2 now). Durability is unchanged. One end-to-end 8-worker run with `--defer-fks` validated cleanly at 260 s total. That isn't directly comparable to the table above (different day, different machine state), which is why the paired number is the one reported.

Memory stays flat regardless of input size: about 120 MB for single-process batched runs, and at most 95 MB per parallel worker.

## Validation (`etl validate`, `python -m datasets.synpuf.score`)

The expected target is built **independently** from the clean CMS files ([`oracle.py`](../datasets/synpuf/oracle.py): separate, deliberately simple parsing, none of the engine's converters) into a sibling schema with the same DDL. The checks:

| Check | Result |
|---|---|
| Row counts, all 8 tables | match the oracle exactly |
| Order-independent checksums: `count` + Σ first 64 bits of `md5(quote_nullable(...))` | 8/8 match |
| Full keyed diff (`FULL JOIN` on natural key, 9.3M rows, ~20 s) | 0 missing / 0 extra / 0 differing |
| FK integrity in target | 0 orphans on all 7 references |
| Injected defects (3,967,754 across 20 types), scored against the truth log | 19/20 types at 100% |

**ICD-9 codes stored as integers by the legacy system** (the resolver's policy is in [DECISIONS.md](../DECISIONS.md) D13, D18, D24):

Once a legacy column stored codes as integers, *every* numeric code is suspect: `389` might be `389` or `038.9` that lost its zero. The resolver checks each zero-padded candidate against the official CMS code lists (FY2008–2011 and FY2015). One valid candidate → `repaired`. Several valid candidates and the received value is one of them → stored, labeled `assumed`. Otherwise → withheld (NULL, with a status). Every code keeps its raw value and its resolution status.

| Across all 5,486,516 codes | Policy A: withhold ambiguous | **Policy C: assume, labeled (current)** |
|---|---:|---:|
| Correct | 5,218,649 (95.1%) | **5,429,410 (99.0%)** |
| Withheld | 265,027 (4.8%) | 46,850 (0.9%) |
| Wrong | 2,840 (0.05%) | 10,256 (0.19%), of which 7,416 are labeled `assumed` |

"Assumed" never fixes a code that actually lost a zero: by definition, the received value is wrong there. The gain is entirely on undamaged codes that policy A was withholding. Consumers who need certainty filter on `code_resolution <> 'assumed'`. The remaining wrong codes are source codes that aren't valid in *any* ICD-9 version, so no reference list can fix them.

The validator found a real bug on its first run: 3 extra `claim_line` rows out of 2.3M, caused by a filter running before survivorship. The details are in D22.
