"""Compare V4X VQM edge features with nearby windows of their saved PCM."""

from __future__ import annotations

import argparse
import hashlib
import json
import mmap
from pathlib import Path
import struct

from audit_frm2_numeric import F, I, Native, P, Q, SHA256, f32
from audit_v4x_vqm_fields import feature_array, feature_scalars
from frm2_phase_controls import PhaseControls
from frm2_pitch_voicing import probability
from frm2_raw_features import raw_features
import probe_frm2
from verify_reference_ddi import load_public_parser, pointer


def run(manifest_path: Path, voice_root: Path, parser_root: Path, dll: Path) -> dict:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    parser_file = parser_root / "utils" / "ddi_utils.py"
    if hashlib.sha256(parser_file.read_bytes()).hexdigest() != manifest["parser_sha256"]:
        raise ValueError("DDI parser hash differs from fixed manifest")
    model_type = load_public_parser(parser_root)
    bank = next(row for row in manifest["banks"]
                if row["bank"] == "MIKU_V4X_Original_EVEC")
    matches = list(voice_root.rglob(bank["bank"] + ".ddi"))
    if len(matches) != 1:
        raise ValueError("expected one V4X Original DDI")
    ddi_path = matches[0]
    ddi = ddi_path.read_bytes()
    if hashlib.sha256(ddi).hexdigest() != bank["ddi_sha256"]:
        raise ValueError("V4X Original DDI hash differs from fixed manifest")
    model = model_type(ddi)
    model.read()
    native = Native(dll)
    config = native.alloc(0x35f0)
    native.fn(0x90de0, P, P)(config)
    for time in (0.0, 1.0):
        native.add(config + 0x5d0 + 0x17 * 0xa0, time, 1.0)
    context = native.alloc(0xce0)
    P.from_address(context + 0xad0).value = config
    raw_frame = native.alloc(0x1a0)
    features = native.alloc(0x228)
    P.from_address(raw_frame + 0x128).value = features
    native_raw_feature = native.fn(0xd92e0, Q, P, P)
    rows = []
    with ddi_path.with_suffix(".ddb").open("rb") as source:
        with mmap.mmap(source.fileno(), 0, access=mmap.ACCESS_READ) as ddb:
            for sample in (model.vqm_data or {}).values():
                frames = []
                for item in sample["epr"]:
                    start, _ = pointer(item, ddi)
                    size = struct.unpack_from("<I", ddb, start + 4)[0]
                    if start + size > len(ddb):
                        raise ValueError("VQM frame extends outside DDB")
                    frame = probe_frm2.parse_frame(ddb[start:start + size])
                    if not isinstance(frame, probe_frm2.VqmFrame):
                        raise ValueError("VQM reference is not a VQM frame")
                    frames.append(frame)
                saved_features = [feature_scalars(item) for item in frames]
                probability_errors = []
                nonunity_probabilities = 0
                active_probability_terms = {"confidence": 0, "crossing": 0,
                                            "noise": 0, "energy": 0, "ratio": 0}
                for item in saved_features:
                    predicted = probability({
                        "feature4": item[0], "feature8": item[1],
                        "feature12": item[2], "confidence": item[4],
                        "ratio": item[14], "strength": item[15],
                    })
                    probability_errors.append(abs(predicted - item[19]))
                    nonunity_probabilities += item[19] != 1.0
                    active_probability_terms["confidence"] += item[4] < 0.2
                    active_probability_terms["crossing"] += item[0] > 0.1
                    active_probability_terms["noise"] += item[1] > 0.2
                    active_probability_terms["energy"] += item[2] < 0.00002
                    active_probability_terms["ratio"] += item[14] < 0.52
                array_counts = {bit: sum(len(feature_array(item, bit)) for item in frames)
                                for bit in (10, 11, 12, 13)}
                array_nonzero_counts = {
                    bit: sum(value != 0.0 for item in frames
                             for value in feature_array(item, bit))
                    for bit in (10, 11, 12, 13)
                }
                if any(array_nonzero_counts.values()):
                    raise AssertionError("V4X VQM feature arrays are no longer all zero")
                phase_controls = PhaseControls()
                all_derivative_errors = [
                    abs(phase_controls.advance(item[2])["derivative"] - item[9])
                    for item in saved_features
                ]
                implied_before_previous = saved_features[0][5] - 2 * saved_features[0][9]
                implied_previous = saved_features[1][5] - 2 * saved_features[1][9]
                derivative_errors = []
                for frame_index in range(2, len(frames)):
                    before_previous = saved_features[frame_index - 2][5]
                    previous = saved_features[frame_index - 1][5]
                    level = saved_features[frame_index][5]
                    predicted = f32(f32(0.5 * f32(previous - before_previous))
                                    + f32(0.5 * f32(level - previous)))
                    derivative_errors.append(abs(predicted - saved_features[frame_index][9]))
                snd_start, count = pointer(sample["snd"], ddi)
                if ddb[snd_start:snd_start + 4] != b"SND ":
                    raise ValueError("VQM SND reference differs")
                if count != len(frames) * 256:
                    raise ValueError("VQM PCM count differs")
                pcm = struct.unpack_from(f"<{count}h", ddb, snd_start + 18)
                pcm_float = native.array(F, pcm)
                P.from_address(context + 0xae0).value = pcm_float
                I.from_address(context + 0xaf0).value = count
                I.from_address(context + 0xbb0).value = 256
                I.from_address(context + 0xad8).value = 44100
                native_saved_matches = {0: 0, 1: 0, 2: 0}
                exceptions = []
                for frame_index in range(1, len(frames)):
                    saved = feature_scalars(frames[frame_index])
                    original_start = (frame_index - 1) * 256
                    baseline = raw_features(pcm, original_start, 256)
                    I.from_address(context + 0xb00).value = original_start + 256
                    native_raw_feature(context, raw_frame)
                    for bit, key, offset in ((0, "feature4", 4), (1, "feature8", 8),
                                             (2, "feature12", 12)):
                        actual = F.from_address(features + offset).value
                        if actual != baseline[key]:
                            raise AssertionError("Native VQM raw feature differs from model")
                        native_saved_matches[bit] += abs(actual - saved[bit]) <= 1e-6
                    for bit, key in ((0, "feature4"), (1, "feature8")):
                        if abs(saved[bit] - baseline[key]) <= 1e-6:
                            continue
                        neighbors = []
                        for shift in range(-256, 257):
                            start = original_start + shift
                            if start < 0 or start + 512 > count:
                                continue
                            actual = raw_features(pcm, start, 256)[key]
                            neighbors.append((abs(saved[bit] - actual), shift))
                        error, shift = min(neighbors)
                        exact_shifts = [position for difference, position in neighbors
                                        if difference <= 1e-6]
                        crossing_context = []
                        for nearby_index in range(max(1, frame_index - 2),
                                                  min(len(frames), frame_index + 3)):
                            nearby_start = (nearby_index - 1) * 256
                            crossing_context.append({
                                "relative_frame": nearby_index - frame_index,
                                "saved_crossings_510": round(
                                    feature_scalars(frames[nearby_index])[0] * 510
                                ) if bit == 0 else None,
                                "pcm_crossings_510": round(
                                    raw_features(pcm, nearby_start, 256)["feature4"] * 510
                                ) if bit == 0 else None,
                            })
                        exceptions.append({
                            "frame_index": frame_index,
                            "feature_bit": bit,
                            "baseline_absolute_error": abs(saved[bit] - baseline[key]),
                            "best_nearby_shift_samples": shift,
                            "best_nearby_absolute_error": error,
                            "nearby_exact_shift_count": len(exact_shifts),
                            "nearest_exact_shift_samples": min(exact_shifts, key=abs)
                            if exact_shifts else None,
                            "crossing_context": crossing_context if bit == 0 else None,
                        })
                rows.append({"frame_count": len(frames),
                             "bit13_internal19_probability": {
                                 "compared": len(probability_errors),
                                 "within_1e_6": sum(error <= 1e-6
                                                    for error in probability_errors),
                                 "max_error": max(probability_errors),
                                 "nonunity_saved_values": nonunity_probabilities,
                                 "active_terms": active_probability_terms,
                             },
                             "feature_array_value_counts": array_counts,
                             "feature_array_nonzero_counts": array_nonzero_counts,
                             "native_raw_feature_matches_saved": native_saved_matches,
                             "level_derivative_bit9": {
                                 "compared": len(derivative_errors),
                                 "within_1e_6": sum(error <= 1e-6 for error in derivative_errors),
                                 "max_error": max(derivative_errors),
                                 "default_zero_history_first_two_errors":
                                 all_derivative_errors[:2],
                                 "implied_pre_history_levels": [
                                     implied_before_previous, implied_previous
                                 ],
                                 "implied_vs_last_two_level_errors": [
                                     abs(implied_before_previous - saved_features[-2][5]),
                                     abs(implied_previous - saved_features[-1][5]),
                                 ],
                             },
                             "exceptions": exceptions})
    if sorted(row["frame_count"] for row in rows) != [72, 87, 87]:
        raise AssertionError("V4X Original independent VQM sample coverage changed")
    totals = {bit: sum(row["native_raw_feature_matches_saved"][bit] for row in rows)
              for bit in (0, 1, 2)}
    if totals != {0: 237, 1: 241, 2: 243}:
        raise AssertionError("VQM saved feature agreement changed")
    derivative_count = sum(row["level_derivative_bit9"]["compared"] for row in rows)
    derivative_matches = sum(row["level_derivative_bit9"]["within_1e_6"]
                             for row in rows)
    if derivative_count != 240 or derivative_matches != 240:
        raise AssertionError("VQM bit9 interior derivative differs")
    probability_count = sum(row["bit13_internal19_probability"]["compared"]
                            for row in rows)
    probability_matches = sum(row["bit13_internal19_probability"]["within_1e_6"]
                              for row in rows)
    if probability_count != 246 or probability_matches != 246:
        raise AssertionError("VQM bit13 internal bit19 probability differs")
    nonunity_count = sum(row["bit13_internal19_probability"]["nonunity_saved_values"]
                         for row in rows)
    array_value_counts = {bit: sum(row["feature_array_value_counts"][bit] for row in rows)
                          for bit in (10, 11, 12, 13)}
    return {
        "dse_sha256": SHA256,
        "ddi_sha256": bank["ddi_sha256"],
        "parser_sha256": manifest["parser_sha256"],
        "independent_vqm_samples": len(rows),
        "native_raw_feature_matches_saved": totals,
        "bit9_level_derivative_interior_matches": derivative_matches,
        "bit9_level_derivative_interior_compared": derivative_count,
        "bit13_internal19_probability_matches": probability_matches,
        "bit13_internal19_nonunity_saved_values": nonunity_count,
        "feature_array_value_counts": array_value_counts,
        "feature_array_nonzero_counts": {bit: 0 for bit in array_value_counts},
        "rows": rows,
        "scope": "Saved V4X PCM near already identified bit13 edge exceptions; no PCM or frame payload is exported.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--voice-root", type=Path, required=True)
    parser.add_argument("--ddb-tools", type=Path, required=True)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = run(args.manifest, args.voice_root, args.ddb_tools, args.dll)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"samples": len(result["rows"]),
                      "exceptions": sum(len(row["exceptions"]) for row in result["rows"])}))


if __name__ == "__main__":
    main()
