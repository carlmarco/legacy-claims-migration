"""Deterministic, order-independent decisions keyed on content.

Python's built-in hash() is salted per process (PYTHONHASHSEED), so it can't be used
for anything that must be reproducible across runs or worker processes.
"""
import hashlib


def unit(*parts: object) -> float:
    """Map any tuple of values to a stable float in [0, 1)."""
    h = hashlib.blake2b("\x1f".join(map(str, parts)).encode(), digest_size=8).digest()
    return int.from_bytes(h, "big") / 2**64


def keep(key: str, pct: float, seed: int = 0) -> bool:
    """Entity-level subset: True for a stable `pct`% of keys."""
    return unit(seed, "subset", key) * 100 < pct
