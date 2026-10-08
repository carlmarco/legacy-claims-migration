"""Loaders for reference code sets (valid-value lists) used by converters."""
import zipfile
from pathlib import Path

LOADERS = {}


def loader(name):
    def register(fn):
        LOADERS[name] = fn
        return fn
    return register


@loader("first_token")
def first_token(path: str, member: str | None = None, encoding: str = "latin-1") -> set[str]:
    """Code is the first whitespace-separated token on each line. Reads from a zip if `member` is set."""
    p = Path(path)
    if member:
        with zipfile.ZipFile(p) as zf:
            text = zf.read(member).decode(encoding)
    else:
        text = p.read_text(encoding=encoding)
    return {line.split(None, 1)[0] for line in text.splitlines() if line.strip()}


def load(spec: dict, base: Path) -> set[str]:
    """spec: {loader: name, path: ..., <loader args>}  or  {loader: name, sources: [{path, ...}, ...]}.

    `sources` are unioned, e.g. several versions of a code set that each apply to part of the data.
    Paths are relative to `base`.
    """
    spec = dict(spec)
    fn = LOADERS[spec.pop("loader")]
    sources = spec.pop("sources", None) or [spec]
    out: set[str] = set()
    for src in sources:
        src = dict(src)
        path = Path(src.pop("path"))
        out |= fn(str(path if path.is_absolute() else base / path), **src)
    return out
