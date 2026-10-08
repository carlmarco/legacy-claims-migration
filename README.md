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
