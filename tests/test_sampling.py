import subprocess
import sys

from etl.sampling import keep, rng_for, unit


def test_unit_is_stable_across_processes():
    # Built-in hash() would fail this: it is salted per process.
    code = "from etl.sampling import unit; print(unit(0, 'subset', 'ABC'))"
    out = {subprocess.check_output([sys.executable, "-c", code], text=True) for _ in range(3)}
    assert len(out) == 1
    assert float(out.pop()) == unit(0, "subset", "ABC")


def test_keep_hits_requested_fraction():
    kept = sum(keep(f"id{i}", 25) for i in range(20_000))
    assert 4_700 < kept < 5_300


def test_keep_is_monotone_in_pct():
    # A 10% subset is contained in the 25% subset: growing the sample never drops entities.
    ids = [f"id{i}" for i in range(5_000)]
    assert {i for i in ids if keep(i, 10)} <= {i for i in ids if keep(i, 25)}


def test_rng_for_is_order_independent():
    a = [rng_for(0, "t", k).random() for k in ("x", "y", "z")]
    b = [rng_for(0, "t", k).random() for k in ("z", "y", "x")][::-1]
    assert a == b
