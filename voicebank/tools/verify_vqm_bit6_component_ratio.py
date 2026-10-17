"""Test the DRS two-band component formula for VQM feature bit6.

The same-hash DSE assembly at 0x1800d4fe0..0x1800d5127 averages
linearized component amplitudes in 100..1500 and 7000..9000 Hz bands,
then divides the high-band mean by the low-band mean. The conversion is
delegated to the native numeric helper at 0x18002af20.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from audit_frm2_numeric import F, Native, SHA256, f32
from verify_vqm_bit16_spectral_power import load_sequences


def predict(components: list[tuple[float, float]] | tuple[tuple[float, float], ...],
            linearize) -> float:
    bands = ((100.0, 1500.0), (7000.0, 9000.0))
    means = []
    for lower, upper in bands:
        total = f32(0)
        count = 0
        for frequency, amplitude in components:
            if frequency > upper:
                break
            if frequency >= lower:
                total = f32(total + linearize(f32(amplitude)))
                count += 1
        means.append(f32(total / count) if count else f32(0))
    return f32(means[1] / means[0]) if means[0] != 0 else f32(-1)


def metrics(rows: list[tuple[float, float]]) -> dict:
    errors = [abs(actual - expected) for actual, expected in rows]
    return {"frames": len(rows),
            "actual_negative_one": sum(actual == -1 for actual, _ in rows),
            "actual_mean": sum(actual for actual, _ in rows) / len(rows),
            "predicted_mean": sum(expected for _, expected in rows) / len(rows),
            "exact_float32": sum(error == 0 for error in errors),
            "within_1e_6": sum(error <= 1e-6 for error in errors),
            "mean_absolute_error": sum(errors) / len(errors),
            "max_absolute_error": max(errors)}


def run(dll: Path, manifest: Path, voice_root: Path,
        parser_root: Path) -> dict:
    native = Native(dll)
    linearize = native.fn(0x2af20, F, F)
    parser_sha, ddi_sha, sequences = load_sequences(
        manifest, voice_root, parser_root)
    saved = [metrics([(frame["bit6"], predict(frame["components"], linearize))
                      for frame in sequence["frames"]])
             for sequence in sequences]
    del native
    if [row["frames"] for row in saved] != [72, 87, 87]:
        raise AssertionError("VQM frame count changed")
    return {"dse_sha256": SHA256, "ddi_sha256": ddi_sha,
            "parser_sha256": parser_sha,
            "bands_hz": [[100, 1500], [7000, 9000]],
            "saved_vqm_cases": saved,
            "scope": "Assembly gives the two component bands and their "
                     "intermediate ratio. This check applies that ratio "
                     "with the native dB-to-linear helper to saved V4X "
                     "components. Intermediate records may differ; no "
                     "payload is written."}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--voice-root", required=True, type=Path)
    parser.add_argument("--ddb-tools", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = run(args.dll, args.manifest, args.voice_root,
                 args.ddb_tools)
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({"saved_frames": sum(row["frames"] for row in
                                          report["saved_vqm_cases"])}))


if __name__ == "__main__":
    main()
