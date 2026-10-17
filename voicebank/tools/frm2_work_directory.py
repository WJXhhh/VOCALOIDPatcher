"""Keep FRM2 experiment scratch files out of the Windows system TEMP folder."""
from __future__ import annotations

import os
from pathlib import Path
import tempfile


def work_root():
    configured = os.environ.get('FRM2_WORK_ROOT', '').strip()
    repository = Path(__file__).resolve().parents[2]
    root = Path(configured).expanduser().resolve() if configured else (
        repository.parent / (repository.name + '-research-work') / 'frm2')
    root.mkdir(parents=True, exist_ok=True)
    return root


def temporary_directory(prefix):
    # TemporaryDirectory removes only the new, uniquely named child it owns.
    return tempfile.TemporaryDirectory(prefix=prefix, dir=work_root())
