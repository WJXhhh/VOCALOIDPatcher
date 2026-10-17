"""Compare saved VQM field changes in loaded auxiliary material at the native mixer."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256


FIELDS = ("4", "6", "14", "15", "16", "17", "18", "outer18", "outer19")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vdm-dll", required=True, type=Path)
    parser.add_argument("--dse-dll", required=True, type=Path)
    parser.add_argument("--explib", required=True, type=Path)
    parser.add_argument("--scratch", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    probe = Path(__file__).with_name("probe_vqm_loaded_aux_mix.py")

    def one(category: int, field: str | None) -> dict:
        command = [sys.executable, str(probe),
                   "--vdm-dll", str(args.vdm_dll),
                   "--dse-dll", str(args.dse_dll),
                   "--explib", str(args.explib),
                   "--scratch", str(args.scratch),
                   "--output", str(args.output),
                   "--single-case", "--category", str(category),
                   "--gain", "1"]
        if field:
            command.extend(("--mutate-field", field))
        child = subprocess.run(command, capture_output=True, text=True,
                               timeout=90, check=False)
        if child.returncode:
            raise RuntimeError(f"category {category}, field {field}: "
                               f"{child.stderr[-1200:]}")
        return json.loads(child.stdout)

    categories = []
    for category in (2, 3):
        baseline = one(category, None)
        if (not baseline["interpolated_frame_present"] or
                baseline["interpolated_feature_present"] or
                baseline["real_nonzero"] <= 0 or
                baseline["imag_nonzero"] <= 0):
            raise AssertionError("loaded baseline is not the expected native path")
        cases = []
        for field in FIELDS:
            case = one(category, field)
            mutation = case["mutation"]
            if (not mutation or mutation["field"] != field or
                    mutation["first_changed"] == mutation["first_original"] or
                    case["loaded_source_feature_frames"] !=
                    mutation["source_frames"] or
                    case["interpolated_feature_present"]):
                raise AssertionError("saved-source field mutation did not arrive")
            unchanged = (case["real_sha256"] == baseline["real_sha256"]
                         and case["imag_sha256"] == baseline["imag_sha256"])
            cases.append({
                "field": field,
                "source_frames_mutated": mutation["source_frames"],
                "saved_source_first_value_changed": True,
                "interpolated_feature_present":
                    case["interpolated_feature_present"],
                "interpolated_outer18": case["interpolated_outer18"],
                "interpolated_outer19": case["interpolated_outer19"],
                "frequency_output_unchanged": unchanged,
            })
        categories.append({
            "category_code": category,
            "loaded_source_feature_frames":
                baseline["loaded_source_feature_frames"],
            "baseline_real_sha256": baseline["real_sha256"],
            "baseline_imag_sha256": baseline["imag_sha256"],
            "baseline_interpolated_outer18":
                baseline["interpolated_outer18"],
            "baseline_interpolated_outer19":
                baseline["interpolated_outer19"],
            "cases": cases,
        })
    report = {"dse_sha256": SHA256,
              "categories": categories,
              "all_frequency_outputs_unchanged": all(
                  case["frequency_output_unchanged"]
                  for category in categories for case in category["cases"]),
              "scope": "Installed auxiliary VQM source frames changed in an "
                       "isolated offline DSE instance, then native event "
                       "interpolation and category frequency mixers run "
                       "against one owned target frame. Other events and "
                       "Editor host routing are not covered."}
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")


if __name__ == "__main__":
    main()
