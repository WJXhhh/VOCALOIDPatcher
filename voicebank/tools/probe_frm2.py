#!/usr/bin/env python3
"""Read, validate, summarize, and round-trip one traditional FRM2 chunk."""

from __future__ import annotations

import argparse
import json
import math
import mmap
import struct
import sys
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import NoReturn


CHUNK_HEADER = struct.Struct("<4sI")
FRAME_PREFIX = struct.Struct("<IdQ")
MAIN_MASK = 0x0000002000E00207
UNVOICED_MASK = 0x0000000000000200
VQM_MASK = 0x00000000000E22B7


class ProbeError(Exception):
    pass


def fail(message: str) -> NoReturn:
    raise ProbeError(message)


class Cursor:
    def __init__(self, data: bytes, position: int = 0) -> None:
        self.data = data
        self.position = position

    def unpack(self, fmt: str, context: str) -> tuple[object, ...]:
        parser = struct.Struct(fmt)
        end = self.position + parser.size
        if end > len(self.data):
            fail(
                f"short {context} at chunk offset 0x{self.position:x}: "
                f"wanted {parser.size} byte(s), only {len(self.data) - self.position} remain"
            )
        values = parser.unpack_from(self.data, self.position)
        self.position = end
        return values

    def u32(self, context: str) -> int:
        return int(self.unpack("<I", context)[0])

    def u8(self, context: str) -> int:
        return int(self.unpack("<B", context)[0])

    def f32(self, context: str) -> float:
        return float(self.unpack("<f", context)[0])

    def f32_array(self, count: int, context: str) -> tuple[float, ...]:
        if count < 0:
            fail(f"negative element count for {context}")
        if count == 0:
            return ()
        return tuple(float(value) for value in self.unpack(f"<{count}f", context))

    def f64_triples(self, count: int, context: str) -> tuple[tuple[float, float, float], ...]:
        triples: list[tuple[float, float, float]] = []
        for index in range(count):
            values = self.unpack("<3d", f"{context}[{index}]")
            triples.append((float(values[0]), float(values[1]), float(values[2])))
        return tuple(triples)

    def take(self, length: int, context: str) -> bytes:
        if length < 0:
            fail(f"negative byte count for {context}")
        end = self.position + length
        if end > len(self.data):
            fail(
                f"short {context} at chunk offset 0x{self.position:x}: "
                f"wanted {length} byte(s), only {len(self.data) - self.position} remain"
            )
        value = self.data[self.position:end]
        self.position = end
        return value


@dataclass(frozen=True)
class Envelope:
    flags: int
    bounds: tuple[float, float, float]
    values: tuple[float, ...]


@dataclass(frozen=True)
class MainFrame:
    kind: int
    time_seconds: float
    mask: int
    frequencies_hz: tuple[float, ...]
    amplitudes: tuple[float, ...]
    phases_radians: tuple[float, ...]
    f0_hz: float
    harmonic_envelope: Envelope
    resonances: tuple[tuple[float, float, float], ...]
    secondary_header: tuple[float, float, float]
    secondary_resonances: tuple[tuple[float, float, float], ...]
    residual_envelope: Envelope


@dataclass(frozen=True)
class UnvoicedFrame:
    kind: int
    time_seconds: float
    mask: int
    f0_hz: float


@dataclass(frozen=True)
class VqmFeatureBlock:
    flags: int
    payload: bytes


@dataclass(frozen=True)
class VqmFrame:
    kind: int
    time_seconds: float
    mask: int
    component_frequencies_hz: tuple[float, ...]
    component_amplitudes: tuple[float, ...]
    component_phases_radians: tuple[float, ...]
    spectrum_amplitudes: tuple[float, ...]
    spectrum_phases_radians: tuple[float, ...]
    f0_hz: float
    feature_block: VqmFeatureBlock
    feature_trailer: int
    bit18_value: int
    bit19_value: int
    bit7_envelope: Envelope
    bit17_envelope: Envelope


