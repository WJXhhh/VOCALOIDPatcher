"""Summarize bounded VQM PCM/DRS feature comparisons without frame values."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from audit_frm2_numeric import SHA256


def summarize(directory: Path) -> dict:
    default = [json.loads((directory / f"vqm_drs_default_sample_{index}.json").read_text())
               for index in range(3)]
    modes = [json.loads((directory / f"vqm_drs_mode_{index}.json").read_text())
             for index in range(6)]
    tracked = json.loads((directory / "vqm_drs_track_sample_0.json").read_text())
    ddi_hash = default[0]["ddi_sha256"]
    for index, row in enumerate(default):
        count = (72, 87, 87)[index]
        if (row["dse_sha256"] != SHA256 or row["ddi_sha256"] != ddi_hash
                or row["sample_index"] != index
                or row["saved_frames"] != count
                or row["produced_frames"] != count
                or row["produced_feature_frames"] != count
                or row["feature_flags"] != {"0x1fffff": count}
                or row["automatic_pitch"] != 0
                or row["effective_analysis_mode"] != 4
                or row["effective_config_5a0"] != 0
                or row["produced_feature_ranges"]["17"] != {
                    "count": count, "min": 0.0, "max": 0.0,
                    "nonzero_over_1e_6": 0,
                }):
            raise AssertionError("Default VQM DRS comparison scope differs")
    for index, row in enumerate(modes):
        if (row["dse_sha256"] != SHA256 or row["ddi_sha256"] != ddi_hash
                or row["sample_index"] != 0
                or row["analysis_mode"] != index
                or row["effective_analysis_mode"] != index
                or row["automatic_pitch"] != 1
                or row["produced_feature_frames"] != 72
                or row["feature_flags"] != {"0x1fffff": 72}
                or row["produced_feature_ranges"]["17"] != {
                    "count": 72, "min": 0.0, "max": 0.0,
                    "nonzero_over_1e_6": 0,
                }):
            raise AssertionError("VQM DRS mode comparison scope differs")
    fields = (0, 1, 2, 9)
    default_rows = []
    for row in default:
        cells = row["comparison_by_frame_shift"]["0"]
        default_rows.append({
            "sample_index": row["sample_index"],
            "frames": row["saved_frames"],
            "matches_with_saved": {
                str(bit): cells[str(bit)]["within_1e_5"] for bit in fields
            },
        })
    totals = {str(bit): sum(row["matches_with_saved"][str(bit)]
                            for row in default_rows) for bit in fields}
    if totals != {"0": 237, "1": 243, "2": 243, "9": 237}:
        raise AssertionError("VQM DRS raw feature match counts differ")
    scored = (4, 14, 15, 16, 17, 18, 19)
    mode_rows = []
    for row in modes:
        cells = row["comparison_by_frame_shift"]["0"]
        mode_rows.append({
            "mode": row["analysis_mode"],
            "saved_field_matches_within_1e_5": {
                str(bit): cells[str(bit)]["within_1e_5"] for bit in scored
            },
            "saved_field_correlations": {
                str(bit): cells[str(bit)]["correlation"] for bit in scored
            },
        })
    if any(any(value for value in row["saved_field_matches_within_1e_5"].values())
           for row in mode_rows):
        raise AssertionError("An analyzed mode now reproduces saved scored fields")
    if (tracked["dse_sha256"] != SHA256
            or tracked["ddi_sha256"] != ddi_hash
            or tracked["sample_index"] != 0
            or tracked["pitch_track"] != "saved_f0"
            or tracked["saved_frames"] != 72
            or tracked["produced_feature_frames"] != 72
            or tracked["produced_feature_sha256"]
            != default[0]["produced_feature_sha256"]
            or tracked["comparison_by_frame_shift"]
            != default[0]["comparison_by_frame_shift"]):
        raise AssertionError("Saved-F0 track differs from the bounded comparison")
    return {
        "dse_sha256": SHA256,
        "ddi_sha256": ddi_hash,
        "independent_sequences": 3,
        "saved_and_produced_frames": 246,
        "default_external_pitch_effective_mode": 4,
        "default_comparison": default_rows,
        "default_total_matches_with_saved": totals,
        "default_produced_bit17_zero_frames": 246,
        "automatic_pitch_mode_sweep": mode_rows,
        "automatic_pitch_produced_bit17_zero_frames": 432,
        "saved_f0_track_comparison": {
            "sample_index": 0,
            "frames": 72,
            "produced_feature_values_equal_to_constant_pitch1": True,
        },
        "scope": "Three unique V4X VQM PCM sequences analyzed in memory; saved feature scalar comparisons only, not the original authoring profile or a complete Growl event.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--research-dir", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = summarize(args.research_dir)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"frames": result["saved_and_produced_frames"],
                      "raw_matches": result["default_total_matches_with_saved"],
                      "modes": len(result["automatic_pitch_mode_sweep"])}))


if __name__ == "__main__":
    main()
