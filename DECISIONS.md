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

### D11 · Native defects in clean SynPUF (25% subset, seed 0) and how the target handles them
Measured by `datasets/synpuf/profile_source.py` → `results/native_profile.json` before injecting anything. These are excluded from injected-defect scoring.

| Finding | Count | Handling |
|---|---|---|
| ESRD indicator changes between 2008 and 2009 | 3,385 benes | ESRD is a **per-year attribute**: it moves to `beneficiary_year`, not `beneficiary`. |
| Death date blank in 2008, set in 2009 | 444 benes | Survivorship: a non-null death date wins. |
| Beneficiaries only in 2008 | 464 | All died in 2008. Expected, not a defect. |
| Claims with no from/thru dates at all | 17 IP, 2,669 OP | Load with NULL dates; target date columns are nullable; counted in validation. |
| NDC placeholder `'OTHER'` (non-numeric) | 7 rows (23 in full file) | Map to NULL `ndc`. Zero-padding is only applied to all-digit values. |
| Negative claim payments | 55 IP (full file) | Valid adjustments; the target allows negative amounts. |
| Duplicate keys, orphans, reversed dates, cross-type CLM_ID reuse | 0 | None. Claim natural key is `(claim_type, clm_id, segment)` anyway. |
| ICD-9 dx codes ambiguous after integer cast (`01000` vs `1000`) | 196 codes / 9,268 occurrences | Sets up B4: see the ICD-9 decision pending. |

**Rejected:** quarantining undated claims. They're genuine source records, and quarantine is reserved for integrity failures.

### D12 · Legacy-izer design
**Decision:**
- Every random decision is drawn from `rng_for(seed, table, natural_key)`, a PRNG seeded from a blake2b hash of the row's key, not from a shared random stream.
- Ground truth goes to a separate `legacy_truth.sqlite` that the migrator never opens.
- Leading-zero loss (B4/B5) is produced by SQLite's own INTEGER affinity, not simulated.
- Builds go to a temp file and are renamed on success.

**Rationale:**
- Content-seeded randomness makes the output byte-identical across runs (verified: two builds have the same `.dump` hash) and independent of processing order, so the legacy-izer can be parallelized later without changing its output.
- A separate truth file prevents the migrator from "passing" by reading the answers.
- Using real INTEGER-affinity behavior means the defect is exactly what a real legacy DB produces, including side effects I'd have missed, such as `'458.0'` → `458`.

**Adjustments found while building:**
- Zero money amounts are not eligible for B3. Unused carrier lines 2–13 are padded with `0.00`, which made up 94% of B3 hits (633k at 1%).
- In a D2 correction pair, the superseded row gets the *higher* REC_ID, so survivorship can't use REC_ID as a stand-in for recency; it has to use LOAD_TS.
- The dot format (B4_dot) applies only to V/E codes, because SQLite converts numeric `'458.0'` to `458` anyway.

**Rejected:**
- A single `random.Random(seed)` stream: output would depend on row order and subset size.
- Writing the truth log into the legacy DB: it would leak the answers to the migrator.

### D13 · ICD-9 codes: store raw + resolved + resolution status; resolve against the official code list; never guess
**Decision:** Each code becomes three columns: `code_raw` (as received), `code` (resolved, or NULL), and `resolution`, one of:
- `exact`: as received and valid.
- `repaired`: exactly one zero-padded candidate is valid.
- `ambiguous`: several candidates are valid, so the code is left NULL.
- `unresolved`: no candidate is valid and zeros may have been lost.
- `not_in_reference`: no zeros could have been lost, but the code isn't in the list. The value is kept.

Candidates are the received digits plus left-padded variants, up to the code system's maximum length (5 for diagnosis, 4 for procedure). The reference list is CMS ICD-9-CM v32.

