#!/usr/bin/env bash
# Run every load strategy from an empty target, then validate each run against the oracle and
# score it per defect type. Results land in results/bench/<strategy>.json and
# results/bench/<strategy>.validation.json / .scores.json.
# Prereqs: docker compose up -d; data/legacy.sqlite; oracle built (python -m datasets.synpuf.oracle).
set -euo pipefail
MAPPING=datasets/synpuf/mapping.yaml
OUT=results/bench
mkdir -p "$OUT"
run() {
  local name=$1; shift
  echo "== $name $(date +%H:%M:%S)"
  etl migrate --mapping "$MAPPING" --fresh --out "$OUT/$name.json" "$@" > /dev/null
  etl validate --mapping "$MAPPING" --oracle synpuf_oracle --out "$OUT/$name.validation.json" > /dev/null
  python -m datasets.synpuf.score --out "$OUT/$name.scores.json" > /dev/null
  python - "$OUT/$name" <<'PY'
import json, sys
p = sys.argv[1]
s, v, sc = (json.load(open(f"{p}{x}.json")) for x in ("", ".validation", ".scores"))
ok = all(c["match"] for c in v["checksums"].values()) and not any(v["fk_orphans_in_target"].values())
print(f"   total={s['total_s']}s merge={s['merge_s']}s checksums_all_match={ok} "
      f"defects_100pct={sum(d['handled_pct'] == 100 for d in sc['defects'].values())}/{len(sc['defects'])}")
PY
}
run naive        --strategy naive
run executemany  --strategy executemany
run batched      --strategy batched
run parallel_w2  --strategy parallel --workers 2
run parallel_w4  --strategy parallel --workers 4
run parallel_w8  --strategy parallel --workers 8
echo "== done $(date +%H:%M:%S)"
