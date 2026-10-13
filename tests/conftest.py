from pathlib import Path

import pytest

ROOT = Path(__file__).parent.parent

# Tests that load the SynPUF mapping need the CMS code lists in data/raw (378 MB, gitignored; fetch with
# datasets/synpuf/download.sh). CI runs without them, so those tests skip with a visible reason instead of failing.
requires_data = pytest.mark.skipif(
    not (ROOT / "data/raw/ICD-9-CM-v32-master-descriptions.zip").exists(),
    reason="needs CMS data in data/raw (datasets/synpuf/download.sh)",
)
