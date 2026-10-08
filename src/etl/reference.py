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
    """spec: {loader: name, path: ..., ...}. Multiple `paths` are unioned (e.g. several code-set versions)."""
    spec = dict(spec)
    fn = LOADERS[spec.pop("loader")]
    paths = spec.pop("paths", None) or [spec.pop("path")]
    out: set[str] = set()
    for p in paths:
        out |= fn(str(base / p) if not Path(p).is_absolute() else p, **spec)
    return out
