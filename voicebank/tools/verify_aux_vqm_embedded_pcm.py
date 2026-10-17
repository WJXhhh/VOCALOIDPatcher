"""Link auxiliary VQM mask-0x40 PCM frames to loaded native sample spans."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys

from audit_aux_vqm_vdb import audit
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
    source = audit(args.explib / "VQM.vdb")
    if source["source_sha256"] != VQM_SHA256:
        raise AssertionError("auxiliary VQM source changed")
    rows = []
    for category, label, kind in ((2, "TENSE", 1), (3, "BRWSP", 3)):
        embedded = next(row for row in source["embedded_pcm_frames"]
                        if row["category"] == label)
        frames = source["by_section"][str(category - 2)]["derived_checks"]
        feature_frame_count = source["frames_by_section"][str(category - 2)]
        probe = Path(__file__).with_name("probe_vqm_loaded_aux_mix.py")
        command = [sys.executable, str(probe), "--vdm-dll", str(args.vdm_dll),
                   "--dse-dll", str(args.dse_dll), "--explib", str(args.explib),
                   "--scratch", str(args.scratch), "--output", str(args.output),
                   "--single-case", "--category", str(category), "--gain", "1"]
        child = subprocess.run(command, capture_output=True, text=True,
                               check=False, timeout=180)
        if child.returncode:
            raise RuntimeError(f"{label}: {child.stderr[-1300:] or child.returncode}")
        loaded = json.loads(child.stdout)
        span = loaded["source_end_seconds"] - loaded["source_start_seconds"]
        difference = span - embedded["duration_at_44100_seconds"]
        if (embedded["kind"] != kind or
                embedded["sample_count"] !=
                ((37888 if category == 2 else 99584)) or
                embedded["sample_count"] != feature_frame_count * 256 or
                abs(frames["time_seconds"]["max"] -
                    (feature_frame_count - 1) * 256 / 44100) > 1e-12 or
                abs(frames["time_step_seconds"]["min"] - 256 / 44100) > 1e-12 or
                abs(frames["time_step_seconds"]["max"] - 256 / 44100) > 1e-12 or
                abs(difference) > 1e-6 or
                loaded["state_record_phase_modes"] !=
                ([0, 0] if category == 2 else [1, 1])):
            raise AssertionError(f"auxiliary PCM/native span changed: {label}")
        rows.append({"category": label, "pcm_frame_kind": kind,
                     "sample_count": embedded["sample_count"],
                     "feature_frame_count": feature_frame_count,
                     "samples_per_feature_frame": 256,
                     "feature_frame_last_time_seconds": frames["time_seconds"]["max"],
                     "pcm_sha256": embedded["pcm_sha256"],
                     "pcm_peak_abs": embedded["peak_abs_sample"],
                     "native_sample_start_seconds": loaded["source_start_seconds"],
                     "native_sample_end_seconds": loaded["source_end_seconds"],
                     "native_span_seconds": span,
                     "sample_count_duration_seconds": embedded[
                         "duration_at_44100_seconds"],
                     "span_difference_seconds": difference,
                     "native_event_state_mode": loaded["state_record_phase_modes"]})
    report = {"dse_sha256": SHA256, "vdm_sha256": VDM_SHA256,
              "auxiliary_vqm_sha256": VQM_SHA256, "categories": rows,
              "scope": "The two mask-0x40 FRM2 chunks contain native-reader "
                       "16-bit PCM arrays. Each sample count equals the associated "
                       "feature-frame count times 256, and feature times follow a "
                       "256-sample hop at 44100 Hz. PCM durations match loaded "
                       "auxiliary sample spans within float32 precision. This "
                       "supports association, not a "
                       "claim that the PCM alone reproduces saved features. "
                       "No source PCM is exported or saved."}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
