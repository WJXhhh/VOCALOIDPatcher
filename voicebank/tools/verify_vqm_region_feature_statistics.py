"""Verify Region append statistics on owned inputs in both V6 DSE layouts.

Uses genuine constructors and append wrappers, in a disposable process. Native
Region destruction is deliberately not called: its vector contains Python-owned
Frame storage. No installed resources are changed; only aggregate evidence is
written, with no commercial PCM, feature payloads or decompilation.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import random
import struct

from audit_frm2_numeric import B, D, F, I, Native, P, Q, SHA256, f32


ROUTES = [
    dict(name="DRS", region_ctor=0xbc3c0, region_size=0x838,
         frame_ctor=0x7ec30, append=0xbdf70, statistics=0xbf090,
         gate=0x810, feature=0x128, classification=0x158,
         mask=0x50, pitch=0xf0, time=0x48, pitch_mean=0xc4,
         scalar_base=0, mfcc=0xf0, band=0x140, class_mean=0x1e0,
         maximum=0x76c),
    dict(name="DSE5", region_ctor=0x13ddb0, region_size=0x1230,
         frame_ctor=0x12a670, append=0x13e180, statistics=0x13e7a0,
         gate=0x1220, feature=0x208, classification=0x238,
         mask=0x150, pitch=0x1d0, time=0x148, pitch_mean=0x190,
         scalar_base=0xcc, mfcc=0x1c0, band=0x210, class_mean=0x2b0,
         maximum=0x1054),
]
SHAPES = ("zero", "constant", "ramp", "impulse", "random",
          "cancellation", "wide")
PROFILES = ("full", "missing_inputs", "gate_gaps")
MASKS = ("full", "empty", "bit17_only")
GUARD = b"\xa5" * 32
INVALID_PITCH = -float.fromhex("0x1.fffffep+127")


def guarded(native, size):
    base = native.alloc(size + 64)
    c.memmove(base, GUARD, 32)
    c.memmove(base + 32 + size, GUARD, 32)
    return base + 32


def check_guard(address, size):
    assert c.string_at(address - 32, 32) == GUARD
    assert c.string_at(address + size, 32) == GUARD


def mapping(route):
    pairs = [(source, target + route["scalar_base"]) for source, target in
             ((4, 0xd4), (0x18, 0xcc), (0x14, 0xd8),
              (0x1c, 0xdc), (0x24, 0xc8), (0x20, 0xd0))]
    pairs += [(0x16c + 4 * k, route["mfcc"] + 4 * k) for k in range(20)]
    pairs += [(0x2c + 4 * k, route["band"] + 4 * k) for k in range(40)]
    return pairs


def outputs(route):
    return ([target for _, target in mapping(route)] +
            [route["pitch_mean"], route["maximum"]] +
            [route["class_mean"] + 4 * k for k in range(7)])


def mean(previous, value, count):
    # The native instructions round each mulss/addss/divss separately.
    return f32(f32(f32(f32(count) * previous) + value) / f32(count + 1))


def value(shape, step, slot, generator):
    if shape == "zero":
        result = 0.
    elif shape == "constant":
        result = 2.25
    elif shape == "ramp":
        result = (step - 12) * .75 + slot / 17.
    elif shape == "impulse":
        result = 17. if step % 7 == 0 else 0.
    elif shape == "random":
        result = generator.uniform(-300., 300.)
    elif shape == "cancellation":
        result = (-1. if step % 2 else 1.) * (1048576. + slot / 8.)
    elif shape == "wide":
        result = (-1. if slot % 2 else 1.) * 10. ** ((slot + step) % 17 - 8)
    else:
        raise ValueError(shape)
    return f32(result)


def packed(values):
    return struct.pack("<" + "f" * len(values), *values)


def run_case(native, route, shape, profile, mask_profile, marker, steps):
    region = guarded(native, route["region_size"])
    assert native.fn(route["region_ctor"], P, P)(region) == region
    assert I.from_address(region + 0x18).value == 0
    assert B.from_address(region + route["gate"]).value == 4
    offsets = outputs(route)
    expected = {off: F.from_address(region + off).value for off in offsets}
    assert all(math.isfinite(v) for v in expected.values())
    append = native.fn(route["append"], None, P, P)
    ctor = native.fn(route["frame_ctor"], P, P)
    generator = random.Random(420)
    trajectory = hashlib.sha256()
    frames = []
    retained = []
    source_checks = 0
    allowed = set(range(0x18, 0x1c)) | set(range(0x20, 0x30))
    for off in offsets:
        allowed.update(range(off, off + 4))
    missing_feature = missing_class = disabled = invalid_pitch = 0
    for step in range(steps):
        frame = guarded(native, 0x400)
        assert ctor(frame) == frame
        feature = guarded(native, 0x228)
        classification = guarded(native, 28)
        for slot, off in enumerate(range(4, 0x228, 4)):
            F.from_address(feature + off).value = value(shape, step, slot, generator)
        for slot in range(7):
            F.from_address(classification + slot * 4).value = value(
                shape, step, slot + 138, generator)
        I.from_address(feature).value = {"full": 0x1fffff, "empty": 0,
                                         "bit17_only": 1 << 17}[mask_profile]
        F.from_address(feature + 0x21c).value = marker
        has_feature = not (profile == "missing_inputs" and step % 4 == 1)
        has_class = not (profile == "missing_inputs" and step % 5 == 2)
        active = not (profile == "gate_gaps" and step % 3 == 1)
        pitch_values = (220., 440., 880., 0., INVALID_PITCH, -10000., 725.25, -275.5)
        pitch = pitch_values[step % len(pitch_values)]
        cents = step % len(pitch_values) >= 4
        mask = (1 << 31 if cents else 0) | (1 << 13 if mask_profile == "full" else 0)
        Q.from_address(frame + route["mask"]).value = mask
        P.from_address(frame + route["feature"]).value = feature if has_feature else 0
        P.from_address(frame + route["classification"]).value = (
            classification if has_class else 0)
        F.from_address(frame + route["pitch"]).value = pitch
        D.from_address(frame + route["time"]).value = step * .01
        B.from_address(region + route["gate"]).value = 4 if active else 0
        before = c.string_at(region, route["region_size"])
        retained.extend((address, size, c.string_at(address, size)) for address, size
                        in ((frame, 0x400), (feature, 0x228), (classification, 28)))
        if active:
            if has_feature:
                for source, target in mapping(route):
                    expected[target] = mean(expected[target],
                                            F.from_address(feature + source).value, step)
                maximum = F.from_address(feature + 0x18).value
                if expected[route["maximum"]] < maximum:
                    expected[route["maximum"]] = maximum
            converted = pitch if cents else (
                f32(math.log(pitch / 440.) * 1200. / math.log(2.))
                if pitch > 0 else INVALID_PITCH)
            if converted > INVALID_PITCH:
                expected[route["pitch_mean"]] = mean(
                    expected[route["pitch_mean"]], converted, step)
            if has_class:
                for slot in range(7):
                    off = route["class_mean"] + 4 * slot
                    expected[off] = mean(expected[off],
                        F.from_address(classification + 4 * slot).value, step)
        append(region, frame)
        frames.append(frame)
        assert I.from_address(region + 0x18).value == step + 1
        vector = P.from_address(region + 0x20).value
        assert vector and list((P * len(frames)).from_address(vector)) == frames
        assert D.from_address(region + 0x28).value == step * .01
        actual = packed([F.from_address(region + off).value for off in offsets])
        model = packed([expected[off] for off in offsets])
        assert all(math.isfinite(F.from_address(region + off).value) for off in offsets)
        assert actual == model, (route["name"], shape, profile, mask_profile,
                                 marker, step, "metadata model")
        trajectory.update(actual)
        after = c.string_at(region, route["region_size"])
        assert all(a == b or k in allowed for k, (a, b) in enumerate(zip(before, after)))
        check_guard(region, route["region_size"])
        # Recheck every earlier source, catching changes through retained pointers.
        for address, size, original in retained:
            assert c.string_at(address, size) == original
            check_guard(address, size)
            source_checks += 1
        missing_feature += not has_feature
        missing_class += not has_class
        disabled += not active
        invalid_pitch += converted <= INVALID_PITCH if active else 0
    # Null is supported by the statistics helper, not the full append wrapper.
    snapshot = c.string_at(region, route["region_size"])
    assert native.fn(route["statistics"], Q, P, P, I)(region, None, steps) == 0
    assert c.string_at(region, route["region_size"]) == snapshot
    return dict(shape=shape, profile=profile, masks=mask_profile, bit17_marker=marker,
                append_calls=steps, exact_metadata_values=steps * len(offsets),
                retained_source_checks=source_checks, missing_feature=missing_feature,
                missing_classification=missing_class, gate_disabled=disabled,
                invalid_pitch=invalid_pitch, metadata_sha256=trajectory.hexdigest(),
                null_helper_preserved=True)


def run(dll, steps):
    native = Native(dll)
    assert F.from_address(native.lib._handle + 0x5bffec).value == INVALID_PITCH
    rows = []
    for route in ROUTES:
        for shape in SHAPES:
            for profile in PROFILES:
                controls = []
                for masks in MASKS:
                    for marker in (0., f32(math.pi)):
                        row = run_case(native, route, shape, profile, masks, marker, steps)
                        row["route"] = route["name"]
                        controls.append(row["metadata_sha256"])
                        rows.append(row)
                assert len(set(controls)) == 1, (route["name"], shape, profile)
    for shape in SHAPES:
        for profile in PROFILES:
            hashes = {row["metadata_sha256"] for row in rows
                      if row["shape"] == shape and row["profile"] == profile}
            assert len(hashes) == 1, (shape, profile, "cross-layout")
    witnesses = []
    for route in ROUTES:
        region = guarded(native, route["region_size"])
        native.fn(route["region_ctor"], P, P)(region)
        means = []
        for step, source in enumerate((2., None, 8.)):
            frame = guarded(native, 0x400)
            native.fn(route["frame_ctor"], P, P)(frame)
            feature = guarded(native, 0x228)
            F.from_address(feature + 4).value = source or 0.
            P.from_address(frame + route["feature"]).value = (
                feature if source is not None else 0)
            F.from_address(frame + route["pitch"]).value = 0.
            D.from_address(frame + route["time"]).value = step * .01
            before = c.string_at(feature, 0x228)
            native.fn(route["append"], None, P, P)(region, frame)
            target = 0xd4 + route["scalar_base"]
            means.append(F.from_address(region + target).value)
            assert I.from_address(region + 0x18).value == step + 1
            assert c.string_at(feature, 0x228) == before
            check_guard(region, route["region_size"])
            check_guard(frame, 0x400)
            check_guard(feature, 0x228)
        assert means == [2., 2., 4.]
        witnesses.append(dict(route=route["name"], first=2., absent_middle=True,
                              third=8., metadata_means=means, valid_only_mean=5.))
    pins = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in
            (Path(__file__), Path(__file__).with_name("audit_frm2_numeric.py"),
             Path(__file__).with_name("probe_frm2.py"))}
    return dict(schema=1, dse_sha256=SHA256, source_sha256=pins,
                routes=ROUTES, inputs="owned synthetic Frame/feature/classification storage",
                steps=steps, cases=rows,
                summary=dict(cases=len(rows), append_calls=sum(x["append_calls"] for x in rows),
                    additional_witness_append_calls=6, null_statistics_calls=len(rows),
                    exact_metadata_values=sum(x["exact_metadata_values"] for x in rows),
                    retained_source_checks=sum(x["retained_source_checks"] for x in rows),
                    source_and_guard_preserved=True, region_changes_limited=True,
                    bit17_and_flag_controls_equal=True, cross_layout_metadata_equal=True),
                missing_input_rule="Prior count includes every appended Frame, not just valid features",
                pitch_invalid_sentinel=INVALID_PITCH,
                missing_input_witnesses=witnesses,
                scope="Region gate bits 1/2 disabled; monotonic frame times; finite owned inputs",
                limits=["Not the original VQM maker or its scoring inputs",
                        "Does not establish absence of bit17 consumers elsewhere",
                        "Missing/empty flags with populated pointers are owned boundary fixtures",
                        "Does not explain commercial zero arrays or nonzero bit17"],
                no_windows_temp=True, installed_files_unchanged=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path,
                        default=Path(r"C:\Program Files\VOCALOID6\Editor\DSE.dll"))
    parser.add_argument("--steps", type=int, default=32)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if not 1 <= args.steps <= 128:
        parser.error("steps must be 1..128")
    report = run(args.dll, args.steps)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps(report["summary"]))


if __name__ == "__main__":
    main()
