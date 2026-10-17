"""Transfer temporal VQM feature fits between TENSE and BRWSP categories."""

from __future__ import annotations

import argparse
import hashlib
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
    parser.add_argument("--v4x-fit-report", required=True, type=Path)
    parser.add_argument("--aux-fit-report", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    fit_report = json.loads(args.aux_fit_report.read_text(encoding="utf-8"))
    if (fit_report["dse_sha256"] != SHA256 or
            fit_report["auxiliary_vqm_sha256"] != VQM_SHA256 or
            {row["category"] for row in fit_report["categories"]} !=
            {"TENSE", "BRWSP"}):
        raise ValueError("auxiliary fit evidence differs from verified material")
    probe = Path(__file__).with_name("compare_aux_vqm_drs_fields.py")
    rows = []
    for target, source in (("TENSE", "BRWSP"), ("BRWSP", "TENSE")):
        command = [sys.executable, str(probe), "--vdb", str(args.vdb),
                   "--dse-dll", str(args.dse_dll), "--output", str(args.output),
                   "--single-category", target, "--analysis-mode", "4",
                   "--temporal-weights", str(args.v4x_fit_report),
                   "--crossfit-report", str(args.aux_fit_report),
                   "--crossfit-source-category", source]
        child = subprocess.run(command, capture_output=True, text=True,
                               check=False, timeout=180)
        if child.returncode:
            raise RuntimeError(f"{target}: {child.stderr[-1300:] or child.returncode}")
        result = json.loads(child.stdout)
        fields = {}
        source_row = next(row for row in fit_report["categories"]
                          if row["category"] == source)
        for bit in (6, 16, 18):
            key = str(bit)
            fields[key] = {
                "source_fitted_weights": source_row["temporal_transfer"][key][
                    "local_fitted_five_weights"],
                "target_identity": result["temporal_transfer"][key]["identity"],
                "target_v4x_fit": result["temporal_transfer"][key][
                    "v4x_fitted_five"],
                "target_crossfit": result["temporal_crossfit"][key],
                "target_local_fit_in_sample": result["temporal_transfer"][key][
                    "local_fitted_five_in_sample"],
            }
        rows.append({"source_category": source, "target_category": target,
                     "target_frames": result["saved_feature_frames"],
                     "fields": fields})
    report = {
        "dse_sha256": SHA256,
        "auxiliary_vqm_sha256": VQM_SHA256,
        "v4x_fit_report_sha256": hashlib.sha256(
            args.v4x_fit_report.read_bytes()).hexdigest(),
        "aux_fit_report_sha256": hashlib.sha256(
            args.aux_fit_report.read_bytes()).hexdigest(),
        "common_interior_margin_frames": 4,
        "cross_category_predictions": rows,
        "scope": "Each symmetric five-tap kernel is fitted on one auxiliary "
                 "category and evaluated on the other without target refitting. "
                 "Both are analyzed from their unchanged embedded PCM in offline "
                 "DRS mode 4. In-sample fits are diagnostic only; only aggregate "
                 "errors and coefficients are retained, never PCM or frame values.",
    }
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
