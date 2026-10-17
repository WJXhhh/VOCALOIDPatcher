"""Compare auxiliary source phase before and after a native interpolation replay."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256
from probe_vqm_loaded_aux_mix import VDM_SHA256, VQM_SHA256


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vdm-dll", required=True, type=Path)
    parser.add_argument("--dse-dll", required=True, type=Path)
    parser.add_argument("--explib", required=True, type=Path)
    parser.add_argument("--scratch", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    probe = Path(__file__).with_name("probe_vqm_loaded_aux_mix.py")
    categories = []
    for category, frame_count in ((2, 148), (3, 389)):
        cases = []
        for mutated in (False, True):
            command = [sys.executable, str(probe), "--vdm-dll", str(args.vdm_dll),
                       "--dse-dll", str(args.dse_dll), "--explib", str(args.explib),
                       "--scratch", str(args.scratch), "--output", str(args.output),
                       "--single-case", "--category", str(category), "--gain", "1",
                       "--reinterpolate-sources"]
            if mutated:
                command.extend(("--mutate-field", "source_phase"))
            child = subprocess.run(command, capture_output=True, text=True,
                                   check=False, timeout=180)
            if child.returncode:
                raise RuntimeError(f"category {category}, mutation={mutated}: "
                                   f"{child.stderr[-1300:] or child.returncode}")
            cases.append(json.loads(child.stdout))
        baseline, changed = cases
        original = baseline["source_interpolation_replay"]
        edited = changed["source_interpolation_replay"]
        if (changed["mutation"]["values_changed"] != frame_count * 350 or
                original["source_rate"] != edited["source_rate"] or
                original["source_rate"] != 22050.0):
            raise AssertionError("auxiliary phase fixture changed")
        before_changed = original["before"]["sha256"] != edited["before"]["sha256"]
        after_changed = original["after"]["sha256"] != edited["after"]["sha256"]
        if (before_changed != (category == 2) or not after_changed or
                original["after"]["count"] != 350 or
                edited["after"]["count"] != 350 or
                original["after"]["nonzero"] != (123 if category == 2 else 73) or
                edited["after"]["nonzero"] != 350):
            raise AssertionError("native phase replay boundary changed")
        categories.append({"category_code": category,
                           "loaded_source_frames": frame_count,
                           "source_phase_values_changed": frame_count * 350,
                           "baseline_source_phase_nonzero_min": baseline[
                               "source_phase_nonzero_min"],
                           "baseline_source_phase_nonzero_max": baseline[
                               "source_phase_nonzero_max"],
                           "event_output": {"baseline": original["before"],
                                            "mutated": edited["before"],
                                            "phase_changed": before_changed},
                           "direct_interpolation_replay": {
                               "baseline": original["after"],
                               "mutated": edited["after"],
                               "phase_changed": after_changed}})
    report = {"dse_sha256": SHA256, "vdm_sha256": VDM_SHA256,
              "auxiliary_vqm_sha256": VQM_SHA256, "categories": categories,
              "scope": "Offline loaded TENSE/BRWSP source phase changed in memory. "
                       "The native source-frame interpolation function is replayed "
                       "after the original wrapper using its post-wrapper source "
                       "copies. This proves the replay can transfer phase, not "
                       "which source copies or parameters the wrapper used earlier. "
                       "No Editor process or bank PCM."}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
