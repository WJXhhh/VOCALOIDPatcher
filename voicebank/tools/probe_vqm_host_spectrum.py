"""Trace owned VQM bit-13 energy through native cross-source spectrum output.

All source PCM and FRM2 frames are generated. A sibling research scratch file
is removed after each isolated native call; only aggregates are reported.
"""

from __future__ import annotations

import argparse
import ctypes as c
from dataclasses import replace
import hashlib
import json
import math
from pathlib import Path
import struct
import subprocess
import sys

from audit_frm2_numeric import B, D, F, I, Native, P, Q, SHA256, f32
from probe_frm2 import (UNVOICED_MASK, UnvoicedFrame, VqmFeatureBlock,
                        VqmFrame, parse_frame, serialize_frame)
from verify_vqm_cross_source_feature_gain import fixture_gain_model
from verify_vqm_feature_dse5_roundtrip import owned_frame


ENERGY_CASES = (("equal", 0.03), ("half", 0.015), ("fourfold", 0.12))


def run_one(dll: Path, scratch_source: Path, saved_energy: float,
            stage: str = "host") -> dict:
    if not scratch_source.parent.is_dir() or scratch_source.exists():
        raise ValueError("scratch source parent must exist and file must be absent")
    native = Native(dll)
    base = parse_frame(owned_frame())
    if not isinstance(base, VqmFrame):
        raise AssertionError("owned VQM fixture changed")
    payload = bytearray(base.feature_block.payload)
    struct.pack_into("<f", payload, 8, saved_energy)
    feature = VqmFeatureBlock(base.feature_block.flags, bytes(payload))
    pcm_values = [int(round(8000 * math.sin(math.tau * 440 * index / 44100)))
                  for index in range(32768)]
    blob = bytearray(struct.pack(f"<{len(pcm_values)}h", *pcm_values))
    offsets = []
    for index in range(96):
        offsets.append(len(blob))
        frame = (replace(base, time_seconds=float(index),
                         feature_block=feature)
                 if index == 25 else
                 UnvoicedFrame(0, float(index), UNVOICED_MASK, 0.0))
        blob.extend(serialize_frame(frame))
    with scratch_source.open("xb") as output:
        output.write(blob)

    crt = c.CDLL("ucrtbase.dll")
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    handle = 0
    try:
        handle = crt._wfopen(str(scratch_source.resolve()), "rb")
        if not handle:
            raise OSError("cannot open owned source")
        context, controls, config, registry, voice, stream = (
            native.alloc(size) for size in
            (0x2B000, 0x5AB00, 0x100, 0x20, 0x280, 0x80))
        P.from_address(context).value = config
        P.from_address(config + 0x10).value = registry
        I.from_address(registry + 8).value = 1
        P.from_address(registry + 0x10).value = native.array(P, [voice])
        P.from_address(registry + 0x18).value = native.array(P, [native.alloc(8)])
        P.from_address(voice + 0x1a0).value = stream
        category_db = native.alloc(0x300)
        category_map = native.alloc(0x180)
        P.from_address(category_db + 0x158).value = category_map
        P.from_address(voice + 0x170).value = category_db
        P.from_address(stream + 8).value = handle
        I.from_address(stream + 0x2c).value = 0x32534D53
        P.from_address(context + 0x1d568).value = controls
        P.from_address(controls).value = native.alloc(0x18)
        I.from_address(controls + 8).value = 1
        score = native.alloc(0x58)
        F.from_address(score).value = 200.0
        F.from_address(score + 0x10).value = 400.0
        F.from_address(score + 0x3c).value = 400.0
        P.from_address(controls + 0x10).value = score
        I.from_address(controls + 0x18).value = 2
        F.from_address(context + 0x1d550).value = 8.0
        for frame_offset in (0x29860, 0x29b88, 0x29eb0):
            native.fn(0x12a670, P, P)(context + frame_offset)

        analysis_owner = native.alloc(0x120)
        construct = native.fn(0x155e70, P, P, I, D, F, B,
                              I, B, B, B)
        if construct(analysis_owner, 4096, 0.01, 22050.0,
                     0, 1, 0, 0, 0) != analysis_owner:
            raise AssertionError("native analysis constructor failed")
        analysis = P.from_address(analysis_owner + 0x18).value
        P.from_address(context + 0x1fc8).value = analysis
        window_size = I.from_address(analysis + 0x8c).value + 1
        if window_size != 2049:
            raise AssertionError("unexpected native analysis window")
        P.from_address(context + 0x29510).value = native.array(
            c.c_short, [0] * window_size)
        P.from_address(context + 0x24118).value = native.alloc(
            window_size * 2)
        I.from_address(context + 0x24120).value = 2
        I.from_address(context + 0x24124).value = 2049
        I.from_address(context + 0x29530).value = 1

        parent = native.alloc(0x180)
        callback = c.CFUNCTYPE(P, P)(lambda obj: parent)
        native.keep.append(callback)
        vtable = native.alloc(0x68)
        P.from_address(vtable + 0x60).value = c.cast(callback, P).value
        source, target = native.alloc(0x280), native.alloc(0x280)
        for part, pitch in ((source, 0.0), (target, 400.0)):
            P.from_address(part).value = vtable
            F.from_address(part + 0x160).value = pitch
            F.from_address(part + 0x16c).value = 0.6
            B.from_address(part + 0x180).value = 1
            P.from_address(part + 0x188).value = native.array(F, [0.0] * 4)
            P.from_address(part + 0x1a8).value = native.array(Q, offsets)
            I.from_address(part + 0x1b0).value = len(offsets)
            Q.from_address(part + 0x1b8).value = 128
            Q.from_address(part + 0x1c0).value = 256
            I.from_address(part + 0x1d0).value = 1024
        P.from_address(parent + 0x148).value = native.array(P, [source, target])
        I.from_address(parent + 0x150).value = 2
        source_groups = [(20*i, 20*i+12, 20*i+3, 20*i+9)
                         for i in range(4)]
        target_groups = [(22*i, 22*i+12, 22*i+3, 22*i+9)
                         for i in range(4)]
        for part, groups in ((source, source_groups), (target, target_groups)):
            alignment = native.array(I, [value for group in groups
                                         for value in group])
            P.from_address(part + 0x1d8).value = alignment
            P.from_address(part + 0x1e0).value = alignment + 64
        item = native.alloc(0x140)
        P.from_address(item + 0x20).value = source
        I.from_address(item + 0x14).value = 1
        working = native.alloc(0x328)
        native.fn(0x12a670, P, P)(working)
        target_energy = native.fn(0x173f70, F, P, P, P, F)(
            context, target, voice, F(27.0))
        if stage in ("load", "gain"):
            loaded = I(0)
            native.fn(0x175dd0, None, P, P, I, P, I, I, P)(
                context, item, 25, working, 0, 1, c.byref(loaded))
            if stage == "gain":
                gain = F(-99)
                native.fn(0x1722c0, None, P, P, I, I, P, P,
                          I, I, B, P)(context, item, 1, 25, working,
                                       0, 1, 0, 0, c.byref(gain))
                return {"stage": "gain", "loaded_pcm": loaded.value,
                        "gain": gain.value,
                        "loaded_energy": F.from_address(
                            P.from_address(working + 0x208).value + 0xc).value,
                        "frame_260": I.from_address(working + 0x260).value,
                        "frame_270": F.from_address(working + 0x270).value,
                        "target_pcm_energy": target_energy}
            return {"stage": "load", "loaded_pcm": loaded.value,
                    "frame_mask": hex(Q.from_address(working + 0x150).value),
                    "feature_present": bool(P.from_address(
                        working + 0x208).value),
                    "target_pcm_energy": target_energy}
        host = native.fn(0x1757a0, None, P, P, I, P,
                         I, I, I, B, I)
        host(context, item, 25, working, 0, 1, 0, 0, 1)
        feature_pointer = P.from_address(working + 0x208).value
        if not feature_pointer:
            raise AssertionError("host frame has no saved VQM feature object")
        loaded_energy = F.from_address(feature_pointer + 0xc).value
        if loaded_energy != f32(saved_energy):
            raise AssertionError("host read changed saved VQM energy")
        bins = I.from_address(working + 0x1a8).value
        if not 0 < bins <= 4097:
            raise AssertionError("host target spectrum was not produced")
        real = P.from_address(working + 0x1b0).value
        imag = P.from_address(working + 0x1b8).value
        if not real or not imag:
            raise AssertionError("host target spectrum arrays are missing")
        real_values = [F.from_address(real + 4*i).value for i in range(bins)]
        imag_values = [F.from_address(imag + 4*i).value for i in range(bins)]
        if not all(math.isfinite(v) for v in real_values + imag_values):
            raise AssertionError("nonfinite host spectrum")
        inverse_length = 2 * (bins - 1)
        packed = native.array(F, [0.0] * (inverse_length + 2))
        F.from_address(packed).value = real_values[0]
        F.from_address(packed + 4).value = real_values[-1]
        for index in range(1, bins - 1):
            F.from_address(packed + 8*index).value = real_values[index]
            F.from_address(packed + 8*index + 4).value = imag_values[index]
        inverse = native.fn(0x14dea0, Q, P, P, I, I)
        if inverse(analysis, packed, inverse_length, 1) != 0:
            raise AssertionError("native inverse FFT failed")
        time_values = [F.from_address(packed + 4*i).value
                       for i in range(inverse_length)]
        if not all(math.isfinite(value) for value in time_values):
            raise AssertionError("nonfinite inverse FFT block")
        return {
            "saved_energy": loaded_energy,
            "target_pcm_energy": target_energy,
            "host_frame_mask": hex(Q.from_address(working + 0x150).value),
            "spectrum_bins": bins,
            "real_nonzero": sum(v != 0 for v in real_values),
            "imag_nonzero": sum(v != 0 for v in imag_values),
            "spectral_power": sum(v*v for v in real_values + imag_values),
            "interior_spectral_power": sum(
                real_values[i]**2 + imag_values[i]**2
                for i in range(1, bins - 1)),
            "edge_spectral_power": sum(values[i]**2
                                       for values in (real_values, imag_values)
                                       for i in (0, bins - 1)),
            "spectrum_sha256": hashlib.sha256(
                c.string_at(real, bins*4) + c.string_at(imag, bins*4)
            ).hexdigest(),
            "inverse_fft_length": inverse_length,
            "inverse_fft_nonzero": sum(value != 0 for value in time_values),
            "inverse_fft_power": sum(value*value for value in time_values),
            "inverse_fft_sha256": hashlib.sha256(
                c.string_at(packed, inverse_length*4)).hexdigest(),
        }
    finally:
        try:
            if handle and crt.fclose(handle) != 0:
                raise OSError("cannot close owned source")
        finally:
            scratch_source.unlink(missing_ok=True)


