"""Locate the source-phase handoff in loaded TENSE and BRWSP PCM events."""

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
    categories = []
    for category, mode, count in ((2, "tense", 148), (3, "breath_d", 389)):
        cases = []
        for mutate in (False, True):
            command = [sys.executable, str(probe), "--vdm-dll", str(args.vdm_dll),
                       "--dse-dll", str(args.dse_dll), "--explib", str(args.explib),
                       "--scratch-source", str(args.scratch_source), "--single-case",
                       "--ordinary-onset", "--mode", mode]
            if category == 3:
                command.extend(("--ordinary-pitch", "300"))
            if mutate:
                command.extend(("--source-category", str(category),
                                "--source-mutation", "phase"))
            child = subprocess.run(command, capture_output=True, text=True,
                                   check=False, timeout=180)
            if child.returncode:
                raise RuntimeError(f"category {category}, mutate={mutate}: "
                                   f"{child.stderr[-1300:] or child.returncode}")
            cases.append(json.loads(child.stdout))
        before, after = cases
        if (len(before["observations"]) != 56 or
                len(after["observations"]) != 56 or
                after["source_mutation_state"]["source_frames"] != count or
                after["source_mutation_state"]["values_changed"] != count * 350):
            raise AssertionError("loaded source fixture changed")
        keys = ("source_a_phase_sha256", "source_b_phase_sha256",
                "phase_sha256", "amplitude_sha256", "frequency_sha256")
        changes = {key: 0 for key in keys}
        active_counts = set()
        sample_indices = set()
        masks = {key: set() for key in (
            "source_a_mask", "source_b_mask", "interpolated_mask")}
        harmonic_counts = set()
        source_phase_nonzero = set()
        source_first_active_nonzero = set()
        interpolated_phase_nonzero = set()
        for old, new in zip(before["observations"], after["observations"]):
            a = old["auxiliary_states"][category - 2]
            b = new["auxiliary_states"][category - 2]
            active_counts.add(a["active_frequency_count"])
            sample_indices.add(a["selected_sample"])
            harmonic_counts.add(a["harmonics"])
            source_phase_nonzero.add(a["source_a_phase_nonzero"])
            source_first_active_nonzero.add(a["source_a_phase_first_active_nonzero"])
            interpolated_phase_nonzero.add(a["interpolated_phase_nonzero"])
            for key in masks:
                masks[key].add(a[key])
            for key in keys:
                changes[key] += a[key] != b[key]
        changes["auxiliary_spectrum_sha256"] = sum(
            a["auxiliary_spectrum_sha256"] != b["auxiliary_spectrum_sha256"]
            for a, b in zip(before["observations"], after["observations"]))
        changes["output_pcm_sha256"] = sum(
            a["sha256"] != b["sha256"]
            for a, b in zip(before["output_blocks"], after["output_blocks"]))
        expected = (56 if category == 2 else 0)
        if (changes["source_a_phase_sha256"] != 56 or
                changes["source_b_phase_sha256"] != 56 or
                any(changes[key] != expected for key in (
                    "phase_sha256", "auxiliary_spectrum_sha256",
                    "output_pcm_sha256")) or
                changes["amplitude_sha256"] or changes["frequency_sha256"]):
            raise AssertionError(f"source-phase handoff changed: {changes}")
        if (active_counts != {1021} or sample_indices != {0} or
                masks != {"source_a_mask": {0x800e2287},
                          "source_b_mask": {0x800e2287},
                          "interpolated_mask": {0x80020287}}):
            raise AssertionError("source selection or frame masks changed")
        categories.append({"category_code": category,
                           "loaded_source_frames": count,
                           "loaded_phase_values_changed": count * 350,
                           "active_frequency_counts": sorted(active_counts),
                           "selected_sample_indices": sorted(sample_indices),
                           "interpolated_harmonic_counts": sorted(harmonic_counts),
                           "source_phase_nonzero_counts": sorted(source_phase_nonzero),
                           "source_first_active_phase_nonzero_counts": sorted(
                               source_first_active_nonzero),
                           "interpolated_phase_nonzero_counts": sorted(
                               interpolated_phase_nonzero),
                           "frame_masks": {key: [hex(value) for value in sorted(values)]
                                           for key, values in masks.items()},
                           "blocks_compared": 56, "changed_blocks": changes,
                           "baseline_output_sha256": before["output_stream_sha256"],
                           "mutated_output_sha256": after["output_stream_sha256"]})
    report = {"dse_sha256": SHA256, "vdm_sha256": VDM_SHA256,
              "auxiliary_vqm_sha256": VQM_SHA256, "categories": categories,
              "scope": "Offline loaded auxiliary frames and owned ordinary-primary "
                       "event. In-memory phase change only; post-block hashes cover "
                       "native source copies, interpolated frame, auxiliary spectrum, "
                       "and final int16 PCM. The copies may be refreshed after the "
                       "current block's interpolation. No Editor process or bank PCM."}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
