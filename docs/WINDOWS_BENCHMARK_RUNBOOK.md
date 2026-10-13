# Benchmark runbook: Windows 11

How to reproduce the benchmarks on a dedicated Windows machine with PostgreSQL installed natively (no Docker or VM layer between the engine and the disk). Setup takes about 30 minutes; the benchmark matrix runs unattended for 1–3 hours.

## 0. Prepare the machine
- Plugged in, with the OEM performance profile set to maximum.
- Settings → System → Power → Power mode: **Best performance**.
- Close other applications and pause Windows Update for the session.

## 1. Install the tools (PowerShell, as Administrator)
```powershell
winget install --id Microsoft.PowerShell -e
winget install --id Microsoft.DotNet.SDK.10 -e
winget install --id Python.Python.3.13 -e
winget install --id PostgreSQL.PostgreSQL.16 -e     # note the 'postgres' superuser password
```
If a package id isn't found, `winget search <name>` shows the current one. Run everything below in **PowerShell 7** (`pwsh`); Windows PowerShell 5.1 won't run the scripts.

## 2. Configure PostgreSQL
```powershell
$env:PGPASSWORD = '<postgres password>'
$psql = 'C:\Program Files\PostgreSQL\16\bin\psql.exe'
& $psql -U postgres -c "CREATE ROLE etl LOGIN PASSWORD 'etl'"
& $psql -U postgres -c "CREATE DATABASE target OWNER etl"
& $psql -U postgres -c "ALTER SYSTEM SET max_wal_size = '4GB'"
& $psql -U postgres -c "SELECT pg_reload_conf()"
[Environment]::SetEnvironmentVariable('ETL_DSN', 'postgresql://etl:etl@localhost:5432/target', 'User')
$env:ETL_DSN = 'postgresql://etl:etl@localhost:5432/target'
```

## 3. Build
Clone or unpack to a short path outside any synced folder (e.g. `C:\etl`):
```powershell
cd C:\etl
py -3.13 -m venv .venv
.\.venv\Scripts\python -m pip install -e ".[dev]"
dotnet build -c Release src\Etl.Cli
```

## 4. Build the legacy database and the oracle, and check reproducibility
```powershell
.\.venv\Scripts\python -m datasets.synpuf.legacyize
.\.venv\Scripts\python scripts\fingerprint_legacy.py
.\.venv\Scripts\python -m datasets.synpuf.oracle
```
The generator is deterministic: the fingerprints must match those of any other build at the same commit, subset and seed.

## 5. Tests
```powershell
dotnet test
.\.venv\Scripts\pytest -q
```

## 6. Benchmarks
```powershell
.\scripts\Invoke-Benchmark.ps1 -Trials 3 -Strategies executemany, batched, parallel -Workers 2, 4, 8
```
Each run starts from an empty target, is validated against the oracle (invalid runs are excluded), and is recorded in `results\bench\matrix\runs.csv` together with a machine fingerprint (`machine.json`).