**Measured against the truth log (25% subset)** by running the real resolver (`zero_padded_code`) over all 41,456 codes with leading zeros lost, against v32 only:
| Outcome | Count |
|---|---:|
| Correct | 25,958 |
| Ambiguous | 13,474 |
| Unresolved | 1,724 |
| **Resolved but wrong** | **300 (0.7%)** |

*(Correction: an earlier ad-hoc estimate said 249 wrong. It used a different length rule for procedure codes. The figures above come from the resolver itself.)*

**Why "unique but wrong" exists:** 2.85% of *clean* SynPUF diagnosis codes aren't in v32. That's version drift (v32 is FY2015, the claims are 2008–2010; e.g. `041.4` was later subdivided) plus synthesis noise (HIV codes `043.x`/`044.x`, retired in 1994). When the true code isn't in the list, the only valid candidate is a different code. This includes 5-digit procedure codes in SynPUF that exceed the ICD-9 procedure format and are kept as `not_in_reference`. Checking against the reference list is necessary but not sufficient, and the validator reports this residual explicitly.

**Open:** adding the code lists for the claim years (v25–v28) should shrink `unresolved` and the wrong repairs. That needs a download, pending approval.

**Rejected:**
- Always left-padding (silently wrong for every ambiguous code).
- Never repairing (throws away 25,958 recoverable codes).
- Picking the most frequent candidate (a guess dressed up as a rule).

