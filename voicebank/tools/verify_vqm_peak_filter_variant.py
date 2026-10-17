"""Compare DRS peak-filter variants with saved V4X VQM scalar features.

The three installed VQM sequences are read only. Native analyses run in
separate child processes and only aggregate errors are recorded.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import statistics
import subprocess
import sys

from audit_frm2_numeric import SHA256
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit18_saved_band import compare_one


def one(dll: Path, sample_index: int, sequence: dict,
        peak_filter: int) -> dict:
    rows: list[dict[str, float | int]] = []
    compare_one(dll, sample_index, sequence, feature_sink=rows,
                config_588=peak_filter)
    if len(rows) != len(sequence["frames"]):
        raise AssertionError("peak-filter VQM frame count changed")
    features = {}
    for bit in (6, 16, 18):
        errors = [abs(float(row[f"drs_bit{bit}"])
                      - float(row[f"saved_bit{bit}"]))
                  for row in rows]
        features[str(bit)] = {
            "frames": len(errors),
            "within_1e_6": sum(value <= 1e-6 for value in errors),
            "mean_absolute_error": statistics.fmean(errors),
            "max_absolute_error": max(errors),
        }
    return {"sample_index": sample_index, "config_588": peak_filter,
            "features": features}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--voice-root", required=True, type=Path)
    parser.add_argument("--ddb-tools", required=True, type=Path)
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--sample-index", type=int)
    parser.add_argument("--peak-filter", type=int, choices=(0, 1))
    args = parser.parse_args()
    parser_hash, ddi_hash, sequences = load_sequences(
        args.manifest, args.voice_root, args.ddb_tools)
    if args.sample_index is not None:
        if args.peak_filter is None or not 0 <= args.sample_index < len(sequences):
            raise ValueError("child case needs valid sample and filter")
        print(json.dumps(one(args.dll, args.sample_index,
                             sequences[args.sample_index],
                             args.peak_filter)))
        return
    cases = []
    for peak_filter in (0, 1):
        for sample_index in range(len(sequences)):
            command = [sys.executable, "-u", str(Path(__file__).resolve()),
                       "--manifest", str(args.manifest),
                       "--voice-root", str(args.voice_root),
                       "--ddb-tools", str(args.ddb_tools),
                       "--dll", str(args.dll),
                       "--output", str(args.output),
                       "--sample-index", str(sample_index),
                       "--peak-filter", str(peak_filter)]
            child = subprocess.run(command, capture_output=True,
                                   text=True, check=False)
            if child.returncode:
                raise RuntimeError(
                    f"VQM peak-filter case {peak_filter}/{sample_index} failed: "
                    f"{child.stderr[-600:]}")
            cases.append(json.loads(child.stdout))
    report = {
        "dse_sha256": SHA256,
        "ddi_sha256": ddi_hash,
        "parser_sha256": parser_hash,
        "cases": cases,
        "scope": "Full DRS streaming analyses of the three saved V4X VQM SND PCM sequences with config +0x588=0/1. Tests whether the documented peak post-filter alone reproduces saved scalar bits6/16/18; other authoring settings remain unknown.",
    }
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({
        "samples": len(sequences),
        "bit6_mae_by_filter": {
            str(flag): [row["features"]["6"]["mean_absolute_error"]
                        for row in cases if row["config_588"] == flag]
            for flag in (0, 1)},
    }))


if __name__ == "__main__":
    main()
