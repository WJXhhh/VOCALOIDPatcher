"""Compare fixed and time-varying F0 projections against saved V4X bit16.

Reads installed V4X material without writing it. Only aggregated numerical
errors and input hashes are stored; no PCM, frame, or pitch trajectory payload.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import statistics

from audit_frm2_numeric import Native, SHA256
from verify_frm2_analysis_windows import window
from verify_vqm_bit16_spectral_power import (load_sequences, predict,
                                              summarize_errors)


SAMPLE_RATE = 44100
HOP = 256
MARGIN = 4
FREQUENCY_BIASES_HZ = (-1., -.5, -.25, 0., .25, .5, 1.)


def phase_projection(sequence: dict, index: int, weights: list[float],
                     mode: str) -> float:
    pcm = sequence["pcm"]
    frames = sequence["frames"]
    size = len(weights)
    start = index * HOP - size // 2
    samples = pcm[start:start + size]
    if len(samples) != size:
        raise ValueError("incomplete PCM window")
    mean = statistics.fmean(samples)
    phase = 0.0
    real = imag = 0.0
    for offset, (sample, weight) in enumerate(zip(samples, weights)):
        absolute = start + offset
        coordinate = absolute / HOP
        left = math.floor(coordinate)
        fraction = coordinate - left
        if mode == "constant":
            frequency = frames[index]["f0"]
        elif mode == "nearest":
            slot = min(max(math.floor(coordinate + 0.5), 0), len(frames) - 1)
            frequency = frames[slot]["f0"]
        elif mode == "left":
            slot = min(max(left, 0), len(frames) - 1)
            frequency = frames[slot]["f0"]
        elif mode == "linear":
            left = min(max(left, 0), len(frames) - 2)
            frequency = ((1.0 - fraction) * frames[left]["f0"] +
                         fraction * frames[left + 1]["f0"])
        else:
            raise ValueError("unknown trajectory")
        phase += math.tau * frequency / SAMPLE_RATE
        value = (sample - mean) * weight
        real += value * math.cos(phase)
        imag -= value * math.sin(phase)
    magnitude = math.hypot(real, imag)
    if magnitude <= 0 or not math.isfinite(magnitude):
        raise AssertionError("invalid trajectory projection")
    return 2.0 * math.log10(magnitude / 32768.0)


def run(manifest: Path, voice_root: Path, parser_root: Path,
        dll: Path) -> dict:
    parser_sha, ddi_sha, sequences = load_sequences(
        manifest, voice_root, parser_root)
    weights = window(Native(dll), 2048, 10)
    per_sample = []
    pooled = {name: [] for name in ("fixed", "nearest", "left", "linear")}
    biased_pooled = {bias: [] for bias in FREQUENCY_BIASES_HZ}
    for sample_index, sequence in enumerate(sequences):
        frames = sequence["frames"]
        errors = {name: [] for name in pooled}
        biased_errors = {bias: [] for bias in FREQUENCY_BIASES_HZ}
        for index in range(MARGIN, len(frames) - MARGIN):
            saved = frames[index]["bit16"]
            fixed = predict(sequence["pcm"], index,
                            frames[index]["f0"], weights)
            check = phase_projection(sequence, index, weights, "constant")
            if abs(check - fixed) > 1e-10:
                raise AssertionError("phase-integral projection disagrees with fixed control")
            errors["fixed"].append(fixed - saved)
            for bias in FREQUENCY_BIASES_HZ:
                biased_errors[bias].append(
                    (fixed if bias == 0. else
                     predict(sequence["pcm"], index,
                             frames[index]["f0"] + bias, weights)) - saved)
            for name in ("nearest", "left", "linear"):
                errors[name].append(
                    phase_projection(sequence, index, weights, name) - saved)
        per_sample.append({
            "sample_index": sample_index,
            "evaluated_frames": len(errors["fixed"]),
            "models": {name: summarize_errors(values)
                       for name, values in errors.items()},
            "better_than_fixed_count": {
                name: sum(abs(a) < abs(b) for a, b in
                          zip(errors[name], errors["fixed"]))
                for name in ("nearest", "left", "linear")},
            "frequency_bias_mae": {
                str(bias): summarize_errors(values)["mean_absolute_error"]
                for bias, values in biased_errors.items()},
        })
        for name in pooled:
            pooled[name].extend(errors[name])
        for bias in FREQUENCY_BIASES_HZ:
            biased_pooled[bias].extend(biased_errors[bias])
    if len(pooled["fixed"]) != 222:
        raise AssertionError("saved VQM interior sample count changed")
    return {
        "dse_sha256": SHA256,
        "ddi_sha256": ddi_sha,
        "parser_sha256": parser_sha,
        "window": "native kind-10, 2048 samples",
        "model_definitions": {
            "fixed": "single saved F0 at frame center",
            "nearest": "phase integral of nearest saved F0 at each sample",
            "left": "phase integral of left saved F0 at each sample",
            "linear": "phase integral of linearly interpolated saved F0 at each sample",
        },
        "pooled": {name: summarize_errors(values)
                   for name, values in pooled.items()},
        "fixed_frequency_bias_mae": {
            str(bias): summarize_errors(values)["mean_absolute_error"]
            for bias, values in biased_pooled.items()},
        "per_sample": per_sample,
        "scope": "Read-only comparison on 222 complete interior V4X VQM windows; "
                 "does not fit any coefficients or use proprietary payload in report.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--voice-root", type=Path, required=True)
    parser.add_argument("--ddb-tools", type=Path, required=True)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = run(args.manifest, args.voice_root, args.ddb_tools, args.dll)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
