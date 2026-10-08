import argparse
import json
import time
from collections import Counter

from .mapping import Mapping


def cmd_transform(args: argparse.Namespace) -> None:
    """Dry run of the row-level transform: counts, quarantine reasons, throughput. Writes nothing."""
    m = Mapping(args.mapping)
    rows: Counter = Counter()
    quarantined: Counter = Counter()
    examples: dict[str, list] = {}
    source_rows = 0
    t0 = time.perf_counter()
    tables = args.tables.split(",") if args.tables else None
    last_src = None
    for rec in m.transform(tables=tables, limit=args.limit):
        key = (rec.table, rec.rec_id) if rec.kind == "quarantine" else None
        if rec.kind == "row":
            rows[rec.entity] += 1
            src = (rec.values[-3], rec.values[-2])
        else:
            reason = f"{rec.entity}:{rec.reason}"
            quarantined[reason] += 1
            if len(examples.setdefault(reason, [])) < 3:
                examples[reason].append(rec.detail)
            src = key
        if src != last_src:
            source_rows += 1
            last_src = src
    elapsed = time.perf_counter() - t0
    excluded = {t: m.excluded_count(t) for t in (tables or m.by_table)}
    print(json.dumps({
        "source_rows_approx": source_rows,
        "excluded_soft_deleted": excluded,
        "target_rows": dict(rows),
        "quarantined": dict(quarantined),
        "quarantine_examples": examples,
        "elapsed_s": round(elapsed, 1),
        "target_rows_per_s": round(sum(rows.values()) / elapsed) if elapsed else None,
    }, indent=2, default=str))


def main() -> None:
    parser = argparse.ArgumentParser(prog="etl")
    sub = parser.add_subparsers(dest="command", required=True)

    t = sub.add_parser("transform", help="dry-run the row-level transform and report counts")
    t.add_argument("--mapping", required=True)
    t.add_argument("--tables", help="comma-separated legacy tables (default: all)")
    t.add_argument("--limit", type=int, help="rows per legacy table")
    t.set_defaults(fn=cmd_transform)

    args = parser.parse_args()
    args.fn(args)


if __name__ == "__main__":
    main()
