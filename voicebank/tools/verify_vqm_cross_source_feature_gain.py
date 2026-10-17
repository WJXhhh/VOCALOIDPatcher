"""Compare owned bit-13 energy inputs in the native cross-source gain path."""

from __future__ import annotations

import argparse
import ctypes as c
import json
import math
from pathlib import Path
import struct

from audit_frm2_numeric import B, D, F, I, Native, P, Q, SHA256
from probe_frm2 import UNVOICED_MASK, UnvoicedFrame, serialize_frame


def fixture_gain_model(source_energy: float, target_energy: float,
                       original_fraction: float) -> dict:
    """Model the reached 6 dB contrast and downward level-correction branch."""
    source_db = 10.0 * math.log10(source_energy)
    target_db = 10.0 * math.log10(target_energy)
    contrast = min(1.0, max(0.0, (abs(2.0 * (source_db - target_db)) - 6.0) / 6.0))
    effective_fraction = original_fraction * (1.0 - contrast)
    new_level = ((1.0 - effective_fraction) * source_db +
                 effective_fraction * target_db)
    original_level = ((1.0 - original_fraction) * source_db +
                      original_fraction * target_db)
    downward_correction = min(new_level, original_level) - new_level
    ratio_term = min(20.0, (target_db - source_db) * effective_fraction)
    return {"gain": 10.0 ** ((ratio_term + downward_correction) / 20.0),
            "effective_fraction": effective_fraction,
            "downward_correction_db": downward_correction}


