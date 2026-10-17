"""Check whether the DSE5 FRM2 reader/writer preserves owned VQM arrays.

Both scratch FRM2 files contain generated values only. Repository evidence
stores field hashes and counts, never the frame payloads.
"""

from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
from pathlib import Path
import struct

from audit_frm2_numeric import I, Native, P, Q, SHA256
from probe_frm2 import (Envelope, VqmFeatureBlock, VqmFrame,
                        VQM_NO_SPECTRUM_MASK, parse_frame, serialize_frame)


ARRAYS = {10: (0x2c, 40), 11: (0xcc, 40),
          12: (0x16c, 20), 13: (0x1bc, 20)}


def owned_frame(bit18: int = 10, bit19: int = 2049) -> bytes:
    if not 0 <= bit18 <= 255 or not 0 <= bit19 <= 0xffffffff:
        raise ValueError("outer VQM control is outside its file type")
    payload = bytearray()
    for bit in range(21):
        if bit < 10 or 14 <= bit < 20:
            payload.extend(struct.pack("<f", bit + 0.125))
        elif bit in (10, 11, 12, 13):
            count = 40 if bit < 12 else 20
            payload.extend(struct.pack(
                f"<{count}f", *(bit + (index + 1) / 100
                                for index in range(count))))
    envelope = Envelope(0, (0.0, 1.0, 1.0), ())
    frame = VqmFrame(
        1, 0.25, VQM_NO_SPECTRUM_MASK,
        (220.0,), (0.5,), (0.0,), (), (), 220.0,
        VqmFeatureBlock(0x1fffff, bytes(payload)), 0, bit18, bit19,
        envelope, envelope,
    )
    raw = serialize_frame(frame)
    if parse_frame(raw) != frame:
        raise AssertionError("owned VQM fixture did not round-trip independently")
    return raw


def feature_summary(frame: int) -> dict:
    feature = P.from_address(frame + 0x208).value
    if not feature or I.from_address(feature).value != 0x1fffff:
        raise AssertionError("DSE5 frame lost owned VQM feature flags")
    return {
        "frame_mask": hex(Q.from_address(frame + 0x150).value),
        "flags": hex(I.from_address(feature).value),
        "array_sha256": {
            str(bit): hashlib.sha256(
                c.string_at(feature + offset, 4 * length)).hexdigest()
            for bit, (offset, length) in ARRAYS.items()
        },
        "bit2_float32_hex": c.string_at(feature + 0x0c, 4).hex(),
        "outer_bit18": I.from_address(frame + 0x258).value & 0xff,
        "outer_bit19": I.from_address(frame + 0x25c).value,
    }


def crt_functions():
    crt = c.CDLL("ucrtbase.dll")
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    return crt


def read_frame(native: Native, crt, path: Path) -> tuple[int, dict]:
    handle = crt._wfopen(str(path.resolve()), "rb")
    if not handle:
        raise OSError("cannot open owned VQM frame")
    frame = native.alloc(0x328)
    native.fn(0x12a670, P, P)(frame)
    stream = native.alloc(0x80)
    P.from_address(stream + 8).value = handle
    I.from_address(stream + 0x2c).value = 0x32534d53
    try:
        code = native.fn(0x106e20, Q, P, P)(frame, stream)
        consumed = Q.from_address(stream).value
    finally:
        if crt.fclose(handle) != 0:
            raise OSError("cannot close owned VQM frame")
    if code != 0 or consumed != path.stat().st_size:
        raise AssertionError("DSE5 reader did not consume owned VQM frame")
    return frame, feature_summary(frame)


def run(dll: Path, source: Path, roundtrip: Path,
        bit18: int = 10, bit19: int = 2049) -> dict:
    if source.resolve() == roundtrip.resolve():
        raise ValueError("source and round-trip paths must differ")
    if not source.parent.is_dir() or not roundtrip.parent.is_dir():
        raise ValueError("scratch directories do not exist")
    raw = owned_frame(bit18, bit19)
    source.write_bytes(raw)
    native = Native(dll)
    crt = crt_functions()
    frame, before = read_frame(native, crt, source)
    handle = crt._wfopen(str(roundtrip.resolve()), "wb")
    if not handle:
        raise OSError("cannot open owned VQM round-trip output")
    stream = native.alloc(0x80)
    P.from_address(stream + 8).value = handle
    Q.from_address(stream + 0x20).value = 0xffffffffffffffff
    I.from_address(stream + 0x2c).value = 0x32534d53
    try:
        code = native.fn(0x107060, Q, P, P)(frame, stream)
        written = Q.from_address(stream).value
    finally:
        if crt.fclose(handle) != 0:
            raise OSError("cannot close owned VQM round-trip output")
    if code != 0 or written != roundtrip.stat().st_size:
        raise AssertionError("DSE5 writer did not complete owned VQM frame")
    _, after = read_frame(native, crt, roundtrip)
    with roundtrip.open("rb") as output:
        output_prefix = output.read(28)
    if len(output_prefix) != 28 or output_prefix[:4] != b"FRM2":
        raise AssertionError("DSE5 writer omitted FRM2 header")
    for field in ("flags", "array_sha256", "bit2_float32_hex",
                  "outer_bit18", "outer_bit19"):
        if before[field] != after[field]:
            raise AssertionError(f"DSE5 writer changed owned VQM {field}")
    if before["outer_bit18"] != bit18 or before["outer_bit19"] != bit19:
        raise AssertionError("DSE5 reader changed requested VQM controls")
    return {
        "dse_sha256": SHA256,
        "fixture_bytes": len(raw),
        "roundtrip_bytes": written,
        "fixture_file_mask": hex(struct.unpack_from("<Q", raw, 20)[0]),
        "roundtrip_file_mask": hex(struct.unpack_from("<Q", output_prefix, 20)[0]),
        "source_memory_mask": before["frame_mask"],
        "roundtrip_memory_mask": after["frame_mask"],
        "flags": before["flags"],
        "array_sha256": before["array_sha256"],
        "bit2_float32_hex": before["bit2_float32_hex"],
        "outer_bit18": before["outer_bit18"],
        "outer_bit19": before["outer_bit19"],
        "arrays_and_controls_preserved": True,
        "scope": "One owned VQM no-spectrum FRM2 through DSE5 native read/write/read; this tests the generic serializer, not the original VQM authoring process.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--scratch-source", type=Path, required=True)
    parser.add_argument("--scratch-roundtrip", type=Path, required=True)
    parser.add_argument("--bit18", type=int, default=10)
    parser.add_argument("--bit19", type=int, default=2049)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.scratch_source, args.scratch_roundtrip,
                 args.bit18, args.bit19)
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({"fixture_bytes": report["fixture_bytes"],
                      "roundtrip_bytes": report["roundtrip_bytes"],
                      "preserved": report["arrays_and_controls_preserved"]}))


if __name__ == "__main__":
    main()
