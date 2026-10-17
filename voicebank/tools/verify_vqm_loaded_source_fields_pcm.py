"""Check loaded auxiliary source-frame changes through offline final PCM."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256
from probe_vqm_loaded_aux_mix import VDM_SHA256, VQM_SHA256


FIELDS = ("frequency", "amplitude", "phase", "feature6",
          "feature17", "outer18", "outer19")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vdm-dll", required=True, type=Path)
    parser.add_argument("--dse-dll", required=True, type=Path)
    parser.add_argument("--explib", required=True, type=Path)
    parser.add_argument("--scratch-source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    probe = Path(__file__).with_name("verify_vqm_loaded_aux_pcm.py")
    rows = []
    for category, mode, expected_frames in ((2, "tense", 148),
                                             (3, "breath_d", 389)):
        cases = {}
        for field in (None, *FIELDS):
            command = [sys.executable, str(probe),
                       "--vdm-dll", str(args.vdm_dll),
                       "--dse-dll", str(args.dse_dll),
                       "--explib", str(args.explib),
                       "--scratch-source", str(args.scratch_source),
                       "--single-case", "--mode", mode,
                       "--ordinary-onset", "--source-category", str(category)]
            if category == 3:
                command.extend(("--ordinary-pitch", "300"))
            if field:
                command.extend(("--source-mutation", field))
            child = subprocess.run(command, capture_output=True, text=True,
                                   check=False, timeout=180)
            if child.returncode:
                raise RuntimeError(f"category {category}, field {field}: "
                                   f"{child.stderr[-1300:] or child.returncode}")
            case = json.loads(child.stdout)
            if (case["blocks_run"] != 106 or
                    len(case["observations"]) != 56 or
                    case["nonzero_blocks"] != 56 or
                    any(block["clipped_samples"] for block in case["output_blocks"])):
                raise AssertionError("source PCM fixture changed")
            if field and (case["source_mutation_state"]["source_frames"] !=
                          expected_frames or
                          case["source_mutation_state"]["values_changed"] <= 0):
                raise AssertionError("source mutation did not reach loaded frames")
            cases[field or "baseline"] = case

        baseline = cases["baseline"]
        base_states = [observation["auxiliary_states"][category - 2]
                       for observation in baseline["observations"]]
        if any(state["feature_present"] or state["outer18"] != 255 or
               state["outer19"] != 0 for state in base_states):
            raise AssertionError("auxiliary interpolation mask changed")
        mutations = []
        for field in FIELDS:
            current = cases[field]
            observed = []
            for before, after in zip(baseline["observations"],
                                     current["observations"]):
                base_state = before["auxiliary_states"][category - 2]
                changed_state = after["auxiliary_states"][category - 2]
                observed.append({
                    key: base_state[key] != changed_state[key]
                    for key in ("frequency_sha256", "amplitude_sha256",
                                "phase_sha256", "feature_present",
                                "outer18", "outer19")
                })
            counts = {key + "_blocks_changed": sum(row[key] for row in observed)
                      for key in observed[0]}
            aux_changed = sum(a["auxiliary_spectrum_sha256"] !=
                              b["auxiliary_spectrum_sha256"]
                              for a, b in zip(baseline["observations"],
                                              current["observations"]))
            pcm_changed = sum(a["sha256"] != b["sha256"]
                              for a, b in zip(baseline["output_blocks"],
                                              current["output_blocks"]))
            mutations.append({"field": field,
                              "source_frames": expected_frames,
                              "source_values_changed": current[
                                  "source_mutation_state"]["values_changed"],
                              "interpolated": counts,
                              "auxiliary_spectrum_blocks_changed": aux_changed,
                              "output_pcm_blocks_changed": pcm_changed,
                              "output_stream_sha256": current[
                                  "output_stream_sha256"]})
        expected_effects = ({
            "frequency": (5, 10), "amplitude": (0, 0),
            "phase": (56, 56), "feature6": (0, 0),
            "feature17": (0, 0), "outer18": (0, 0),
            "outer19": (0, 0),
        } if category == 2 else {field: (0, 0) for field in FIELDS})
        if any((row["auxiliary_spectrum_blocks_changed"],
                row["output_pcm_blocks_changed"]) !=
               expected_effects[row["field"]] for row in mutations):
            raise AssertionError("loaded source field PCM boundary changed")
        rows.append({"category_code": category, "mode": mode,
                     "owned_ordinary_pitch_hz": ("220 + source frame index"
                                                  if category == 2 else 300),
                     "baseline_interpolated_harmonic_counts": sorted({
                         state["harmonics"] for state in base_states}),
                     "baseline_interpolated_feature_present": False,
                     "baseline_interpolated_outer18": 255,
                     "baseline_interpolated_outer19": 0,
                     "baseline_output_stream_sha256": baseline[
                         "output_stream_sha256"],
                     "baseline_peak_abs_pcm": max(block["peak_abs"]
                                                  for block in baseline["output_blocks"]),
                     "mutations": mutations})
    report = {"dse_sha256": SHA256, "vdm_sha256": VDM_SHA256,
              "auxiliary_vqm_sha256": VQM_SHA256,
              "categories": rows,
              "scope": "Genuine loaded TENSE/BRWSP source frames, mutated only "
                       "in offline memory, with an owned ordinary-primary "
                       "event through native frequency processing and final "
                       "int16 PCM. No Editor process or installed bank PCM."}
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")


if __name__ == "__main__":
    main()
