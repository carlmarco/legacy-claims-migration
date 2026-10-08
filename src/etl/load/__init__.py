"""Load strategies: each takes the transformed record stream and writes it into staging.

Strategies differ only in how rows reach Postgres. Transform, merge and validation are shared,
so benchmark differences are attributable to the load path alone.
"""
from collections.abc import Callable

STRATEGIES: dict[str, Callable] = {}


def strategy(name: str):
    def register(fn):
        STRATEGIES[name] = fn
        return fn
    return register


from . import batched, naive, parallel  # noqa: E402,F401  (registers strategies)
