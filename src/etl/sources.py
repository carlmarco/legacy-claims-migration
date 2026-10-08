"""Streaming readers for source files. Constant memory regardless of file size."""
import csv
import io
import zipfile
from collections.abc import Iterator
from pathlib import Path


def iter_zip_csv(path: str | Path, member: str | None = None) -> Iterator[dict[str, str]]:
    """Yield rows of a CSV inside a zip as dicts, without extracting to disk.

    If `member` is None the archive must contain exactly one file.
    """
    with zipfile.ZipFile(path) as zf:
        names = [n for n in zf.namelist() if not n.endswith("/")]
        if member is None:
            if len(names) != 1:
                raise ValueError(f"{path}: expected one member, found {names}")
            member = names[0]
        with zf.open(member) as raw:
            yield from csv.DictReader(io.TextIOWrapper(raw, encoding="utf-8", newline=""))
