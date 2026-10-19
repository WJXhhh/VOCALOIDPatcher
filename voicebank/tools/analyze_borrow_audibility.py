"""Native s-only gain diagnostics after negative human listening feedback."""
import argparse
import json
from pathlib import Path

import numpy as np

from analyze_explicit_borrow import read_wave, write_wave


def analyze(root, previous):
    old = json.loads((root / "old-final/explicit-borrow.json").read_text(encoding="utf-8"))
    new = json.loads((root / "new-final/explicit-borrow.json").read_text(encoding="utf-8"))
    assert old[-1]["kind"] == new[-1]["kind"] == "complete"
    old_renders = {r["mode"]: r for r in old if r["kind"] == "render"}
    renders = {r["mode"]: r for r in new if r["kind"] == "render"}
    baseline = read_wave(root / "new-final/baseline.wav")
    assert np.array_equal(baseline, read_wave(previous / "new-final/baseline.wav"))
    summaries = []
    for name, row in renders.items():
        s_frames = row["sFrames"]
        same_timing_baseline = baseline if s_frames == 2 else read_wave(root / "new-final" / ("baseline-s" + str(s_frames) + ".wav"))
        signal = read_wave(root / "new-final" / row["file"])
        assert np.array_equal(signal, read_wave(root / "old-final" / old_renders[name]["file"]))
        assert row["phones"] == "s ei"
        assert row["boundaries"] == ([-25, -13, 477] if s_frames == 2 else renders["baseline-s" + str(s_frames)]["boundaries"])
        assert row["scheduledFrameCount"] == 94 + s_frames - 2
        if name != "warmup":
            assert row["scheduleUnchanged"]
        if name == "restore-final":
            assert np.array_equal(signal, baseline)
        if name in ("7-mid", "a-mid"):
            assert np.array_equal(signal, read_wave(previous / "new-final" / row["file"]))
        gain = json.loads((root / "new-final" / (name + "-gain.json")).read_text(encoding="utf-8"))
        multiplier = 64 if "-g64" in name else 16 if "-g16" in name else 4 if "-g4" in name else 1
        for frame in gain:
            actual_multiplier = multiplier if 3343 - s_frames <= frame["Time"] < 3343 and frame["Selected"] else 1
            assert abs(frame["AppliedGain"] - frame["OriginalGain"] * actual_multiplier) < 0.0001
        head = signal[round(1.474 * 44100):round(1.492 * 44100)]
        changed = np.flatnonzero(signal != same_timing_baseline)
        after = signal[round(1.5 * 44100):] - same_timing_baseline[round(1.5 * 44100):]
        if row["reads"]:
            assert len(changed)
            if s_frames == 2:
                assert not np.any(after)
            reference_file = name[0] + "-mid-gain.json" if s_frames == 2 else name[0] + "-full-s" + str(s_frames) + "-gain.json"
            reference = json.loads((root / "new-final" / reference_file).read_text())
            assert [(f["Time"], f["Lane"], f["InputHash"], f["OriginalGain"]) for f in gain] == [
                (f["Time"], f["Lane"], f["InputHash"], f["OriginalGain"]) for f in reference]
        assert not np.any(np.abs(signal) >= 32767), "Clipped diagnostic: " + name
        s_window = signal[max(0, (3343 - s_frames - 3087) * 256 - 256):(3343 - 3087) * 256 + 256]
        summaries.append({
            "mode": name, "cross_sdk_pcm_equal": True, "same_context_schedule": row["scheduleUnchanged"],
            "s_output_frames": s_frames, "duration_diagnostic_only": row["durationDiagnosticOnly"],
            "boundaries": row["boundaries"], "s_window_rms_lsb": float(np.sqrt(np.mean(s_window ** 2))),
            "post_1_5_delta_samples": int(np.count_nonzero(after)), "post_1_5_delta_peak_lsb": float(np.max(np.abs(after))),
            "post_1_5_delta_rms_lsb": float(np.sqrt(np.mean(after ** 2))),
            "s_contribution_multiplier": multiplier, "head_rms_lsb": float(np.sqrt(np.mean(head ** 2))),
            "head_peak_lsb": float(np.max(np.abs(head))), "whole_peak_lsb": float(np.max(np.abs(signal))),
            "different_pcm_samples": len(changed),
            "different_seconds": [float(changed[0] / 44100), float(changed[-1] / 44100)] if len(changed) else None,
            "s_native_gain": [{k: f[k] for k in ("Time", "Lane", "OriginalGain", "AppliedGain")}
                              for f in gain if f["Selected"]],
        })
    assert np.array_equal(read_wave(root / "new-final/7-mid-g16.wav"), read_wave(root / "new-final/7-mid-g16-repeat.wav"))
    names = ["baseline", "7-mid", "7-mid-g4", "7-mid-g16"]
    clips = [read_wave(root / "new-final" / (n + ".wav"))[round(1.3 * 44100):round(2.2 * 44100)] for n in names]
    donor = read_wave(root / "new-final/donor-s-7.wav")[round(1.3 * 44100):round(2.2 * 44100)]
    common_gain = min(4.0, 0.85 * 32767 / max(float(np.max(np.abs(x))) for x in clips + [donor]))
    listening = root / "listening"
    listening.mkdir(exist_ok=True)
    for name, clip in zip(names, clips):
        write_wave(listening / (name + ".wav"), clip * common_gain)
    write_wave(listening / "native-s7-control.wav", donor * common_gain)
    combined = np.concatenate([part for clip in clips for part in (clip * common_gain, np.zeros(round(0.4 * 44100)))])
    write_wave(listening / "baseline-borrow-plus12-plus24.wav", combined)
    duration_names = ["7-mid", "7-full-s6", "7-full-s12", "7-full-s22"]
    duration_clips = [read_wave(root / "new-final" / (n + ".wav"))[round(1.3 * 44100):round(2.2 * 44100)] for n in duration_names]
    duration_combined = np.concatenate([part for clip in duration_clips for part in (clip * common_gain, np.zeros(round(0.4 * 44100)))])
    write_wave(listening / "diagnostic-only-12-35-70-128ms.wav", duration_combined)
    report = {
        "date": "2026-10-18", "previous_user_feedback": "s is not audible",
        "status": "Not accepted as intelligible s; native contribution gain diagnostic only.",
        "sdk_versions": [old[0]["sdk"], new[0]["sdk"]], "target_renders_per_sdk": len(renders),
        "donor_renders_per_sdk": 2, "s_output_frames": 2, "s_output_ms": 2 * 256 / 44100 * 1000,
        "gain_applied_before_native_ola_only_at": [3341, 3342],
        "phonemes": "s ei", "boundaries": [-25, -13, 477],
        "listening_order": names, "listening_common_gain": common_gain,
        "duration_listening_order": duration_names,
        "new_human_listening_passed": False, "cases": summaries,
    }
    (root / "analysis.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("previous", type=Path)
    args = parser.parse_args()
    analyze(args.directory, args.previous)
