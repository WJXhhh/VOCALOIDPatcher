"""Audit installed auxiliary VQM.vdb metadata without exporting frame data."""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
from pathlib import Path
import struct

from audit_frm2_numeric import f32
from frm2_pitch_voicing import probability
from probe_frm2 import VqmFrame, parse_frame


ENCODED_VQM_MASK = 0x800E2287
PLAIN_VQM_MASK = 0x000E2287


def numbers(values: list[float | int]) -> dict[str, float | int]:
    if not values:
        raise ValueError("missing values")
    if not all(math.isfinite(value) for value in values):
        raise ValueError("nonfinite feature value")
    return {
        "count": len(values),
        "unique": len(set(values)),
        "zero_count": sum(value == 0 for value in values),
        "min": min(values),
        "max": max(values),
    }


def feature_fields(frame: VqmFrame) -> tuple[dict[int, float], dict[int, tuple[float, ...]]]:
    flags = frame.feature_block.flags
    payload = frame.feature_block.payload
    offset = 0
    scalars: dict[int, float] = {}
    arrays: dict[int, tuple[float, ...]] = {}
    for bit in range(21):
        if not flags & (1 << bit):
            continue
        if bit < 10 or 14 <= bit < 20:
            scalars[bit] = struct.unpack_from("<f", payload, offset)[0]
            offset += 4
        elif bit in (10, 11, 12, 13):
            count = 40 if bit in (10, 11) else 20
            arrays[bit] = struct.unpack_from(f"<{count}f", payload, offset)
            offset += count * 4
        elif bit != 20:
            raise ValueError(f"unsupported feature flag bit {bit}")
    if offset != len(payload):
        raise ValueError("feature payload length mismatch")
    return scalars, arrays


def exact_pairs(predicted: list[float], saved: list[float]) -> dict[str, float | int]:
    if len(predicted) != len(saved):
        raise ValueError("comparison lengths differ")
    return {
        "compared": len(saved),
        "float32_exact": sum(struct.pack("<f", a) == struct.pack("<f", b)
                             for a, b in zip(predicted, saved)),
        "max_absolute_error": max((abs(a - b) for a, b in zip(predicted, saved)),
                                  default=0.0),
    }


def derived_checks(rows: list[dict[int, float]], times: list[float]) -> dict:
    level = [row[5] for row in rows]
    level_prediction = [f32(10.0 * f32(math.log10(row[2]))) for row in rows]
    history_prediction = [
        f32(f32(0.5 * f32(level[index - 1] - level[index - 2]))
            + f32(0.5 * f32(level[index] - level[index - 1])))
        for index in range(2, len(rows))
    ]
    probability_prediction = [
        probability({"confidence": row[4], "strength": row[15],
                     "ratio": row[14], "feature4": row[0],
                     "feature8": row[1], "feature12": row[2]})
        for row in rows
    ]
    branches: Counter[str] = Counter()
    for row in rows:
        branches["confidence_below_0_2"] += row[4] < f32(0.2)
        branches["crossing_above_0_1"] += row[0] > f32(0.1)
        branches["noise_above_0_2"] += row[1] > f32(0.2)
        branches["noise_strength_relief"] += row[1] > f32(0.2) and row[15] > f32(0.015)
        branches["energy_below_0_00002"] += row[2] < f32(0.00002)
        branches["ratio_below_0_52"] += row[14] < 0.52
    steps = [later - earlier for earlier, later in zip(times, times[1:])]
    if any(step <= 0 for step in steps):
        raise ValueError("auxiliary VQM frame times are not increasing")
    phase_values = [row[17] for row in rows]
    if any(not 0.0 <= value < 2.0 * math.pi for value in phase_values):
        raise ValueError("auxiliary VQM bit17 is outside one circle")
    phase_steps = [math.remainder(later - earlier, 2.0 * math.pi)
                   for earlier, later in zip(phase_values, phase_values[1:])]
    return {
        "time_seconds": numbers(times),
        "time_step_seconds": numbers(steps),
        "bit5_from_bit2": exact_pairs(level_prediction, level),
        "bit9_from_bit5_history": exact_pairs(
            history_prediction, [row[9] for row in rows[2:]]),
        "bit19_probability": exact_pairs(
            probability_prediction, [row[19] for row in rows]),
        "probability_branch_input_counts": dict(sorted(branches.items())),
        "bit17_circular_steps": {
            "compared": len(phase_steps),
            "max_absolute_radians": max((abs(step) for step in phase_steps), default=0.0),
            "near_quarter_radian": sum(abs(abs(step) - 0.25) < 1e-5
                                       for step in phase_steps),
            "over_0_250001_radian_indexes": [index for index, step in enumerate(phase_steps)
                                              if abs(step) > 0.250001],
            "over_0_5_radian": sum(abs(step) > 0.5 for step in phase_steps),
            "raw_wraps_over_pi": sum(abs(later - earlier) > math.pi
                                      for earlier, later in zip(phase_values, phase_values[1:])),
        },
    }


