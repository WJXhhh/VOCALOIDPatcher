"""Test local saved-F0 trajectories against VQM bit16 spectral power.

Only aggregate prediction errors and file hashes leave this read-only audit.
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
from verify_vqm_bit16_spectral_power import load_sequences, summarize_errors


def frequencies(frames: list[dict], index: int, pitch1: float) -> dict[str, float]:
    previous = frames[index - 1]["f0"]
    current = frames[index]["f0"]
    following = frames[index + 1]["f0"]
    result = {
        "saved_current": current,
        "saved_previous": previous,
        "saved_next": following,
        "three_frame_mean": (previous + current + following) / 3,
        "three_frame_median": statistics.median((previous, current, following)),
        "pitch1": pitch1,
    }
    for weight in (0.25, 0.5, 0.75):
        result[f"previous_{weight}"] = current + weight * (previous - current)
        result[f"next_{weight}"] = current + weight * (following - current)
    return result


def run(manifest: Path, voice_root: Path, parser_root: Path,
        dll: Path) -> dict:
    parser_hash, ddi_hash, sequences = load_sequences(
        manifest, voice_root, parser_root)
    native = Native(dll)
    weights = window(native, 2048, 10)
    errors = {}
    per_sequence = []
    for sequence in sequences:
        sequence_errors = {}
        frames = sequence["frames"]
        pcm = sequence["pcm"]
        for index in range(5, len(frames) - 5):
            start = index * 256 - 1024
            samples = pcm[start:start + 2048]
            if len(samples) != 2048:
                raise AssertionError("candidate window exceeded saved PCM")
            mean = statistics.fmean(samples)
            weighted = [(sample - mean) * weight
                        for sample, weight in zip(samples, weights)]
            for name, frequency in frequencies(frames, index,
                                               sequence["pitch1"]).items():
                if not math.isfinite(frequency) or frequency <= 0:
                    raise ValueError("candidate F0 is invalid")
                projection = sum(value * cmath.exp(
                    -1j * math.tau * frequency * sample_index / 44100)
                    for sample_index, value in enumerate(weighted))
                if abs(projection) <= 0:
                    raise ValueError("candidate projection is zero")
                residual = (2 * math.log10(abs(projection) / 32768)
                            - frames[index]["bit16"])
                errors.setdefault(name, []).append(residual)
                sequence_errors.setdefault(name, []).append(residual)
        per_sequence.append(sequence_errors)
    rows = [{"frequency_rule": name,
             **summarize_errors(values),
             "per_sequence_mean_absolute_error": [
                 summarize_errors(sequence[name])["mean_absolute_error"]
                 for sequence in per_sequence]}
            for name, values in errors.items()]
    if any(row["count"] != 216 for row in rows):
        raise AssertionError("candidate frame count changed")
    best = min(rows, key=lambda row: row["mean_absolute_error"])
    baseline = next(row for row in rows
                    if row["frequency_rule"] == "saved_current")
    if best["frequency_rule"] != "saved_current":
        raise AssertionError("a local F0 candidate now explains bit16 better")
    return {
        "dse_sha256": SHA256,
        "ddi_sha256": ddi_hash,
        "parser_sha256": parser_hash,
        "independent_sequences": len(sequences),
        "window_size": 2048,
        "window_kind": 10,
        "rows": rows,
        "best_frequency_rule": best["frequency_rule"],
        "best_reduction_fraction_vs_current": (
            (baseline["mean_absolute_error"] - best["mean_absolute_error"])
            / baseline["mean_absolute_error"]),
        "scope": "Finite local saved-F0 candidate family on 216 fully bounded VQM frames; a better candidate is not proof of the original frequency estimator.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--voice-root", type=Path, required=True)
    parser.add_argument("--ddb-tools", type=Path, required=True)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = run(args.manifest, args.voice_root,
                 args.ddb_tools, args.dll)
    args.output.write_text(json.dumps(result, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({"frames_per_rule": result["rows"][0]["count"],
                      "rules": len(result["rows"]),
                      "best": result["best_frequency_rule"]}))


if __name__ == "__main__":
    main()
