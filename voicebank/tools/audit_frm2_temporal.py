#!/usr/bin/env python3
"""Rebuild bit-22 curves and apply the observed DRS in-place temporal filter.

Reference payloads stay in memory. Output contains statistics and frame identifiers.
"""
from __future__ import annotations

import argparse
import json
import math
from dataclasses import replace
from pathlib import Path

from audit_frm2_numeric import Native, SHA256, F, I, P, Q, B, f32, read_frame, stats
from probe_frm2 import MainFrame
from verify_reference_ddi import load_public_parser, pointer, samples


def smooth_curve(values):
    result = list(values)
    for _ in range(2):
        if len(result) > 2:
            result[1] = f32(result[0] / 3 + result[1] / 3 + result[2] / 3)
        if len(result) > 3:
            result[-2] = f32(result[-1] / 3 + result[-2] / 3 + result[-3] / 3)
        for i in range(2, len(result) - 2):
            result[i] = f32(sum(value / 5 for value in result[i - 2:i + 3]))
    return result


def estimator(native):
    function = native.fn(0x75a90, Q, P, B, I, P, P, I, P, P, P, P, P, F, I, I, I)

    def run(frame):
        obj = native.frame(frame.frequencies_hz, frame.amplitudes, frame.f0_hz, (0, 0, 0), [], [])
        Q.from_address(obj + 0x50).value = 3
        code = function(obj, 0, 0, obj + 0x180, 0, 0, obj + 0x1b0, 0,
                        obj + 0x198, obj + 0x1a0, obj + 0x1a8, 22050, 0, 1, 0)
        if code:
            raise RuntimeError(f"Native estimator failed: {code}")
        result = tuple(F.from_address(obj + offset).value for offset in (0x198, 0x1a0, 0x1a8))
        # No native pointer escapes this synchronous call; release Python buffers.
        # Small constructor-owned native allocations last until this process exits.
        native.keep.clear()
        return result
    return run


def unrestricted_slope(frame):
    """Array estimator's C before the current DRS applies its lower bound.

    V4X can retain slopes below this DSE version's -0.0009 bound. A and B
    are already computed before that bound in the native estimator.
    """
    first = frame.frequencies_hz[0]
    reference = max((a for hz, a in zip(frame.frequencies_hz, frame.amplitudes)
                     if hz <= 250 and a != 10000), default=frame.amplitudes[0])
    sx = sy = 0.0
    count = 0
    for hz, amplitude in zip(frame.frequencies_hz, frame.amplitudes):
        if hz > 10000:
            break
        if hz >= 0 and amplitude != 10000:
            sx = f32(sx + f32(hz - first))
            sy = f32(sy + f32(math.log(max(amplitude, -99.999) + 100)))
            count += 1
    if count < 3 or sx == 0 or reference <= -100:
        raise ValueError("Insufficient ordinary harmonics for independent slope")
    return f32(f32(f32(sy / count) - f32(math.log(f32(reference + 100)))) / f32(sx / count))


def v4x_estimation_input(frame):
    """Lift a very quiet spectrum as a whole before curve estimation.

    Changing only the reference or only its harmonic does not preserve the
    logarithmic regression. The saved input arrays themselves remain untouched.
    """
    shift = max(0.0, -99.5 - frame.amplitudes[0])
    if not shift:
        return frame
    return replace(frame, amplitudes=tuple(f32(a + shift) if a != 10000 else a
                                           for a in frame.amplitudes))


