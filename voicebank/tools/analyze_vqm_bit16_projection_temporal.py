"""Test whether temporal smoothing closes saved VQM bit16 projection residuals.

All installed PCM and frame data remain in memory. The report contains only
aggregate errors, fitted weights and fixed-kernel comparison results.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import statistics

from audit_frm2_numeric import Native, SHA256
from analyze_vqm_bit18_temporal import fit_symmetric_five
from verify_frm2_analysis_windows import window
from verify_vqm_bit16_spectral_power import load_sequences, predict


MARGIN = 4


def smooth(values: list[float], index: int, weights: list[float],
           domain: str) -> float:
    radius = len(weights) // 2
    total = sum(weights)
    neighbors = [values[index + offset - radius]
                 for offset in range(len(weights))]
    if domain == "log_amplitude":
        return sum(value * weight for value, weight
                   in zip(neighbors, weights)) / total
    divisor = 2 if domain == "linear_amplitude" else 1
    average = sum(10 ** (value / divisor) * weight
                  for value, weight in zip(neighbors, weights)) / total
    if average <= 0:
        raise AssertionError("smoothed positive spectral quantity vanished")
    return divisor * math.log10(average)


def metrics(source: list[float], saved: list[float],
            weights: list[float], domain: str) -> dict:
    residuals = [smooth(source, index, weights, domain) - saved[index]
                 for index in range(MARGIN, len(saved) - MARGIN)]
    return {
        "frames": len(residuals),
        "mean_signed_error": statistics.fmean(residuals),
        "mean_absolute_error": statistics.fmean(map(abs, residuals)),
        "max_absolute_error": max(map(abs, residuals)),
    }


def run(manifest: Path, voice_root: Path, parser_root: Path,
        dll: Path) -> dict:
    parser_hash, ddi_hash, sequences = load_sequences(
        manifest, voice_root, parser_root)
    weights = window(Native(dll), 2048, 10)
    series = []
    for sequence in sequences:
        frames = sequence["frames"]
        source = [predict(sequence["pcm"], index,
                          frames[index]["f0"], weights)
                  for index in range(4, len(frames) - 4)]
        saved = [frame["bit16"] for frame in frames[4:-4]]
        series.append((source, saved, []))
    frequency_controls = []
    for mode in ("frame_f0", "first_component_frequency", "pitch1"):
        per_sample = []
        for sequence in sequences:
            frames = sequence["frames"]
            source = []
            for index in range(4, len(frames) - 4):
                frequency = (frames[index]["f0"] if mode == "frame_f0"
                             else frames[index]["first_component_frequency"]
                             if mode == "first_component_frequency"
                             else sequence["pitch1"])
                if frequency is None or frequency <= 0:
                    raise AssertionError("VQM projection frequency missing")
                source.append(predict(sequence["pcm"], index,
                                      frequency, weights))
            saved = [frame["bit16"] for frame in frames[4:-4]]
            per_sample.append(metrics(source, saved, [1], "log_amplitude"))
        frequency_controls.append({"frequency": mode,
                                   "per_sample": per_sample})
    component_pitch_differences = [{
        "sample_index": index,
        "different_float_count": sum(
            frame["first_component_frequency"] != frame["f0"]
            for frame in sequence["frames"]),
        "max_absolute_difference_hz": max(
            abs(frame["first_component_frequency"] - frame["f0"])
            for frame in sequence["frames"]),
    } for index, sequence in enumerate(sequences)]
    controls = []
    kernels = {
        "identity": [1],
        "three_mean": [1, 1, 1],
        "three_binomial": [1, 2, 1],
        "five_mean": [1, 1, 1, 1, 1],
        "five_binomial": [1, 4, 6, 4, 1],
        "five_1_3_4_3_1": [1, 3, 4, 3, 1],
        "nine_gaussian_sigma_1_1": [
            math.exp(-0.5 * (offset / 1.1) ** 2)
            for offset in range(-4, 5)],
    }
    for name, kernel in kernels.items():
        for domain in ("log_amplitude", "linear_amplitude",
                       "linear_power"):
            controls.append({
                "kernel": name,
                "domain": domain,
                "per_sample": [metrics(source, saved, kernel, domain)
                               for source, saved, _ in series],
            })
    holdouts = []
    for held_out in range(len(series)):
        fitted = fit_symmetric_five(series, held_out)
        source, saved, _ = series[held_out]
        holdouts.append({
            "held_out_sample_index": held_out,
            "trained_on_sample_indices": [index for index in range(3)
                                          if index != held_out],
            "symmetric_five_weights": fitted,
            "identity": metrics(source, saved, [1], "log_amplitude"),
            "fitted": metrics(source, saved, fitted, "log_amplitude"),
        })
    return {
        "dse_sha256": SHA256,
        "ddi_sha256": ddi_hash,
        "parser_sha256": parser_hash,
        "projection": "2048-point kind-10 DSE window, each saved frame F0, 2log10(amplitude/32768)",
        "common_interior_margin_frames": 8,
        "frequency_controls": frequency_controls,
        "component_pitch_differences": component_pitch_differences,
        "fixed_kernel_controls": controls,
        "leave_one_sequence_out_fits": holdouts,
        "scope": "Three independent VQM sequences from one bank. Tests smoothing the already close PCM projection in log amplitude, linear amplitude or linear power domains. Fitted weights are diagnostic; nonzero residuals do not identify original authoring state.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--voice-root", required=True, type=Path)
    parser.add_argument("--ddb-tools", required=True, type=Path)
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = run(args.manifest, args.voice_root, args.ddb_tools,
                 args.dll)
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({
        "identity_mae": [row["identity"]["mean_absolute_error"]
                         for row in report["leave_one_sequence_out_fits"]],
        "fitted_mae": [row["fitted"]["mean_absolute_error"]
                       for row in report["leave_one_sequence_out_fits"]],
        "weights": [row["symmetric_five_weights"]
                    for row in report["leave_one_sequence_out_fits"]],
    }))


if __name__ == "__main__":
    main()
