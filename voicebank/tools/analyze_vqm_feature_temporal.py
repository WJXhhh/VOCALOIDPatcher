"""Check whether temporal shaping extends across VQM feature bits 6, 16 and 18.

The three installed V4X VQM sequences are read only. The report contains
aggregate errors and leave-one-sequence-out coefficients, not frame values.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from audit_frm2_numeric import SHA256
from analyze_vqm_bit18_temporal import MARGIN, errors, fit_symmetric_five
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit18_saved_band import compare_one


def run(manifest: Path, voice_root: Path, parser_root: Path, dll: Path) -> dict:
    parser_hash, ddi_hash, sequences = load_sequences(
        manifest, voice_root, parser_root)
    observed: list[dict[str, float | int]] = []
    for index, sequence in enumerate(sequences):
        compare_one(dll, index, sequence, feature_sink=observed)
    if len(observed) != 246:
        raise AssertionError("VQM temporal feature count changed")

    fields = []
    for bit in (6, 16, 18):
        series = []
        for sample_index, sequence in enumerate(sequences):
            rows = [row for row in observed
                    if row["sample_index"] == sample_index]
            if ([row["frame_index"] for row in rows]
                    != list(range(len(sequence["frames"])))):
                raise AssertionError("feature frame order changed")
            source = [float(row[f"drs_bit{bit}"]) for row in rows]
            saved = [float(row[f"saved_bit{bit}"]) for row in rows]
            series.append((source, saved, []))

        fixed_controls = []
        for name, weights in (
                ("identity", [1]),
                ("five_frame_mean", [1, 1, 1, 1, 1]),
                ("five_frame_binomial", [1, 4, 6, 4, 1]),
                ("five_frame_1_3_4_3_1", [1, 3, 4, 3, 1])):
            fixed_controls.append({
                "kernel": name,
                "per_sample": [errors(source, saved, weights)
                               for source, saved, _ in series],
            })
        held_out = []
        for sample_index, (source, saved, _) in enumerate(series):
            weights = fit_symmetric_five(series, sample_index)
            identity = errors(source, saved, [1])
            fitted = errors(source, saved, weights)
            if fitted["mean_absolute_error"] >= identity["mean_absolute_error"]:
                raise AssertionError("held-out temporal fit did not transfer")
            held_out.append({
                "sample_index": sample_index,
                "trained_on_sample_indices": [index for index in range(3)
                                              if index != sample_index],
                "symmetric_weights_left_to_right": weights,
                "identity_error": identity,
                "held_out_error": fitted,
            })
        fields.append({
            "feature_bit": bit,
            "fixed_kernel_controls": fixed_controls,
            "leave_one_sequence_out_fits": held_out,
        })
    return {
        "dse_sha256": SHA256,
        "parser_sha256": parser_hash,
        "ddi_sha256": ddi_hash,
        "common_interior_margin_frames": MARGIN,
        "features": fields,
        "scope": "Three independent sequences in one V4X bank; field-specific temporal relationships transfer across those sequences but are not exact authoring formulas.",
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
    print(json.dumps({
        str(field["feature_bit"]): {
            "identity_mae": [row["identity_error"]["mean_absolute_error"]
                             for row in field["leave_one_sequence_out_fits"]],
            "held_out_mae": [row["held_out_error"]["mean_absolute_error"]
                             for row in field["leave_one_sequence_out_fits"]],
        } for field in report["features"]
    }))


if __name__ == "__main__":
    main()
