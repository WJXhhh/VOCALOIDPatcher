"""Verify complete-source, fixed-allocation native synthesis experiments."""
import argparse
import json
import struct
from pathlib import Path

import numpy as np

from analyze_explicit_borrow import read_wave, write_wave


def analyze(root):
    old = json.loads((root / "old-final/explicit-borrow.json").read_text())
    new = json.loads((root / "new-final/explicit-borrow.json").read_text())
    assert old[-1]["kind"] == new[-1]["kind"] == "complete"
    originals = {r["mode"]: r for r in old if r["kind"] == "render"}
    cases = {r["mode"]: r for r in new if r["kind"] == "render"}
    signals = {n: read_wave(root / "new-final" / r["file"]) for n, r in cases.items()}
    waves = sorted((root / "old-final").glob("*.wav"))
    for path in waves:
        assert np.array_equal(read_wave(path), read_wave(root / "new-final" / path.name)), path.name
    sources = []
    for row in (r for r in new if r["kind"] == "complete-source"):
        vowel = row["Vowel"]
        indices = [struct.unpack("<f", struct.pack("<i", f["SourceBits"]))[0] for f in row["sourceFrames"]]
        assert indices == list(range(row["first"], row["last"] + 1))
        reference = read_wave(root / "new-final" / ("complete-source-" + vowel + "-reference.wav"))
        muted = read_wave(root / "new-final" / ("complete-source-" + vowel + "-muted.wav"))
        restored = read_wave(root / "new-final" / ("complete-source-" + vowel + "-restored.wav"))
        assert np.array_equal(reference, restored)
        difference = reference - muted
        begin, end = row["supportFirstSample"], row["supportLastSample"]
        assert not np.any(difference[:begin]) and not np.any(difference[end:])
        sources.append({"vowel": vowel, "all_native_source_indices": indices, "frames": len(indices),
                        "native_gain_mute_restores_exactly": True, "isolated_samples": end - begin,
                        "donor_vowel_outside_support_delta_samples": 0})
    before = (3341 - 3087) * 256
    summaries = []
    for name, row in cases.items():
        assert row["phones"] == "s ei" and row["boundaries"] == [-25, -13, 477]
        assert row["scheduledFrameCount"] == 94
        if name != "warmup":
            assert row["allocationUnchanged"]
            if not row["seamAdvance"]:
                assert row["scheduleUnchanged"]
        assert row["pcm"] == originals[name]["pcm"]
        trace = json.loads((root / "new-final" / (name + "-gain.json")).read_text())
        assert all(f["OriginalGain"] == f["AppliedGain"] for f in trace)
        if "-pool" in name:
            entries = row["fullSource"]
            assert {f["Source"] for f in entries} == set(range(22 if name[0] == "7" else 23))
            for batch in {f["Batch"] for f in entries}:
                assert abs(sum(f["Weight"] for f in entries if f["Batch"] == batch) - 1) < 1e-12
        signal = signals[name]
        assert not np.any(np.abs(signal) >= 32767)
        base_delta = signal - signals["baseline"]
        if name != "warmup" and not row["seamAdvance"]:
            assert not np.any(base_delta[round(1.5 * 44100):])
        summary = {"mode": name, "same_94_frame_allocation": row["allocationUnchanged"],
                   "original_source_map_unchanged": row["scheduleUnchanged"], "ei_entry_advance": row["seamAdvance"],
                   "ei_source_map_writes": row["seamWrites"],
                   "cross_sdk_pcm_equal": True, "native_gain_unchanged": True,
                   "native_packets_applied": row["nativePacketsApplied"],
                   "post_1_5_seconds_delta_samples": int(np.count_nonzero(base_delta[round(1.5 * 44100):]))}
        expected_file = root / (name + "-expected.npy")
        if expected_file.exists():
            expected = np.load(expected_file)
            zero = "7-grain-layer-zero" if "-layer" in name else name[0] + "-grain-zero"
            delta = signal - signals[zero]
            actual = delta[before:before + len(expected)]
            error = actual - expected
            assert np.max(np.abs(error)) <= 1.05
            assert not np.any(delta[:before]) and not np.any(delta[before + len(expected):])
            reference = (signals[name[0] + "-mid"] - signals[name[0] + "-grain-zero"])[before:before + len(expected)]
            fit = np.dot(actual, reference) / max(1, np.dot(reference, reference))
            residual = np.linalg.norm(actual - fit * reference) / max(1e-12, np.linalg.norm(actual))
            assert residual > 0.9, "Not a distinct temporal synthesis: " + name
            summary.update({"output_samples": len(expected), "strict_two_hops": len(expected) == 512,
                            "microprocess_rms_lsb": float(np.sqrt(np.mean(actual ** 2))),
                            "native_reconstruction_peak_error_lsb": float(np.max(np.abs(error))),
                            "native_reconstruction_rms_error_lsb": float(np.sqrt(np.mean(error ** 2))),
                            "added_contribution_samples_outside_designed_support": 0,
                            "best_scalar_fit_residual_vs_old_slice": float(residual),
                            "total_delta_peak_outside_strict_two_hops_lsb": float(np.max(np.abs(base_delta[before + 512:])))})
        summaries.append(summary)
    for vowel in ("7", "a"):
        assert np.array_equal(signals[vowel + "-mid"], signals[vowel + "-reread"])
        assert np.array_equal(signals[vowel + "-mid"], signals[vowel + "-grain-identity"])
    assert np.array_equal(signals["7-grain-identity"], signals["7-grain-identity-repeat"])
    assert np.array_equal(signals["7-pool"], signals["7-pool-repeat"])
    for name in ("restore-final", "restore-grains"):
        assert np.array_equal(signals[name], signals["baseline"])
    assert np.array_equal(signals["7-grain-layer-zero"], signals["baseline"])
    assert np.array_equal(signals["feed-control"], signals["baseline"])
    preparation = json.loads((root / "micrograin-preparation.json").read_text())
    assert all(r["all_source_cells_covered"] and all(abs(c["weight_sum"] - 1) < 1e-12 for c in r["cells"]) for r in preparation)
    names = ["baseline", "7-mid", "7-grain-layer128-n512", "7-grain-layer128-n512-seam",
             "a-grain-layer128-n512", "a-grain-layer128-n512-seam"]
    clips = [signals[n][round(1.3 * 44100):round(2.2 * 44100)] for n in names]
    gain = min(16., .9 * 32767 / max(float(np.max(np.abs(c))) for c in clips))
    listening = root / "listening"
    listening.mkdir(exist_ok=True)
    for name, clip in zip(names, clips):
        write_wave(listening / (name + ".wav"), clip * gain)
    combined = np.concatenate([p for clip in clips for p in (clip * gain, np.zeros(round(.4 * 44100)))])
    write_wave(listening / "original-slice-complete7-joined7-completea-joineda.wav", combined)
    report = {"date": "2026-10-18", "sdk_versions": [old[0]["sdk"], new[0]["sdk"]],
              "target_renders_per_sdk": len(cases), "all_wav_pcm_cross_sdk_equal": len(waves),
              "status": "Complete-source mapping verified, but user listening rejected the target: s remains inaudible.",
              "preserved_phonemes": "s ei", "preserved_note_relative_ticks": [-25, -13, 477],
              "s_output_frames": 2, "strict_s_output_samples": 512,
              "native_synthesis": {"hop": 256, "fft": 2048, "inverse_scale": 1, "bins": 1025},
              "sources": sources, "microprocess": [{k: v for k, v in r.items() if k != "cells"} for r in preparation],
              "cases": summaries, "listening_order": names, "common_playback_gain": gain,
              "individual_s_gain_adjustment": False, "human_listening_passed": False,
              "human_listening_feedback": "User reports s is inaudible in the target, including complete-source and seam variants.",
              "live_editor_query": "No MCP-enabled instance available; no Editor was launched.",
              "production_integration": False, "frontend": False, "deployed": False}
    (root / "analysis.json").write_text(json.dumps(report, indent=2))
    print(json.dumps({k: v for k, v in report.items() if k not in ("cases", "microprocess")}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    analyze(parser.parse_args().directory)
