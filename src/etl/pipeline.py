"""One migration run: extract -> transform -> load (strategy) into staging -> merge into the target.

Timing is split per phase so the benchmark can show where the bottleneck actually is:
  transform_s  time spent producing records (SQLite read + Python conversion)
  load_s       time spent in the strategy writing to staging (wall time minus transform)
  merge_s      time spent in the set-based SQL merge
"""
import json
import sys
import time
from collections.abc import Iterator
from pathlib import Path

from psycopg.types.json import Jsonb

from . import db, merge, stage
from .load import STRATEGIES
from .mapping import Mapping, Record

ENGINE_SQL = Path(__file__).parent / "sql" / "engine.sql"


class Timed:
    """Wraps an iterator and accumulates the time spent inside next()."""

    def __init__(self, it: Iterator):
        self.it, self.seconds = it, 0.0

    def __iter__(self):
        return self

    def __next__(self):
        t = time.perf_counter()
        try:
            return next(self.it)
        finally:
            self.seconds += time.perf_counter() - t


def peak_rss_mb(children: bool = False) -> float:
    """Peak resident memory. `resource` is Unix-only, so Windows uses psutil's peak working set.
    children=True reports the largest single (waited-for) child process (Unix only)."""
    if sys.platform == "win32":
        import psutil
        return psutil.Process().memory_info().peak_wset / 1e6 if not children else float("nan")
    import resource
    r = resource.getrusage(resource.RUSAGE_CHILDREN if children else resource.RUSAGE_SELF).ru_maxrss
    return r / 1e6 if sys.platform == "darwin" else r / 1e3  # bytes on macOS, KiB on Linux


def run(mapping_path: str, strategy: str, dry_run: bool = False, limit: int | None = None,
        merge_step: bool = True, fresh: bool = False, workers: int = 1, defer_fks: bool = False) -> dict:
    m = Mapping(mapping_path)
    load = STRATEGIES[strategy]
    with db.connect(autocommit=True) as con:
        if fresh:  # benchmark from an empty target: measures the insert path, not the no-op re-run
            con.execute(f'DROP SCHEMA IF EXISTS "{m.target_schema}" CASCADE')
            if _has_engine(con):
                con.execute("DELETE FROM etl.quarantine WHERE dataset = %s", (m.name,))
            # the watermark describes the target just dropped; kept, the next sync would skip rows the target no longer has
            if con.execute("SELECT to_regclass('etl.sync_watermark') IS NOT NULL").fetchone()[0]:
                con.execute("DELETE FROM etl.sync_watermark WHERE dataset = %s", (m.name,))
        stage.ensure_schema(con, m, ENGINE_SQL.read_text())
        stage.reset(con, m)
        run_id = con.execute(
            "INSERT INTO etl.migration_run (dataset, strategy, dry_run) VALUES (%s, %s, %s) RETURNING run_id",
            (m.name, strategy, dry_run)).fetchone()[0]

    stats: dict = {"run_id": run_id, "dataset": m.name, "strategy": strategy, "dry_run": dry_run,
                   "limit": limit, "workers": workers, "defer_fks": defer_fks}
    try:
        t0 = time.perf_counter()
        if strategy == "parallel":
            # Phases overlap across workers, so report per-worker totals instead of a wall-clock split.
            counts, timing = load(db.DSN, m, limit=limit, workers=workers)
            stats.update(timing)
        else:
            records = Timed(m.transform(limit=limit))
            counts = load(db.DSN, m, records)
        extract_load_s = time.perf_counter() - t0
        if strategy != "parallel":
            stats.update(transform_s=round(records.seconds, 2), load_s=round(extract_load_s - records.seconds, 2))
        staged = sum(v for k, v in counts.items() if not k.startswith("_"))
        stats.update(staged_rows=staged, staged_by_entity=counts, extract_load_s=round(extract_load_s, 2),
                     staged_rows_per_s=round(staged / extract_load_s) if extract_load_s else None)

        if merge_step:
            t1 = time.perf_counter()
            with db.connect() as con:  # one transaction for the whole merge
                stats["merge"] = merge.merge_all(con, m, run_id, defer_fks=defer_fks)
                if dry_run:
                    con.rollback()
                else:
                    con.commit()
            stats["merge_s"] = round(time.perf_counter() - t1, 2)
        stats["total_s"] = round(time.perf_counter() - t0, 2)
        stats["peak_rss_mb"] = round(peak_rss_mb(), 1)
        if strategy == "parallel":
            stats["peak_rss_worker_mb"] = round(peak_rss_mb(children=True), 1)
        _finish(run_id, "succeeded", stats)
    except BaseException as e:
        stats["error"] = repr(e)
        _finish(run_id, "failed", stats)
        raise
    return stats


def _has_engine(con) -> bool:
    return con.execute("SELECT to_regclass('etl.quarantine') IS NOT NULL").fetchone()[0]


def _finish(run_id: int, status: str, stats: dict) -> None:
    with db.connect(autocommit=True) as con:
        con.execute("UPDATE etl.migration_run SET finished_at = now(), status = %s, stats = %s WHERE run_id = %s",
                    (status, Jsonb(json.loads(json.dumps(stats, default=str))), run_id))
