"""Audit VDM-registered V4X database paths against adjacent VQM.vdb files.

The offline VDM harness enumerates installed banks without starting the Editor.
Only bank identifiers, verified DDI hashes, and file-existence results are saved.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess


PATH_RE = re.compile(r"^bank\[(\d+)\]\.path=(.+)$")


def run(manifest_path: Path, project: Path, expected_explib: Path) -> dict:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    expected = {row["bank"]: row["ddi_sha256"] for row in manifest["banks"]}
    child = subprocess.run(
        ["dotnet", "run", "--project", str(project), "-c", "Debug"],
        capture_output=True, text=True, check=False, timeout=120)
    if child.returncode:
        raise RuntimeError(f"offline VDM enumeration failed: {child.stderr[-1000:]}")
    lines = child.stdout.splitlines()
    library = [line.removeprefix("expression_library=") for line in lines
               if line.startswith("expression_library=")]
    if library != [str(expected_explib)]:
        raise AssertionError("VDM expression library path differs")
    paths = [Path(match.group(2)) for line in lines
             if (match := PATH_RE.fullmatch(line))]
    if len(paths) != len(expected) or len(set(paths)) != len(paths):
        raise AssertionError("VDM bank count or uniqueness differs from manifest")
    banks = []
    for ddb in paths:
        name = ddb.stem
        ddi = ddb.with_suffix(".ddi")
        if name not in expected or not ddb.is_file() or not ddi.is_file():
            raise AssertionError("registered DDB/DDI differs from V4X manifest")
        digest = hashlib.sha256(ddi.read_bytes()).hexdigest()
        if digest != expected[name]:
            raise AssertionError("registered DDI hash differs from fixed manifest")
        banks.append({
            "bank": name,
            "ddi_sha256": digest,
            "same_directory_vqm_exists": ddb.with_name("VQM.vdb").is_file(),
        })
    explib_vqm = expected_explib / "VQM.vdb"
    if not explib_vqm.is_file():
        raise AssertionError("expression library VQM.vdb is missing")
    return {
        "registered_v4x_banks": len(banks),
        "banks": sorted(banks, key=lambda row: row["bank"]),
        "banks_with_same_directory_vqm": sum(row["same_directory_vqm_exists"]
                                              for row in banks),
        "expression_library_vqm_sha256": hashlib.sha256(
            explib_vqm.read_bytes()).hexdigest(),
        "scope": "VDM-registered DDB paths and adjacent-file existence. "
                 "Does not prove which database object DSE passes to its "
                 "optional VQM.vdb loader at runtime.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--vdm-project", type=Path, required=True)
    parser.add_argument("--explib", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.write_text(json.dumps(
        run(args.manifest, args.vdm_project, args.explib), indent=2) + "\n",
        encoding="utf-8")


if __name__ == "__main__":
    main()