def parse_envelope(cursor: Cursor, context: str) -> Envelope:
    start = cursor.position
    magic, chunk_size = cursor.unpack("<4sI", f"{context} header")
    if magic != b"ENV ":
        fail(f"expected ENV at chunk offset 0x{start:x}, found {magic!r}")
    if int(chunk_size) < 28:
        fail(f"invalid {context} size {chunk_size} at chunk offset 0x{start:x}")

    flags = cursor.u32(f"{context} flags")
    data_bytes = cursor.u32(f"{context} data size")
    bounds_raw = cursor.unpack("<3f", f"{context} bounds")
    expected_size = 28 + data_bytes
    if int(chunk_size) != expected_size:
        fail(
            f"{context} size mismatch at chunk offset 0x{start:x}: "
            f"header says {chunk_size}, payload implies {expected_size}"
        )
    if data_bytes % 4:
        fail(f"{context} data size {data_bytes} is not float32-aligned")
    values = cursor.f32_array(data_bytes // 4, f"{context} values")
    if cursor.position != start + int(chunk_size):
        fail(f"{context} cursor mismatch at chunk offset 0x{start:x}")
    return Envelope(
        flags=flags,
        bounds=(float(bounds_raw[0]), float(bounds_raw[1]), float(bounds_raw[2])),
        values=values,
    )


def vqm_feature_payload_size(flags: int) -> int:
    """Return the variable payload length read by CSMSFrame's bit-13 helper."""
    size = 0
    for bit in range(10):
        if flags & (1 << bit):
            size += 4
    for bit in (10, 11):
        if flags & (1 << bit):
            size += 160
    for bit in (12, 13):
        if flags & (1 << bit):
            size += 80
    for bit in range(14, 20):
        if flags & (1 << bit):
            size += 4
    return size


def parse_vqm_feature_block(cursor: Cursor) -> VqmFeatureBlock:
    flags = cursor.u32("VQM bit-13 feature flags")
    unknown_flags = flags & ~0x001FFFFF
    if unknown_flags:
        fail(f"unsupported VQM bit-13 feature flags 0x{unknown_flags:08x}")
    payload = cursor.take(vqm_feature_payload_size(flags), "VQM bit-13 feature payload")
    return VqmFeatureBlock(flags, payload)


def parse_frame(data: bytes) -> MainFrame | UnvoicedFrame | VqmFrame:
    cursor = Cursor(data)
    magic, chunk_size = cursor.unpack("<4sI", "FRM2 header")
    if magic != b"FRM2":
        fail(f"expected FRM2, found {magic!r}")
    if int(chunk_size) != len(data):
        fail(f"FRM2 size field is {chunk_size}, but {len(data)} byte(s) were read")
    kind, time_seconds, mask = cursor.unpack("<IdQ", "FRM2 prefix")
    kind = int(kind)
    time_seconds = float(time_seconds)
    mask = int(mask)

    if mask == UNVOICED_MASK:
        f0_hz = cursor.f32("unvoiced F0")
        if cursor.position != len(data):
            fail(f"unvoiced FRM2 has {len(data) - cursor.position} trailing byte(s)")
        return UnvoicedFrame(kind, time_seconds, mask, f0_hz)

    if mask == VQM_MASK:
        component_count = cursor.u32("VQM sinusoid component count")
        component_frequencies = cursor.f32_array(
            component_count, "VQM component frequencies"
        )
        component_amplitudes = cursor.f32_array(
            component_count, "VQM component amplitudes"
        )
        component_phases = cursor.f32_array(component_count, "VQM component phases")
        spectrum_count = cursor.u32("VQM spectrum bin count")
        spectrum_amplitudes = cursor.f32_array(
            spectrum_count, "VQM spectrum amplitudes"
        )
        spectrum_phases = cursor.f32_array(spectrum_count, "VQM spectrum phases")
        f0_hz = cursor.f32("VQM F0")
        feature_block = parse_vqm_feature_block(cursor)
        feature_trailer = cursor.u8("VQM bit-13 trailer")
        bit18_value = cursor.u8("VQM bit-18 value")
        bit19_value = cursor.u32("VQM bit-19 value")
        bit7_envelope = parse_envelope(cursor, "VQM bit-7 envelope")
        bit17_envelope = parse_envelope(cursor, "VQM bit-17 envelope")
        if cursor.position != len(data):
            fail(f"VQM FRM2 has {len(data) - cursor.position} trailing byte(s)")
        return VqmFrame(
            kind=kind,
            time_seconds=time_seconds,
            mask=mask,
            component_frequencies_hz=component_frequencies,
            component_amplitudes=component_amplitudes,
            component_phases_radians=component_phases,
            spectrum_amplitudes=spectrum_amplitudes,
            spectrum_phases_radians=spectrum_phases,
            f0_hz=f0_hz,
            feature_block=feature_block,
            feature_trailer=feature_trailer,
            bit18_value=bit18_value,
            bit19_value=bit19_value,
            bit7_envelope=bit7_envelope,
            bit17_envelope=bit17_envelope,
        )
    if mask != MAIN_MASK:
        fail(f"unsupported FRM2 mask 0x{mask:016x}")

    count = cursor.u32("harmonic count")
    frequencies = cursor.f32_array(count, "harmonic frequencies")
    amplitudes = cursor.f32_array(count, "harmonic amplitudes")
    phases = cursor.f32_array(count, "harmonic phases")
    f0_hz = cursor.f32("F0")
    harmonic_envelope = parse_envelope(cursor, "harmonic envelope")
    resonance_count = cursor.u32("resonance count")
    resonances = cursor.f64_triples(resonance_count, "resonance")
    secondary_header_raw = cursor.unpack("<3f", "secondary resonance header")
    secondary_count = cursor.u32("secondary resonance count")
    secondary_resonances = cursor.f64_triples(secondary_count, "secondary resonance")
    residual_envelope = parse_envelope(cursor, "residual envelope")

    if cursor.position != len(data):
        fail(f"ordinary FRM2 has {len(data) - cursor.position} trailing byte(s)")
    return MainFrame(
        kind=kind,
        time_seconds=time_seconds,
        mask=mask,
        frequencies_hz=frequencies,
        amplitudes=amplitudes,
        phases_radians=phases,
        f0_hz=f0_hz,
        harmonic_envelope=harmonic_envelope,
        resonances=resonances,
        secondary_header=(
            float(secondary_header_raw[0]),
            float(secondary_header_raw[1]),
            float(secondary_header_raw[2]),
        ),
        secondary_resonances=secondary_resonances,
        residual_envelope=residual_envelope,
    )


def pack_f32_array(values: tuple[float, ...]) -> bytes:
    if not values:
        return b""
    return struct.pack(f"<{len(values)}f", *values)


def serialize_envelope(envelope: Envelope) -> bytes:
    payload = pack_f32_array(envelope.values)
    return b"".join(
        (
            struct.pack("<4sI", b"ENV ", 28 + len(payload)),
            struct.pack("<II3f", envelope.flags, len(payload), *envelope.bounds),
            payload,
        )
    )


def serialize_frame(frame: MainFrame | UnvoicedFrame | VqmFrame) -> bytes:
    payload = [struct.pack("<IdQ", frame.kind, frame.time_seconds, frame.mask)]
    if isinstance(frame, UnvoicedFrame):
        payload.append(struct.pack("<f", frame.f0_hz))
    elif isinstance(frame, VqmFrame):
        component_count = len(frame.component_frequencies_hz)
        if (
            len(frame.component_amplitudes) != component_count
            or len(frame.component_phases_radians) != component_count
        ):
            fail("VQM component arrays do not have equal lengths")
        spectrum_count = len(frame.spectrum_amplitudes)
        if len(frame.spectrum_phases_radians) != spectrum_count:
            fail("VQM spectrum arrays do not have equal lengths")
        expected_feature_size = vqm_feature_payload_size(frame.feature_block.flags)
        if len(frame.feature_block.payload) != expected_feature_size:
            fail(
                "VQM feature payload length does not agree with its flags: "
                f"{len(frame.feature_block.payload)} != {expected_feature_size}"
            )
        payload.extend(
            (
                struct.pack("<I", component_count),
                pack_f32_array(frame.component_frequencies_hz),
                pack_f32_array(frame.component_amplitudes),
                pack_f32_array(frame.component_phases_radians),
                struct.pack("<I", spectrum_count),
                pack_f32_array(frame.spectrum_amplitudes),
                pack_f32_array(frame.spectrum_phases_radians),
                struct.pack("<fI", frame.f0_hz, frame.feature_block.flags),
                frame.feature_block.payload,
                struct.pack(
                    "<BBI",
                    frame.feature_trailer,
                    frame.bit18_value,
                    frame.bit19_value,
                ),
                serialize_envelope(frame.bit7_envelope),
                serialize_envelope(frame.bit17_envelope),
            )
        )
    else:
        count = len(frame.frequencies_hz)
        if len(frame.amplitudes) != count or len(frame.phases_radians) != count:
            fail("ordinary FRM2 harmonic arrays do not have equal lengths")
        payload.extend(
            (
                struct.pack("<I", count),
                pack_f32_array(frame.frequencies_hz),
                pack_f32_array(frame.amplitudes),
                pack_f32_array(frame.phases_radians),
                struct.pack("<f", frame.f0_hz),
                serialize_envelope(frame.harmonic_envelope),
                struct.pack("<I", len(frame.resonances)),
            )
        )
        payload.extend(struct.pack("<3d", *triple) for triple in frame.resonances)
        payload.append(
            struct.pack(
                "<3fI", *frame.secondary_header, len(frame.secondary_resonances)
            )
        )
        payload.extend(
            struct.pack("<3d", *triple) for triple in frame.secondary_resonances
        )
        payload.append(serialize_envelope(frame.residual_envelope))
    body = b"".join(payload)
    return struct.pack("<4sI", b"FRM2", 8 + len(body)) + body


def finite_range(values: tuple[float, ...]) -> list[float] | None:
    finite = [value for value in values if math.isfinite(value)]
    if not finite:
        return None
    return [min(finite), max(finite)]


def envelope_summary(envelope: Envelope) -> dict[str, object]:
    pairs = list(zip(envelope.values[0::2], envelope.values[1::2]))
    x_values = tuple(pair[0] for pair in pairs)
    y_values = tuple(pair[1] for pair in pairs)
    x_step = None
    if len(x_values) >= 2:
        x_step = x_values[1] - x_values[0]
    return {
        "flags": f"0x{envelope.flags:08x}",
        "bounds": list(envelope.bounds),
        "float_count": len(envelope.values),
        "pair_count": len(pairs),
        "x_range": finite_range(x_values),
        "first_x_step": x_step,
        "y_range": finite_range(y_values),
        "first_pairs": [list(pair) for pair in pairs[:4]],
    }


def frame_summary(
    frame: MainFrame | UnvoicedFrame | VqmFrame,
    offset: int,
    chunk_size: int,
    roundtrip_equal: bool | None,
    mask: int,
) -> dict[str, object]:
    common: dict[str, object] = {
        "offset": offset,
        "offset_hex": f"0x{offset:x}",
        "chunk_size": chunk_size,
        "mask": f"0x{mask:016x}",
        "roundtrip_equal": roundtrip_equal,
    }
    common.update(
        {
            "kind": frame.kind,
            "time_seconds": frame.time_seconds,
            "f0_hz": frame.f0_hz,
            "f0_cents_from_a4": (
                1200.0 * math.log2(frame.f0_hz / 440.0) if frame.f0_hz > 0 else None
            ),
            "layout_supported": True,
        }
    )
    if isinstance(frame, UnvoicedFrame):
        common["layout"] = "unvoiced"
        return common

    if isinstance(frame, VqmFrame):
        common.update(
            {
                "layout": "vqm",
                "component_count": len(frame.component_frequencies_hz),
                "component_frequency_range_hz": finite_range(
                    frame.component_frequencies_hz
                ),
                "first_component_frequencies_hz": list(
                    frame.component_frequencies_hz[:8]
                ),
                "component_amplitude_range": finite_range(frame.component_amplitudes),
                "component_phase_range_radians": finite_range(
                    frame.component_phases_radians
                ),
                "spectrum_bin_count": len(frame.spectrum_amplitudes),
                "spectrum_amplitude_range": finite_range(frame.spectrum_amplitudes),
                "spectrum_phase_range_radians": finite_range(
                    frame.spectrum_phases_radians
                ),
                "feature_flags": f"0x{frame.feature_block.flags:08x}",
                "feature_payload_bytes": len(frame.feature_block.payload),
                "feature_trailer": frame.feature_trailer,
                "bit18_value": frame.bit18_value,
                "bit19_value": frame.bit19_value,
                "bit7_envelope": envelope_summary(frame.bit7_envelope),
                "bit17_envelope": envelope_summary(frame.bit17_envelope),
            }
        )
        return common

    active_frequencies = tuple(value for value in frame.frequencies_hz if value > 0)
    common.update(
        {
            "layout": "ordinary",
            "harmonic_count": len(frame.frequencies_hz),
            "active_harmonic_count": len(active_frequencies),
            "frequency_range_hz": finite_range(active_frequencies),
            "first_frequencies_hz": list(frame.frequencies_hz[:8]),
            "amplitude_range": finite_range(frame.amplitudes),
            "first_amplitudes": list(frame.amplitudes[:8]),
            "phase_range_radians": finite_range(frame.phases_radians),
            "first_phases_radians": list(frame.phases_radians[:8]),
            "harmonic_envelope": envelope_summary(frame.harmonic_envelope),
            "resonance_count": len(frame.resonances),
            "first_resonances": [list(value) for value in frame.resonances[:4]],
            "secondary_header": list(frame.secondary_header),
            "secondary_resonance_count": len(frame.secondary_resonances),
            "first_secondary_resonances": [
                list(value) for value in frame.secondary_resonances[:4]
            ],
            "residual_envelope": envelope_summary(frame.residual_envelope),
        }
    )
    return common


def parse_offset(value: str) -> int:
    try:
        result = int(value, 0)
    except ValueError as error:
        raise argparse.ArgumentTypeError("offset must be decimal or 0x-prefixed hex") from error
    if result < 0:
        raise argparse.ArgumentTypeError("offset must not be negative")
    return result


def require_range(position: int, length: int, end: int, context: str) -> None:
    if length < 0 or position < 0 or position + length > end:
        fail(
            f"{context} at file offset 0x{position:x} exceeds "
            f"FRM2 end 0x{end:x}"
        )


def validate_envelope_at(data: mmap.mmap, position: int, frame_end: int, context: str) -> int:
    require_range(position, 28, frame_end, f"{context} header")
    magic, chunk_size = struct.unpack_from("<4sI", data, position)
    if magic != b"ENV ":
        fail(f"expected ENV at file offset 0x{position:x}, found {magic!r}")
    if chunk_size < 28:
        fail(f"invalid {context} size {chunk_size} at file offset 0x{position:x}")
    require_range(position, chunk_size, frame_end, context)
    data_bytes = struct.unpack_from("<I", data, position + 12)[0]
    if chunk_size != 28 + data_bytes:
        fail(
            f"{context} size mismatch at file offset 0x{position:x}: "
            f"header says {chunk_size}, payload implies {28 + data_bytes}"
        )
    if data_bytes % 4:
        fail(f"{context} data size {data_bytes} is not float32-aligned")
    return position + chunk_size


def validate_frame_at(data: mmap.mmap, offset: int, file_size: int) -> tuple[int, int, int]:
    """Validate one known FRM2 layout without materializing its float arrays."""
    require_range(offset, 28, file_size, "FRM2 prefix")
    magic, chunk_size = CHUNK_HEADER.unpack_from(data, offset)
    if magic != b"FRM2":
        fail(f"expected FRM2 at file offset 0x{offset:x}, found {magic!r}")
    if chunk_size < 28:
        fail(f"invalid FRM2 size {chunk_size} at file offset 0x{offset:x}")
    frame_end = offset + chunk_size
    require_range(offset, chunk_size, file_size, "FRM2")
    kind = struct.unpack_from("<I", data, offset + 8)[0]
    mask = struct.unpack_from("<Q", data, offset + 20)[0]
    position = offset + 28

    if mask == UNVOICED_MASK:
        require_range(position, 4, frame_end, "unvoiced F0")
        position += 4
    elif mask == MAIN_MASK:
        require_range(position, 4, frame_end, "harmonic count")
        count = struct.unpack_from("<I", data, position)[0]
        position += 4
        require_range(position, count * 12, frame_end, "harmonic arrays")
        position += count * 12
        require_range(position, 4, frame_end, "F0")
        position += 4
        position = validate_envelope_at(data, position, frame_end, "harmonic envelope")
        require_range(position, 4, frame_end, "resonance count")
        resonance_count = struct.unpack_from("<I", data, position)[0]
        position += 4
        require_range(position, resonance_count * 24, frame_end, "resonance array")
        position += resonance_count * 24
        require_range(position, 16, frame_end, "secondary resonance header")
        secondary_count = struct.unpack_from("<I", data, position + 12)[0]
        position += 16
        require_range(
            position,
            secondary_count * 24,
            frame_end,
            "secondary resonance array",
        )
        position += secondary_count * 24
        position = validate_envelope_at(data, position, frame_end, "residual envelope")
    elif mask == VQM_MASK:
        require_range(position, 4, frame_end, "VQM component count")
        component_count = struct.unpack_from("<I", data, position)[0]
        position += 4
        require_range(position, component_count * 12, frame_end, "VQM component arrays")
        position += component_count * 12
        require_range(position, 4, frame_end, "VQM spectrum count")
        spectrum_count = struct.unpack_from("<I", data, position)[0]
        position += 4
        require_range(position, spectrum_count * 8, frame_end, "VQM spectrum arrays")
        position += spectrum_count * 8
        require_range(position, 8, frame_end, "VQM F0 and feature flags")
        feature_flags = struct.unpack_from("<I", data, position + 4)[0]
        unknown_flags = feature_flags & ~0x001FFFFF
        if unknown_flags:
            fail(
                f"unsupported VQM feature flags 0x{unknown_flags:08x} "
                f"at file offset 0x{position + 4:x}"
            )
        position += 8
        feature_size = vqm_feature_payload_size(feature_flags)
        require_range(position, feature_size + 6, frame_end, "VQM feature/control data")
        position += feature_size + 6
        position = validate_envelope_at(data, position, frame_end, "VQM bit-7 envelope")
        position = validate_envelope_at(data, position, frame_end, "VQM bit-17 envelope")
    else:
        fail(f"unsupported FRM2 mask 0x{mask:016x} at file offset 0x{offset:x}")

    if position != frame_end:
        fail(
            f"FRM2 at 0x{offset:x} has {frame_end - position} unconsumed byte(s)"
        )
    return chunk_size, kind, mask


def scan_file(path: Path) -> dict[str, object]:
    file_size = path.stat().st_size
    frame_masks: Counter[str] = Counter()
    frame_kinds: Counter[int] = Counter()
    frame_sizes: dict[str, dict[str, int | None]] = {}
    snd_count = 0
    offset = 0
    with path.open("rb") as stream, mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as data:
        while offset < file_size:
            require_range(offset, CHUNK_HEADER.size, file_size, "top-level chunk header")
            magic, chunk_size = CHUNK_HEADER.unpack_from(data, offset)
            if chunk_size < CHUNK_HEADER.size:
                fail(f"invalid top-level chunk size {chunk_size} at 0x{offset:x}")
            require_range(offset, chunk_size, file_size, "top-level chunk")
            if magic == b"FRM2":
                validated_size, kind, mask = validate_frame_at(data, offset, file_size)
                if validated_size != chunk_size:
                    fail(f"internal FRM2 size mismatch at 0x{offset:x}")
                mask_name = f"0x{mask:016x}"
                frame_masks[mask_name] += 1
                frame_kinds[kind] += 1
                values = frame_sizes.setdefault(
                    mask_name, {"min": None, "max": None, "total": 0}
                )
                values["min"] = (
                    chunk_size if values["min"] is None else min(int(values["min"]), chunk_size)
                )
                values["max"] = (
                    chunk_size if values["max"] is None else max(int(values["max"]), chunk_size)
                )
                values["total"] = int(values["total"] or 0) + chunk_size
            elif magic == b"SND ":
                snd_count += 1
            else:
                fail(f"unknown top-level magic {magic!r} at file offset 0x{offset:x}")
            offset += chunk_size
    if offset != file_size:
        fail(f"scan ended at 0x{offset:x}, file ends at 0x{file_size:x}")
    return {
        "file_name": path.name,
        "file_size": file_size,
        "validated_bytes": offset,
        "frm2_count": sum(frame_masks.values()),
        "snd_count": snd_count,
        "frame_kinds": dict(sorted(frame_kinds.items())),
        "frame_masks": dict(sorted(frame_masks.items())),
        "frame_sizes": dict(sorted(frame_sizes.items())),
        "all_known_layouts_structurally_valid": True,
    }


def print_scan_human(summary: dict[str, object]) -> None:
    print(f"file: {summary['file_name']}")
    print(
        f"validated: {summary['validated_bytes']}/{summary['file_size']} bytes  "
        f"FRM2: {summary['frm2_count']}  SND: {summary['snd_count']}"
    )
    print(f"frame kinds: {summary['frame_kinds']}")
    print("frame masks:")
    for mask, count in summary["frame_masks"].items():
        sizes = summary["frame_sizes"][mask]
        print(
            f"  {mask}: count={count} min={sizes['min']} "
            f"max={sizes['max']} total={sizes['total']}"
        )


def probe(path: Path, offset: int) -> dict[str, object]:
    file_size = path.stat().st_size
    if offset + CHUNK_HEADER.size > file_size:
        fail(f"offset 0x{offset:x} does not contain a complete chunk header")
    with path.open("rb") as stream:
        stream.seek(offset)
        header = stream.read(CHUNK_HEADER.size)
        magic, chunk_size = CHUNK_HEADER.unpack(header)
        if magic != b"FRM2":
            fail(f"expected FRM2 at file offset 0x{offset:x}, found {magic!r}")
        if chunk_size < 28:
            fail(f"invalid FRM2 size {chunk_size} at file offset 0x{offset:x}")
        if offset + chunk_size > file_size:
            fail(
                f"FRM2 at 0x{offset:x} ends at 0x{offset + chunk_size:x}, "
                f"past file size 0x{file_size:x}"
            )
        stream.seek(offset)
        data = stream.read(chunk_size)
    mask = struct.unpack_from("<Q", data, 20)[0]
    frame = parse_frame(data)
    roundtrip_equal = serialize_frame(frame) == data
    if not roundtrip_equal:
        fail("parse/serialize round trip did not reproduce the original FRM2 bytes")
    result = frame_summary(frame, offset, chunk_size, roundtrip_equal, mask)
    result["file_name"] = path.name
    return result


def print_human(summary: dict[str, object]) -> None:
    print(f"file: {summary['file_name']}")
    print(
        f"offset: {summary['offset_hex']}  size: {summary['chunk_size']}  "
        f"mask: {summary['mask']}"
    )
    print(
        f"layout: {summary['layout']}  supported: {summary['layout_supported']}  "
        f"round-trip: {summary['roundtrip_equal']}"
    )
    print(
        f"kind: {summary['kind']}  time: {summary['time_seconds']:.12g} s  "
        f"F0: {summary['f0_hz']:.9g} Hz  cents(A4): {summary['f0_cents_from_a4']}"
    )
    if summary["layout"] == "ordinary":
        print(
            f"harmonics: {summary['active_harmonic_count']}/{summary['harmonic_count']} active  "
            f"resonances: {summary['resonance_count']} + "
            f"{summary['secondary_resonance_count']}"
        )
        print(
            "envelopes: "
            f"{summary['harmonic_envelope']['pair_count']} + "
            f"{summary['residual_envelope']['pair_count']} pairs"
        )
    elif summary["layout"] == "vqm":
        print(
            f"components: {summary['component_count']}  "
            f"spectrum bins: {summary['spectrum_bin_count']}  "
            f"feature payload: {summary['feature_payload_bytes']} bytes"
        )
        print(
            "envelopes: "
            f"{summary['bit7_envelope']['pair_count']} + "
            f"{summary['bit17_envelope']['pair_count']} pairs"
        )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("ddb", type=Path, help="DDB file containing the frame")
    parser.add_argument("offset", nargs="?", type=parse_offset, help="FRM2 chunk offset")
    parser.add_argument(
        "--scan",
        action="store_true",
        help="validate every FRM2 layout in the DDB without decoding all float values",
    )
    parser.add_argument("--json", action="store_true", help="emit JSON")
    args = parser.parse_args()
    if args.scan == (args.offset is not None):
        parser.error("provide either an offset or --scan")
    try:
        summary = scan_file(args.ddb) if args.scan else probe(args.ddb, args.offset)
    except (OSError, ProbeError, struct.error) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    if args.json:
        print(json.dumps(summary, ensure_ascii=False, indent=2))
    elif args.scan:
        print_scan_human(summary)
    else:
        print_human(summary)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