def verify_filter(native):
    errors = []
    smoother = native.fn(0xc1610, Q, P, B, F, I)
    for count in (3, 4, 5, 9, 25):
        frames, initial = [], []
        for i in range(count):
            abc = tuple(map(f32, (-30 + i * i / 10, 70 + i * i / 10, -.0001 - i * i * .000001)))
            initial.append(abc)
            frames.append(native.frame([200, 400, 600, 800, 1000], [-20, -25, -30, -35, -40],
                                        200, abc, [(1000, 10, 200)], [(200, 0, 200)]))
        region = native.alloc(0x80)
        I.from_address(region + 0x18).value = count
        P.from_address(region + 0x20).value = native.array(P, frames)
        if smoother(region, 0, 22050, 0):
            raise RuntimeError("Native temporal filter failed")
        expected = list(zip(*(smooth_curve([values[j] for values in initial]) for j in range(3))))
        for obj, values in zip(frames, expected):
            errors.extend(abs(F.from_address(obj + offset).value - value)
                          for offset, value in zip((0x198, 0x1a0, 0x1a8), values))
    result = stats(errors)
    if result["max"] >= 1e-4:
        raise AssertionError(f"Temporal filter contract mismatch: {result}")
    native.keep.clear()
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--voice-root", type=Path, required=True)
    parser.add_argument("--parser-project", type=Path, required=True)
    parser.add_argument("--category", choices=("STA", "ART"), default="STA")
    parser.add_argument("--parts-per-bank", type=int, default=0, help="0 = all; otherwise evenly spaced sample")
    parser.add_argument("--v4x-estimator", "--v4x-slope", dest="v4x_slope", action="store_true",
                        help="Apply V4X quiet-spectrum lift and independent slope before DSE's lower clamp")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.parts_per_bank < 0:
        parser.error("--parts-per-bank must be nonnegative")
    native = Native(args.dll)
    validation = verify_filter(native)
    estimate = estimator(native)
    model_type = load_public_parser(args.parser_project)
    banks = []
    for path in sorted(args.voice_root.rglob("*V4X*.ddi")):
        raw = path.read_bytes()
        model = model_type(raw)
        model.read()
        parts = [part for category, part in samples(model) if category == args.category]
        indices = list(range(len(parts)))
        if args.parts_per_bank > 0 and len(parts) > args.parts_per_bank:
            if args.parts_per_bank == 1:
                indices = [0]
            else:
                indices = sorted({round(i * (len(parts) - 1) / (args.parts_per_bank - 1)) for i in range(args.parts_per_bank)})
        errors, groups, failures, skipped = [], 0, [], 0
        axis_errors = [[], [], []]
        matches = 0
        unsupported = []
        slope_range = [math.inf, -math.inf]
        for index in indices:
            runs, current = [], []
            for frame_index, ep in enumerate(parts[index]["epr"]):
                offset = pointer(ep, raw)[0]
                frame = read_frame(path.with_suffix(".ddb"), offset)
                # ART region timestamps restart inside an already smoothed unit.
                # They are runtime alignment boundaries, not estimation boundaries.
                if current and not isinstance(frame, MainFrame):
                    runs.append(current)
                    current = []
                if isinstance(frame, MainFrame):
                    source = v4x_estimation_input(frame) if args.v4x_slope else frame
                    abc = estimate(source)
                    if args.v4x_slope:
                        try:
                            slope = unrestricted_slope(source)
                        except ValueError:
                            unsupported.append({"part_index": index, "frame_index": frame_index,
                                                "offset": hex(offset)})
                        else:
                            slope_range = [min(slope_range[0], slope), max(slope_range[1], slope)]
                            abc = (*abc[:2], min(f32(-.00003), slope))
                    current.append((offset, frame_index, frame.secondary_header, abc))
                else:
                    skipped += 1
            if current:
                runs.append(current)
            for run in runs:
                groups += 1
                predicted = list(zip(*(smooth_curve([row[3][j] for row in run]) for j in range(3))))
                for row, values in zip(run, predicted):
                    per_axis = [abs(a - b) for a, b in zip(row[2], values)]
                    for axis, error in zip(axis_errors, per_axis):
                        axis.append(error)
                    good = per_axis[0] < 1e-4 and per_axis[1] < 1e-4 and per_axis[2] < 1e-9
                    matches += int(good)
                    error = max(per_axis)
                    errors.append(error)
                    if not good and len(failures) < 16:
                        failures.append({"part_index": index, "frame_index": row[1],
                                         "offset": hex(row[0]), "ABC_error": per_axis})
        bank = {"bank": path.stem, "parts": len(indices), "ordinary_runs": groups,
                "nonordinary_frames": skipped, "error": stats(errors), "matches": matches,
                "axis_errors": [stats(axis) for axis in axis_errors],
                "raw_slope_range": slope_range if args.v4x_slope else None,
                "unsupported_slope_inputs": unsupported,
                "failure_examples": failures}
        banks.append(bank)
        print(json.dumps({key: bank[key] for key in ("bank", "parts", "matches", "error")}), flush=True)
        # Keep completed bank statistics if a later bank has an unexpected input.
        args.output.write_text(json.dumps({"dll_sha256": SHA256, "complete": False,
                                           "category": args.category, "banks": banks}, indent=2) + "\n",
                               encoding="utf-8")
    report = {"dll_sha256": SHA256, "complete": True, "category": args.category, "parts_per_bank": args.parts_per_bank,
              "synthetic_filter": validation, "grouping": "DDI unit, split only at nonordinary frames",
              "v4x_slope": args.v4x_slope, "tolerances_ABC": [1e-4, 1e-4, 1e-9],
              "frames": sum(bank["error"]["count"] for bank in banks),
              "matches": sum(bank["matches"] for bank in banks), "banks": banks}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"frames={report['frames']} matches={report['matches']}")


if __name__ == "__main__":
    main()
