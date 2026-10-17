"""Compare saved VQM feature bit17 with saved first-component phase.

Uses only installed read-only V4X material. Evidence records aggregate
circular errors, not phase sequences or frame payloads.
"""

from __future__ import annotations

import argparse
import cmath
import json
import math
from pathlib import Path
import statistics

from verify_vqm_bit16_spectral_power import load_sequences


def circular_error(actual: list[float], candidate: list[float],
                   sign: int) -> dict:
    differences = [math.remainder(a - sign * b, math.tau)
                   for a, b in zip(actual, candidate)]
    mean_vector = sum(cmath.exp(1j * value) for value in differences)
    offset = cmath.phase(mean_vector)
    errors = [abs(math.remainder(value - offset, math.tau))
              for value in differences]
    return {"sign": sign, "mean_absolute_circular_error":
            statistics.fmean(errors),
            "median_absolute_circular_error": statistics.median(errors),
            "max_absolute_circular_error": max(errors),
            "concentration": abs(mean_vector) / len(differences)}


def step_error(actual: list[float], candidate: list[float],
               sign: int, clipped: bool) -> dict:
    observed_steps = [math.remainder(b - a, math.tau)
                      for a, b in zip(actual, actual[1:])]
    candidate_steps = [math.remainder(b - a, math.tau)
                       for a, b in zip(candidate, candidate[1:])]
    predicted = [sign * value for value in candidate_steps]
    if clipped:
        predicted = [min(0.25, max(-0.25, value)) for value in predicted]
    errors = [abs(a - b) for a, b in zip(observed_steps, predicted)]
    return {"sign": sign, "clipped": clipped,
            "mean_absolute_step_error": statistics.fmean(errors),
            "median_absolute_step_error": statistics.median(errors),
            "max_absolute_step_error": max(errors)}


def run(manifest: Path, voice_root: Path, parser_root: Path) -> dict:
    parser_hash, ddi_hash, sequences = load_sequences(
        manifest, voice_root, parser_root)
    rows = []
    for index, sequence in enumerate(sequences):
        frames = sequence["frames"]
        actual = [frame["features"][17] for frame in frames]
        phase = [frame["first_component_phase"] for frame in frames]
        if any(value is None or not math.isfinite(value) for value in phase):
            raise AssertionError("saved VQM first component phase missing")
        level = [circular_error(actual, phase, sign)
                 for sign in (-1, 1)]
        step = [step_error(actual, phase, sign, clipped)
                for sign in (-1, 1) for clipped in (False, True)]
        rows.append({"sample_index": index, "frames": len(frames),
                     "phase_min": min(phase), "phase_max": max(phase),
                     "best_fixed_rotation": min(
                         level, key=lambda row: row["mean_absolute_circular_error"]),
                     "best_adjacent_step": min(
                         step, key=lambda row: row["mean_absolute_step_error"]),
                     "zero_step_control_mae": statistics.fmean(
                         abs(math.remainder(b - a, math.tau))
                         for a, b in zip(actual, actual[1:]))})
    return {
        "ddi_sha256": ddi_hash,
        "parser_sha256": parser_hash,
        "samples": rows,
        "scope": "Saved first-component phase versus internal feature bit17, allowing one rotation and sign per sequence or a quarter-radian cap on direct phase steps. This does not test hidden production state or PCM-derived phase tracks.",
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
        "rotation_mae": row["best_fixed_rotation"]["mean_absolute_circular_error"],
        "step_mae": row["best_adjacent_step"]["mean_absolute_step_error"],
        "zero_step_mae": row["zero_step_control_mae"],
    } for row in report["samples"]]))


if __name__ == "__main__":
    main()
