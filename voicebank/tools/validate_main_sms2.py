#!/usr/bin/env python3
"""Validate final traditional-main FRM2 semantics inside a DRS SMS2 file."""

from __future__ import annotations

import argparse
import math
import mmap
import struct
import sys
from pathlib import Path

import probe_frm2
import probe_sms2


NYQUIST_HZ = 22050.0
HARMONIC_SLOTS = 350
HARMONIC_ENVELOPE_FLAGS = 0x01820002
RESIDUAL_ENVELOPE_FLAGS = 0x00960002


class ValidationError(Exception):
    pass


def close(left: float, right: float, scale: float = 2.0e-6) -> bool:
    return math.isclose(left, right, rel_tol=scale, abs_tol=max(1.0e-6, abs(right) * scale))


def envelope_pairs(envelope: probe_frm2.Envelope) -> list[tuple[float, float]]:
    if len(envelope.values) % 2:
        raise ValidationError("ENV payload has an odd float count")
    return list(zip(envelope.values[0::2], envelope.values[1::2]))


def validate_frame(frame: probe_frm2.MainFrame, index: int) -> None:
    prefix = f"frame[{index}] at {frame.time_seconds:.12g}s"
    if frame.mask != probe_frm2.MAIN_MASK:
        raise ValidationError(f"{prefix}: mask is 0x{frame.mask:016x}")
    if len(frame.frequencies_hz) != HARMONIC_SLOTS:
        raise ValidationError(
            f"{prefix}: harmonic slot count is {len(frame.frequencies_hz)}, expected 350"
        )
    if not math.isfinite(frame.f0_hz) or frame.f0_hz <= 0.0:
        raise ValidationError(f"{prefix}: invalid F0 {frame.f0_hz!r}")

    active_count = min(HARMONIC_SLOTS, math.ceil(NYQUIST_HZ / frame.f0_hz))
    for harmonic, (frequency, amplitude, phase) in enumerate(
        zip(frame.frequencies_hz, frame.amplitudes, frame.phases_radians),
        start=1,
    ):
        if harmonic <= active_count:
            expected_frequency = harmonic * frame.f0_hz
            if not close(frequency, expected_frequency):
                raise ValidationError(
                    f"{prefix}: harmonic {harmonic} frequency {frequency} != "
                    f"{expected_frequency}"
                )
            if not math.isfinite(amplitude) or amplitude == 10000.0:
                raise ValidationError(
                    f"{prefix}: harmonic {harmonic} has unused/invalid amplitude {amplitude}"
                )
            if not math.isfinite(phase) or phase < -math.pi - 1.0e-5 or phase > math.pi + 1.0e-5:
                raise ValidationError(
                    f"{prefix}: harmonic {harmonic} phase {phase} is outside [-pi, pi]"
                )
        elif frequency != 0.0 or amplitude != 10000.0 or phase != 0.0:
            raise ValidationError(
                f"{prefix}: unused harmonic {harmonic} is not (0, 10000, 0)"
            )

    harmonic_envelope = frame.harmonic_envelope
    if harmonic_envelope.flags != HARMONIC_ENVELOPE_FLAGS:
        raise ValidationError(
            f"{prefix}: harmonic ENV flags are 0x{harmonic_envelope.flags:08x}"
        )
    if harmonic_envelope.bounds != (-20000.0, 20000.0, 0.0):
        raise ValidationError(f"{prefix}: harmonic ENV bounds are {harmonic_envelope.bounds}")
    pairs = envelope_pairs(harmonic_envelope)
    internal_count = math.floor(NYQUIST_HZ / frame.f0_hz)
    if len(pairs) != internal_count + 2:
        raise ValidationError(
            f"{prefix}: harmonic ENV has {len(pairs)} points, expected {internal_count + 2}"
        )
    if pairs[0][0] != 0.0 or pairs[-1][0] != 1.0:
        raise ValidationError(f"{prefix}: harmonic ENV endpoints are not x=0 and x=1")
    if pairs[0][1] != pairs[1][1] or pairs[-1][1] != pairs[-2][1]:
        raise ValidationError(
            f"{prefix}: harmonic ENV endpoint values are not edge copies: "
            f"first={pairs[:2]} last={pairs[-2:]}"
        )
    for harmonic, (position, _) in enumerate(pairs[1:-1], start=1):
        expected_position = harmonic * frame.f0_hz / NYQUIST_HZ
        if not close(position, expected_position):
            raise ValidationError(
                f"{prefix}: harmonic ENV point {harmonic} x={position} != "
                f"{expected_position}"
            )

    residual = frame.residual_envelope
    if residual.flags != RESIDUAL_ENVELOPE_FLAGS:
        raise ValidationError(
            f"{prefix}: residual ENV flags are 0x{residual.flags:08x}"
        )
    if residual.bounds != (-100.0, 10.0, 0.0):
        raise ValidationError(f"{prefix}: residual ENV bounds are {residual.bounds}")
    residual_pairs = envelope_pairs(residual)
    if len(residual_pairs) != 736:
        raise ValidationError(
            f"{prefix}: residual ENV has {len(residual_pairs)} points, expected 736"
        )
    if residual_pairs[0][0] != 0.0 or residual_pairs[-1][0] != 1.0:
        raise ValidationError(f"{prefix}: residual ENV endpoints are not x=0 and x=1")


def validate(path: Path) -> tuple[int, int]:
    file_size = path.stat().st_size
    with path.open("rb") as stream, mmap.mmap(
        stream.fileno(), 0, access=mmap.ACCESS_READ
    ) as data:
        if file_size < 8:
            raise ValidationError("file is shorter than an SMS2 header")
        magic, declared_size = struct.unpack_from("<4sI", data, 0)
        if magic != b"SMS2" or declared_size != file_size:
            raise ValidationError(
                f"invalid SMS2 header: magic={magic!r} size={declared_size}/{file_size}"
            )
        runs = probe_sms2.find_frame_runs(data, file_size)
        frames = [item for run in runs for item in run]
        if not frames:
            raise ValidationError("no embedded FRM2 run found")
        for index, item in enumerate(frames):
            offset = int(item["offset"])
            chunk_size = int(item["chunk_size"])
            raw = bytes(data[offset : offset + chunk_size])
            frame = probe_frm2.parse_frame(raw)
            if not isinstance(frame, probe_frm2.MainFrame):
                raise ValidationError(f"frame[{index}] is not a traditional main frame")
            if probe_frm2.serialize_frame(frame) != raw:
                raise ValidationError(f"frame[{index}] does not round-trip byte-exactly")
            validate_frame(frame, index)
    return len(runs), len(frames)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sms2", type=Path)
    args = parser.parse_args()
    try:
        run_count, frame_count = validate(args.sms2)
    except (OSError, ValueError, struct.error, probe_frm2.ProbeError, ValidationError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    print(
        f"valid traditional-main SMS2: {args.sms2.name}  "
        f"runs={run_count} frames={frame_count}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
