# Legacy Claims Migration

[![CI](https://github.com/carlmarco/legacy-claims-migration/actions/workflows/ci.yml/badge.svg)](https://github.com/carlmarco/legacy-claims-migration/actions/workflows/ci.yml)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4) ![PostgreSQL 16](https://img.shields.io/badge/PostgreSQL-16-336791) ![License: MIT](https://img.shields.io/badge/license-MIT-green)

**A C#/.NET migration engine that moves a messy legacy database into a clean PostgreSQL schema, and proves the result is correct, row by row.**

**[▶ Try the live demo](https://carlmarco.github.io/legacy-claims-migration/)**: the same engine, compiled to WebAssembly, running in your browser. Profile damaged legacy data, edit the mapping, migrate, and export to CSV, JSON or a PostgreSQL / SQL Server script.

Real migrations fail quietly: a date format nobody noticed, codes that lost their leading zeros, a record loaded twice. This project takes 2.9 million rows of Medicare claims data that was *deliberately damaged* in 20 realistic ways, migrates it, and measures exactly how much was repaired, flagged or rejected, against a ground truth the engine never sees.

> Built using Claude Code.

## At a glance

| | |
|---|---|
| **Scale** | 2,894,755 legacy rows → **9,255,375** rows in a normalized 8-table schema |
| **Correctness** | Matches an independently built expected result on **every row**: 8/8 tables checksum-identical, 0 differing rows, 0 broken references |
| **Data quality** | 3,967,754 injected defects across 20 types; **19 of 20 types handled 100%**. The 20th (medical codes stored as integers) is 99.0% correct, and uncertain values are *labeled*, never silently guessed |
| **Performance** | Bulk loading is **85× faster** than row-by-row; the C# engine is **4.6× faster** than the Python reference implementation, with identical output |
| **Incremental sync** | Re-syncing 9.26M rows with no changes takes **0.6 s**. A fix to how unchanged rows are handled cut database write volume by **99.8%** |
| **Quality bar** | 178 automated tests (65 xUnit, 113 pytest) running against real PostgreSQL in CI. 33 design decisions logged with the alternatives rejected |

*Performance figures are single runs on an Apple M4 laptop; repeated-trial benchmarks are in progress. Full methodology and caveats: [docs/RESULTS.md](docs/RESULTS.md).*

## How it works

```
 legacy SQLite DB ──► DISCOVER  profile an unfamiliar schema: types, formats, keys, duplicates, code domains
 (2.9M rows,     ──► TRANSFORM  declarative YAML mapping → typed converters, unpivots, ICD-code resolution
  20 defect types)               anything unparseable → quarantine with a reason (never coerced to NULL)
                 ──► LOAD       naive | batched | binary COPY | parallel producer/consumer with backpressure
                 ──► MERGE      one transaction: dedupe (newest wins) → orphan check → change-guarded upsert
                 ──► VALIDATE   vs an expected result built independently: checksums, row-level diff, FK checks
                 ──► SYNC       afterwards: watermark-based incremental updates and deletes
```

The engine contains **no dataset-specific code**. The Medicare dataset is a 230-line [YAML mapping](datasets/synpuf/mapping.yaml), and a second, unrelated test dataset (a dental practice system) runs through the same engine unchanged.

## Try it

Requires Docker, .NET 10 and Python 3.11+.

```bash
docker compose up -d                                     # PostgreSQL 16
./datasets/synpuf/download.sh                            # CMS synthetic Medicare data + ICD-9 code lists (~380 MB)
python -m venv .venv && .venv/bin/pip install -e ".[dev]"
.venv/bin/python -m datasets.synpuf.legacyize            # build the damaged "legacy" database (~3 min)
.venv/bin/python -m datasets.synpuf.oracle               # build the expected result, independently (~3 min)

dotnet run -c Release --project src/Etl.Cli -- discover --source data/legacy.sqlite --out discovery.md
dotnet run -c Release --project src/Etl.Cli -- migrate  --mapping datasets/synpuf/mapping.yaml --strategy parallel --fresh
.venv/bin/etl validate --mapping datasets/synpuf/mapping.yaml --oracle synpuf_oracle
dotnet run -c Release --project src/Etl.Cli -- sync     --mapping datasets/synpuf/mapping.yaml
```
Every command supports a dry run (`--dry-run`): the full migration runs and reports, then rolls back. PowerShell wrappers are in [`scripts/`](scripts/).

## Engineering highlights

- **Never guess.** When a legacy system stored medical codes as integers, `038.9` (septicemia) and `389` (hearing loss) became indistinguishable. Each code keeps its raw value, its resolved value, and *how* it was resolved, so uncertainty is queryable rather than hidden. ([D13](DECISIONS.md), [D24](DECISIONS.md))
- **A bug that only row-level validation could find.** 3 stale rows out of 2.3M survived because a filter ran *before* deduplication. Fixed by making child rows follow their parent's surviving version; regression tests are verified to fail without the fix. ([D22](DECISIONS.md))
- **"Zero rows written" was still writing 554 MB.** Postgres's `INSERT … ON CONFLICT DO UPDATE` locks every row it touches, even when nothing changes. Filtering unchanged rows out first cut that to 1 MB. ([D33](DECISIONS.md))
- **Ported Python → C# with proof, not hope.** Both engines checksum every transformed value. They agree on all 9,527,947 rows, and the C# output passes the Python validator. ([D30](DECISIONS.md))
- **Reproducible by construction.** Every random decision in the data generator is keyed on the record, not on a shared random stream, so two builds are byte-identical regardless of order or parallelism. ([D12](DECISIONS.md))
- **Measured honestly.** Paired before/after runs, rejected hypotheses kept in the log, and a machine-contention problem disclosed rather than averaged away. ([D25–D26](DECISIONS.md), [D33](DECISIONS.md))

## Repository map

| Path | What's there |
|---|---|
| [`src/Etl.Engine/`](src/Etl.Engine/) | The portable core (no database dependency): mapping compiler, converters, transform, profiling, output writers |
| [`src/Etl.Core/`](src/Etl.Core/) | Everything database-facing: SQLite source, PostgreSQL loaders, merge, incremental sync |
| [`demo/Etl.Demo/`](demo/Etl.Demo/) | Blazor WebAssembly demo: the engine running in the browser, deployed to GitHub Pages |
| [`src/Etl.Cli/`](src/Etl.Cli/) | `etl transform \| discover \| migrate \| sync` |
| [`tests/Etl.Tests/`](tests/Etl.Tests/) | xUnit: unit tests plus end-to-end runs against real PostgreSQL |
| [`datasets/synpuf/`](datasets/synpuf/) | The Medicare mapping and target schema, plus the data generator, the oracle and the defect scorer (Python) |
| [`src/etl/`](src/etl/) | The Python reference engine and the validator used to cross-check the C# engine |
| [`scripts/`](scripts/) | PowerShell: run a migration, run the benchmark matrix |
| [`docs/`](docs/) | [Results detail](docs/RESULTS.md), [T-SQL / SQL Server notes](docs/TSQL_NOTES.md), [benchmark runbook](docs/WINDOWS_BENCHMARK_RUNBOOK.md) |
| [`DECISIONS.md`](DECISIONS.md) | Every design decision, with rationale and the alternatives rejected |

**Stack:** C# / .NET 10, Npgsql (binary COPY), Microsoft.Data.Sqlite, YamlDotNet, xUnit, Blazor WebAssembly · PostgreSQL 16 · Python 3.13 (reference engine, oracle, validation) · PowerShell 7 · GitHub Actions

**Data:** [CMS DE-SynPUF](https://www.cms.gov/data-research/statistics-trends-and-reports/medicare-claims-synthetic-public-use-files), public *synthetic* Medicare claims. No real patient data is used anywhere.

## License

[MIT](LICENSE)
