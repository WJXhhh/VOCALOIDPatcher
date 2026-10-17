#!/usr/bin/env python3
"""Verify the experimental main-frame builder with owned PCM and native readback."""
from __future__ import annotations

import argparse
import ctypes as c
import json
import math
import os
import re
from pathlib import Path
import statistics
import struct
import subprocess
import wave

from audit_frm2_numeric import Native, SHA256, F, I, P, Q, read_frame
from probe_frm2 import MAIN_MASK
from frm2_work_directory import temporary_directory


def verify(native, dll, harness, directory, frequency, peak, harmonics):
    wav = directory / "input.wav"
    output = directory / "output.sms2"
    with wave.open(str(wav), "wb") as stream:
        stream.setparams((1, 2, 44100, 0, "NONE", "not compressed"))
        stream.writeframes(b"".join(struct.pack("<h", round(32767 * peak * sum(
            weight * math.sin(math.tau * frequency * k * i / 44100)
            for k, weight in enumerate(harmonics, 1)))) for i in range(22050)))
    env = {key: value for key, value in os.environ.items() if not key.startswith("DRS_HARNESS_")}
    env["DRS_HARNESS_BUILD_MAIN_FIELDS"] = "1"
    run = subprocess.run(["dotnet", str(harness), str(dll), str(output), ".5", str(frequency),
                          "external", str(frequency), str(wav)], env=env, capture_output=True,
                         text=True, timeout=60)
    if (run.returncode or "failed=0" not in run.stdout or re.search(r"\bfailed=[1-9]", run.stdout)
            or "readback.result=0" not in run.stdout):
        raise RuntimeError(run.stdout + run.stderr)
    data = output.read_bytes()
    offsets, frames = [], []
    offset = 0
    while (offset := data.find(b"FRM2", offset)) >= 0:
        frame = read_frame(output, offset)
        if frame.mask != MAIN_MASK:
            raise AssertionError("Unexpected output mask")
        if any(frame.harmonic_envelope.values[1::2]):
            raise AssertionError("Bit 37 must remain an explicitly neutral phase placeholder")
        offsets.append(offset)
        frames.append(frame)
        offset += struct.unpack_from("<I", data, offset + 4)[0]
    central = [frame for frame in frames if .15 < frame.time_seconds < .35]
    if len(central) < 20:
        raise AssertionError("Insufficient steady-state frames")
    measured = [statistics.median(frame.amplitudes[k] for frame in central) for k in range(len(harmonics))]
    expected = [20 * math.log10(peak * weight * 32767 / 32768) for weight in harmonics]
    errors = [abs(a - b) for a, b in zip(measured, expected)]
    if max(errors) > .05:
        raise AssertionError(f"Known-tone amplitude error exceeds 0.05 dB: {errors}")

    # Read the actual serialized output through DSE5, including the SMS2 header context.
    crt = c.CDLL("ucrtbase")
    fopen = crt._wfopen
    fopen.argtypes, fopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    seek = crt._fseeki64
    seek.argtypes, seek.restype = [P, c.c_int64, I], I
    close = crt.fclose
    close.argtypes = [P]
    constructor = native.fn(0x12a670, P, P)
    reader = native.fn(0x106e20, Q, P, P)
    handle = fopen(str(output), "rb")
    if not handle:
        raise OSError("Native readback open failed")
    try:
        for offset, frame in zip(offsets, frames):
            obj, stream = native.alloc(0x328), native.alloc(0x80)
            constructor(obj)
            P.from_address(stream + 8).value = handle
            Q.from_address(stream).value = offset
            I.from_address(stream + 0x2c).value = 0x32534d53
            if seek(handle, offset, 0) or reader(obj, stream):
                raise AssertionError("Native frame read failed")
            size = struct.unpack_from("<I", data, offset + 4)[0]
            if (Q.from_address(stream).value != offset + size
                    or Q.from_address(obj + 0x150).value != (MAIN_MASK | (1 << 31))):
                raise AssertionError(f"Native cursor/mask mismatch: cursor={Q.from_address(stream).value}, "
                                     f"expected={offset + size}, mask={Q.from_address(obj + 0x150).value:#x}")
            if abs(F.from_address(obj + 0x1d0).value - 1200 * math.log2(frame.f0_hz / 440)) > .0003:
                raise AssertionError("Native Hz-to-cents conversion mismatch")
            for field, expected_values in ((0x160, frame.frequencies_hz), (0x168, frame.amplitudes),
                                           (0x170, frame.phases_radians)):
                ptr = P.from_address(obj + field).value
                actual = tuple((F * len(expected_values)).from_address(ptr))
                if actual != expected_values:
                    raise AssertionError("Native harmonic arrays changed during readback")
            for field, envelope in ((0x218, frame.harmonic_envelope), (0x288, frame.residual_envelope)):
                ptr = P.from_address(obj + field).value
                if I.from_address(ptr + 0x15c).value != len(envelope.values) // 2:
                    raise AssertionError("Native envelope point count mismatch")
    finally:
        close(handle)
    return {"frequency": frequency, "peak": peak, "harmonic_weights": harmonics,
            "frames": len(frames), "central_frames": len(central), "measured_levels": measured,
            "expected_levels": expected, "max_level_error": max(errors),
            "native_readback_frames": len(frames), "neutral_phase_all_frames": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--harness", type=Path, required=True, help="Built DrsHarness.dll")
    parser.add_argument("--output", type=Path, required=True, help="Aggregate JSON report")
    args = parser.parse_args()
    native = Native(args.dll.resolve())
    rows = []
    with temporary_directory(prefix="numeric-signals-") as path:
        for frequency, harmonics in ((110, [1]), (220, [1]), (440, [1]), (220, [1, .5, .25, .125])):
            pair = []
            for peak in (.1, .2):
                row = verify(native, args.dll.resolve(), args.harness.resolve(), Path(path), frequency, peak, harmonics)
                rows.append(row)
                pair.append(row)
            gain_error = max(abs(b - a - 20 * math.log10(2))
                             for a, b in zip(pair[0]["measured_levels"], pair[1]["measured_levels"]))
            if gain_error > .01:
                raise AssertionError(f"Doubling input gain did not produce 6.0206 dB: {gain_error}")
            pair[1]["doubling_gain_error"] = gain_error
    report = {"dll_sha256": SHA256, "signals": rows,
              "max_level_error": max(row["max_level_error"] for row in rows),
              "native_readback_frames": sum(row["native_readback_frames"] for row in rows)}
    args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