def audit(path: Path) -> dict:
    data = path.read_bytes()
    if data[:4] != b"VQM ":
        raise ValueError("not a VQM database")
    sections = []
    cursor = 0
    while True:
        start = data.find(b"VQMs", cursor)
        if start < 0:
            break
        size = struct.unpack_from("<I", data, start + 4)[0]
        if size < 8 or start + size > len(data):
            raise ValueError("invalid VQMs extent")
        sections.append((start, start + size))
        cursor = start + size
    if not sections:
        raise ValueError("no VQMs sections")
    section_labels = []
    for start, _ in sections:
        label = data[start - 34:start - 29].decode("ascii") if start >= 34 else ""
        if label not in ("TENSE", "BRWSP"):
            raise ValueError(f"unexpected VQMs category before {start}")
        section_labels.append(label)

    masks: Counter[str] = Counter()
    kinds: Counter[str] = Counter()
    sizes: Counter[str] = Counter()
    section_counts: Counter[str] = Counter()
    controls: dict[str, list[int]] = defaultdict(list)
    scalar_values: dict[int, list[float]] = defaultdict(list)
    array_values: dict[int, list[float]] = defaultdict(list)
    array_nonzero_frames: Counter[str] = Counter()
    pitch_cents: list[float] = []
    component_counts: list[int] = []
    spectrum_counts: list[int] = []
    section_pitches: dict[int, list[float]] = defaultdict(list)
    section_scalars: dict[int, dict[int, list[float]]] = defaultdict(lambda: defaultdict(list))
    section_rows: dict[int, list[dict[int, float]]] = defaultdict(list)
    section_times: dict[int, list[float]] = defaultdict(list)
    parsed = 0
    skipped_masks: Counter[str] = Counter()
    embedded_pcm_frames = []
    position = 0
    while (position := data.find(b"FRM2", position)) >= 0:
        if position + 28 > len(data):
            raise ValueError("short FRM2 prefix")
        size = struct.unpack_from("<I", data, position + 4)[0]
        mask = struct.unpack_from("<Q", data, position + 20)[0]
        section = next((index for index, (start, end) in enumerate(sections)
                        if start <= position and position + size <= end), None)
        if section is None or size < 28:
            raise ValueError(f"FRM2 outside a valid VQMs section at {position}")
        if mask != ENCODED_VQM_MASK:
            if mask == 0x40:
                sample_count = struct.unpack_from("<I", data, position + 28)[0]
                if size != 32 + sample_count * 2:
                    raise ValueError("invalid auxiliary PCM frame extent")
                pcm_bytes = data[position + 32:position + size]
                samples = struct.unpack(f"<{sample_count}h", pcm_bytes)
                embedded_pcm_frames.append({
                    "category": section_labels[section],
                    "kind": struct.unpack_from("<I", data, position + 8)[0],
                    "time_seconds": struct.unpack_from("<d", data, position + 12)[0],
                    "sample_count": sample_count,
                    "duration_at_44100_seconds": sample_count / 44100.0,
                    "pcm_sha256": hashlib.sha256(pcm_bytes).hexdigest(),
                    "nonzero_samples": sum(sample != 0 for sample in samples),
                    "peak_abs_sample": max(map(abs, samples), default=0),
                })
            skipped_masks[f"0x{mask:x}"] += 1
            position += size
            continue
        # The native reader treats bit 31 as the stored-pitch encoding switch.
        # Clearing it only in this in-memory copy lets the existing structural
        # parser validate all remaining fields without misnaming the pitch.
        frame_bytes = bytearray(data[position:position + size])
        struct.pack_into("<Q", frame_bytes, 20, PLAIN_VQM_MASK)
        frame = parse_frame(frame_bytes)
        if not isinstance(frame, VqmFrame):
            raise ValueError("expected an auxiliary VQM frame")
        scalars, arrays = feature_fields(frame)
        section_rows[section].append(scalars)
        section_times[section].append(frame.time_seconds)
        for bit, value in scalars.items():
            scalar_values[bit].append(value)
            section_scalars[section][bit].append(value)
        for bit, values in arrays.items():
            array_values[bit].extend(values)
            array_nonzero_frames[str(bit)] += any(value != 0 for value in values)
        pitch_cents.append(frame.f0_hz)
        section_pitches[section].append(frame.f0_hz)
        component_counts.append(len(frame.component_frequencies_hz))
        spectrum_counts.append(len(frame.spectrum_amplitudes))
        controls["trailer"].append(frame.feature_trailer)
        controls["outer_bit18"].append(frame.bit18_value)
        controls["outer_bit19"].append(frame.bit19_value)
        masks[f"0x{mask:x}"] += 1
        kinds[str(frame.kind)] += 1
        sizes[str(size)] += 1
        section_counts[str(section)] += 1
        parsed += 1
        position += size

    if parsed == 0:
        raise ValueError("no encoded-pitch VQM frames")
    return {
        "source_sha256": hashlib.sha256(data).hexdigest(),
        "source_bytes": len(data),
        "vqm_sections": [{"category": section_labels[index],
                          "offset": start, "size": end - start}
                         for index, (start, end) in enumerate(sections)],
        "parsed_frames": parsed,
        "skipped_frame_masks": dict(sorted(skipped_masks.items())),
        "embedded_pcm_frames": embedded_pcm_frames,
        "frames_by_section": dict(sorted(section_counts.items())),
        "frame_masks": dict(sorted(masks.items())),
        "frame_kinds": dict(sorted(kinds.items())),
        "frame_sizes": dict(sorted(sizes.items())),
        "stored_pitch_cents": numbers(pitch_cents),
        "derived_pitch_hz": numbers([440.0 * 2.0 ** (value / 1200.0)
                                     for value in pitch_cents]),
        "component_counts": numbers(component_counts),
        "saved_spectrum_counts": numbers(spectrum_counts),
        "outer_controls": {name: numbers(values) for name, values in sorted(controls.items())},
        "feature_scalars": {str(bit): numbers(values)
                            for bit, values in sorted(scalar_values.items())},
        "feature_arrays": {str(bit): {"values": numbers(values),
                                       "nonzero_frames": array_nonzero_frames[str(bit)]}
                           for bit, values in sorted(array_values.items())},
        "by_section": {str(index): {
            "stored_pitch_cents": numbers(section_pitches[index]),
            "feature_scalars": {str(bit): numbers(values)
                                for bit, values in sorted(section_scalars[index].items())},
            "derived_checks": derived_checks(section_rows[index], section_times[index]),
        } for index in range(len(sections))},
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vdb", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = audit(args.vdb)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"source_sha256": report["source_sha256"],
                      "parsed_frames": report["parsed_frames"],
                      "outer_controls": report["outer_controls"]}, ensure_ascii=False))


if __name__ == "__main__":
    main()