def run(dll: Path, scratch_source: Path) -> dict:
    cases = []
    for name, energy in ENERGY_CASES + (ENERGY_CASES[0],):
        results = []
        for stage in ("host", "gain"):
            command = [sys.executable, "-u", str(Path(__file__).resolve()),
                       "--dll", str(dll), "--scratch-source", str(scratch_source),
                       "--single-energy", str(energy), "--stage", stage]
            child = subprocess.run(command, capture_output=True, text=True,
                                   check=False, timeout=20)
            if child.returncode:
                raise RuntimeError(f"native {stage} case {name} failed: "
                                   f"{child.stderr[-1000:]}")
            results.append(json.loads(child.stdout))
        host, gain = results
        if (gain["loaded_pcm"] != 1 or
                host["saved_energy"] != gain["loaded_energy"] or
                host["target_pcm_energy"] != gain["target_pcm_energy"]):
            raise AssertionError("host and gain cases have different inputs")
        model = fixture_gain_model(host["saved_energy"],
                                   host["target_pcm_energy"], 0.5)
        if abs(gain["gain"] - model["gain"]) > 1e-6:
            raise AssertionError("native cross-source gain differs from model")
        cases.append({"case": name, **host,
                      "independent_native_gain": gain["gain"],
                      "fixture_gain_model": model["gain"]})
    baseline = cases[0]
    if cases[-1] != {**baseline}:
        raise AssertionError("repeat host spectrum or gain changed")
    if (len({case["spectrum_sha256"] for case in cases[:3]}) != 3 or
            len({case["inverse_fft_sha256"] for case in cases[:3]}) != 3 or
            any(case["spectrum_bins"] != 2049 or
                case["inverse_fft_length"] != 4096 or
                case["inverse_fft_nonzero"] < 100 or
                case["real_nonzero"] < 100 or
                case["imag_nonzero"] < 100 for case in cases)):
        raise AssertionError("expected active, distinct host spectra")
    ratio_errors = []
    parseval_errors = []
    for case in cases:
        expected_power = (case["edge_spectral_power"] +
                          2*case["interior_spectral_power"]) / 4096
        parseval_errors.append(abs(case["inverse_fft_power"] -
                                   expected_power))
    if max(parseval_errors) > 0.02:
        raise AssertionError("inverse FFT power does not match half spectrum")
    for case in cases[1:3]:
        if case["edge_spectral_power"] != baseline["edge_spectral_power"]:
            raise AssertionError("host spectrum endpoint power changed")
        observed = (case["interior_spectral_power"] /
                    baseline["interior_spectral_power"])
        expected = ((case["independent_native_gain"] /
                     baseline["independent_native_gain"])**2)
        ratio_errors.append(abs(observed - expected))
    if max(ratio_errors) > 1e-5:
        raise AssertionError("host interior spectrum ratio differs from gain")
    return {"dse_sha256": SHA256, "cases": cases,
            "interior_power_gain_square_max_error": max(ratio_errors),
            "inverse_fft_parseval_max_absolute_error": max(parseval_errors),
            "scope": "Owned PCM and VQM frames through native 0x1757a0; "
                     "isolated target spectrum, not final overlap-add PCM."}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--scratch-source", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--single-energy", type=float)
    parser.add_argument("--stage", choices=("load", "gain", "host"),
                        default="host")
    args = parser.parse_args()
    if args.single_energy is not None:
        print(json.dumps(run_one(args.dll, args.scratch_source,
                                 args.single_energy, args.stage)))
        return
    if args.output is None:
        parser.error("--output is required outside --single-energy")
    report = run(args.dll, args.scratch_source)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({"cases": len(report["cases"]),
                      "spectrum_bins": [row["spectrum_bins"]
                                        for row in report["cases"]]}))


if __name__ == "__main__":
    main()
