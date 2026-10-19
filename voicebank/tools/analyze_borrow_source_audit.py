"""Donor lane/split audit; listening controls are diagnostic, not target fixes."""
import argparse
import json
from pathlib import Path

import numpy as np

from analyze_explicit_borrow import read_wave, write_wave


def describe(signal):
    active = np.flatnonzero(signal)
    spectrum = np.abs(np.fft.rfft(signal)) ** 2
    frequency = np.fft.rfftfreq(len(signal), 1 / 44100)
    return {"rms_lsb": float(np.sqrt(np.mean(signal ** 2))),
            "peak_lsb": float(np.max(np.abs(signal))),
            "changed_samples": len(active),
            "support_samples": [int(active[0]), int(active[-1]) + 1] if len(active) else None,
            "power_below_4khz": float(spectrum[frequency < 4000].sum() / max(spectrum.sum(), 1e-20))}


def analyze(root, previous):
    rows = json.loads((root / "old/source-audit.json").read_text(encoding="utf-8"))
    old_metadata = json.loads((root / "old/explicit-borrow.json").read_text(encoding="utf-8"))[0]
    assert old_metadata["sdk"] == "6.13.1.1"
    new = root / "new"
    versions = [old_metadata["sdk"]]
    if new.exists():
        new_metadata = json.loads((new / "explicit-borrow.json").read_text(encoding="utf-8"))[0]
        assert new_metadata["sdk"] == "6.13.3.1"
        versions.append(new_metadata["sdk"])
        assert rows == json.loads((new / "source-audit.json").read_text(encoding="utf-8"))
        for path in (root / "old").glob("*.wav"):
            assert np.array_equal(read_wave(path), read_wave(new / path.name)), path.name
    findings, clips = [], []
    isolated_sources = {}
    for vowel in ("7", "a"):
        group = [r for r in rows if r["vowel"] == vowel]
        cases = {r["pass"]: read_wave(root / "old" / r["file"]) for r in group}
        row = next(r for r in group if r["pass"] == "reference")
        reference = cases["reference"]
        assert np.array_equal(reference, cases["restored"])
        assert np.array_equal(reference, cases["auxiliary-muted"])
        assert np.array_equal(cases["left-muted"], cases["primary-muted"])
        begin, split, end = ((row[k] - 3087) * 256 for k in ("begin", "split", "end"))
        trace = json.loads((root / "old" / ("source-" + vowel + "-reference-gain.json")).read_text(encoding="utf-8"))
        left = [f for f in trace if f["Selected"] and f["Time"] < row["split"]]
        assert left and {f["Lane"] for f in left} == {"primary"}
        parts = {name: reference - cases[name] for name in
                 ("left-muted", "right-first4-muted", "right-late-muted", "all-muted")}
        isolated_sources[vowel] = parts["left-muted"][begin:split + 256]
        for name, (first, last) in {"left-muted": (begin, split + 256),
                                  "right-first4-muted": (split, split + 5 * 256),
                                  "right-late-muted": (split + 4 * 256, end + 256)}.items():
            assert not np.any(parts[name][:first]) and not np.any(parts[name][last:])
        # Integer PCM quantization permits tiny errors when summing differences.
        additive = parts["all-muted"] - sum(parts[n] for n in
                    ("left-muted", "right-first4-muted", "right-late-muted"))
        assert np.max(np.abs(additive)) <= 2
        findings.append({"vowel": vowel, "left_native_lanes": sorted({f["Lane"] for f in left}),
                         "left_primary_calls": len(left), "auxiliary_left_mute_pcm_equal_reference": True,
                         "left_all_lanes_mute_pcm_equal_primary_mute": True,
                         "additive_quantization_error_lsb": float(np.max(np.abs(additive))),
                         "left": describe(parts["left-muted"][begin:split + 256]),
                         "right_first4": describe(parts["right-first4-muted"][split:split + 5 * 256]),
                         "right_late": describe(parts["right-late-muted"][split + 4 * 256:end + 256])})
        # No time compression or relative gain change. Full CV and isolated left
        # use exactly the same crop and playback gain as the target controls.
        clips.extend([(vowel + "-native", reference), (vowel + "-isolated-left", parts["left-muted"])])
    clips.extend([(name, read_wave(previous / "new-final" / (name + ".wav")))
                  for name in ("baseline", "7-grain-layer128-n512")])
    clips = [(name, signal[round(1.25 * 44100):round(2.15 * 44100)]) for name, signal in clips]
    gain = min(16., .9 * 32767 / max(float(np.max(np.abs(x))) for _, x in clips))
    listening = root / "listening"
    listening.mkdir(exist_ok=True)
    for name, signal in clips:
        write_wave(listening / (name + ".wav"), signal * gain)
    write_wave(listening / "native7-isolated7-nativea-isolateda-target-compressed.wav",
               np.concatenate([part for _, clip in clips for part in (clip * gain, np.zeros(22050))]))
    baseline = read_wave(previous / "new-final/baseline.wav")
    target = read_wave(previous / "new-final/7-grain-layer128-n512.wav")
    first = (3341 - 3087) * 256
    isolated = target - baseline
    assert not np.any(isolated[:first]) and not np.any(isolated[first + 512:])
    compact = isolated[first:first + 512]
    # Offline listening ablation only: move the following vowel later to test
    # masking. This changes acoustic timing and is explicitly not a candidate
    # satisfying the fixed-timing requirement, nor an engine-rendered fix.
    pad = np.zeros(round(.15 * 44100))
    original_clip = isolated_sources["7"]
    attached = target[first:round(2.15 * 44100)]
    gap_control = np.concatenate((compact, np.zeros(round(.08 * 44100)), attached[512:]))
    ablations = [("uncompressed-s", original_clip), ("compact-s-only", compact),
                 ("compact-s-with-ei", attached), ("compact-s-with-ei-after-80ms-gap", gap_control)]
    write_wave(listening / "duration-and-masking-diagnostic.wav", np.concatenate([
        part for _, signal in ablations for part in (pad, signal * gain, np.zeros(round(.5 * 44100)))]))
    for name, signal in ablations:
        write_wave(listening / (name + ".wav"), np.concatenate((pad, signal * gain, pad)))
    feedback_file = root / "listening-feedback.json"
    feedback = json.loads(feedback_file.read_text(encoding="utf-8-sig")) if feedback_file.exists() else None
    ablation_feedback_file = root / "ablation-listening-feedback.json"
    ablation_feedback = json.loads(ablation_feedback_file.read_text(encoding="utf-8-sig")) if ablation_feedback_file.exists() else None
    report = {"sdk_versions": versions, "source_audit": findings, "cross_sdk_pcm_equal": new.exists(),
              "audit_renders_per_sdk": len(rows), "preparation_renders_per_sdk": 2,
              "listening_order": [name for name, _ in clips], "common_playback_gain": gain,
              "source_controls_are_uncompressed": True, "is_target_fix": False,
              "prior_target_listening_feedback": "User reports s remains inaudible.",
              "source_control_human_listening": feedback or "not yet evaluated",
              "compact_vs_native_s": {"source_samples": len(original_clip), "target_samples": len(compact),
                  "source_rms_lsb": float(np.sqrt(np.mean(original_clip ** 2))),
                  "target_rms_lsb": float(np.sqrt(np.mean(compact ** 2))),
                  "total_energy_ratio": float(np.dot(compact, compact) / np.dot(original_clip, original_clip)),
                  "interpretation": "Duration and energy change together; not proof of a perceptual minimum duration."},
              "ablation": {"order": [name for name, _ in ablations], "offline_diagnostic_only": True,
                  "gap_changes_vowel_timing": True, "human_listening_passed": False,
                  "human_listening_feedback": ablation_feedback or "not yet evaluated"},
              "frontend": False, "deployed": False}
    (root / "analysis.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("previous", type=Path)
    args = parser.parse_args()
    analyze(args.directory, args.previous)
