#!/usr/bin/env python3
"""Verify secondary-resonance calibration and discrete fit objectives on own data."""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

from audit_frm2_numeric import Native, SHA256, F, D, I, P, Q, B, f32, response, stats


def verify(native):
    calibrate = native.fn(0x6de40, Q, P, B, I, P, P, I, P, P, F, I, I, F)
    bandwidth = native.fn(0x75270, D, P, P, I, I, D, D, D, D, I, F)
    amplitude = native.fn(0x741d0, D, P, P, I, I, D, D, D, D, I, F)
    center = native.fn(0x74b00, D, P, P, I, I, D, D, I, F)
    errors = []
    for f0 in (100, 200, 400):
        for hz in (200, 250, 750):
            for bw in (200, 350):
                abc = tuple(map(f32, (-20, 60, -.0002)))
                obj = native.frame([200, 400, 600, 800, 1000], [-20, -22, -24, -26, -28],
                                    f0, abc, [], [(hz, 0, bw)])
                points = [(0, -23), (.1, -20), (.5, -40), (1, -70)]
                env = native.env(points, mode=1, regular=0, bounds=(-20000, 20000, 0))
                P.from_address(obj + 0x108).value = env
                Q.from_address(obj + 0x50).value |= 128
                ptr = P.from_address(obj + 0x1b8).value
                if calibrate(obj, 0, 0, obj + 0x180, 0, 1, obj + 0x1b0, ptr, 22050, 0, 0, 100):
                    raise AssertionError("Fixed-geometry calibration failed")
                res = P.from_address(ptr).value
                curve = abc[0] + f32(f32(f32(math.exp(f32(hz * abc[2]))) - 1) * abc[1])
                expected = max(native.evaluate(env, f32(f0 / 22050)),
                               native.evaluate(env, hz / 22050)) - curve
                errors.extend(abs(D.from_address(res + offset).value - value)
                              for offset, value in zip((0x40, 0x48, 0x58), (hz, expected, bw)))
                # Index/availability checks must reject without touching the list.
                for enabled, index in ((0, 0), (1, -1), (1, 1)):
                    if calibrate(obj, 0, 0, obj + 0x180, 0, enabled, obj + 0x1b0,
                                 ptr, 22050, index, 0, 100) != 8:
                        raise AssertionError("Invalid resonance index was accepted")
                native.keep.clear()

    grid_results = []
    frequencies = [100.0 * i for i in range(1, 16)]
    for truth in ((450, 8, 220), (453, 8.13, 223), (987, 11.7, 187)):
        target = [f32(20 * math.log10(response(hz, truth) / 32768)) for hz in frequencies]
        points = [(0, target[0] - 3), *zip((hz / 22050 for hz in frequencies), target), (1, target[-1])]
        env = native.env(points, mode=1, regular=0, bounds=(-20000, 20000, 0))
        obj = native.alloc(4096)  # Center search reads mask +0x50; no optional list.

        def loss(candidate):
            return sum((response(hz, candidate) - f32(32768 * math.exp(y * math.log(10) / 20))) ** 2
                       for hz, y in zip(frequencies, target))

        choices = {
            "bandwidth": [(truth[0], truth[1], 100 + i * 5) for i in range(40)],
            "amplitude": [(truth[0], i * .4, truth[2]) for i in range(40)],
            "center": [(100 + i * 35, truth[1], truth[2]) for i in range(40)],
        }
        actual = {
            "bandwidth": bandwidth(obj, env, 1, 15, truth[0], truth[1], 100, 300, 40, 22050),
            "amplitude": amplitude(obj, env, 1, 15, truth[0], truth[2], 0, 16, 0, 22050),
            "center": center(obj, env, 1, 15, truth[1], truth[2], 40, 22050),
        }
        for name, index in (("bandwidth", 2), ("amplitude", 1), ("center", 0)):
            expected = min(choices[name], key=loss)[index]
            grid_results.append({"truth": truth, "parameter": name, "expected": expected,
                                 "actual": actual[name], "error": abs(expected - actual[name])})
        native.keep.clear()
    result = {"fixed_geometry": stats(errors), "invalid_index_checks": 54,
              "grid_search": grid_results, "grid_errors": stats([row["error"] for row in grid_results])}
    if result["fixed_geometry"]["max"] >= 1e-4 or result["grid_errors"]["max"] >= 1e-8:
        raise AssertionError(result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = {"dll_sha256": SHA256, "scope": "Self-generated envelopes; set 0; no bit10 offset",
              **verify(Native(args.dll))}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
