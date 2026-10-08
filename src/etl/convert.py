"""Named value converters. Mapping specs reference these by name; nothing here knows any dataset.

A converter factory takes (args, ctx) and returns a function value -> converted value.
Converters raise ConversionError on input they can't interpret. They never guess.
A converter may return a dict to emit several columns: {"": main, "_raw": ..., "_resolution": ...}
expands to <col>, <col>_raw, <col>_resolution.
"""
import re
from collections.abc import Callable
from datetime import date, datetime
from decimal import Decimal, InvalidOperation
from typing import Any

Converter = Callable[[Any], Any]


class ConversionError(ValueError):
    pass


REGISTRY: dict[str, Callable[[dict, "Context"], Converter]] = {}


def converter(name: str):
    def register(factory):
        REGISTRY[name] = factory
        return factory
    return register


class Context:
    """Shared, read-only lookups available to converters: code maps and reference sets."""

    def __init__(self, code_maps: dict[str, dict] | None = None, references: dict[str, set] | None = None):
        self.code_maps = code_maps or {}
        self.references = references or {}


def build(name: str, args: dict | None, ctx: Context) -> Converter:
    if name not in REGISTRY:
        raise KeyError(f"unknown converter {name!r}; known: {sorted(REGISTRY)}")
    return REGISTRY[name](args or {}, ctx)


# --- scalars --------------------------------------------------------------------------------
@converter("text")
def _text(args, ctx):
    def f(v):
        s = str(v).strip()
        return s or None
    return f


@converter("id")
def _id(args, ctx):
    """Identifier normalization: trim and upper-case. Optional regex the result must match."""
    pattern = re.compile(args["pattern"]) if "pattern" in args else None

    def f(v):
        s = str(v).strip().upper()
        if pattern and not pattern.fullmatch(s):
            raise ConversionError(f"id {v!r} does not match {pattern.pattern}")
        return s
    return f


@converter("integer")
def _integer(args, ctx):
    def f(v):
        try:
            d = Decimal(str(v).strip())
        except InvalidOperation:
            raise ConversionError(f"not a number: {v!r}") from None
        if d != d.to_integral_value():
            raise ConversionError(f"not a whole number: {v!r}")
        return int(d)
    return f


@converter("decimal")
def _decimal(args, ctx):
    def f(v):
        try:
            return Decimal(str(v).strip())
        except InvalidOperation:
            raise ConversionError(f"not a number: {v!r}") from None
    return f


@converter("money")
def _money(args, ctx):
    """'$1,200.00', ' 1200.0 ', '(50.00)' -> Decimal, quantized to cents."""
    cents = Decimal("0.01")

    def f(v):
        s = str(v).strip().replace("$", "").replace(",", "").strip()
        neg = s.startswith("(") and s.endswith(")")
        if neg:
            s = s[1:-1]
        try:
            d = Decimal(s)
        except InvalidOperation:
            raise ConversionError(f"not money: {v!r}") from None
        return (-d if neg else d).quantize(cents)
    return f


_TWO_DIGIT_YEAR = re.compile(r"(\d{1,2})/(\d{1,2})/(\d{2})")


@converter("date")
def _date(args, ctx):
    """Parse any of `formats`. Two-digit years need an explicit `century`, or they are an error."""
    formats = args.get("formats", ["%Y%m%d", "%m/%d/%Y", "%Y-%m-%d"])
    century = args.get("century")

    def f(v):
        s = str(v).strip()
        m = _TWO_DIGIT_YEAR.fullmatch(s)
        if m:
            if century is None:
                raise ConversionError(f"two-digit year with no century rule: {v!r}")
            try:
                return date(century + int(m[3]), int(m[1]), int(m[2]))
            except ValueError:
                raise ConversionError(f"bad date: {v!r}") from None
        for fmt in formats:
            try:
                return datetime.strptime(s, fmt).date()
            except ValueError:
                continue
        raise ConversionError(f"unrecognized date: {v!r}")
    return f


@converter("code")
def _code(args, ctx):
    """Map through a named code map. Unknown codes are an error, never passed through."""
    name = args["map"]
    table = ctx.code_maps[name]

    def f(v):
        key = str(v).strip()
        if key in table:
            return table[key]
        if key.lower() in table:
            return table[key.lower()]
        raise ConversionError(f"code {v!r} not in map {name!r}")
    return f


@converter("padded_digits")
def _padded_digits(args, ctx):
    """Fixed-width numeric identifiers that lost leading zeros (NDC, ZIP, ...).

    `segments` gives the canonical width of each hyphen-separated part, e.g. [5, 4, 2] for NDC.
    Non-numeric placeholders listed in `placeholders` become NULL; any other non-numeric is an error.
    """
    width = args["width"]
    segments = args.get("segments")
    placeholders = {p.upper() for p in args.get("placeholders", [])}

    def f(v):
        s = str(v).strip()
        if s.upper() in placeholders:
            return None
        if "-" in s and segments:
            parts = s.split("-")
            if len(parts) != len(segments) or not all(p.isdigit() for p in parts):
                raise ConversionError(f"bad segmented code: {v!r}")
            s = "".join(p.zfill(w) for p, w in zip(parts, segments))
        if not s.isdigit() or len(s) > width:
            raise ConversionError(f"bad fixed-width code: {v!r}")
        return s.zfill(width)
    return f


@converter("zero_padded_code")
def _zero_padded_code(args, ctx):
    """Resolve a code that may have lost leading zeros (e.g. stored in an INTEGER column).

    Candidates are the received digits plus left-padded variants up to `max_len`; each is checked
    against the reference set. Exactly one valid -> use it. Several -> ambiguous.
    `on_ambiguous` decides what an ambiguous code stores:
      withhold          code NULL, status 'ambiguous' (never guess)
      assume_received   if the value as received is itself valid, store it with status 'assumed'
                        (a labeled guess: consumers can filter on the status); otherwise withhold
    Returns {"": code, "_raw": received, "_resolution": status}.
    """
    ref = ctx.references[args["reference"]]
    max_len = args["max_len"]
    strip_dot = args.get("strip_dot", True)
    on_ambiguous = args.get("on_ambiguous", "withhold")
    if on_ambiguous not in ("withhold", "assume_received"):
        raise ValueError(f"on_ambiguous must be withhold|assume_received, got {on_ambiguous!r}")

    def f(v):
        raw = str(v).strip()
        s = raw.replace(".", "").upper() if strip_dot else raw.upper()
        if not s:
            raise ConversionError(f"empty code: {v!r}")
        if not s.isdigit() or s[0] == "0" or len(s) >= max_len:
            # Nothing can have been lost: alphanumeric, zeros intact, or already at max length.
            return {"": s, "_raw": raw, "_resolution": "exact" if s in ref else "not_in_reference"}
        candidates = [s.zfill(n) for n in range(len(s), max_len + 1)]
        valid = [c for c in candidates if c in ref]
        if len(valid) == 1:
            return {"": valid[0], "_raw": raw, "_resolution": "exact" if valid[0] == s else "repaired"}
        if len(valid) > 1:
            if on_ambiguous == "assume_received" and s in valid:
                return {"": s, "_raw": raw, "_resolution": "assumed"}
            return {"": None, "_raw": raw, "_resolution": "ambiguous"}
        return {"": None, "_raw": raw, "_resolution": "unresolved"}
    return f
