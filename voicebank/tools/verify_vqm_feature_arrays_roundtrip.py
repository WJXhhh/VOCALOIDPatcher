"""Compare DRS bit-13 arrays before and after native SMS2 writer/reader.

The only persisted SMS2 is generated from owned harmonic PCM at --scratch.
The repository report contains hashes and counts, not frame payloads.
"""

from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import struct

from audit_frm2_numeric import F, I, Native, P, Q, SHA256
from probe_frm2_batch_outputs import run as analyze_batch


ARRAYS = {10: (0x2c, 40), 11: (0xcc, 40),
          12: (0x16c, 20), 13: (0x1bc, 20)}


def collect(collection: int) -> dict:
    count = I.from_address(collection + 0x44).value
    generic_array = P.from_address(collection + 0x48).value
    if not 0 < count < 100 or not generic_array:
        raise AssertionError("DRS collection has no generic entries")
    hashes = {bit: hashlib.sha256() for bit in ARRAYS}
    elements = {bit: 0 for bit in ARRAYS}
    nonzero = {bit: 0 for bit in ARRAYS}
    energy_hash = hashlib.sha256()
    masks = {}
    frames = 0
    for generic_index in range(count):
        generic = P.from_address(generic_array + 8 * generic_index).value
        tracks = P.from_address(generic + 0x50).value
        for track_index in range(I.from_address(generic + 0x48).value):
            track = P.from_address(tracks + 8 * track_index).value
            regions = P.from_address(track + 0x148).value
            for region_index in range(I.from_address(track + 0x140).value):
                region = P.from_address(regions + 8 * region_index).value
                frame_array = P.from_address(region + 0x20).value
                for frame_index in range(I.from_address(region + 0x18).value):
                    frame = P.from_address(frame_array + 8 * frame_index).value
                    mask = Q.from_address(frame + 0x50).value
                    masks[hex(mask)] = masks.get(hex(mask), 0) + 1
                    feature = P.from_address(frame + 0x128).value
                    if not mask & (1 << 13) or not feature:
                        raise AssertionError("DRS frame lost bit-13 feature")
                    flags = I.from_address(feature).value
                    if any(not flags & (1 << bit)
                           for bit in (*ARRAYS, 2)):
                        raise AssertionError("DRS frame lost feature array flags")
                    energy_hash.update(c.string_at(feature + 0x0c, 4))
                    frames += 1
                    for bit, (offset, length) in ARRAYS.items():
                        raw = c.string_at(feature + offset, 4 * length)
                        values = struct.unpack(f"<{length}f", raw)
                        if any(not math.isfinite(value) for value in values):
                            raise ValueError("DRS feature array is nonfinite")
                        hashes[bit].update(raw)
                        elements[bit] += length
                        nonzero[bit] += sum(value != 0 for value in values)
    return {
        "frames": frames,
        "frame_masks": masks,
        "array_elements": {str(bit): elements[bit] for bit in ARRAYS},
        "array_nonzero": {str(bit): nonzero[bit] for bit in ARRAYS},
        "array_sha256": {str(bit): hashes[bit].hexdigest()
                         for bit in ARRAYS},
        "feature_energy_sha256": energy_hash.hexdigest(),
    }


def file_functions():
    crt = c.CDLL("ucrtbase.dll")
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    return crt


def write_and_read(native: Native, collection: int,
                   scratch: Path) -> tuple[dict, int]:
    crt = file_functions()
    before = collect(collection)
    handle = crt._wfopen(str(scratch.resolve()), "wb")
    if not handle:
        raise OSError("could not open owned SMS2 scratch output")
    stream = native.alloc(0x80)
    P.from_address(stream + 8).value = handle
    Q.from_address(stream + 0x20).value = 0xffffffffffffffff
    try:
        code = native.fn(0x92f40, Q, P, P)(collection, stream)
        written = Q.from_address(stream).value
    finally:
        if crt.fclose(handle) != 0:
            raise OSError("could not close owned SMS2 scratch output")
    if code != 0 or written != scratch.stat().st_size:
        raise AssertionError("DRS writer did not complete owned SMS2")
    with scratch.open("rb") as source:
        header = source.read(8)
    if len(header) != 8 or header[:4] != b"SMS2" or struct.unpack_from("<I", header, 4)[0] != written:
        raise AssertionError("owned SMS2 header differs")
    handle = crt._wfopen(str(scratch.resolve()), "rb")
    if not handle:
        raise OSError("could not reopen owned SMS2 scratch output")
    loaded = native.alloc(0x68)
    native.fn(0x938c0, P, P)(loaded)
    read_stream = native.alloc(0x80)
    P.from_address(read_stream + 8).value = handle
    try:
        code = native.fn(0x93000, Q, P, P)(loaded, read_stream)
        consumed = Q.from_address(read_stream).value
    finally:
        if crt.fclose(handle) != 0:
            raise OSError("could not close owned SMS2 scratch input")
    if code != 0 or consumed != written:
        raise AssertionError("DRS reader did not consume owned SMS2")
    after = collect(loaded)
    if before != after:
        raise AssertionError("DRS bit-13 arrays changed during SMS2 round-trip")
    return before, written


def run(dll: Path, scratch: Path) -> dict:
    if scratch.suffix.lower() != ".sms2":
        raise ValueError("scratch output must have .sms2 suffix")
    if not scratch.parent.is_dir():
        raise ValueError("scratch output directory does not exist")
    holder = []

    def observe(native: Native, context: int, collection: int) -> None:
        holder.append(write_and_read(native, collection, scratch))

    result = analyze_batch(dll, 0, 1, "steady", 440,
                           analysis_mode=4, pitch_seed=440,
                           result_observer=observe)
    if len(holder) != 1 or holder[0][0]["frames"] != result["frames"]:
        raise AssertionError("round-trip callback did not match analysis")
    return {
        "dse_sha256": SHA256,
        "input": "owned 440 Hz harmonic PCM",
        "analysis_mode": 4,
        "native_writer_rva": "0x92f40",
        "native_reader_rva": "0x93000",
        "sms2_bytes": holder[0][1],
        "before_and_after_equal": True,
        "feature_summary": holder[0][0],
        "scope": "Native DRS collection writer and reader preserve bit-13 arrays on one owned signal. This does not identify the later VQM-specific save path.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--scratch", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.scratch)
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({"frames": report["feature_summary"]["frames"],
                      "sms2_bytes": report["sms2_bytes"],
                      "arrays_preserved": report["before_and_after_equal"]}))


if __name__ == "__main__":
    main()
