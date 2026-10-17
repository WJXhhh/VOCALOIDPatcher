"""Analyze embedded auxiliary PCM and compare native DRS features to VQM frames."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import statistics
import struct
import subprocess
import sys

from audit_frm2_numeric import B, D, F, I, P, Q, SHA256
from audit_aux_vqm_vdb import ENCODED_VQM_MASK, PLAIN_VQM_MASK, feature_fields
from analyze_vqm_bit18_temporal import (errors as temporal_errors,
                                         fit_symmetric_five)
from probe_frm2 import VqmFrame, parse_frame
from probe_frm2_batch_outputs import run as analyze_batch
from probe_vqm_loaded_aux_mix import VQM_SHA256


OFFSETS = {0: 4, 1: 8, 2: 12, 4: 0x14, 6: 0x1c, 9: 0x28,
           14: 0x20c, 15: 0x214, 16: 0x218, 17: 0x21c,
           18: 0x220, 19: 0x224}


def material(path: Path, category: str) -> tuple[list[int], list[dict[int, float]]]:
    data = path.read_bytes()
    if hashlib.sha256(data).hexdigest() != VQM_SHA256:
        raise ValueError("auxiliary VQM file differs from verified sample")
    labels = ("TENSE", "BRWSP")
    sections = []
    cursor = 0
    while (cursor := data.find(b"VQMs", cursor)) >= 0:
        size = struct.unpack_from("<I", data, cursor + 4)[0]
        label = data[cursor - 34:cursor - 29].decode("ascii")
        if label not in labels or size < 8 or cursor + size > len(data):
            raise ValueError("unexpected auxiliary section")
        sections.append((label, cursor, cursor + size))
        cursor += size
    section = next((item for item in sections if item[0] == category), None)
    if section is None or len(sections) != 2:
        raise ValueError("auxiliary category not found")
    pcm = None
    saved = []
    position = section[1]
    while (position := data.find(b"FRM2", position, section[2])) >= 0:
        size = struct.unpack_from("<I", data, position + 4)[0]
        mask = struct.unpack_from("<Q", data, position + 20)[0]
        if size < 32 or position + size > section[2]:
            raise ValueError("auxiliary frame extent changed")
        if mask == 0x40:
            if pcm is not None:
                raise ValueError("multiple auxiliary PCM frames")
            count = struct.unpack_from("<I", data, position + 28)[0]
            if size != 32 + count * 2:
                raise ValueError("auxiliary PCM length changed")
            pcm = list(struct.unpack_from(f"<{count}h", data, position + 32))
        elif mask == ENCODED_VQM_MASK:
            raw = bytearray(data[position:position + size])
            struct.pack_into("<Q", raw, 20, PLAIN_VQM_MASK)
            frame = parse_frame(raw)
            if not isinstance(frame, VqmFrame):
                raise ValueError("auxiliary feature frame changed")
            scalars, _ = feature_fields(frame)
            saved.append(scalars)
            if abs(frame.time_seconds - (len(saved) - 1) * 256 / 44100) > 1e-12:
                raise ValueError("auxiliary feature timing changed")
        else:
            raise ValueError(f"unexpected auxiliary frame mask {mask:#x}")
        position += size
    if pcm is None or len(pcm) != len(saved) * 256 or len(saved) != \
            (148 if category == "TENSE" else 389):
        raise ValueError("auxiliary PCM and feature frame counts differ")
    return pcm, saved


def single_case(vdb: Path, dse: Path, category: str, mode: int,
                pitch_hz: float, temporal_weights: dict[int, list[float]] | None = None,
                crossfit_weights: dict[int, list[float]] | None = None) -> dict:
    pcm, saved = material(vdb, category)
    holder = []
    summary = analyze_batch(
        dse, 0, 1, "steady", pitch_hz, analysis_mode=mode,
        pitch_seed=pitch_hz, pcm_override=pcm,
        setup_observer=lambda native, context: holder.append((native, context)))
    native, context = holder[0]
    track = P.from_address(context + 0xcd0).value
    region_count = I.from_address(track + 0x140).value
    region_array = P.from_address(track + 0x148).value
    produced = []
    for region_index in range(region_count):
        region = P.from_address(region_array + region_index * 8).value
        count = I.from_address(region + 0x18).value
        pointers = P.from_address(region + 0x20).value
        for index in range(count):
            frame = P.from_address(pointers + index * 8).value
            mask = Q.from_address(frame + 0x50).value
            feature = P.from_address(frame + 0x128).value
            if not mask & (1 << 13) or not feature:
                continue
            flags = I.from_address(feature).value
            values = {bit: F.from_address(feature + offset).value
                      for bit, offset in OFFSETS.items() if flags & (1 << bit)}
            frame_index = round(D.from_address(frame + 0x48).value * 44100 / 256)
            produced.append((frame_index, values))
    comparisons = {}
    for shift in range(-4, 5):
        by_bit = {}
        for bit in OFFSETS:
            pairs = [(values[bit], saved[index + shift][bit])
                     for index, values in produced
                     if bit in values and 0 <= index + shift < len(saved)]
            if not pairs:
                continue
            errors = [abs(a - b) for a, b in pairs]
            by_bit[str(bit)] = {
                "compared": len(pairs),
                "float32_exact": sum(struct.pack("<f", a) == struct.pack("<f", b)
                                     for a, b in pairs),
                "within_1e_6": sum(error <= 1e-6 for error in errors),
                "mean_absolute_error": statistics.fmean(errors),
                "max_absolute_error": max(errors),
            }
        comparisons[str(shift)] = by_bit
    if not produced or any(not math.isfinite(value)
                           for _, row in produced for value in row.values()):
        raise AssertionError("native DRS feature output missing or nonfinite")
    if summary["frames"] != len(saved) or len(produced) != len(saved):
        raise AssertionError("auxiliary DRS frame alignment changed")
    result = {"category": category, "source_pcm_samples": len(pcm),
            "saved_feature_frames": len(saved), "produced_frames": summary["frames"],
            "produced_feature_frames": len(produced),
            "mode": mode, "external_pitch_hz": pitch_hz,
            "produced_bit17_nonzero": sum(row.get(17, 0) != 0 for _, row in produced),
            "comparison_by_frame_shift": comparisons}
    if temporal_weights is not None:
        ordered = sorted(produced)
        if [index for index, _ in ordered] != list(range(len(saved))):
            raise AssertionError("auxiliary DRS temporal frame order changed")
        transfer = {}
        for bit, weights in temporal_weights.items():
            source_values = [row[bit] for _, row in ordered]
            saved_values = [row[bit] for row in saved]
            local_fit = fit_symmetric_five(
                [(source_values, saved_values, [])], held_out=-1)
            transfer[str(bit)] = {
                "identity": temporal_errors(source_values, saved_values, [1]),
                "v4x_fitted_five": temporal_errors(source_values, saved_values,
                                                   weights),
                "five_frame_mean": temporal_errors(source_values, saved_values,
                                                   [1, 1, 1, 1, 1]),
                "local_fitted_five_weights": local_fit,
                "local_fitted_five_in_sample": temporal_errors(
                    source_values, saved_values, local_fit),
            }
        result["temporal_transfer"] = transfer
        if crossfit_weights is not None:
            result["temporal_crossfit"] = {
                str(bit): temporal_errors([row[bit] for _, row in ordered],
                                          [row[bit] for row in saved], vector)
                for bit, vector in crossfit_weights.items()}
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vdb", required=True, type=Path)
    parser.add_argument("--dse-dll", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--single-category", choices=("TENSE", "BRWSP"))
    parser.add_argument("--analysis-mode", type=int, choices=range(6), default=4)
    parser.add_argument("--temporal-weights", type=Path)
    parser.add_argument("--crossfit-report", type=Path)
    parser.add_argument("--crossfit-source-category", choices=("TENSE", "BRWSP"))
    args = parser.parse_args()
    weights = None
    if args.temporal_weights is not None:
        source = json.loads(args.temporal_weights.read_text(encoding="utf-8"))
        weights = {}
        for field in source["features"]:
            bit = field["feature_bit"]
            if bit not in (6, 16, 18):
                continue
            fits = [item["symmetric_weights_left_to_right"]
                    for item in field["leave_one_sequence_out_fits"]]
            if len(fits) != 3 or any(len(row) != 5 for row in fits):
                raise ValueError("V4X temporal fit coverage changed")
            weights[bit] = [statistics.fmean(row[index] for row in fits)
                            for index in range(5)]
        if set(weights) != {6, 16, 18}:
            raise ValueError("V4X temporal fields changed")
    crossfit_weights = None
    if args.crossfit_report is not None:
        if weights is None or args.crossfit_source_category is None:
            raise ValueError("crossfit requires V4X weights and source category")
        source = json.loads(args.crossfit_report.read_text(encoding="utf-8"))
        source_category = next(row for row in source["categories"]
                               if row["category"] == args.crossfit_source_category)
        crossfit_weights = {
            bit: source_category["temporal_transfer"][str(bit)][
                "local_fitted_five_weights"] for bit in (6, 16, 18)}
        if any(len(vector) != 5 for vector in crossfit_weights.values()):
            raise ValueError("auxiliary crossfit weights changed")
    if args.single_category:
        category = args.single_category
        if crossfit_weights is not None and category == args.crossfit_source_category:
            raise ValueError("crossfit source and target must differ")
        print(json.dumps(single_case(args.vdb, args.dse_dll, category,
                                     args.analysis_mode,
                                     181.0 if category == "TENSE" else 300.0,
                                     temporal_weights=weights,
                                     crossfit_weights=crossfit_weights)))
        return
    if crossfit_weights is not None:
        raise ValueError("crossfit is available only for a single target category")
    rows = []
    for category in ("TENSE", "BRWSP"):
        command = [sys.executable, str(Path(__file__).resolve()),
                   "--vdb", str(args.vdb), "--dse-dll", str(args.dse_dll),
                   "--output", str(args.output), "--single-category", category,
                   "--analysis-mode", str(args.analysis_mode)]
        if args.temporal_weights is not None:
            command.extend(("--temporal-weights", str(args.temporal_weights)))
        child = subprocess.run(command, capture_output=True, text=True,
                               check=False, timeout=180)
        if child.returncode:
            raise RuntimeError(f"{category}: {child.stderr[-1300:] or child.returncode}")
        rows.append(json.loads(child.stdout))
    if weights is None:
        report = {"dse_sha256": SHA256, "auxiliary_vqm_sha256": VQM_SHA256,
                  "categories": rows,
                  "scope": "Embedded VQM.vdb PCM16 supplied unchanged to the existing "
                           f"offline DRS mode-{args.analysis_mode} batch analyzer at external reference "
                           "pitch 181/300 Hz. Saved and produced scalar features "
                           "are compared by nearby frame shifts. No PCM is exported, "
                           "no Editor process or installed bank PCM."}
    else:
        report = {"dse_sha256": SHA256, "auxiliary_vqm_sha256": VQM_SHA256,
                  "v4x_fit_report_sha256": hashlib.sha256(
                      args.temporal_weights.read_bytes()).hexdigest(),
                  "transferred_weights": {str(bit): vector
                                          for bit, vector in weights.items()},
                  "analysis_mode": args.analysis_mode,
                  "common_interior_margin_frames": 4,
                  "categories": [
                      {"category": row["category"],
                       "source_pcm_samples": row["source_pcm_samples"],
                       "saved_feature_frames": row["saved_feature_frames"],
                       "produced_feature_frames": row["produced_feature_frames"],
                       "temporal_transfer": row["temporal_transfer"]}
                      for row in rows],
                  "scope": "Mean of three V4X leave-one-sequence-out five-tap fits "
                           "from the named evidence report, transferred without "
                           f"auxiliary refitting to TENSE/BRWSP mode-{args.analysis_mode} DRS outputs "
                           "from their unchanged embedded PCM. Separate local fits "
                           "are in-sample diagnostics, not transfer evidence. Only "
                           "aggregate interior errors and coefficients retained; "
                           "no PCM or per-frame fields exported."}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
