"""Audit the circular time behavior of saved V4X VQM feature bit17.

Installed PCM and frames are read only. The report contains counts and bounded
aggregate statistics, never commercial frame sequences or PCM samples.
"""

from __future__ import annotations

import argparse
import cmath
import json
import math
from pathlib import Path
import statistics

from audit_frm2_numeric import Native, SHA256
from verify_frm2_analysis_windows import window
from verify_vqm_bit16_spectral_power import load_sequences


def circular_projection_controls(sequence: dict,
                                 weights: list[float]) -> dict:
    frames = sequence["frames"]
    pcm = sequence["pcm"]
    candidates = {"local_f0_projection": [],
                  "projection_minus_cumulative_f0": []}
    saved = []
    cumulative_cycles = 0.0
    for frame_index, frame in enumerate(frames):
        if 4 <= frame_index < len(frames) - 4:
            start = frame_index * 256 - 1024
            samples = pcm[start:start + 2048]
            mean = statistics.fmean(samples)
            projection = sum(
                (sample - mean) * weight
                * cmath.exp(-1j * math.tau * frame["f0"] * index / 44100)
                for index, (sample, weight) in enumerate(zip(samples, weights))
            )
            local_phase = cmath.phase(projection)
            candidates["local_f0_projection"].append(local_phase)
            candidates["projection_minus_cumulative_f0"].append(
                local_phase - math.tau * cumulative_cycles)
            saved.append(frame["features"][17])
        cumulative_cycles += frame["f0"] * 256 / 44100
    scores = {}
    for name, phases in candidates.items():
        alternatives = []
        for sign in (1, -1):
            deltas = [math.remainder(observed - sign * predicted, math.tau)
                      for observed, predicted in zip(saved, phases)]
            mean_vector = sum(cmath.exp(1j * value)
                              for value in deltas) / len(deltas)
            offset = cmath.phase(mean_vector)
            errors = [abs(math.remainder(value - offset, math.tau))
                      for value in deltas]
            alternatives.append({"phase_sign": sign,
                                 "circular_mean_absolute_error":
                                     statistics.fmean(errors),
                                 "circular_concentration": abs(mean_vector)})
        scores[name] = min(alternatives,
                           key=lambda row: row["circular_mean_absolute_error"])
    return {"compared_full_window_frames": len(saved), "candidates": scores}


def run(manifest: Path, voice_root: Path, parser_root: Path,
        dll: Path) -> dict:
    parser_hash, ddi_hash, sequences = load_sequences(
        manifest, voice_root, parser_root)
    weights = window(Native(dll), 2048, 10)
    samples = []
    for sample_index, sequence in enumerate(sequences):
        values = [frame["features"][17] for frame in sequence["frames"]]
        raw = [right - left for left, right in zip(values, values[1:])]
        circular = [math.remainder(value, math.tau) for value in raw]
        near_limit = sum(abs(abs(value) - 0.25) <= 1e-5
                         for value in circular)
        wraps = [{"frame_index": index + 1,
                  "raw_delta": value,
                  "circular_delta": circular[index]}
                 for index, value in enumerate(raw) if abs(value) > math.pi]
        if (not all(0 <= value < math.tau for value in values)
                or any(abs(value) > 0.25001 for value in circular)):
            raise AssertionError("saved VQM bit17 circular boundary changed")
        samples.append({
            "sample_index": sample_index,
            "frames": len(values),
            "bit17_min": min(values),
            "bit17_median": statistics.median(values),
            "bit17_max": max(values),
            "adjacent_pairs": len(circular),
            "max_raw_delta": max(abs(value) for value in raw),
            "max_circular_delta": max(abs(value) for value in circular),
            "median_absolute_circular_delta": statistics.median(
                abs(value) for value in circular),
            "near_quarter_radian_step_count": near_limit,
            "wraps": wraps,
            "projection_controls": circular_projection_controls(sequence,
                                                               weights),
        })
    if (sum(item["frames"] for item in samples) != 246
            or sum(item["adjacent_pairs"] for item in samples) != 243
            or sum(len(item["wraps"]) for item in samples) != 1):
        raise AssertionError("independent VQM sequence coverage changed")
    return {
        "dse_sha256": SHA256,
        "ddi_sha256": ddi_hash,
        "parser_sha256": parser_hash,
        "samples": samples,
        "scope": "The saved field behaves as a wrapped, quarter-radian-step-limited angle. Direct 2048-point PCM fundamental projection controls do not exactly reproduce it, even after an optimized per-sample rotation and sign choice. Its producer, exact phase convention, and consumer remain unproven.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--voice-root", type=Path, required=True)
    parser.add_argument("--ddb-tools", type=Path, required=True)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.manifest, args.voice_root, args.ddb_tools, args.dll)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"samples": len(report["samples"]),
                      "frames": sum(item["frames"] for item in report["samples"]),
                      "adjacent_pairs": sum(item["adjacent_pairs"]
                                            for item in report["samples"])}))


if __name__ == "__main__":
    main()
