#!/usr/bin/env python3
"""Finalize a one-STA/one-ARTp DSE skeleton using a bank DDB manifest."""

from __future__ import annotations

import argparse
import json
import math
import struct
import sys
from pathlib import Path

import finalize_stationary_ddi as stationary


ARTP_MAGIC = b"ARTp"


def unit_info(manifest: dict[str, object], index: int) -> stationary.DdbInfo:
    units = manifest.get("units")
    if not isinstance(units, list) or index < 0 or index >= len(units):
        raise stationary.FinalizeError(f"manifest has no unit {index}")
    item = units[index]
    if not isinstance(item, dict):
        raise stationary.FinalizeError(f"manifest unit {index} is not an object")
    try:
        info = stationary.DdbInfo(
            frame_offsets=[int(value) for value in item["frame_offsets"]],
            snd_offset=int(item["snd_offset"]),
            snd_size=int(item["snd_size"]),
            sample_rate=int(item["sample_rate"]),
            channels=int(item["channels"]),
            pcm_count=int(item["pcm_count"]),
        )
    except (KeyError, TypeError, ValueError) as error:
        raise stationary.FinalizeError(f"invalid manifest unit {index}: {error}") from error
    if not info.frame_offsets:
        raise stationary.FinalizeError(f"manifest unit {index} has no frames")
    expected_pcm = (
        len(info.frame_offsets) * stationary.HOP_SAMPLES
        + 2 * stationary.ANALYSIS_MARGIN_SAMPLES
    )
    if (
        info.sample_rate != stationary.SAMPLE_RATE
        or info.channels != 1
        or info.pcm_count != expected_pcm
    ):
        raise stationary.FinalizeError(f"manifest unit {index} is not a valid ordinary unit")
    return info


def articulation_positions(skeleton: bytes) -> list[int]:
    positions: list[int] = []
    cursor = 0
    while True:
        cursor = skeleton.find(ARTP_MAGIC, cursor)
        if cursor < 0:
            return positions
        positions.append(cursor)
        cursor += len(ARTP_MAGIC)


def insert_articulation_at(
    skeleton: bytes,
    artp: int,
    ddb: stationary.DdbInfo,
    pitch_hz: float,
    split_frame: int | None,
    unknown2: float,
    dynamics: float,
    tempo: float,
    alignment_groups: tuple[tuple[int, int, int, int], ...] | None = None,
) -> tuple[bytes, dict[str, object]]:
    if artp < 0 or skeleton[artp : artp + len(ARTP_MAGIC)] != ARTP_MAGIC:
        raise stationary.FinalizeError(f"no ARTp begins at 0x{artp:x}")

    cursor = artp + 4
    cursor = stationary.expect(
        skeleton, cursor, struct.pack("<III", 0, 0, 1), "ARTp header"
    )
    duration_offset = cursor
    cursor += 8
    cursor = stationary.expect(
        skeleton, cursor, struct.pack("<H", 1), "ARTp scalar marker"
    )
    pitch1_offset = cursor
    cursor += 5 * 4
    cursor = stationary.expect(
        skeleton, cursor, struct.pack("<I", 2), "ARTp child count"
    )

    snd_source_offset_position = cursor
    (snd_source_offset,) = struct.unpack_from("<Q", skeleton, cursor)
    if snd_source_offset <= 0:
        raise stationary.FinalizeError(
            f"ARTp SND source offset must be positive, got 0x{snd_source_offset:x}"
        )
    cursor += 8
    cursor = stationary.expect(
        skeleton, cursor, stationary.EMPTY_SND, "ARTp SND empty reference"
    )
    epr_source_offset_position = cursor
    cursor += 8
    cursor = stationary.expect(
        skeleton, cursor, stationary.EMPTY_EPR, "ARTp EpR empty reference"
    )
    metadata_position = cursor

    frame_count = len(ddb.frame_offsets)
    boundary = frame_count // 2 if split_frame is None else split_frame
    if boundary <= 0 or boundary >= frame_count:
        raise stationary.FinalizeError(
            f"split frame must be inside 1..{frame_count - 1}, got {boundary}"
        )
    duration = ddb.pcm_count / ddb.sample_rate
    pitch_cents = 1200.0 * math.log2(pitch_hz / 440.0)
    epr_source_offset = snd_source_offset + ddb.snd_size + 4 + len("EpR")
    snd_payload_pointer = ddb.snd_offset + stationary.SND_HEADER.size
    snd_core_pointer = (
        snd_payload_pointer + stationary.ANALYSIS_MARGIN_SAMPLES * 2
    )
    alignments = alignment_groups or (
        (0, boundary, 0, boundary),
        (boundary, frame_count, boundary, frame_count),
    )
    if len(alignments) != 2:
        raise stationary.FinalizeError("ARTp must have exactly two alignment groups")
    first, second = alignments
    if first[0] != 0 or first[1] != boundary or second[0] != boundary or second[1] != frame_count:
        raise stationary.FinalizeError(
            "alignment outer ranges must be [0, split) and [split, frame_count)"
        )
    for index, (outer_start, outer_end, inner_start, inner_end) in enumerate(alignments):
        if not (outer_start <= inner_start <= inner_end <= outer_end):
            raise stationary.FinalizeError(
                f"alignment {index} inner range [{inner_start}, {inner_end}) "
                f"is outside outer range [{outer_start}, {outer_end})"
            )

    metadata = bytearray()
    metadata += struct.pack("<I", frame_count)
    metadata += struct.pack(f"<{frame_count}Q", *ddb.frame_offsets)
    metadata += struct.pack(
        "<IHIQQI",
        ddb.sample_rate,
        ddb.channels,
        ddb.pcm_count,
        snd_payload_pointer,
        snd_core_pointer,
        len(alignments),
    )
    for alignment in alignments:
        metadata += struct.pack("<iiii", *alignment)

    result = bytearray(skeleton)
    struct.pack_into("<d", result, duration_offset, duration)
    struct.pack_into(
        "<fffff",
        result,
        pitch1_offset,
        pitch_cents,
        pitch_cents,
        unknown2,
        dynamics,
        tempo,
    )
    struct.pack_into("<Q", result, epr_source_offset_position, epr_source_offset)
    result[metadata_position:metadata_position] = metadata
    return bytes(result), {
        "artp_skeleton_offset": artp,
        "frame_count": frame_count,
        "duration_seconds": duration,
        "pitch_hz": pitch_hz,
        "pitch_cents_relative_to_a4": pitch_cents,
        "snd_source_offset": snd_source_offset,
        "epr_source_offset": epr_source_offset,
        "snd_chunk_offset": ddb.snd_offset,
        "snd_payload_pointer": snd_payload_pointer,
        "snd_core_pointer": snd_core_pointer,
        "pcm_count": ddb.pcm_count,
        "frame_alignments": [list(value) for value in alignments],
        "metadata_bytes_inserted": len(metadata),
    }