def run(dll: Path, scratch_source: Path) -> dict:
    if not scratch_source.parent.is_dir() or scratch_source.exists():
        raise ValueError("scratch source parent must exist and file must be absent")
    native = Native(dll)
    evaluate = native.fn(0x1722c0, None, P, P, I, I, P, P, I, I, B, P)
    read_energy = native.fn(0x173f70, F, P, P, P, F)
    initialize = native.fn(0x12a670, P, P)
    pcm = struct.pack("<4096h", *([10000] * 4096))
    offsets = []
    blob = bytearray(pcm)
    for index in range(96):
        offsets.append(len(blob))
        blob.extend(serialize_frame(UnvoicedFrame(0, float(index), UNVOICED_MASK, 0.0)))
    with scratch_source.open("xb") as output:
        output.write(blob)
    crt = c.CDLL("ucrtbase.dll")
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    handle = 0
    try:
        handle = crt._wfopen(str(scratch_source.resolve()), "rb")
        if not handle:
            raise OSError("cannot open owned cross-source data")
        context, controls, config, registry, voice, stream = (native.alloc(size)
            for size in (0x2B000, 0x5AB00, 0x100, 0x20, 0x280, 0x80))
        P.from_address(context).value = config
        P.from_address(config + 0x10).value = registry
        I.from_address(registry + 8).value = 1
        P.from_address(registry + 0x10).value = native.array(P, [voice])
        P.from_address(registry + 0x18).value = native.array(P, [native.alloc(8)])
        P.from_address(voice + 0x1a0).value = stream
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
        analysis = native.alloc(0x100)
        I.from_address(analysis + 0x8c).value = 16
        P.from_address(context + 0x1fc8).value = analysis
        F.from_address(context + 0x1d550).value = 8.0
        P.from_address(context + 0x24118).value = native.alloc(34)
        for scratch in (0x29860, 0x29b88, 0x29eb0):
            initialize(context + scratch)
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
        source_groups = [(20*i, 20*i+12, 20*i+3, 20*i+9) for i in range(4)]
        target_groups = [(22*i, 22*i+12, 22*i+3, 22*i+9) for i in range(4)]
        for part, groups in ((source, source_groups), (target, target_groups)):
            alignment = native.array(I, [value for group in groups for value in group])
            P.from_address(part + 0x1d8).value = alignment
            P.from_address(part + 0x1e0).value = alignment + 64
        item = native.alloc(0x140)
        P.from_address(item + 0x20).value = source
        I.from_address(item + 0x14).value = 1
        I.from_address(context + 0x29530).value = 1
        target_pcm_energy = read_energy(context, target, voice, F(27.0))
        if not target_pcm_energy > 0:
            raise AssertionError("target PCM fallback energy is not positive")
        rows = []
        energy_cases = (("fallback", None), ("saved_equal", 0.09313204),
                        ("saved_three_quarters", 0.06984903),
                        ("saved_half", 0.04656602),
                        ("saved_quarter", 0.02328301),
                        ("saved_twofold", 0.18626408),
                        ("saved_fourfold", 0.37252816))
        cases = [(pitch, name, value) for pitch in (100, 200, 300)
                 for name, value in energy_cases]
        cases.extend(((200, "fallback_repeat", None),
                      (200, "saved_fourfold_repeat", 0.37252816)))
        for score_pitch, name, saved_energy in cases:
            F.from_address(score).value = float(score_pitch)
            current_frame = native.alloc(0x328)
            initialize(current_frame)
            if saved_energy is not None:
                feature = native.alloc(0x228)
                I.from_address(feature).value = 1 << 2
                F.from_address(feature + 0x0c).value = saved_energy
                P.from_address(current_frame + 0x208).value = feature
            gain = F(-99.0)
            evaluate(context, item, 1, 25, current_frame, 0, 1, 0, 0,
                     c.byref(gain))
            first_time = D.from_address(context + 0x29b88 + 0x148).value
            if not math.isfinite(gain.value) or gain.value == -99.0:
                raise AssertionError({"case": name, "gain": gain.value,
                                      "first_time": first_time})
            model = (fixture_gain_model(saved_energy, target_pcm_energy,
                                        score_pitch / 400.0)
                     if saved_energy is not None else None)
            model_error = abs(gain.value - model["gain"]) if model else None
            if model_error is not None and model_error > 1e-6:
                raise AssertionError({"case": name, "native": gain.value,
                                      "model": model, "error": model_error})
            rows.append({"case": f"p{score_pitch}_{name}",
                         "score_pitch": score_pitch,
                         "source_target_fraction": score_pitch / 400.0,
                         "saved_energy": saved_energy,
                         "gain": gain.value, "secondary_frame_time": first_time,
                         "fixture_model": model,
                         "fixture_model_absolute_error": model_error})
        gains = {row["case"]: row["gain"] for row in rows}
        if gains["p200_fallback"] != 1.0 or gains["p200_fallback_repeat"] != 1.0:
            raise AssertionError("fallback gain changed across calls")
        if gains["p200_saved_fourfold"] != gains["p200_saved_fourfold_repeat"]:
            raise AssertionError("saved feature gain changed across calls")
        if not 0 < gains["p200_saved_fourfold"] < gains["p200_saved_equal"]:
            raise AssertionError("saved energy did not affect cross-source gain")
        return {
            "dse_sha256": SHA256,
            "native_cross_source_rva": "0x1722c0",
            "pcm_source": "owned constant int16 PCM; scratch source removed after run",
            "target_pcm_fallback_energy": target_pcm_energy,
            "fixture_model_max_absolute_error": max(
                row["fixture_model_absolute_error"] or 0.0 for row in rows),
            "cases": rows,
            "scope": "Owned same-parent cross-source fixture with one controlled saved energy and a PCM fallback on the other side. The reported 6 dB contrast/downward-correction model covers this exact reached branch and settings; this observes native gain output, not final overlap-add PCM.",
        }
    finally:
        try:
            if handle and crt.fclose(handle) != 0:
                raise OSError("cannot close owned cross-source data")
        finally:
            scratch_source.unlink()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--scratch-source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.scratch_source)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"cases": len(report["cases"]),
                      "max_model_error": report["fixture_model_max_absolute_error"]}))


if __name__ == "__main__":
    main()