### D14 · Row-local transforms in Python; set-based work (dedup, survivorship, FK checks) in Postgres
**Decision:** The engine streams legacy rows through compiled converters in Python: type parsing, code maps, ICD resolution, unpivot, split. Rows land in staging tables, and dedup, survivorship (`DISTINCT ON … ORDER BY load_ts DESC`), field-level merge rules, and orphan detection run as SQL in Postgres.
**Rationale:** Row-local work is embarrassingly parallel and needs no memory beyond the current row. Dedup and FK checks need the whole table, and holding 1.2M wide carrier rows in a Python dict would take GBs, while the database does it with sorts and hash joins it's designed for. This is the ETL/ELT split, made per operation rather than for the whole pipeline.
**Rejected:**
- All-Python dedup (memory-bound).
- All-SQL transforms (date/money/ICD parsing in SQL is unreadable, and the ICD resolver needs a reference set).
- Sorting in SQLite before transform (keys aren't normalized yet, so `' abc'` and `'ABC'` sort apart).

### D15 · Natural composite keys in the target, no surrogate IDs
**Decision:** `claim` is keyed `(claim_type, clm_id, segment)`, with carrier segment = 1. Child tables carry the same three columns. `drug_event` is keyed `pde_id`; `beneficiary` is keyed `beneficiary_id`.
**Rationale:** Idempotency: a re-run computes the same key from the same source, so an upsert lands on the same row. Surrogate IDs would need a lookup round trip for every child row and could drift between runs.
**Rejected:** `bigserial` surrogates (lookup cost; non-deterministic across runs).

### D16 · State stored as SSA code, not USPS
**Decision:** Normalize both legacy forms to the 2-digit SSA code.
**Rationale:** It's lossless. SSA `54` ("Others") has no USPS equivalent, and SynPUF county codes are SSA county codes that only mean something alongside the SSA state code. USPS can be derived in a view.
**Rejected:** USPS (loses `54` and breaks the county code's meaning).

### D17 · Strict row conversion: any unparseable value quarantines the row
**Rationale:** In a migration, a silently NULLed field is worse than a quarantined row. Quarantine is visible and countable, and the raw row is preserved as JSONB for replay. Values that are legitimately null use the explicit null-token list.
**Rejected:** Coercing failures to NULL (silent data loss).

### D18 · ICD-9 reference = union of v25–v28 (the claim years) and v32
**Decision:** The resolver's reference set is the union of the CMS code lists for FY2008–FY2011 and the final v32.

**Measured against the truth log** (41,456 codes with leading zeros lost):
| Reference set | Correct | Ambiguous | Unresolved | Wrong |
|---|---:|---:|---:|---:|
| v32 only | 25,958 | 13,474 | 1,724 | 300 |
| v25–v28 + v32 | **26,626** | 13,540 | **993** | **297** |

**Reading:** Version drift caused the *unresolved* codes: those dropped 42%, and 668 more codes are repaired correctly. It did **not** cause the *wrong* resolutions, which barely moved. Those come from SynPUF's synthetic codes that are invalid in every version (e.g. `044.x`, retired in 1994). No reference list can fix them. The residual 297 (0.7%) is reported, not hidden. Ambiguity rises slightly (+66), because a bigger valid set means more collisions. That's the cost of better coverage.

**Rejected:**
- A per-claim-year reference, i.e. choosing the version by service date. It's more precise in principle, but it adds a date dependency to a row-level converter, and the union already captures most of the gain. Revisit only if the validator shows that cross-version collisions matter.

### D19 · Merge semantics
**Decision:** Staging tables are UNLOGGED and created with `CREATE … AS TABLE target WITH NO DATA`, which copies types but not constraints. The merge runs in **one transaction** across all entities, parent before child (topological order from `references`). Per entity:
1. `DISTINCT ON (key) … ORDER BY key, <survivorship order>, _src_rec_id DESC`, with field rules (e.g. `death_date: max`) applied as window aggregates.
2. Orphans (parent key missing from the merged parent table) go to `etl.quarantine` as `ORPHAN_FK`.
3. `INSERT … ON CONFLICT (key) DO UPDATE … WHERE (data cols) IS DISTINCT FROM (EXCLUDED…)`, with `RETURNING (xmax = 0)` to count inserted vs updated rows.

`etl.quarantine` reflects the latest run: it's cleared for the dataset and rebuilt inside the merge transaction. **Dry run** is the same code path, with `ROLLBACK` instead of `COMMIT`.

**Rationale:**
- One transaction means the target is never half-migrated.
- The `IS DISTINCT FROM` guard makes a re-run write zero rows (verified: 0 inserted / 0 updated on every entity), and that's what makes "re-runnable" cheap rather than just correct.
- Sharing a code path means the dry run can't drift from the real run.
- Lineage columns are excluded from the change comparison, so a re-load that only changes `_version` isn't treated as a change.

**Known limitation:** the merge doesn't delete. If a re-run's source drops a diagnosis position or a whole claim, the old target row stays. That's correct for incremental loads; a full-refresh mode would need an anti-join delete per touched parent. Deferred until it's needed.

**Rejected:**
- Per-entity transactions (a failure mid-merge leaves inconsistent parent and child tables).
- Doing survivorship in Python (memory-bound, D14).
- A separate dry-run implementation (it would drift).

### D20 · Load strategies are defined by the load path only
**Decision:** Strategies share the transform, staging, merge and validation; only "records → staging" differs.
- `naive`: one `INSERT` per row with autocommit. One round trip **and** one WAL flush per row, which is what default ORM code does.
- `executemany`: batches of 20k rows, pipelined `INSERT`s. Removes round trips but keeps per-row statement execution.
- `batched`: batches of 20k rows via `COPY FROM STDIN`. One statement per batch.
- `parallel`: N processes, each transforming a REC_ID range and running COPY. More chunks than workers, so the work balances out.

**Rationale:** Isolating the load path makes benchmark differences attributable to it alone. `executemany` sits between the other two so the result separates round-trip cost from per-statement cost.

**Rejected:** a "naive with a transaction" variant (`executemany` already covers removing the commit cost); binary COPY (exact-type matching across smallint/int adds fragility for a gain that's second-order next to parallelizing the transform).

### D21 · Validation compares against an independent oracle, entirely in SQL
**Decision:** `datasets/synpuf/oracle.py` builds the expected target from the **clean zips** into schema `synpuf_oracle`, with the same DDL as the target. It deliberately reuses none of the engine's converters: dates are parsed with slicing, codes are taken verbatim. The generic validator (`src/etl/validate.py`) then runs:
- Per-entity counts and quarantine reasons.
- FK integrity in the target.
- Order-independent checksums on both sides: `count(*)` plus `sum` of the first 64 bits of `md5(row text)`, with NULL encoded as `\N`.
- A full keyed diff (`FULL JOIN` on the natural key: missing, extra, differing rows, mismatches per column).
- For code-resolver columns, a resolution × outcome matrix.

The per-defect scorer joins the truth log to staging lineage (`_src_table`, `_src_rec_id`) to find each defect's natural key, then compares target with oracle at that key.

**Rationale:**
- An oracle built with the engine's own converters would share their bugs.
- Checksums are the cheap cross-system check (source and target in different databases). The keyed diff says *what* differs once a checksum mismatches.
- `sum`, not `xor`: duplicate rows don't cancel out.
- Resolver columns are excluded from checksum and diff because withholding a value is an allowed outcome. They're scored separately, so they can't mask or fake a mismatch.

**Rejected:**
- Sampling-only field diffs (a full keyed diff of 9.3M rows takes about 20 s in Postgres, so there's no reason to sample).
- Comparing against the legacy DB (it *is* the thing with defects).

### D22 · Child rows follow their parent's surviving version (`follow_parent`), and child sets are replaced
**Bug found by the validator:** `claim_line` had 3 extra rows. All 3 were D2 correction pairs where the corrected amount was `0.00`. In the newer, correct version, line 1 had no HCPCS and all-zero amounts, so `keep_if_any` dropped that slot in the transform. Survivorship then only saw the *older*, wrong version's line, and the stale row won by default. **Filtering before dedup turns "this version has no such row" into "use the older row."**

**Fix:** For unpivot children, `survivorship.follow_parent: <entity>`.
- Children are kept only if they were emitted by the parent's surviving legacy row (`_src_table`, `_src_rec_id`).
- In the same merge, target children of those parents that the surviving version no longer has are deleted. This resolves D19's "merge never deletes" limitation for child sets.
- Regression tests on the dental fixture fail without the fix (slots `[1, 2]` instead of `[1]`).

**Second bug, caught while verifying the first:** in `merge_entity`, the orphan loop `for parent, … in e.references.items()` shadowed the `follow_parent` variable. That made child-set deletion run for *every* entity with an FK. On an incremental run, it would have deleted a beneficiary's claims that weren't in the current batch. Found because the "deletes children" test also passed *without* the fix, which shouldn't be possible. The test now also asserts that an entity with only an FK never deletes.

**Cost:** merge time rose from about 157 s to about 224 s at 8 workers (the parent-version join plus the delete's anti-join scan). It's the largest stage and the next optimization target.

**Rejected:**
- Moving `keep_if_any` after survivorship. That stages every padded slot: 13 × 1.2M carrier slots, about 6× the `claim_line` staging volume.
- Tombstone rows for filtered slots (more machinery for the same result).

### D23 · Validation result (25% subset, 8-worker run)
- All 8 tables match the oracle by checksum and full diff: 0 missing, 0 extra, 0 differing across 9,255,375 rows. All 7 FK checks report 0.
- 19 of 20 injected defect types are verified at 100%, meaning repaired, or excluded exactly where the oracle has no row.
- B4_int_cast (leading zeros lost from ICD-9 codes) has 41,456 cases:
  | Outcome | Count |
  |---|---:|
  | Repaired | 26,505 |
  | Withheld (ambiguous or unresolved) | 14,454 |
  | Wrong | 295 (0.7%) |
  | Excluded (on dropped lines) | 202 |

**Broader finding:** integer storage makes *every* numeric code suspect, not just the damaged ones. Across all three diagnosis columns, 223,689 codes are `ambiguous`, of which **210,695 (94.2%) are actually undamaged** and 12,994 really lost a zero. Whether to withhold or to trust the as-received value is a coverage/precision trade-off. Resolved in D24.
