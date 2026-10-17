"""Compare saved V4X VQM bit6 with a bounded PCM band-energy candidate.

Installed voicebank data is read only. Output contains correlations and hashes,
never commercial PCM, spectra, or frame payloads.
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


SIZE = 2048
SAMPLE_RATE = 44100
BANDS = {"low_control": (5000, 6500),
         "target": (7250, 8750),
         "high_control": (10000, 11500)}


def run(manifest: Path, voice_root: Path, parser_root: Path,
        dll: Path) -> dict:
    parser_hash, ddi_hash, sequences = load_sequences(
        manifest, voice_root, parser_root)
    weights = window(Native(dll), SIZE, 10)
    bins = {
        name: list(range(math.ceil(low * SIZE / SAMPLE_RATE),
                         math.floor(high * SIZE / SAMPLE_RATE) + 1))
        for name, (low, high) in BANDS.items()
    }
    bases = {
        k: [cmath.exp(-1j * math.tau * k * index / SIZE)
            for index in range(SIZE)]
        for k in sorted(set().union(*bins.values()))
    }
    results = []
    normalized_targets = []
    saved_targets = []
    per_sample_pairs = []
    for sample_index, sequence in enumerate(sequences):
        observed = []
        candidates = {name: [] for name in BANDS}
        normalized = {name: [] for name in BANDS}
        for frame_index in range(4, len(sequence["frames"]) - 4):
            samples = sequence["pcm"][frame_index * 256 - SIZE // 2:
                                      frame_index * 256 + SIZE // 2]
            mean = statistics.fmean(samples)
            signal = [(value - mean) * weight
                      for value, weight in zip(samples, weights)]
            rms = math.sqrt(sum(value * value for value in signal))
            spectrum = {k: abs(sum(value * basis
                                   for value, basis in zip(signal, bases[k])))
                        for k in bases}
            for name, frequencies in bins.items():
                magnitude = math.sqrt(sum(spectrum[k] ** 2
                                          for k in frequencies))
                candidates[name].append(magnitude)
                normalized[name].append(magnitude / rms)
            observed.append(sequence["frames"][frame_index]["bit6"])
        correlations = {
            name: {
                "raw": statistics.correlation(candidates[name], observed),
                "normalized_by_window_energy": statistics.correlation(
                    normalized[name], observed),
            }
            for name in BANDS
        }
        if (len(observed) < 60
                or correlations["target"]["normalized_by_window_energy"] <= 0.9
                or any(correlations["target"]["normalized_by_window_energy"] <=
                       correlations[name]["normalized_by_window_energy"]
                       for name in ("low_control", "high_control"))):
            raise AssertionError("saved VQM band relation changed")
        results.append({"sample_index": sample_index,
                        "compared_frames": len(observed),
                        "correlations": correlations})
        normalized_targets.extend(normalized["target"])
        saved_targets.extend(observed)
        per_sample_pairs.append((normalized["target"], observed))
    if sum(row["compared_frames"] for row in results) != 222:
        raise AssertionError("unexpected VQM interior frame count")
    mean_x = statistics.fmean(normalized_targets)
    mean_y = statistics.fmean(saved_targets)
    slope = sum((x - mean_x) * (y - mean_y)
                for x, y in zip(normalized_targets, saved_targets)) / sum(
                    (x - mean_x) ** 2 for x in normalized_targets)
    intercept = mean_y - slope * mean_x
    fit_errors = []
    for result, (xs, ys) in zip(results, per_sample_pairs):
        residuals = [slope * x + intercept - y for x, y in zip(xs, ys)]
        mean_saved = statistics.fmean(ys)
        result["global_linear_fit"] = {
            "mean_absolute_error": statistics.fmean(abs(value)
                                                    for value in residuals),
            "r_squared": 1 - sum(value * value for value in residuals) / sum(
                (value - mean_saved) ** 2 for value in ys),
        }
        fit_errors.extend(residuals)
    return {
        "dse_sha256": SHA256,
        "ddi_sha256": ddi_hash,
        "parser_sha256": parser_hash,
        "window_size": SIZE,
        "window_kind": 10,
        "bands_hz": BANDS,
        "per_sample": results,
        "global_linear_fit": {
            "slope": slope,
            "intercept": intercept,
            "mean_absolute_error": statistics.fmean(abs(value)
                                                   for value in fit_errors),
            "correlation": statistics.correlation(normalized_targets,
                                                   saved_targets),
        },
        "scope": "2048-point centered PCM windows and direct DFT band magnitudes. A single least-squares calibration still leaves material error, so correlation supports a frequency-band candidate but not the exact producer or normalization.",
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
    print(json.dumps({"samples": len(report["per_sample"]),
                      "frames": sum(row["compared_frames"]
                                    for row in report["per_sample"])}))


if __name__ == "__main__":
    main()
