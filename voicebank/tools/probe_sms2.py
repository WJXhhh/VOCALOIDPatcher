#!/usr/bin/env python3
"""Locate and summarize embedded FRM2 runs in a DRS SMS2 analysis file."""

from __future__ import annotations

import argparse
import json
import math
import mmap
import struct
import sys
from collections import Counter
from pathlib import Path
from typing import NoReturn


CHUNK_HEADER = struct.Struct("<4sI")
FRAME_PREFIX = struct.Struct("<IdQ")
FLOAT32_MAX = struct.unpack("<f", bytes.fromhex("ffff7f7f"))[0]


class ProbeError(Exception):
    pass


def fail(message: str) -> NoReturn:
    raise ProbeError(message)


def require_range(position: int, length: int, end: int, context: str) -> None:
    if length < 0 or position < 0 or position + length > end:
        fail(f"{context} at 0x{position:x} exceeds end 0x{end:x}")


def parse_frame_leading_fields(
    data: mmap.mmap, offset: int, file_size: int
) -> dict[str, object]:
    require_range(offset, 28, file_size, "FRM2 prefix")
    magic, chunk_size = CHUNK_HEADER.unpack_from(data, offset)
    if magic != b"FRM2":
        fail(f"expected FRM2 at 0x{offset:x}, found {magic!r}")
    if chunk_size < 28:
        fail(f"invalid FRM2 size {chunk_size} at 0x{offset:x}")
    frame_end = offset + chunk_size
    require_range(offset, chunk_size, file_size, "FRM2")

    kind, time_seconds, mask = FRAME_PREFIX.unpack_from(data, offset + 8)
    position = offset + 28
    harmonic_count: int | None = None
    secondary_count: int | None = None

    if mask & 0x7:
        require_range(position, 4, frame_end, "harmonic count")
        harmonic_count = struct.unpack_from("<I", data, position)[0]
        position += 4
        array_count = sum(bool(mask & (1 << bit)) for bit in (1, 0, 2))
        require_range(
            position,
            harmonic_count * 4 * array_count,
            frame_end,
            "harmonic arrays",
        )
        position += harmonic_count * 4 * array_count

    if mask & 0x30:
        require_range(position, 4, frame_end, "secondary spectrum count")
        secondary_count = struct.unpack_from("<I", data, position)[0]
        position += 4
        array_count = int(bool(mask & 0x10)) + int(bool(mask & 0x20))
        require_range(
            position,
            secondary_count * 4 * array_count,
            frame_end,
            "secondary spectrum arrays",
        )
        position += secondary_count * 4 * array_count

    if mask & 0x40:
        require_range(position, 4, frame_end, "int16 spectrum count")
        count = struct.unpack_from("<I", data, position)[0]
        position += 4
        require_range(position, count * 2, frame_end, "int16 spectrum")
        position += count * 2

    serialized_pitch: float | None = None
    if mask & 0x200:
        require_range(position, 4, frame_end, "F0/pitch")
        serialized_pitch = struct.unpack_from("<f", data, position)[0]
        position += 4

    pitch_hz: float | None = None
    is_unvoiced = False
    if serialized_pitch is not None:
        is_unvoiced = not math.isfinite(serialized_pitch) or serialized_pitch <= -0.99 * FLOAT32_MAX
        if not is_unvoiced:
            if mask & 0x80000000:
                pitch_hz = 440.0 * 2.0 ** (serialized_pitch / 1200.0)
            elif serialized_pitch > 0.0:
                pitch_hz = serialized_pitch

    return {
        "offset": offset,
        "chunk_size": chunk_size,
        "kind": kind,
        "time_seconds": time_seconds,
        "mask": mask,
        "harmonic_count": harmonic_count,
        "secondary_count": secondary_count,
        "serialized_pitch": serialized_pitch,
        "pitch_hz": pitch_hz,
        "is_unvoiced": is_unvoiced,
        "leading_bytes_validated": position - offset,
    }


