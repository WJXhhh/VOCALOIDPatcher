"""Check auxiliary VQM class dispatch with real note events in all eight V4X banks."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256
from probe_vqm_loaded_aux_mix import VDM_SHA256, VQM_SHA256
from verify_vqm_real_v4x_event import MODES


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
    probe = Path(__file__).with_name("verify_vqm_real_v4x_event.py")
    records = []
    for bank in banks:
        cases = {}
        for mode in MODES:
            command = [sys.executable, str(probe), "--dse", str(args.dse),
                       "--vdm", str(args.vdm), "--explib", str(args.explib),
                       "--manifest", str(args.manifest), "--voice-root",
                       str(args.voice_root), "--ddb-tools", str(args.ddb_tools),
                       "--bank", bank, "--first", args.first,
                       "--second", args.second, "--single-mode", mode]
            if args.third is not None:
                command += ["--third", args.third]
            child = subprocess.run(command, capture_output=True, text=True,
                                   check=False, timeout=180)
            if child.returncode:
                raise RuntimeError(f"{bank}/{mode}: "
                                   f"{child.stderr[-1500:] or child.returncode}")
            cases[mode] = json.loads(child.stdout)
        baseline = cases["unbound"]
        hashes = baseline["output_block_hashes"]
        if hashes != cases["off"]["output_block_hashes"]:
            raise AssertionError(f"{bank}: default auxiliary binding changed PCM")
        rows = []
        for mode in MODES:
            case = cases[mode]
            if case["blocks"] != baseline["blocks"]:
                raise AssertionError(f"{bank}: inconsistent block count")
            rows.append({"mode": mode, "blocks": case["blocks"],
                         "nonzero_blocks": case["nonzero_blocks"],
                         "art_pcm_checks": case["art_pcm_checks"],
                         "sta_pcm_checks": case["sta_pcm_checks"],
                         "changed_pcm_blocks": sum(left != right for left, right
                                                   in zip(hashes,
                                                          case["output_block_hashes"])),
                         "source_copies_present": {
                             key: value["source_copies_present"]
                             for key, value in case["categories"].items()},
                         "work_harmonics_present": {
                             key: value["work_harmonics_present"]
                             for key, value in case["categories"].items()},
                         "output_stream_sha256": case["output_stream_sha256"]})
        records.append({"bank": bank, "ddi_sha256": next(
            row["ddi_sha256"] for row in manifest["banks"]
            if row["bank"] == bank), "cases": rows})
    report = {"dse_sha256": SHA256, "vdm_sha256": VDM_SHA256,
              "auxiliary_vqm_sha256": VQM_SHA256,
              "manifest_sha256": hashlib.sha256(args.manifest.read_bytes()).hexdigest(),
              "bank_count": len(records), "banks": records,
              "route": [args.first, args.second] + ([args.third]
                                                    if args.third else []),
              "scope": "Each installed V4X bank contributes one indexed "
                       "note event with real DDI/DDB ART/STA source material. "
                       "Five independent offline DSE control cases per bank; "
                       "fixture control values do not establish Editor automation. "
                       "Only aggregate counts and output hashes retained, no PCM."}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
