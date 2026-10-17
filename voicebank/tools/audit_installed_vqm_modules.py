"""Inventory VQM-related literal signatures in installed Editor PE modules.

This is a bounded lead search, not proof that a module lacks VQM logic.
Only file metadata, hashes, and literal counts are retained.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


LITERALS = ("VQM", "DVQM", "VQMs", "VQM.vdb", "FRM2", "SMS2", "Growl", "GROWL")


def inspect(path: Path) -> dict:
    data = path.read_bytes()
    if data[:2] != b"MZ":
        raise ValueError(f"not a PE module: {path.name}")
    matches = {}
    for literal in LITERALS:
        ascii_bytes = literal.encode("ascii")
        utf16_bytes = literal.encode("utf-16le")
        ascii_count = data.count(ascii_bytes)
        utf16_count = data.count(utf16_bytes)
        if ascii_count or utf16_count:
            matches[literal] = {"ascii": ascii_count, "utf16le": utf16_count}
    return {"name": path.name, "size": len(data),
            "sha256": hashlib.sha256(data).hexdigest(), "literal_hits": matches}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--editor-dir", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    paths = sorted((path for path in args.editor_dir.iterdir()
                    if path.is_file() and path.suffix.lower() in (".dll", ".exe")),
                   key=lambda path: path.name.lower())
    if not paths:
        raise ValueError("no Editor PE modules found")
    rows = [inspect(path) for path in paths]
    report = {"modules_scanned": len(rows), "literal_signatures": list(LITERALS),
              "modules": rows,
              "scope": "Read-only literal search of top-level Editor DLL/EXE files. "
                       "Case-sensitive ASCII and UTF-16LE counts only; absence "
                       "does not exclude computed names, indirect calls, packed "
                       "data, or code in other installed components. No binary "
                       "contents or user-specific paths are retained."}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
