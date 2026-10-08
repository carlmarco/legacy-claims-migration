# Decisions

Format: decision · rationale · alternatives rejected. Check here before proposing anything.

---

### D1 · Source data: CMS DE-SynPUF Sample 1, deliberately degraded into a "legacy" SQLite DB
**Decision:** Use real-shaped Medicare synthetic claims (DE-SynPUF 2008–2010, Sample 1) as the ground truth. A seeded "legacy-izer" denormalizes it, corrupts types and codes, and injects duplicates and orphans into SQLite. The engine then migrates that legacy DB into a normalized Postgres target.
**Rationale:** Real column names, code systems (ICD-9, HCPCS, NDC), and volume. Because we hold the clean original, every injected defect is known, so validation can measure repair recall instead of only "counts match."
**Rejected:** Hand-built Faker data (toy-shaped, unconvincing); migrating SynPUF as-is (already clean, so there's nothing to migrate).

### D2 · Generic engine + per-dataset YAML mapping spec; no expression DSL
**Decision:** The engine (`src/etl/`) contains zero dataset names. Each dataset lives in `datasets/<name>/` with a `mapping.yaml` declaring source→target columns, named coercers, code maps, unpivots, natural keys, dedup keys, and FKs. Anything irregular is handled by a registered Python function referenced by name.
**Rationale:** Generality comes from the mechanisms (staging, upsert, unpivot, dedup, validation), not from inventing a language. It's proven by running a second, small dataset through the same engine in tests.
**Rejected:** A fully declarative transform DSL (a framework nobody asked for, and harder to debug than Python); SynPUF-hardcoded pipeline (fails the generality goal).

### D3 · Subset by beneficiary hash, default 25%
**Decision:** Keep a beneficiary iff `hash(DESYNPUF_ID) % 100 < pct`; every claim and drug event follows its beneficiary. The default is `pct=25`.
**Rationale:** The machine has ~9.6 GB free; full Sample 1 (~13M source rows, more after unpivot) won't fit alongside the legacy DB and Postgres. Sampling at the entity level keeps the subset referentially closed.
**Rejected:** Per-row random sampling of each file (breaks FK closure); the full dataset locally (doesn't fit on disk). The full-scale run happens once on a rented machine with its hardware recorded in the README.

### D4 · Stream from the zip archives; never unzip
**Decision:** Read CSVs via `zipfile` streams.
**Rationale:** Saves ~3.5 GB of disk and keeps extraction memory constant regardless of file size.
**Rejected:** Unzipping to disk (disk constraint, no benefit).

### D5 · psycopg 3 + raw SQL; no ORM, no pandas in the load path
**Rationale:** The benchmark is about round trips and I/O shape; an ORM hides both. psycopg 3 gives native COPY.
**Rejected:** SQLAlchemy ORM (hides what we measure); pandas `to_sql` (memory-bound, slow, opaque).

### D6 · Idempotency via staging + `INSERT … ON CONFLICT (natural key) DO UPDATE … WHERE IS DISTINCT FROM`
**Rationale:** A re-run converges to the same state and writes zero rows when nothing changed. This supports incremental re-runs.
**Rejected:** Truncate-and-reload (not incremental; leaves a window with an empty table).

### D7 · Record-level dedup, not patient matching
**Decision:** Deduplicate exact and near-duplicate claim loads and normalize ID variants (case and whitespace). There's no demographic fuzzy matching.
**Rationale:** SynPUF has no names, phones, or addresses, so demographic matching has nothing to match on. This supersedes the patient-matching idea from the initial plan.
**Rejected:** Probabilistic patient matching (no signal in this data).

### D8 · Postgres 16 in Docker on host port 5433, default durability settings
**Rationale:** Reproducible. Keeping `fsync`/`synchronous_commit` at their defaults keeps benchmark numbers honest; turning them off would inflate throughput in ways no production migration would accept.
**Rejected:** Local Homebrew Postgres (not reproducible); a tuned-for-speed config with fsync off (misleading numbers).

### D9 · Local repo, not a notebook; full-scale run on a rented VM
**Rationale:** The deliverable is a package with a CLI, tests, and reproducible benchmarks. Colab's 2 noisy vCPUs and ephemeral sessions make the benchmarks meaningless. A notebook is used only to plot results.
**Rejected:** Colab as the primary environment.

### D10 · No 2010 beneficiary year: CMS's Sample 1 link serves Sample 20
**Decision:** Use the 2008 and 2009 beneficiary summary files only. Claims still span 2008–2010.
**Evidence:** The 2010 file linked on the Sample 1 page is `..._Sample_20.csv`. It has 112,811 IDs and **0** overlap with 2008 (116,352) or 2009 (114,538). Every other URL variant for a Sample 1 2010 file returns 404.
**Consequence:** Beneficiaries are built from 2008 and 2009. 2010 claims for those beneficiaries are still valid, and `beneficiary_year` simply has no 2010 rows. This is a real upstream data defect, documented and handled rather than hidden.
**Rejected:** Using the Sample 20 file (would create ~113k fake orphans); switching to a different sample (same broken link pattern is likely; not worth the risk).
