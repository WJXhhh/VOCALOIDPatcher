"""Test a quarter-radian-limited phase tracker against saved VQM bit17.

The candidate target is a local PCM projection or a saved component phase.
Only bounded aggregate errors and candidate settings are recorded; installed
PCM and frame values are never exported.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import statistics

from audit_frm2_numeric import Native, SHA256
from verify_frm2_analysis_windows import window
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit17_projection_family import corrected, phase_for


WINDOW_SIZES = (2048, 4096)
FREQUENCIES = ("frame_f0", "pitch1", "mean_f0")
CORRECTIONS = ("none", "minus_cumulative_f0", "plus_cumulative_f0",
               "minus_pitch1", "plus_pitch1")
LAGS = (-1, 0, 1)
ALPHAS = (0.05, 0.1, 0.2, 0.5, 1.0)
FRAME_MARGIN = 11
OFFSET_GRID = 360


def clamp(value: float) -> float:
    return min(0.25, max(-0.25, value))


def fit_offset(previous: list[float], target: list[float],
               observed: list[float], train_count: int,
               alpha: float) -> float:
    def error(offset: float) -> float:
        return statistics.fmean(
            abs(clamp(alpha * math.remainder(value + offset - prior, math.tau))
                - step)
            for prior, value, step in zip(previous[:train_count],
                                          target[:train_count],
                                          observed[:train_count]))

    best = min(range(OFFSET_GRID),
               key=lambda index: error(math.tau * index / OFFSET_GRID))
    coarse = math.tau * best / OFFSET_GRID
    return min((coarse + math.tau * index / (OFFSET_GRID * 16)
                for index in range(-16, 17)), key=error)


def evaluate(previous: list[float], target: list[float],
             observed: list[float], offset: float,
             alpha: float, start: int = 0) -> dict:
    predicted = [clamp(alpha * math.remainder(value + offset - prior, math.tau))
                 for prior, value in zip(previous[start:], target[start:])]
    actual = observed[start:]
    errors = [abs(left - right) for left, right in zip(predicted, actual)]
    return {"steps": len(errors),
            "mean_absolute_error": statistics.fmean(errors),
            "median_absolute_error": statistics.median(errors),
            "zero_step_control_mae": statistics.fmean(map(abs, actual)),
            "predicted_limit_hits": sum(abs(value) >= 0.25
                                        for value in predicted),
            "actual_limit_hits": sum(abs(abs(value) - 0.25) <= 1e-5
                                     for value in actual)}


def run(manifest: Path, voice_root: Path, parser_root: Path,
        dll: Path) -> dict:
    parser_hash, ddi_hash, sequences = load_sequences(
        manifest, voice_root, parser_root)
    native = Native(dll)
    cumulative = []
    for sequence in sequences:
        cycles = [0.0]
        for frame in sequence["frames"]:
            cycles.append(cycles[-1] + frame["f0"] * 256 / 44100)
        cumulative.append(cycles)
    phase_sources = []
    for size in WINDOW_SIZES:
        weights = window(native, size, 10)
        for frequency_mode in FREQUENCIES:
            phases = []
            for sequence in sequences:
                mean_f0 = statistics.fmean(frame["f0"]
                                           for frame in sequence["frames"])
                frequencies = [frame["f0"] for frame in sequence["frames"]]
                phases.append({
                    index: phase_for(sequence, weights, index,
                                     frequencies[index]
                                     if frequency_mode == "frame_f0" else
                                     sequence["pitch1"]
                                     if frequency_mode == "pitch1" else mean_f0)
                    for index in range(8, len(frequencies) - 8)
                })
            phase_sources.append((f"pcm_{size}_{frequency_mode}", size,
                                  phases, CORRECTIONS))
    component_phases = [
        {index: frame["first_component_phase"]
         for index, frame in enumerate(sequence["frames"])}
        for sequence in sequences]
    if any(value is None for phases in component_phases
           for value in phases.values()):
        raise AssertionError("saved first-component phase missing")
    phase_sources.append(("saved_first_component", 0, component_phases,
                          CORRECTIONS))

    candidates = []
    for name, size, phases, corrections in phase_sources:
        for correction in corrections:
            for lag in LAGS:
                for sign in (-1, 1):
                    prepared = []
                    for sample_index, sequence in enumerate(sequences):
                        saved = [frame["features"][17]
                                 for frame in sequence["frames"]]
                        indices = range(FRAME_MARGIN,
                                        len(saved) - FRAME_MARGIN)
                        previous = [saved[index - 1] for index in indices]
                        target = [sign * corrected(
                            phases[sample_index][index + lag], sequence,
                            index + lag, size, correction,
                            cumulative[sample_index]) for index in indices]
                        observed = [math.remainder(saved[index] - saved[index - 1],
                                                   math.tau) for index in indices]
                        prepared.append((previous, target, observed))
                    for alpha in ALPHAS:
                        per_sample = []
                        for previous, target, observed in prepared:
                            offset = fit_offset(previous, target, observed,
                                                len(observed), alpha)
                            per_sample.append(evaluate(previous, target,
                                                       observed, offset, alpha))
                        candidates.append({
                            "source": name,
                            "correction": correction,
                            "lag_frames": lag,
                            "sign": sign,
                            "alpha": alpha,
                            "per_sample": per_sample,
                            "mean_sample_mae": statistics.fmean(
                                row["mean_absolute_error"] for row in per_sample),
                            "prepared": prepared,
                        })
    ranked = sorted(candidates, key=lambda row: row["mean_sample_mae"])
    holdouts = []
    for held_out in range(len(sequences)):
        selected = min(candidates, key=lambda row: statistics.fmean(
            metric["mean_absolute_error"]
            for index, metric in enumerate(row["per_sample"])
            if index != held_out))
        previous, target, observed = selected["prepared"][held_out]
        training_steps = min(12, len(observed) // 3)
        offset = fit_offset(previous, target, observed,
                            training_steps, selected["alpha"])
        holdouts.append({
            "held_out_sample_index": held_out,
            "selected_source": selected["source"],
            "selected_correction": selected["correction"],
            "selected_lag_frames": selected["lag_frames"],
            "selected_sign": selected["sign"],
            "selected_alpha": selected["alpha"],
            "offset_calibration_steps": training_steps,
            "validation": evaluate(previous, target, observed,
                                   offset, selected["alpha"], training_steps),
        })
    for row in ranked:
        del row["prepared"]
    best_full_gain = min((row for row in ranked if row["alpha"] == 1.0),
                         key=lambda row: row["mean_sample_mae"])
    return {
        "dse_sha256": SHA256,
        "ddi_sha256": ddi_hash,
        "parser_sha256": parser_hash,
        "candidate_count": len(candidates),
        "model": "predicted_step=clamp(alpha*remainder(sign*target_phase+rotation-saved_previous_angle, 2*pi), ±0.25)",
        "phase_source_sizes": WINDOW_SIZES,
        "frequency_modes": FREQUENCIES,
        "corrections": CORRECTIONS,
        "lag_frames": LAGS,
        "tracking_gains": ALPHAS,
        "common_margin_frames": FRAME_MARGIN,
        "best_full_gain": best_full_gain,
        "best_three_full_fit": ranked[:3],
        "leave_one_sequence_out": holdouts,
        "scope": "One constant phase rotation fitted per sequence; the held-out sequence calibrates that rotation on only its first 12 interior steps. This tests a bounded tracker family, not arbitrary hidden phase state or the production algorithm.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--voice-root", required=True, type=Path)
    parser.add_argument("--ddb-tools", required=True, type=Path)
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = run(args.manifest, args.voice_root, args.ddb_tools, args.dll)
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({
        "candidates": report["candidate_count"],
        "best_mae": report["best_three_full_fit"][0]["mean_sample_mae"],
        "holdout_mae": [row["validation"]["mean_absolute_error"]
                        for row in report["leave_one_sequence_out"]],
        "holdout_zero": [row["validation"]["zero_step_control_mae"]
                         for row in report["leave_one_sequence_out"]],
    }))


if __name__ == "__main__":
    main()
