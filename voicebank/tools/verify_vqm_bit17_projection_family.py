"""Bound a family of direct-PCM phase candidates for VQM feature bit17.

Only aggregate circular errors and hashes are written. Installed voicebank
material is read without exporting PCM or frame sequences.
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


WINDOW_SIZES = (512, 1024, 2048, 4096)
FREQUENCIES = ("frame_f0", "pitch1", "mean_f0")
CORRECTIONS = ("none", "minus_cumulative_f0", "plus_cumulative_f0",
               "minus_pitch1", "plus_pitch1", "global_frame_f0",
               "global_pitch1")
LAGS = range(-3, 4)


def phase_for(sequence: dict, weights: list[float], index: int,
              frequency: float) -> float:
    size = len(weights)
    start = index * 256 - size // 2
    samples = sequence["pcm"][start:start + size]
    mean = statistics.fmean(samples)
    projection = sum(
        (sample - mean) * weight
        * cmath.exp(-1j * math.tau * frequency * offset / 44100)
        for offset, (sample, weight) in enumerate(zip(samples, weights))
    )
    return cmath.phase(projection)


def corrected(phase: float, sequence: dict, index: int,
              size: int, correction: str, cumulative_cycles: list[float]) -> float:
    frame = sequence["frames"][index]
    if correction == "none":
        return phase
    if correction == "minus_cumulative_f0":
        return phase - math.tau * cumulative_cycles[index]
    if correction == "plus_cumulative_f0":
        return phase + math.tau * cumulative_cycles[index]
    if correction == "minus_pitch1":
        return phase - math.tau * index * 256 * sequence["pitch1"] / 44100
    if correction == "plus_pitch1":
        return phase + math.tau * index * 256 * sequence["pitch1"] / 44100
    if correction == "global_frame_f0":
        return phase - math.tau * (index * 256 - size // 2) * frame["f0"] / 44100
    if correction == "global_pitch1":
        return phase - math.tau * (index * 256 - size // 2) * sequence["pitch1"] / 44100
    raise ValueError(correction)


def score(sequences: list[dict], projections: list[dict[int, float]],
          size: int, correction: str, lag: int, sign: int,
          cumulative: list[list[float]]) -> list[float]:
    errors = []
    for sample_index, sequence in enumerate(sequences):
        deltas = []
        for index in range(11, len(sequence["frames"]) - 11):
            source_index = index + lag
            predicted = corrected(projections[sample_index][source_index],
                                  sequence, source_index, size, correction,
                                  cumulative[sample_index])
            saved = sequence["frames"][index]["features"][17]
            deltas.append(math.remainder(saved - sign * predicted, math.tau))
        offset = cmath.phase(sum(cmath.exp(1j * delta)
                                 for delta in deltas))
        errors.append(statistics.fmean(
            abs(math.remainder(delta - offset, math.tau))
            for delta in deltas))
    return errors


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
    candidates = []
    for size in WINDOW_SIZES:
        weights = window(native, size, 10)
        for frequency_mode in FREQUENCIES:
            projections = []
            for sequence in sequences:
                mean_f0 = statistics.fmean(frame["f0"]
                                           for frame in sequence["frames"])
                phases = {}
                for index in range(8, len(sequence["frames"]) - 8):
                    frequency = (sequence["frames"][index]["f0"]
                                 if frequency_mode == "frame_f0" else
                                 sequence["pitch1"]
                                 if frequency_mode == "pitch1" else mean_f0)
                    phases[index] = phase_for(sequence, weights, index,
                                              frequency)
                projections.append(phases)
            for correction in CORRECTIONS:
                for lag in LAGS:
                    for sign in (1, -1):
                        errors = score(sequences, projections, size,
                                       correction, lag, sign, cumulative)
                        candidates.append({
                            "window_size": size,
                            "frequency_mode": frequency_mode,
                            "correction": correction,
                            "lag_frames": lag,
                            "phase_sign": sign,
                            "mean_sample_mae_radians": statistics.fmean(errors),
                            "per_sample_mae_radians": errors,
                        })
    best = min(candidates, key=lambda row: row["mean_sample_mae_radians"])
    if (len(candidates) != 1176
            or best["mean_sample_mae_radians"] <= 0.1):
        raise AssertionError("projection family result changed")
    return {
        "dse_sha256": SHA256,
        "ddi_sha256": ddi_hash,
        "parser_sha256": parser_hash,
        "candidate_count": len(candidates),
        "common_frame_counts": [len(sequence["frames"]) - 22
                                for sequence in sequences],
        "window_sizes": WINDOW_SIZES,
        "frequency_modes": FREQUENCIES,
        "corrections": CORRECTIONS,
        "lag_frames": [min(LAGS), max(LAGS)],
        "best": best,
        "scope": "Best of a bounded direct fundamental-projection family after independent phase rotation per sample. Nonzero error rejects these simple candidates, not all possible phase producers.",
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
    print(json.dumps({"candidates": report["candidate_count"],
                      "best_mean_mae": report["best"]["mean_sample_mae_radians"]}))


if __name__ == "__main__":
    main()
