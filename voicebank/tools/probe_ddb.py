#!/usr/bin/env python3
"""Read-only structural probe for VOCALOID traditional DDB files.

The probe walks the top-level chunk stream without reading or exporting PCM
payloads.  It intentionally depends only on the Python standard library.
"""

from __future__ import annotations

import argparse
import json
import struct
import sys
from collections import Counter
from pathlib import Path
from typing import BinaryIO, NoReturn


CHUNK_HEADER = struct.Struct("<4sI")
SND_HEADER = struct.Struct("<IHI")
FRM2_PREFIX = struct.Struct("<IdQ")
KNOWN_MAGICS = {b"FRM2", b"SND "}


class ProbeError(Exception):
    pass


def fail(message: str) -> NoReturn:
    raise ProbeError(message)


def read_exact(stream: BinaryIO, length: int, context: str) -> bytes:
    data = stream.read(length)
    if len(data) != length:
        fail(f"short read while reading {context}: wanted {length}, got {len(data)}")
    return data


def update_range(summary: dict[str, int | None], value: int) -> None:
    summary["count"] = int(summary["count"] or 0) + 1
    summary["total_bytes"] = int(summary["total_bytes"] or 0) + value
    current_min = summary["min_bytes"]
    current_max = summary["max_bytes"]
    summary["min_bytes"] = value if current_min is None else min(current_min, value)
    summary["max_bytes"] = value if current_max is None else max(current_max, value)


def probe(path: Path) -> dict[str, object]:
    file_size = path.stat().st_size
    chunk_sizes: dict[str, dict[str, int | None]] = {}
    snd_rates: Counter[int] = Counter()
    snd_channels: Counter[int] = Counter()
    snd_sample_counts: dict[str, int | None] = {
        "min": None,
        "max": None,
        "total": 0,
    }
    frm2_masks: Counter[str] = Counter()
    frm2_kinds: Counter[int] = Counter()
    frm2_time_values: dict[str, float | None] = {"min": None, "max": None}
    offset = 0
    chunk_count = 0

    with path.open("rb") as stream:
        while offset < file_size:
            remaining = file_size - offset
            if remaining < CHUNK_HEADER.size:
                fail(f"trailing {remaining} byte(s) at 0x{offset:x}")

            stream.seek(offset)
            magic, chunk_size = CHUNK_HEADER.unpack(
                read_exact(stream, CHUNK_HEADER.size, f"chunk header at 0x{offset:x}")
            )
            if magic not in KNOWN_MAGICS:
                fail(f"unknown chunk magic {magic!r} at 0x{offset:x}")
            if chunk_size < CHUNK_HEADER.size:
                fail(f"invalid chunk size {chunk_size} at 0x{offset:x}")

            chunk_end = offset + chunk_size
            if chunk_end > file_size:
                fail(
                    f"chunk {magic!r} at 0x{offset:x} ends at 0x{chunk_end:x}, "
                    f"past file size 0x{file_size:x}"
                )

            name = magic.decode("ascii")
            size_summary = chunk_sizes.setdefault(
                name,
                {"count": 0, "min_bytes": None, "max_bytes": None, "total_bytes": 0},
            )
            update_range(size_summary, chunk_size)

            if magic == b"SND ":
                if chunk_size < CHUNK_HEADER.size + SND_HEADER.size:
                    fail(f"short SND chunk ({chunk_size} bytes) at 0x{offset:x}")
                sample_rate, channels, sample_count = SND_HEADER.unpack(
                    read_exact(stream, SND_HEADER.size, f"SND metadata at 0x{offset:x}")
                )
                expected_size = CHUNK_HEADER.size + SND_HEADER.size + sample_count * 2
                if chunk_size != expected_size:
                    fail(
                        f"SND size mismatch at 0x{offset:x}: header says {chunk_size}, "
                        f"sample count implies {expected_size}"
                    )
                if sample_rate == 0:
                    fail(f"zero SND sample rate at 0x{offset:x}")
                if channels == 0:
                    fail(f"zero SND channel count at 0x{offset:x}")
                snd_rates[sample_rate] += 1
                snd_channels[channels] += 1
                snd_sample_counts["total"] = int(snd_sample_counts["total"] or 0) + sample_count
                current_min = snd_sample_counts["min"]
                current_max = snd_sample_counts["max"]
                snd_sample_counts["min"] = (
                    sample_count if current_min is None else min(current_min, sample_count)
                )
                snd_sample_counts["max"] = (
                    sample_count if current_max is None else max(current_max, sample_count)
                )
            else:
                minimum_size = CHUNK_HEADER.size + FRM2_PREFIX.size
                if chunk_size < minimum_size:
                    fail(f"short FRM2 chunk ({chunk_size} bytes) at 0x{offset:x}")
                frame_kind, time_value, field_mask = FRM2_PREFIX.unpack(
                    read_exact(stream, FRM2_PREFIX.size, f"FRM2 prefix at 0x{offset:x}")
                )
                frm2_kinds[frame_kind] += 1
                frm2_masks[f"0x{field_mask:016x}"] += 1
                current_min = frm2_time_values["min"]
                current_max = frm2_time_values["max"]
                frm2_time_values["min"] = (
                    time_value if current_min is None else min(current_min, time_value)
                )
                frm2_time_values["max"] = (
                    time_value if current_max is None else max(current_max, time_value)
                )

            chunk_count += 1
            offset = chunk_end

    return {
        "file_name": path.name,
        "file_size": file_size,
        "chunk_count": chunk_count,
        "chunks": chunk_sizes,
        "snd": {
            "sample_rates": dict(sorted(snd_rates.items())),
            "channels": dict(sorted(snd_channels.items())),
            "sample_counts": snd_sample_counts,
        },
        "frm2": {
            "frame_kinds": dict(sorted(frm2_kinds.items())),
            "field_masks": dict(sorted(frm2_masks.items())),
            "time_values": frm2_time_values,
        },
        "validated_bytes": offset,
    }


def print_human(summary: dict[str, object]) -> None:
    print(f"file: {summary['file_name']}")
    print(f"size: {summary['file_size']} bytes")
    print(f"chunks: {summary['chunk_count']}")
    for magic, values in summary["chunks"].items():
        print(
            f"  {magic!r}: count={values['count']} "
            f"min={values['min_bytes']} max={values['max_bytes']} "
            f"total={values['total_bytes']}"
        )
    snd = summary["snd"]
    print(f"SND sample rates: {snd['sample_rates']}")
    print(f"SND channels: {snd['channels']}")
    print(f"SND sample counts: {snd['sample_counts']}")
    frm2 = summary["frm2"]
    print(f"FRM2 frame kinds: {frm2['frame_kinds']}")
    print(f"FRM2 time values: {frm2['time_values']}")
    print("FRM2 field masks:")
    for mask, count in frm2["field_masks"].items():
        print(f"  {mask}: {count}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("ddb", type=Path, help="DDB file to inspect")
    parser.add_argument("--json", action="store_true", help="emit JSON")
    args = parser.parse_args()

    try:
        summary = probe(args.ddb)
    except (OSError, ProbeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2

    if args.json:
        print(json.dumps(summary, ensure_ascii=False, indent=2))
    else:
        print_human(summary)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
