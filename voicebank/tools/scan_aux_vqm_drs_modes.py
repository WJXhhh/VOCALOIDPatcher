"""Scan six DRS analysis modes against embedded auxiliary VQM PCM features."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256
from probe_vqm_loaded_aux_mix import VQM_SHA256


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vdb", required=True, type=Path)
    parser.add_argument("--dse-dll", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    probe = Path(__file__).with_name("compare_aux_vqm_drs_fields.py")
    rows = []
    for category in ("TENSE", "BRWSP"):
        for mode in range(6):
            command = [sys.executable, str(probe), "--vdb", str(args.vdb),
                       "--dse-dll", str(args.dse_dll), "--output", str(args.output),
                       "--single-category", category, "--analysis-mode", str(mode)]
            child = subprocess.run(command, capture_output=True, text=True,
                                   check=False, timeout=180)
            if child.returncode:
                raise RuntimeError(f"{category}, mode {mode}: "
                                   f"{child.stderr[-1300:] or child.returncode}")
            case = json.loads(child.stdout)
            zero_shift = case["comparison_by_frame_shift"]["0"]
            fields = {}
            for bit in (0, 1, 2, 6, 16, 17, 18):
                key = str(bit)
                best = min(((int(shift), values[key]) for shift, values in
                            case["comparison_by_frame_shift"].items() if key in values),
                           key=lambda pair: pair[1]["mean_absolute_error"])
                fields[key] = {
                    "zero_shift_float32_exact": zero_shift[key]["float32_exact"],
                    "zero_shift_mean_absolute_error": zero_shift[key][
                        "mean_absolute_error"],
                    "max_nearby_float32_exact": max(
                        values[key]["float32_exact"] for values in
                        case["comparison_by_frame_shift"].values() if key in values),
                    "best_nearby_shift": best[0],
                    "best_nearby_mean_absolute_error": best[1][
                        "mean_absolute_error"],
                }
            rows.append({"category": category, "analysis_mode": mode,
                         "saved_frames": case["saved_feature_frames"],
                         "produced_frames": case["produced_feature_frames"],
                         "produced_bit17_nonzero": case["produced_bit17_nonzero"],
                         "fields": fields})
    if len(rows) != 12 or any(row["saved_frames"] != row["produced_frames"]
                              for row in rows):
        raise AssertionError("auxiliary DRS mode coverage changed")
    report = {"dse_sha256": SHA256, "auxiliary_vqm_sha256": VQM_SHA256,
              "cases": rows,
              "scope": "The two embedded auxiliary PCM16 sequences are analyzed "
                       "unchanged in six DRS modes with fixed external reference "
                       "pitch (181/300 Hz). Counts and scalar comparison statistics "
                       "are retained; no PCM is exported or written."}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