def insert_articulation(
    skeleton: bytes,
    ddb: stationary.DdbInfo,
    pitch_hz: float,
    split_frame: int | None,
    unknown2: float,
    dynamics: float,
    tempo: float,
    alignment_groups: tuple[tuple[int, int, int, int], ...] | None = None,
) -> tuple[bytes, dict[str, object]]:
    positions = articulation_positions(skeleton)
    if len(positions) != 1:
        raise stationary.FinalizeError("skeleton must contain exactly one ARTp")
    return insert_articulation_at(
        skeleton,
        positions[0],
        ddb,
        pitch_hz,
        split_frame,
        unknown2,
        dynamics,
        tempo,
        alignment_groups,
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("skeleton", type=Path)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--pitch-hz", type=float, required=True)
    parser.add_argument("--sta-unit", type=int, default=0)
    parser.add_argument("--art-unit", type=int, default=1)
    parser.add_argument("--split-frame", type=int)
    parser.add_argument("--singer-name")
    parser.add_argument("--unknown2", type=float, default=0.0)
    parser.add_argument("--dynamics", type=float, default=0.6)
    parser.add_argument("--tempo", type=float, default=90.0)
    args = parser.parse_args()

    if not math.isfinite(args.pitch_hz) or args.pitch_hz <= 0:
        parser.error("--pitch-hz must be finite and positive")
    try:
        manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
        sta = unit_info(manifest, args.sta_unit)
        art = unit_info(manifest, args.art_unit)
        with_art, art_report = insert_articulation(
            args.skeleton.read_bytes(),
            art,
            args.pitch_hz,
            args.split_frame,
            args.unknown2,
            args.dynamics,
            args.tempo,
        )
        ddi, sta_report = stationary.finalize_skeleton(
            with_art,
            sta,
            args.pitch_hz,
            args.unknown2,
            args.dynamics,
            args.tempo,
            args.singer_name or args.output.stem,
        )
        stationary.write_atomic(args.output, ddi)
    except (OSError, ValueError, json.JSONDecodeError, struct.error,
            stationary.FinalizeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2

    print(
        json.dumps(
            {
                "output": str(args.output.resolve()),
                "output_bytes": len(ddi),
                "stationary": sta_report,
                "articulation": art_report,
            },
            ensure_ascii=False,
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
