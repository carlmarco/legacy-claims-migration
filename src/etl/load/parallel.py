"""Parallel workers: split each legacy table into REC_ID ranges; each worker process transforms
its range and COPYs it into staging over its own connection.

Why processes, not threads: the transform is CPU-bound Python (the GIL would serialize threads).
Why more chunks than workers: chunks differ in cost (a carrier row unpivots into ~2 lines and ~3
diagnoses; a drug event into 1 row), so small chunks pulled from a shared queue keep every worker busy.
Why this works at all: transforms are row-local and deterministic (D12, D14), so the partitioning
can't change the output. Dedup and FK checks happen afterwards, in the single merge step.
"""
import multiprocessing as mp
import time

from ..mapping import Mapping
from . import strategy
from .batched import BATCH_SIZE, Buffers, _flush_copy

CHUNKS_PER_WORKER = 4


def _ranges(m: Mapping, table: str, n: int, limit: int | None) -> list[tuple[int, int]]:
    with m.connect() as con:
        lo, hi = con.execute(f"SELECT min({m.rec_id_col}), max({m.rec_id_col}) FROM {table}").fetchone()
        if lo is None:
            return []
        if limit:
            row = con.execute(f"SELECT {m.rec_id_col} FROM {table} ORDER BY {m.rec_id_col} "
                              f"LIMIT 1 OFFSET {int(limit) - 1}").fetchone()
            hi = row[0] if row else hi
    step = max(1, -(-(hi - lo + 1) // n))
    return [(a, min(a + step - 1, hi)) for a in range(lo, hi + 1, step)]


def _work(args: tuple) -> tuple[dict, float, float]:
    mapping_path, dsn, table, rec_range = args
    import psycopg

    m = Mapping(mapping_path)
    transform_s = 0.0
    t0 = time.perf_counter()
    with psycopg.connect(dsn) as con:
        bufs = Buffers(con, m, _flush_copy, BATCH_SIZE)
        it = m.transform(tables=[table], rec_range=rec_range)
        while True:
            t = time.perf_counter()
            try:
                r = next(it)
            except StopIteration:
                transform_s += time.perf_counter() - t
                break
            transform_s += time.perf_counter() - t
            bufs.add(r)
        counts = bufs.close()
    return counts, transform_s, time.perf_counter() - t0


@strategy("parallel")
def load(dsn: str, m: Mapping, limit: int | None = None, workers: int = 4) -> tuple[dict, dict]:
    tasks = [(str(m.path), dsn, table, r)
             for table in m.by_table
             for r in _ranges(m, table, workers * CHUNKS_PER_WORKER, limit)]
    # Largest tables first: long chunks start early instead of becoming the tail.
    sizes = {t: hi - lo for _, _, t, (lo, hi) in tasks}
    tasks.sort(key=lambda t: -sizes[t[2]])
    counts: dict[str, int] = {}
    transform_total = worker_total = 0.0
    with mp.get_context("spawn").Pool(workers) as pool:
        for c, ts, wall in pool.imap_unordered(_work, tasks):
            for k, v in c.items():
                counts[k] = counts.get(k, 0) + v
            transform_total += ts
            worker_total += wall
    return counts, {
        "chunks": len(tasks),
        "worker_transform_cpu_s": round(transform_total, 2),
        "worker_load_s": round(worker_total - transform_total, 2),
    }