def find_frame_runs(data: mmap.mmap, file_size: int) -> list[list[dict[str, object]]]:
    runs: list[list[dict[str, object]]] = []
    search_position = 0
    while search_position < file_size:
        offset = data.find(b"FRM2", search_position)
        if offset < 0:
            break
        try:
            first = parse_frame_leading_fields(data, offset, file_size)
        except (ProbeError, struct.error):
            search_position = offset + 4
            continue

        run = [first]
        next_offset = offset + int(first["chunk_size"])
        while next_offset + 28 <= file_size and data[next_offset : next_offset + 4] == b"FRM2":
            frame = parse_frame_leading_fields(data, next_offset, file_size)
            run.append(frame)
            next_offset += int(frame["chunk_size"])
        runs.append(run)
        search_position = next_offset
    return runs


def numeric_range(values: list[float | int | None]) -> list[float | int] | None:
    present = [value for value in values if value is not None]
    if not present:
        return None
    return [min(present), max(present)]


def summarize(path: Path) -> dict[str, object]:
    file_size = path.stat().st_size
    with path.open("rb") as stream, mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as data:
        if file_size < 8:
            fail("file is shorter than an SMS2 header")
        magic, declared_size = CHUNK_HEADER.unpack_from(data, 0)
        if magic != b"SMS2":
            fail(f"expected SMS2, found {magic!r}")
        if declared_size != file_size:
            fail(f"SMS2 size field is {declared_size}, file size is {file_size}")
        runs = find_frame_runs(data, file_size)

    frames = [frame for run in runs for frame in run]
    if not frames:
        fail("no structurally valid embedded FRM2 run found")

    masks = Counter(f"0x{int(frame['mask']):016x}" for frame in frames)
    kinds = Counter(int(frame["kind"]) for frame in frames)
    pitches = [float(frame["pitch_hz"]) for frame in frames if frame["pitch_hz"] is not None]
    times = [float(frame["time_seconds"]) for frame in frames]
    steps = [later - earlier for earlier, later in zip(times, times[1:]) if later >= earlier]
    frame_bits = sorted({bit for frame in frames for bit in range(64) if int(frame["mask"]) & (1 << bit)})

    return {
        "file_name": path.name,
        "file_size": file_size,
        "declared_size": declared_size,
        "frame_run_count": len(runs),
        "frame_run_lengths": [len(run) for run in runs],
        "frame_count": len(frames),
        "frame_masks": dict(sorted(masks.items())),
        "field_bits_seen": frame_bits,
        "frame_kinds": dict(sorted(kinds.items())),
        "frame_size_range": numeric_range([int(frame["chunk_size"]) for frame in frames]),
        "time_range_seconds": numeric_range(times),
        "time_step_range_seconds": numeric_range(steps),
        "harmonic_count_range": numeric_range(
            [frame["harmonic_count"] for frame in frames]
        ),
        "secondary_count_range": numeric_range(
            [frame["secondary_count"] for frame in frames]
        ),
        "voiced_frame_count": len(pitches),
        "unvoiced_frame_count": sum(bool(frame["is_unvoiced"]) for frame in frames),
        "pitch_range_hz": numeric_range(pitches),
        "first_frames": frames[:3],
        "leading_layouts_structurally_valid": True,
    }


def print_human(summary: dict[str, object]) -> None:
    print(f"file: {summary['file_name']}")
    print(
        f"size: {summary['file_size']}  frame runs: {summary['frame_run_lengths']}  "
        f"frames: {summary['frame_count']}"
    )
    print(f"masks: {summary['frame_masks']}")
    print(f"field bits: {summary['field_bits_seen']}")
    print(
        f"frame sizes: {summary['frame_size_range']}  "
        f"time: {summary['time_range_seconds']}  step: {summary['time_step_range_seconds']}"
    )
    print(
        f"voiced/unvoiced: {summary['voiced_frame_count']}/{summary['unvoiced_frame_count']}  "
        f"pitch Hz: {summary['pitch_range_hz']}"
    )
    print(
        f"harmonics: {summary['harmonic_count_range']}  "
        f"secondary spectrum: {summary['secondary_count_range']}"
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sms2", type=Path, help="DRS SMS2 file to inspect")
    parser.add_argument("--json", action="store_true", help="emit JSON")
    args = parser.parse_args()
    try:
        summary = summarize(args.sms2)
    except (OSError, ProbeError, struct.error, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    if args.json:
        print(json.dumps(summary, ensure_ascii=False, indent=2))
    else:
        print_human(summary)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
