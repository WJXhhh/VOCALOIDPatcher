"""Test a bounded pitch-drift explanation for saved VQM phase steps.

Reads installed material through the fixed manifest. The report contains
aggregate errors only; no commercial frame or PCM samples are emitted.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import statistics

from verify_vqm_bit16_spectral_power import load_sequences


SAMPLE_RATE = 44100
HOP = 256
STEP_LIMIT = 0.25


def clamp(value: float) -> float:
    return min(STEP_LIMIT, max(-STEP_LIMIT, value))


def steps(sequence: dict) -> list[float]:
    angles = [frame["features"][17] for frame in sequence["frames"]]
    result = [math.remainder(right - left, math.tau)
              for left, right in zip(angles, angles[1:])]
    if any(abs(value) > STEP_LIMIT + 2e-7 for value in result):
        raise AssertionError("saved VQM phase step exceeds known boundary")
    return result


def frequencies(sequence: dict, source: str) -> list[float]:
    frames = sequence["frames"]
    pitch = [frame["f0"] for frame in frames]
    if source == "previous_f0":
        return pitch[:-1]
    if source == "current_f0":
        return pitch[1:]
    if source == "mean_f0":
        return [(left + right) / 2 for left, right in zip(pitch, pitch[1:])]
    if source == "previous_component":
        return [float(frame["first_component_frequency"])
                for frame in frames[:-1]]
    if source == "current_component":
        return [float(frame["first_component_frequency"])
                for frame in frames[1:]]
    raise ValueError(source)


def score(actual: list[float], frequencies_hz: list[float],
          reference_hz: float, sign: int) -> dict:
    predicted = [clamp(sign * math.tau * (frequency - reference_hz)
                       * HOP / SAMPLE_RATE)
                 for frequency in frequencies_hz]
    errors = [abs(left - right) for left, right in zip(actual, predicted)]
    return {"mean_absolute_error": statistics.fmean(errors),
            "median_absolute_error": statistics.median(errors),
            "max_absolute_error": max(errors),
            "correlation": statistics.correlation(actual, predicted)
            if len(set(predicted)) > 1 else None,
            "predicted_saturation_count": sum(abs(value) >= STEP_LIMIT
                                              for value in predicted)}


def run(manifest: Path, voice_root: Path, parser_root: Path) -> dict:
    parser_hash, ddi_hash, sequences = load_sequences(
        manifest, voice_root, parser_root)
    sources = ("previous_f0", "current_f0", "mean_f0",
               "previous_component", "current_component")
    rows = []
    for sequence_index, sequence in enumerate(sequences):
        actual = steps(sequence)
        pitch1 = sequence["pitch1"]
        candidates = []
        for source in sources:
            observed_frequencies = frequencies(sequence, source)
            for sign in (-1, 1):
                fixed = score(actual, observed_frequencies, pitch1, sign)
                fitted = min(
                    ((offset / 20, score(actual, observed_frequencies,
                                         pitch1 + offset / 20, sign))
                     for offset in range(-400, 401)),
                    key=lambda pair: pair[1]["mean_absolute_error"])
                candidates.append({"source": source, "sign": sign,
                                   "fixed_pitch1": fixed,
                                   "best_reference_offset_hz": fitted[0],
                                   "best_fitted": fitted[1]})
        best_fixed = min(candidates,
                         key=lambda row: row["fixed_pitch1"]["mean_absolute_error"])
        best_fitted = min(candidates,
                          key=lambda row: row["best_fitted"]["mean_absolute_error"])
        rows.append({
            "sample_index": sequence_index,
            "adjacent_steps": len(actual),
            "pitch1_hz": pitch1,
            "median_frame_f0_hz": statistics.median(
                frame["f0"] for frame in sequence["frames"]),
            "observed_saturation_count": sum(abs(abs(value) - STEP_LIMIT) < 1e-5
                                             for value in actual),
            "zero_step_control_mae": statistics.fmean(map(abs, actual)),
            "best_fixed_pitch1": best_fixed,
            "best_fitted_reference": best_fitted,
        })
    return {
        "ddi_sha256": ddi_hash,
        "parser_sha256": parser_hash,
        "phase_step_model": "clamp(sign*2*pi*(source_frequency-reference_hz)*256/44100, -0.25, 0.25)",
        "fitted_reference_grid": "pitch1 ±20 Hz in 0.05 Hz increments, fitted within each sequence",
        "samples": rows,
        "scope": "Three independent VQM sequences in one V4X bank. A fitted reference is diagnostic only, not evidence of an authoring parameter. This tests pitch drift, not PCM phase or other stateful phase trackers.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--voice-root", required=True, type=Path)
    parser.add_argument("--ddb-tools", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = run(args.manifest, args.voice_root, args.ddb_tools)
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps([{
        "sample": row["sample_index"],
        "zero_mae": row["zero_step_control_mae"],
        "best_fixed": row["best_fixed_pitch1"]["fixed_pitch1"]["mean_absolute_error"],
        "best_fitted": row["best_fitted_reference"]["best_fitted"]["mean_absolute_error"],
    } for row in report["samples"]]))


if __name__ == "__main__":
    main()
