"""Validate isolated borrowing traces/PCM and make common-gain listening copies.

NumPy is an analysis-only dependency. No per-clip normalization, alignment,
denoising, crossfade or other postprocessing is applied to the test renders.
"""
import argparse
import json
import wave
from pathlib import Path

import numpy as np


def read_wave(path):
    with wave.open(str(path), "rb") as stream:
        assert (stream.getframerate(), stream.getnchannels(), stream.getsampwidth()) == (44100, 1, 2)
        return np.frombuffer(stream.readframes(stream.getnframes()), "<i2").astype(np.float64)


def write_wave(path, samples):
    with wave.open(str(path), "wb") as stream:
        stream.setparams((1, 2, 44100, 0, "NONE", "not compressed"))
        stream.writeframes(np.rint(samples).astype("<i2").tobytes())


def analyze(root):
    old_rows = json.loads((root / "old-final/explicit-borrow.json").read_text(encoding="utf-8"))
    new_rows = json.loads((root / "new-final/explicit-borrow.json").read_text(encoding="utf-8"))
    assert old_rows[-1]["kind"] == new_rows[-1]["kind"] == "complete"
    baseline = read_wave(root / "new-final/baseline.wav")
    renders = [r for r in new_rows if r["kind"] == "render"]
    old = {r["mode"]: r for r in old_rows if r["kind"] == "render"}
    donor_ranges = {r["vowel"]: (r["first"], r["last"]) for r in new_rows if r["kind"] == "donor"}
    records = []
    for row in renders:
        name = row["mode"]
        signal = read_wave(root / "new-final" / row["file"])
        assert np.array_equal(signal, read_wave(root / "old-final" / old[name]["file"])), name
        assert row["phones"] == "s ei" and row["boundaries"] == [-25, -13, 477]
        assert row["scheduledFrameCount"] == 94
        if name != "warmup":
            assert row["scheduleUnchanged"]
        changed = np.flatnonzero(signal != baseline)
        if name.startswith("restore"):
            assert not len(changed), name
        for call in row["reads"]:
            first, last = donor_ranges[name.split("-")[0]]
            assert first <= call["Source"] <= last
            assert call["Frame"] in (3341, 3342)
        head = signal[round(1.474 * 44100):round(1.492 * 44100)]
        spectrum = np.abs(np.fft.rfft(head * np.hanning(len(head)), 8192)) ** 2
        frequency = np.fft.rfftfreq(8192, 1 / 44100)
        after = signal[round(1.5 * 44100):] - baseline[round(1.5 * 44100):]
        if row["reads"]:
            assert len(changed) > 0 and not np.any(after)
        records.append({
            "mode": name, "pcm_sha256": row["pcm"], "same_pcm_both_sdks": True,
            "schedule_unchanged": row["scheduleUnchanged"], "borrow_reads": len(row["reads"]),
            "scheduled_frame_count": row["scheduledFrameCount"],
            "changed_samples": len(changed),
            "changed_seconds": [int(changed[0]) / 44100, int(changed[-1]) / 44100] if len(changed) else None,
            "head_rms_lsb": float(np.sqrt(np.mean(head ** 2))),
            "head_peak_lsb": float(np.max(np.abs(head))),
            "head_power_above_4khz": float(spectrum[frequency > 4000].sum() / spectrum.sum()),
            "head_spectral_centroid_hz": float((frequency * spectrum).sum() / spectrum.sum()),
            "delta_samples_after_1_5_seconds": int(np.count_nonzero(after)),
            "render_ms": row["milliseconds"],
        })
    assert next(r for r in renders if r["mode"] == "7-full")["pcm"] == next(
        r for r in renders if r["mode"] == "7-full-repeat")["pcm"]
    listening = root / "listening"
    listening.mkdir(exist_ok=True)
    clips = [(name, read_wave(root / "new-final" / (name + ".wav"))[round(1.30 * 44100):round(2.20 * 44100)])
             for name in ("baseline", "7-mid", "a-mid")]
    peak = max(float(np.max(np.abs(signal))) for _, signal in clips)
    gain = min(16.0, 0.9 * 32767 / peak)
    for name, signal in clips:
        write_wave(listening / (name + ".wav"), signal * gain)
    combined = np.concatenate([part for _, signal in clips for part in (signal * gain, np.zeros(round(0.4 * 44100)))])
    write_wave(listening / "baseline-then-s7-then-sa.wav", combined)
    report = {
        "sdk_versions": [old_rows[0]["sdk"], new_rows[0]["sdk"]],
        "renders_per_sdk": len(renders), "donor_renders_per_sdk": 2,
        "protected_phonemes": "s ei", "protected_note_relative_ticks": [-25, -13, 477],
        "s_output_frames": 2, "s_output_milliseconds": 2 * 256 / 44100 * 1000,
        "read_source_range_7": donor_ranges["7"], "read_source_range_a": donor_ranges["a"],
        "listening": {"order": [name for name, _ in clips], "common_linear_gain": gain,
                      "crop_seconds": [1.30, 2.20], "per_clip_normalization": False,
                      "human_listening_verified": False},
        "cases": records,
    }
    (root / "analysis.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    analyze(parser.parse_args().directory)
