#!/usr/bin/env python3
"""Turn a one-STAp DSE tree skeleton plus its DDB into a compact DDI."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import struct
import sys
from dataclasses import dataclass
from pathlib import Path

import probe_frm2


SAMPLE_RATE = 44100
HOP_SAMPLES = 256
ANALYSIS_MARGIN_SAMPLES = 1024
SND_HEADER = struct.Struct("<4sIIHI")
STAP_MAGIC = b"STAp"
EMPTY_SND = b"EMPT\x00\x00\x00\x00\x03\x00\x00\x00SND"
EMPTY_EPR = b"EMPT\x00\x00\x00\x00\x03\x00\x00\x00EpR"
ZERO_SIZE_CHUNKS = (b"TDB ", b"TMM ", b"ARR ", b"DBV ", b"STA ", b"STAu", b"STAp", b"ART ", b"ARTu", b"ARTp")
MATERIALIZED_CHUNKS = (b"DBV ", b"STA ", b"STAu", b"STAp", b"ART ", b"ARTu")


class FinalizeError(Exception):
    pass


@dataclass(frozen=True)
class DdbInfo:
    frame_offsets: list[int]
    snd_offset: int
    snd_size: int
    sample_rate: int
    channels: int
    pcm_count: int


def inspect_ddb(path: Path) -> DdbInfo:
    data = path.read_bytes()
    offsets: list[int] = []
    position = 0
    snd: tuple[int, int, int, int, int] | None = None
    while position < len(data):
        if len(data) - position < 8:
            raise FinalizeError(f"truncated DDB chunk header at 0x{position:x}")
        magic, size = struct.unpack_from("<4sI", data, position)
        if size < 8 or position + size > len(data):
            raise FinalizeError(
                f"invalid {magic!r} chunk size {size} at 0x{position:x}"
            )
        if magic == b"FRM2":
            if snd is not None:
                raise FinalizeError("FRM2 occurs after SND")
            raw = data[position : position + size]
            frame = probe_frm2.parse_frame(raw)
            if probe_frm2.serialize_frame(frame) != raw:
                raise FinalizeError(f"FRM2 at 0x{position:x} does not round-trip")
            offsets.append(position)
        elif magic == b"SND ":
            if snd is not None:
                raise FinalizeError("DDB contains more than one SND chunk")
            if size < SND_HEADER.size:
                raise FinalizeError("SND is shorter than its header")
            _, declared_size, sample_rate, channels, pcm_count = SND_HEADER.unpack_from(
                data, position
            )
            if declared_size != size:
                raise FinalizeError("SND size fields disagree")
            expected_size = SND_HEADER.size + pcm_count * channels * 2
            if expected_size != size:
                raise FinalizeError(
                    f"SND size is {size}; PCM metadata implies {expected_size}"
                )
            snd = position, size, sample_rate, channels, pcm_count
        else:
            raise FinalizeError(f"unexpected DDB chunk {magic!r} at 0x{position:x}")
        position += size

    if not offsets:
        raise FinalizeError("DDB contains no FRM2 frames")
    if snd is None:
        raise FinalizeError("DDB contains no SND chunk")
    snd_offset, snd_size, sample_rate, channels, pcm_count = snd
    if sample_rate != SAMPLE_RATE or channels != 1:
        raise FinalizeError(
            f"stationary unit must be 44.1 kHz mono, got {sample_rate} Hz/{channels} ch"
        )
    expected_pcm_count = len(offsets) * HOP_SAMPLES + 2 * ANALYSIS_MARGIN_SAMPLES
    if pcm_count != expected_pcm_count:
        raise FinalizeError(
            f"SND has {pcm_count} samples; {len(offsets)} FRM2 frames require "
            f"{expected_pcm_count}"
        )
    return DdbInfo(
        frame_offsets=offsets,
        snd_offset=snd_offset,
        snd_size=snd_size,
        sample_rate=sample_rate,
        channels=channels,
        pcm_count=pcm_count,
    )


def expect(data: bytes, offset: int, value: bytes, description: str) -> int:
    actual = data[offset : offset + len(value)]
    if actual != value:
        raise FinalizeError(
            f"unexpected {description} at 0x{offset:x}: {actual.hex()} != {value.hex()}"
        )
    return offset + len(value)


def normalize_compact_ddi(data: bytearray) -> dict[str, int]:
    if data[8:12] != b"DBS ":
        raise FinalizeError(f"unexpected root magic {bytes(data[8:12])!r}")
    data[8:12] = b"DBSe"
    struct.pack_into("<I", data, 12, 0)

    size_fields = 0
    materialized_offsets = 0
    for magic in ZERO_SIZE_CHUNKS:
        position = 0
        while True:
            position = data.find(magic, position)
            if position < 0:
                break
            if position + 8 > len(data):
                raise FinalizeError(f"truncated {magic!r} chunk")
            struct.pack_into("<I", data, position + 4, 0)
            size_fields += 1
            if magic in MATERIALIZED_CHUNKS:
                if position < 8:
                    raise FinalizeError(f"{magic!r} has no compact source position")
                source_position = bytes(data[position - 8 : position])
                if source_position not in (b"\x00" * 8, b"\xff" * 8):
                    raise FinalizeError(
                        f"unexpected source position before {magic!r} at 0x{position:x}"
                    )
                data[position - 8 : position] = b"\x00" * 8
                materialized_offsets += 1
            position += 4
    position = 0
    while True:
        position = data.find(b"ARR ", position)
        if position < 0:
            break
        if position >= 8 and position + 32 <= len(data):
            child_count = struct.unpack_from("<I", data, position + 16)[0]
            first_child_magic = bytes(data[position + 28 : position + 32])
            if child_count > 0 and first_child_magic in MATERIALIZED_CHUNKS:
                source_position = bytes(data[position - 8 : position])
                if source_position not in (b"\x00" * 8, b"\xff" * 8):
                    raise FinalizeError(
                        f"unexpected source position before materialized ARR at 0x{position:x}"
                    )
                data[position - 8 : position] = b"\x00" * 8
                materialized_offsets += 1
        position += 4
    return {
        "normalized_size_fields": size_fields,
        "normalized_materialized_offsets": materialized_offsets,
    }


def insert_dbse_authentication(data: bytearray, singer_name: str) -> dict[str, object]:
    try:
        encoded_name = singer_name.upper().encode("ascii")
    except UnicodeEncodeError as error:
        raise FinalizeError("singer name must be ASCII for the DSE DBSe digest") from error
    phdc = data.find(b"PHDC")
    if phdc < 0 or phdc + 8 > len(data):
        raise FinalizeError("skeleton has no complete PHDC chunk")
    phdc_size = struct.unpack_from("<I", data, phdc + 4)[0]
    phdc_end = phdc + phdc_size
    if phdc_size < 8 or phdc_end > len(data):
        raise FinalizeError(f"invalid PHDC size {phdc_size}")
    digest = hashlib.md5(b"K2ho" + encoded_name + b"nF").hexdigest().encode("ascii")
    authentication = digest + b"\x00" * (0x104 - len(digest))
    data[phdc_end:phdc_end] = authentication
    return {
        "dbse_singer_name": singer_name,
        "dbse_digest": digest.decode("ascii"),
        "dbse_authentication_bytes_inserted": len(authentication),
    }


def stationary_positions(skeleton: bytes) -> list[int]:
    positions: list[int] = []
    cursor = 0
    while True:
        cursor = skeleton.find(STAP_MAGIC, cursor)
        if cursor < 0:
            return positions
        positions.append(cursor)
        cursor += len(STAP_MAGIC)


def insert_stationary_at(
    skeleton: bytes,
    stap: int,
    ddb: DdbInfo,
    pitch_hz: float,
    unknown2: float,
    dynamics: float,
    tempo: float,
) -> tuple[bytes, dict[str, object]]:
    if stap < 0 or skeleton[stap : stap + len(STAP_MAGIC)] != STAP_MAGIC:
        raise FinalizeError(f"no STAp begins at 0x{stap:x}")

    cursor = stap + 4
    cursor = expect(skeleton, cursor, struct.pack("<III", 0, 0, 1), "STAp header")
    duration_offset = cursor
    cursor += 8
    cursor = expect(skeleton, cursor, struct.pack("<H", 1), "STAp scalar marker")
    pitch1_offset = cursor
    cursor += 5 * 4
    cursor = expect(skeleton, cursor, struct.pack("<II", 0, 2), "STAp child count")

    snd_source_offset_position = cursor
    (snd_source_offset,) = struct.unpack_from("<Q", skeleton, cursor)
    if snd_source_offset != 0x3D:
        raise FinalizeError(
            f"SND source offset is 0x{snd_source_offset:x}; expected canonical 0x3d"
        )
    cursor += 8
    cursor = expect(skeleton, cursor, EMPTY_SND, "SND empty reference")
    epr_source_offset_position = cursor
    cursor += 8
    cursor = expect(skeleton, cursor, EMPTY_EPR, "EpR empty reference")
    metadata_position = cursor

    duration = ddb.pcm_count / ddb.sample_rate
    pitch_cents = 1200.0 * math.log2(pitch_hz / 440.0)
    epr_source_offset = 0x3D + ddb.snd_size + 4 + len("EpR")
    snd_pointer = (
        ddb.snd_offset + SND_HEADER.size + ANALYSIS_MARGIN_SAMPLES * 2
    )
    metadata = bytearray()
    metadata += struct.pack("<iI", -1, len(ddb.frame_offsets))
    metadata += struct.pack(f"<{len(ddb.frame_offsets)}Q", *ddb.frame_offsets)
    metadata += struct.pack(
        "<IHIQiiii",
        ddb.sample_rate,
        ddb.channels,
        ddb.pcm_count,
        snd_pointer,
        -1,
        -1,
        -1,
        -1,
    )

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

    report: dict[str, object] = {
        "stap_skeleton_offset": stap,
        "frame_count": len(ddb.frame_offsets),
        "duration_seconds": duration,
        "pitch_hz": pitch_hz,
        "pitch_cents_relative_to_a4": pitch_cents,
        "snd_source_offset": snd_source_offset,
        "epr_source_offset": epr_source_offset,
        "snd_chunk_offset": ddb.snd_offset,
        "snd_pointer": snd_pointer,
        "pcm_count": ddb.pcm_count,
        "metadata_bytes_inserted": len(metadata),
        "integrity_payload": "four signed -1 values (absent)",
    }
    return bytes(result), report


def finalize_skeleton(
    skeleton: bytes,
    ddb: DdbInfo,
    pitch_hz: float,
    unknown2: float,
    dynamics: float,
    tempo: float,
    singer_name: str,
) -> tuple[bytes, dict[str, object]]:
    positions = stationary_positions(skeleton)
    if len(positions) != 1:
        raise FinalizeError("skeleton must contain exactly one STAp")
    inserted, report = insert_stationary_at(
        skeleton,
        positions[0],
        ddb,
        pitch_hz,
        unknown2,
        dynamics,
        tempo,
    )
    result = bytearray(inserted)
    authentication = insert_dbse_authentication(result, singer_name)
    normalization = normalize_compact_ddi(result)
    report.update(normalization)
    report.update(authentication)
    return bytes(result), report


def write_atomic(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    try:
        with temporary.open("wb") as output:
            output.write(data)
            output.flush()
            os.fsync(output.fileno())
        temporary.replace(path)
    finally:
        if temporary.exists():
            temporary.unlink()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("skeleton", type=Path, help="one-STAp .tree from tree_harness")
    parser.add_argument("ddb", type=Path, help="matching one-unit stationary DDB")
    parser.add_argument("output", type=Path, help="output compact DDI")
    parser.add_argument("--pitch-hz", type=float, required=True)
    parser.add_argument(
        "--singer-name",
        help="base name used to open <name>.ddi; defaults to the output stem",
    )
    parser.add_argument("--unknown2", type=float, default=0.0)
    parser.add_argument("--dynamics", type=float, default=0.6)
    parser.add_argument("--tempo", type=float, default=90.0)
    args = parser.parse_args()

    if not math.isfinite(args.pitch_hz) or args.pitch_hz <= 0:
        parser.error("--pitch-hz must be finite and positive")
    for name in ("unknown2", "dynamics", "tempo"):
        if not math.isfinite(getattr(args, name)):
            parser.error(f"--{name} must be finite")

    try:
        ddb = inspect_ddb(args.ddb)
        ddi, report = finalize_skeleton(
            args.skeleton.read_bytes(),
            ddb,
            args.pitch_hz,
            args.unknown2,
            args.dynamics,
            args.tempo,
            args.singer_name or args.output.stem,
        )
        write_atomic(args.output, ddi)
    except (OSError, OverflowError, ValueError, struct.error, FinalizeError,
            probe_frm2.ProbeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2

    report.update(
        {
            "skeleton": str(args.skeleton.resolve()),
            "ddb": str(args.ddb.resolve()),
            "output": str(args.output.resolve()),
            "output_bytes": len(ddi),
        }
    )
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
