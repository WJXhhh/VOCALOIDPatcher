"""Test whether prefilled BRWSP source and work phases survive an owned event."""

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
    cases = []
    variants = (("baseline", ()),
                ("source_prefilled", ("--prefill-source-copies",)),
                ("work_prefilled", ("--prefill-interpolated-phase",)),
                ("work_prefilled_source_changed",
                 ("--prefill-interpolated-phase", "--source-mutation", "phase")))
    for name, extra in variants:
        command = [sys.executable, str(probe), "--vdm-dll", str(args.vdm_dll),
                   "--dse-dll", str(args.dse_dll), "--explib", str(args.explib),
                   "--scratch-source", str(args.scratch_source), "--single-case",
                   "--ordinary-onset", "--ordinary-pitch", "300", "--mode",
                   "breath_d", "--source-category", "3", *extra]
        child = subprocess.run(command, capture_output=True, text=True,
                               check=False, timeout=180)
        if child.returncode:
            raise RuntimeError(f"{name}: {child.stderr[-1300:] or child.returncode}")
        case = json.loads(child.stdout)
        states = [row["auxiliary_states"][1] for row in case["observations"]]
        if (len(states) != 56 or case["nonzero_blocks"] != 56 or
                {state["interpolated_phase_nonzero"] for state in states} != {0} or
                any(block["clipped_samples"] for block in case["output_blocks"])):
            raise AssertionError(f"BRWSP event state changed: {name}")
        cases.append({"variant": name,
                      "prefilled_phase_nonzero": case["source_mutation_state"].get(
                          "prefilled_phase_nonzero"),
                      "source_values_changed": case["source_mutation_state"].get(
                          "values_changed", 0),
                      "post_event_work_phase_nonzero": 0,
                      "output_stream_sha256": case["output_stream_sha256"],
                      "auxiliary_spectrum_sha256": [row["auxiliary_spectrum_sha256"]
                                                    for row in case["observations"]]})
    if (cases[2]["prefilled_phase_nonzero"] != 73 or
            cases[3]["prefilled_phase_nonzero"] != 350 or
            cases[3]["source_values_changed"] != 389 * 350 or
            len({case["output_stream_sha256"] for case in cases}) != 1 or
            any(case["auxiliary_spectrum_sha256"] !=
                cases[0]["auxiliary_spectrum_sha256"] for case in cases[1:])):
        raise AssertionError("BRWSP phase prefill boundary changed")
    for case in cases:
        case.pop("auxiliary_spectrum_sha256")
    report = {"dse_sha256": SHA256, "vdm_sha256": VDM_SHA256,
              "auxiliary_vqm_sha256": VQM_SHA256, "cases": cases,
              "scope": "Offline loaded BRWSP and owned 300-Hz ordinary-primary "
                       "event. The native copier/interpolator prefills object "
                       "state before event processing. Post-event work-frame phase, "
                       "auxiliary spectrum, and final PCM are compared. This tests "
                       "only the event's preparation boundary; no Editor process "
                       "or bank PCM."}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
