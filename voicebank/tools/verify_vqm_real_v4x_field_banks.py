"""Run real V4X VQM source-field perturbations across all eight installed banks."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256
from probe_vqm_loaded_aux_mix import VDM_SHA256, VQM_SHA256


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dse", required=True, type=Path)
    parser.add_argument("--vdm", required=True, type=Path)
    parser.add_argument("--explib", required=True, type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--voice-root", required=True, type=Path)
    parser.add_argument("--ddb-tools", required=True, type=Path)
    parser.add_argument("--first", default="a")
    parser.add_argument("--second", default="Sil")
    parser.add_argument("--third")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    banks = [row["bank"] for row in manifest["banks"]]
    if len(banks) != 8 or len(set(banks)) != 8:
        raise AssertionError("installed V4X bank manifest changed")
    probe = Path(__file__).with_name("verify_vqm_real_v4x_field_matrix.py")
    records = []
    for bank in banks:
        command = [sys.executable, str(probe), "--dse", str(args.dse),
                   "--vdm", str(args.vdm), "--explib", str(args.explib),
                   "--manifest", str(args.manifest), "--voice-root",
                   str(args.voice_root), "--ddb-tools", str(args.ddb_tools),
                   "--bank", bank, "--first", args.first,
                   "--second", args.second]
        if args.third is not None:
            command += ["--third", args.third]
        child = subprocess.run(command, capture_output=True, text=True,
                               check=False, timeout=900)
        if child.returncode:
            raise RuntimeError(f"{bank}: {child.stderr[-1500:] or child.returncode}")
        result = json.loads(child.stdout)
        if result["dse_sha256"] != SHA256 or result["bank"] != bank:
            raise AssertionError("bank field matrix identity changed")
        records.append({"bank": bank, "ddi_sha256": next(
            row["ddi_sha256"] for row in manifest["banks"]
            if row["bank"] == bank), "categories": result["cases"]})
    report = {"dse_sha256": SHA256, "vdm_sha256": VDM_SHA256,
              "auxiliary_vqm_sha256": VQM_SHA256,
              "manifest_sha256": hashlib.sha256(args.manifest.read_bytes()).hexdigest(),
              "bank_count": len(records), "banks": records,
              "route": [args.first, args.second] + ([args.third]
                                                    if args.third else []),
              "scope": "Eight installed V4X banks, one real DDI/DDB "
                       "source event each. Each TENSE/BRWSP field mutation "
                       "runs in an independent offline DSE process with a "
                       "repeated baseline. Only aggregate output block "
                       "differences and hashes retained, no source payloads."}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
