"""Compare loaded TENSE/BRWSP routing through the offline DSE dispatch entry."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256


MODES = ("defaults", "tense", "breath", "both")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vdm-dll", required=True, type=Path)
    parser.add_argument("--dse-dll", required=True, type=Path)
    parser.add_argument("--explib", required=True, type=Path)
    parser.add_argument("--scratch", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    probe = Path(__file__).with_name("probe_vqm_loaded_aux_mix.py")
    targets = []
    for category in (2, 3):
        modes = []
        target_pitch = None
        for mode in MODES:
            command = [sys.executable, str(probe),
                       "--vdm-dll", str(args.vdm_dll),
                       "--dse-dll", str(args.dse_dll),
                       "--explib", str(args.explib),
                       "--scratch", str(args.scratch),
                       "--output", str(args.output),
                       "--single-case", "--category", str(category),
                       "--gain", "1", "--dispatch-controls", mode,
                       "--large-phase-buffer"]
            child = subprocess.run(command, capture_output=True, text=True,
                                   timeout=90, check=False)
            if child.returncode:
                raise RuntimeError(f"target {category}, mode {mode}: "
                                   f"{child.stderr[-1200:]}")
            row = json.loads(child.stdout)
            dispatch = row["engine_dispatch"]
            if (dispatch["control_mode"] != mode or
                    row["engine_factor_bin_count"] != 0 or
                    row["engine_timeline_count"] != 0 or
                    row["engine_frame_index"] != 0 or
                    row["engine_event_count"] != 1 or
                    row["engine_pitch_step"] != 0.0 or
                    not row["large_phase_buffer"] or
                    row["other_dispatch_workspace_bytes"] <= 0 or
                    row["loaded_source_feature_frames"] not in (148, 389)):
                raise AssertionError("dispatch fixture changed")
            if target_pitch is None:
                target_pitch = row["target_pitch_hz"]
            elif target_pitch != row["target_pitch_hz"]:
                raise AssertionError("target pitch changed across control modes")
            values = dispatch["native_control_values"]
            expected = {
                "defaults": {"0xd": 0.0, "0x14": 0.0, "0x15": 0.5},
                "tense": {"0xd": 0.0, "0x14": 0.0, "0x15": 1.0},
                "breath": {"0xd": 1.0, "0x14": 1.0, "0x15": 0.5},
                "both": {"0xd": 1.0, "0x14": 1.0, "0x15": 1.0},
            }[mode]
            if values != expected:
                raise AssertionError("native control value did not change")
            modes.append(dispatch)
        if modes[0]["real_nonzero"] or modes[0]["imag_nonzero"]:
            raise AssertionError("default VQM dispatch unexpectedly nonzero")
        if not all(mode["real_nonzero"] > 0 and mode["imag_nonzero"] > 0
                   for mode in modes[1:]):
            raise AssertionError("enabled VQM class did not produce a spectrum")
        if len({mode["real_sha256"] for mode in modes}) != len(MODES):
            raise AssertionError("VQM control modes did not separate")
        targets.append({"prepared_category_code": category,
                        "target_pitch_hz": target_pitch,
                        "modes": modes})
    report = {"dse_sha256": SHA256,
              "targets": targets,
              "scope": "An offline DSE engine with installed auxiliary VQM "
                       "material and owned ordinary target frames. The native "
                       "0x171430 dispatch receives an explicitly allocated "
                       "factor and phase workspace, first-voiced-frame phase "
                       "state, and controlled default-value inputs. "
                       "Editor events and final PCM are not observed."}
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")


if __name__ == "__main__":
    main()
