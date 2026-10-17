"""Check whether a shared PCM-window time shift explains saved VQM bit16 residuals.

Installed V4X files are read only. Output contains errors and input hashes only.
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


SAMPLE_RATE = 44100
WINDOW_SIZE = 2048
HOP = 256
OFFSETS = (-256, -128, -64, -32, -16, -8, -4, -2, -1,
           0, 1, 2, 4, 8, 16, 32, 64, 128, 256)


def run(manifest: Path, voice_root: Path, parser_root: Path,
        dll: Path) -> dict:
    parser_hash, ddi_hash, sequences = load_sequences(
        manifest, voice_root, parser_root)
    native = Native(dll)
    weights = window(native, WINDOW_SIZE, 10)
    if len(weights) != WINDOW_SIZE:
        raise AssertionError("DSE analysis window differs")
    per_offset = {offset: [] for offset in OFFSETS}
    per_sequence = [{offset: [] for offset in OFFSETS}
                    for _ in sequences]
    for sequence_index, sequence in enumerate(sequences):
        pcm = sequence["pcm"]
        frames = sequence["frames"]
        for frame_index in range(5, len(frames) - 5):
            frequency = frames[frame_index]["f0"]
            if not math.isfinite(frequency) or frequency <= 0:
                raise ValueError("VQM frame has invalid frequency")
            kernel = [weight * cmath.exp(
                -1j * math.tau * frequency * index / SAMPLE_RATE)
                      for index, weight in enumerate(weights)]
            for offset in OFFSETS:
                start = frame_index * HOP - WINDOW_SIZE // 2 + offset
                samples = pcm[start:start + WINDOW_SIZE]
                if len(samples) != WINDOW_SIZE:
                    raise AssertionError("candidate window exceeded saved PCM")
                mean = statistics.fmean(samples)
                projection = sum((sample - mean) * coefficient
                                 for sample, coefficient in zip(samples, kernel))
                magnitude = abs(projection)
                if not math.isfinite(magnitude) or magnitude <= 0:
                    raise ValueError("candidate projection is invalid")
                error = (2 * math.log10(magnitude / 32768)
                         - frames[frame_index]["bit16"])
                per_offset[offset].append(error)
                per_sequence[sequence_index][offset].append(error)
    rows = [{"offset_samples": offset,
             **summarize_errors(per_offset[offset]),
             "per_sequence_mean_absolute_error": [
                 summarize_errors(sequence[offset])["mean_absolute_error"]
                 for sequence in per_sequence]}
            for offset in OFFSETS]
    if any(row["count"] != 216 for row in rows):
        raise AssertionError("candidate frame count changed")
    baseline = next(row for row in rows if row["offset_samples"] == 0)
    best = min(rows, key=lambda row: row["mean_absolute_error"])
    reduction = (baseline["mean_absolute_error"]
                 - best["mean_absolute_error"]) / baseline["mean_absolute_error"]
    if reduction >= 0.05 or best["mean_absolute_error"] <= 0.001:
        raise AssertionError("an integer time shift now explains bit16 residuals")
    return {
        "dse_sha256": SHA256,
        "ddi_sha256": ddi_hash,
        "parser_sha256": parser_hash,
        "independent_sequences": len(sequences),
        "window_size": WINDOW_SIZE,
        "window_kind": 10,
        "hop_samples": HOP,
        "candidate_offsets_samples": OFFSETS,
        "rows": rows,
        "best_offset_samples": best["offset_samples"],
        "best_reduction_fraction_vs_zero": reduction,
        "scope": "Saved F0 and the DSE kind-10 window on 216 fully bounded interior frames; a shared integer PCM offset alone does not establish the original bit16 producer.",
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
    print(json.dumps({"frames_per_offset": result["rows"][0]["count"],
                      "offsets": len(result["rows"]),
                      "best": result["best_offset_samples"]}))


if __name__ == "__main__":
    main()
