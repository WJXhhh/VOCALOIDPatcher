"""Probe the native PCM-to-target-spectrum kernel used before Growl mixing."""

from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import struct

from audit_frm2_numeric import F, I, Native, P, Q, SHA256


def run(dll: Path, pcm_amplitude: float) -> dict:
    native = Native(dll)
    growl = native.alloc(0x120)
    construct = native.fn(0x155e70, P, P, I, c.c_double, F,
                          c.c_ubyte, I, c.c_ubyte, c.c_ubyte, c.c_ubyte)
    if construct(growl, 1024, 0.01, 22050.0, 0, 1, 0, 0, 0) != growl:
        raise AssertionError("native constructor failed")
    analysis = P.from_address(growl + 0x18).value
    pcm_count = I.from_address(analysis + 0x8c).value + 1
    window_mode = c.c_ubyte.from_address(analysis + 0x94).value
    descriptor_length = (I.from_address(analysis + 4).value
                         * I.from_address(analysis + 0x64).value)
    if descriptor_length != 4096:
        raise AssertionError("native descriptor length changed")
    pcm = native.array(c.c_short,
                       [int(round(pcm_amplitude*math.sin(math.tau*440*i/44100)))
                        for i in range(pcm_count)])
    produced_count = I(-1)
    workspace = P()
    producer = native.fn(0x1492b0, Q, P, P, c.c_uint, c.c_ubyte,
                         Q, P, P, P, c.c_ubyte, P, F, I)
    code = producer(analysis, pcm, pcm_count, window_mode, 0, None, None,
                    c.byref(produced_count), 3, c.byref(workspace), 2.0, 0)
    if code != 0 or workspace.value is None or not 0 < produced_count.value < 65536:
        raise AssertionError("native target spectrum production failed")
    count = produced_count.value
    values = [F.from_address(workspace.value + i*4).value
              for i in range(count*2+2)]
    if not all(math.isfinite(value) for value in values):
        raise AssertionError("native target spectrum contains nonfinite values")
    return {"pcm_amplitude": pcm_amplitude,
            "pcm_count": pcm_count,
            "window_mode": window_mode,
            "fft_length": descriptor_length,
            "analysis_scale": 2.0,
            "produced_half_count": count,
            "workspace_nonzero": sum(value != 0 for value in values),
            "workspace_max_absolute": max(map(abs, values)),
            "workspace_sha256": hashlib.sha256(
                struct.pack(f"<{len(values)}f", *values)).hexdigest()}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    cases = [run(args.dll, 5000.0), run(args.dll, 0.0)]
    if (cases[0]["produced_half_count"] != 2048
            or cases[0]["workspace_nonzero"] <= 0
            or cases[1]["workspace_nonzero"] != 0):
        raise AssertionError("native target spectrum kernel changed")
    report = {"dse_sha256": SHA256,
              "cases": cases,
              "scope": "Native mode-3 PCM-to-spectrum kernel on owned PCM "
                       "using the matching 4096-point analysis descriptor. "
                       "Full host event and final audio are not tested."}
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({"cases": len(cases),
                      "nonzero": [case["workspace_nonzero"]
                                  for case in cases]}))


if __name__ == "__main__":
    main()
