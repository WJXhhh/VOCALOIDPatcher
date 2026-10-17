#!/usr/bin/env python3
"""Check recovered FRM2 production rules without copying bank payloads.

DRS candidates remain as historical controls. DSE lookup reconstruction and its
independent consumer formula are verified contracts; bit37 generation is open.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

from audit_frm2_numeric import Native, SHA256, F, I, P, f32, read_frame, stats
from frm2_dse_model import DseModel, rebuild_residual


def inspect(native, frame):
    dse = DseModel(native, frame.secondary_header, frame.resonances, frame.secondary_resonances)
    rebuilt = rebuild_residual(dse, frame)
    dse_formula_errors = []
    for hz in frame.frequencies_hz:
        if 0 < hz < 22050:
            dse_formula_errors.append(abs(dse.amplitude(hz) - dse.independent_amplitude(hz)))
    obj = native.frame(frame.frequencies_hz, frame.amplitudes, frame.f0_hz,
                       frame.secondary_header, frame.resonances, frame.secondary_resonances)
    saved = frame.residual_envelope.values[1::2]
    dc_candidate = f32(frame.amplitudes[0] - 3 - native.amplitude(obj, 0)[0])
    knot_errors = []
    for frequency, amplitude in zip(frame.frequencies_hz, frame.amplitudes):
        if not 0 < frequency < 22050 or amplitude == 10000:
            continue
        index = min(len(saved) - 1, int(frequency / 30 + .5))
        predicted = min(10, max(-100, f32(amplitude - native.amplitude(obj, frequency)[0])))
        knot_errors.append(abs(saved[index] - predicted))
    if native.generate(obj, 0, 22050):
        raise RuntimeError("Native residual generation failed")
    env = P.from_address(obj + 0x1c8).value
    count = I.from_address(env + 0x5c).value
    if count != len(saved):
        raise AssertionError("Native and stored residual grid lengths differ")
    points = P.from_address(env + 0x78).value
    actual = [F.from_address(points + 16 * i + 8).value for i in range(count)]
    first_index = min(count - 1, int(frame.frequencies_hz[0] / 30 + .5))
    if first_index <= 0:
        raise ValueError("DC candidate requires a positive first harmonic above 15 Hz")
    corrected = list(actual)
    for i in range(first_index):
        corrected[i] = min(10, max(-100, f32(dc_candidate * (1 - i / first_index)
                                           + actual[first_index] * i / first_index)))

    phase = frame.harmonic_envelope.values[1::2]
    delta = phase[1] - frame.phases_radians[0]
    phase_errors, exceptions = [], []
    for k, y in enumerate(phase[1:-1], 1):
        if k > len(frame.phases_radians) or frame.frequencies_hz[k - 1] <= 0:
            continue
        error = abs(math.remainder(y - frame.phases_radians[k - 1] - k * delta, math.tau))
        phase_errors.append(error)
        if error >= 1e-4:
            exceptions.append(k)
    result = {
        "dse_bit23_rebuild": stats([abs(a - b) for a, b in zip(saved, rebuilt)]),
        "dse_independent_formula": stats(dse_formula_errors),
        "native_bit23": stats([abs(a - b) for a, b in zip(saved, actual)]),
        "dc_corrected_candidate": stats([abs(a - b) for a, b in zip(saved, corrected)]),
        "harmonic_knots_candidate": stats(knot_errors),
        "dc_candidate_error": abs(saved[0] - dc_candidate),
        "bit23_tail_error": abs(saved[-1] - actual[-1]),
        "phase_linear_alignment": stats(phase_errors),
        "phase_exception_harmonics": exceptions,
    }
    native.keep.clear()
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--voice-root", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True,
                        help="Numeric audit JSON report or list of frame identifiers")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    data = json.loads(args.manifest.read_text(encoding="utf-8"))
    manifest = data["manifest"] if isinstance(data, dict) else data
    if not isinstance(manifest, list) or not 1 <= len(manifest) <= 1000:
        parser.error("Require 1..1000 frame identifiers")
    paths = {path.stem: path for path in args.voice_root.rglob("*V4X*.ddb")}
    native = Native(args.dll)
    rows = []
    for row in manifest:
        frame = read_frame(paths[row["bank"]], int(row["offset"], 16))
        rows.append({**row, **inspect(native, frame)})
    keys = ("native_bit23", "dc_corrected_candidate", "harmonic_knots_candidate", "phase_linear_alignment",
            "dse_bit23_rebuild", "dse_independent_formula")
    summary = {key: {"frames": len(rows), "matches": sum(row[key]["max"] < 1e-4 for row in rows),
                     "points": sum(row[key]["count"] for row in rows),
                     "point_matches": sum(row[key]["within_1e-4"] for row in rows),
                     "max": max(row[key]["max"] for row in rows)} for key in keys}
    summary["dc_candidate"] = stats([row["dc_candidate_error"] for row in rows])
    report = {"dll_sha256": SHA256,
              "status": "DSE bit23 and consumer contracts; DRS controls and unresolved bit37 observations",
              "summary": summary, "frames": rows}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary, indent=2))
    for key in ("dse_bit23_rebuild", "dse_independent_formula"):
        if summary[key]["max"] >= 1e-4:
            raise AssertionError(f"Recovered contract failed: {key}; inspect saved frame identifiers")


if __name__ == "__main__":
    main()
