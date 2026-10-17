"""Check BRWSP event-state phase mode against loaded source phase and PCM."""

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
    parser.add_argument("--scratch-source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    probe = Path(__file__).with_name("verify_vqm_loaded_aux_pcm.py")
    rows = []
    for mode in ("default", "clear_10", "clear_both"):
        cases = []
        for mutate in (False, True):
            command = [sys.executable, str(probe), "--vdm-dll", str(args.vdm_dll),
                       "--dse-dll", str(args.dse_dll), "--explib", str(args.explib),
                       "--scratch-source", str(args.scratch_source),
                       "--single-case", "--ordinary-onset", "--ordinary-pitch",
                       "300", "--mode", "breath_d", "--source-category", "3",
                       "--breath-state-phase-mode", mode]
            if mutate:
                command.extend(("--source-mutation", "phase"))
            child = subprocess.run(command, capture_output=True, text=True,
                                   check=False, timeout=180)
            if child.returncode:
                raise RuntimeError(f"{mode}, mutate={mutate}: "
                                   f"{child.stderr[-1300:] or child.returncode}")
            cases.append(json.loads(child.stdout))
        before, after = cases
        if (len(before["observations"]) != 56 or
                len(after["observations"]) != 56 or
                before["nonzero_blocks"] != 56 or after["nonzero_blocks"] != 56 or
                after["source_mutation_state"]["source_frames"] != 389 or
                after["source_mutation_state"]["values_changed"] != 389 * 350 or
                any(block["clipped_samples"] for case in cases
                    for block in case["output_blocks"])):
            raise AssertionError("BRWSP event fixture changed")
        states = [[observation["auxiliary_states"][1]
                   for observation in case["observations"]] for case in cases]
        phase_nonzero = [sorted({state["interpolated_phase_nonzero"]
                                 for state in series}) for series in states]
        work_phase_changed = sum(a["phase_sha256"] != b["phase_sha256"]
                                 for a, b in zip(*states))
        spectrum_changed = sum(
            a["auxiliary_spectrum_sha256"] != b["auxiliary_spectrum_sha256"]
            for a, b in zip(before["observations"], after["observations"]))
        pcm_changed = sum(a["sha256"] != b["sha256"]
                          for a, b in zip(before["output_blocks"],
                                          after["output_blocks"]))
        expected = 0 if mode == "default" else 56
        if (phase_nonzero != ([[0], [0]] if mode == "default"
                             else [[73], [74]]) or
                (work_phase_changed, spectrum_changed, pcm_changed) !=
                (expected, expected, expected)):
            raise AssertionError(f"BRWSP phase mode boundary changed: {mode}")
        rows.append({"state_mode": mode,
                     "source_phase_changed": 389 * 350,
                     "work_phase_nonzero_counts": phase_nonzero,
                     "work_phase_blocks_changed": work_phase_changed,
                     "auxiliary_spectrum_blocks_changed": spectrum_changed,
                     "output_pcm_blocks_changed": pcm_changed,
                     "baseline_output_sha256": before["output_stream_sha256"],
                     "mutated_output_sha256": after["output_stream_sha256"]})
    if rows[1]["baseline_output_sha256"] != rows[2]["baseline_output_sha256"] or \
            rows[1]["mutated_output_sha256"] != rows[2]["mutated_output_sha256"]:
        raise AssertionError("second state-mode byte changed this event")
    report = {"dse_sha256": SHA256, "vdm_sha256": VDM_SHA256,
              "auxiliary_vqm_sha256": VQM_SHA256, "cases": rows,
              "scope": "Offline loaded BRWSP event-state copy of the category "
                       "record. Only its +0x10/+0x11 mode bytes are changed in "
                       "memory, alongside loaded source phase. A 300-Hz owned "
                       "ordinary-primary event runs through final int16 PCM. "
                       "The default +0x10=1 branch clears and rebuilds working "
                       "component arrays after interpolation; +0x10=0 exposes "
                       "saved source phase to the tested output. No Editor process "
                       "or bank PCM."}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
