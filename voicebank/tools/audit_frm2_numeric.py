#!/usr/bin/env python3
"""Bounded, read-only FRM2 numeric experiments against a hash-pinned local DSE.

Runs in a disposable Python process: native allocations live until process exit.
Only statistics/identifiers are written. Never serializes reference frame payloads.
"""
from __future__ import annotations

import argparse
import bisect
import ctypes as c
import hashlib
import json
import math
import struct
from pathlib import Path

from probe_frm2 import parse_frame

SHA256 = "7a4965bd377dbc2b08710e807ae1345b67d9fff6579ca0c01a769f744366f5b5"
F, D, I, P, Q, B = c.c_float, c.c_double, c.c_int, c.c_void_p, c.c_uint64, c.c_ubyte


def f32(x):
    return F(x).value


class Native:
    def __init__(self, dll):
        if hashlib.sha256(dll.read_bytes()).hexdigest() != SHA256:
            raise ValueError("Unsupported DSE hash; addresses must be reviewed first")
        self.directory = __import__("os").add_dll_directory(str(dll.parent))
        self.lib = c.CDLL(str(dll))
        self.keep = []
        self.frame_ctor = self.fn(0x7ec30, P, P)
        self.env_ctor = self.fn(0x60f90, P, P)
        self.cfg = self.fn(0x611e0, Q, P, B, B, B, F, F, F, I, I, I)
        self.add = self.fn(0x3af0, Q, P, D, F)
        self.evaluate = self.fn(0x2b30, F, P, D)
        self.res_set = self.fn(0x62760, None, P, D, D, D)
        self.consume = self.fn(0x77e40, Q, P, F, F, P, P, B, P)
        self.generate = self.fn(0x763b0, Q, P, B, F)

    def fn(self, rva, result, *args):
        return c.CFUNCTYPE(result, *args)(self.lib._handle + rva)

    def alloc(self, size):
        obj = c.create_string_buffer(size)
        self.keep.append(obj)
        return c.addressof(obj)

    def array(self, ty, values):
        obj = (ty * len(values))(*values)
        self.keep.append(obj)
        return c.addressof(obj)

    def env(self, points, mode=1, regular=1, bounds=(-100, 10, 0)):
        addr = self.alloc(160)
        self.env_ctor(addr)
        self.cfg(addr, 3, 1, mode, *bounds, 1, 0, 0)
        for x, y in points:
            self.add(addr, x, y)
        I.from_address(addr + 0x58).value = regular
        return addr

    def frame(self, frequencies, amplitudes, f0, abc, primary, secondary):
        addr = self.alloc(4096)
        self.frame_ctor(addr)
        Q.from_address(addr + 0x50).value = 0x600203
        I.from_address(addr + 0x58).value = len(frequencies)
        P.from_address(addr + 0x60).value = self.array(F, frequencies)
        P.from_address(addr + 0x68).value = self.array(F, amplitudes)
        F.from_address(addr + 0xf0).value = f0
        for off, value in zip((0x198, 0x1a0, 0x1a8), abc):
            F.from_address(addr + off).value = value
        for count_off, ptr_off, resonances in ((0x180, 0x188, primary), (0x1b0, 0x1b8, secondary)):
            pointers = []
            for center, amplitude, bandwidth in resonances:
                resonance = self.alloc(0x90)
                self.res_set(resonance, center, amplitude, bandwidth)
                pointers.append(resonance)
            I.from_address(addr + count_off).value = len(pointers)
            P.from_address(addr + ptr_off).value = self.array(P, pointers)
        return addr

    def amplitude(self, addr, frequency, threshold=5000):
        amp, phase, cutoff = F(), F(), F(threshold)
        result = self.consume(addr, frequency, 22050, c.byref(amp), c.byref(phase), 0, c.byref(cutoff))
        if result != 0:
            raise RuntimeError(f"consume returned {result}")
        return amp.value, phase.value

    def dse_env(self, points, mode=3, at=None):
        # Both layouts independently mapped through the native ENV readers.
        source = self.env(points, mode, 0, (-20000, 20000, 0))
        target = at or self.alloc(0x1c8)
        self.fn(0x117060, P, P)(target)
        c.memmove(target + 0x140, source + 0x40, 0x60)
        return target


def interpolate(points, x, cubic=False, regular=False):
    """Ordinary linear/grid or >=3-node cubic ENV, with explicit float rounding."""
    if cubic and len(points) < 3:
        raise ValueError('DSE cubic ENV requires at least three nodes for content-defined evaluation')
    if x <= points[0][0]:
        return points[0][1]
    if x >= points[-1][0]:
        return points[-1][1]
    if regular:
        position = x / points[1][0]
        i = int(position)
        if i >= len(points) - 1:
            return points[-1][1]
        if x == points[i][0]:
            return points[i][1]
        t = position - i
    else:
        i = bisect.bisect_right([p[0] for p in points], x) - 1
        t = (x - points[i][0]) / (points[i + 1][0] - points[i][0])
    if not cubic:
        return f32(points[i][1] + t * f32(points[i + 1][1] - points[i][1]))
    weights = (-(t * (t - 1) * (t - 2)) / 6,
               (t + 1) * (t - 1) * (t - 2) / 2,
               -(t + 1) * t * (t - 2) / 2,
               (t + 1) * t * (t - 1) / 6)
    result = 0.0
    for j, weight in zip(range(i - 1, i + 3), weights):
        result = f32(result + f32(points[min(len(points) - 1, max(0, j))][1] * f32(weight)))
    return result


def synthetic_checks(native):
    errors = []
    dse_eval = native.fn(0x11acc0, F, P, D)
    for xs in ((0, .25, .5, .75, 1), (0, .1, .37, .8, 1)):
        points = list(zip(xs, (-10, 5, -20, 9, -3)))
        for mode in (1, 3):
            drs = native.env(points, mode, 0, (-20000, 20000, 0))
            dse = native.dse_env(points, mode)
            for x in (i / 200 for i in range(201)):
                expected = interpolate(points, x, mode == 3)
                errors.extend((abs(native.evaluate(drs, x) - expected), abs(dse_eval(dse, x) - expected)))

    # Actual phase consumer: identity frequency warp, two alternate source frames.
    context = native.alloc(0x2b000)
    frame = native.alloc(0x328)
    P.from_address(context + 0x2238).value = frame
    F.from_address(context + 0x1d564).value = 22050
    native.dse_env([(0, 0), (1, 1)], mode=1, at=context + 0x247f0)
    alternatives = ([(0, -1), (.5, 7), (1, 15)], [(0, 2), (.5, 4), (1, 6)])
    for offset, points in zip((0x247b8, 0x247c0), alternatives):
        source = native.alloc(0x328)
        P.from_address(context + offset).value = source
        P.from_address(source + 0x218).value = native.dse_env(points)
    frequencies = [-1, 0, 5512.5, 11025, 16537.5, 22050, 22051, 1000]
    phases = native.array(F, [.25] * len(frequencies))
    I.from_address(frame + 0x158).value = len(frequencies)
    P.from_address(frame + 0x160).value = native.array(F, frequencies)
    P.from_address(frame + 0x170).value = phases
    consume = native.fn(0x16fa70, None, P, B)
    phase_errors = []
    for enabled in (0, 1):
        for selector in (0, .5, .5001, 1):
            F.from_address(context + 0x2415c).value = selector
            for j in range(len(frequencies)):
                F.from_address(phases + 4 * j).value = .25
            consume(context, enabled)
            points = alternatives[int(selector > .5)]
            stopped = False
            for j, frequency in enumerate(frequencies):
                expected = .25
                if enabled and not stopped and frequency > 0:
                    if frequency > 22050:
                        stopped = True
                    else:
                        # The consumer's reciprocal and product both round to float32.
                        x = f32(frequency * f32(1 / 22050))
                        warp = f32(x)
                        value = interpolate(points, warp, True)
                        wrapped = value - (math.trunc(value / math.tau) * math.tau
                                           - (math.tau if value < 0 else 0))
                        expected = f32(.25 + f32(wrapped))
                phase_errors.append(abs(F.from_address(phases + 4 * j).value - expected))
    fit = native.fn(0x75a90, Q, P, B, I, P, P, I, P, P, P, P, P, F, I, I, I)
    curve_errors = []
    for first in (100, 200, 300, 600):
        for count in (5, 25):
            for initial, slope in ((-20, -2), (-20, 0), (-80, 1)):
                frequencies = [first * (i + 1) for i in range(count)]
                amplitudes = [initial + slope * i for i in range(count)]
                obj = native.frame(frequencies, amplitudes, first, (0, 0, 0), [], [])
                Q.from_address(obj + 0x50).value = 3
                if fit(obj, 0, 0, obj + 0x180, 0, 0, obj + 0x1b0, 0,
                       obj + 0x198, obj + 0x1a0, obj + 0x1a8, 22050, 0, 1, 0):
                    raise AssertionError("Curve estimation failed")
                low = [a for f, a in zip(frequencies, amplitudes) if f <= 250]
                reference = max(low) if low else amplitudes[0]
                selected = [(f, a) for f, a in zip(frequencies, amplitudes) if f <= 10000]
                sum_x = sum_y = 0.0
                for frequency, amplitude in selected:
                    sum_x = f32(sum_x + f32(frequency - first))
                    sum_y = f32(sum_y + f32(math.log(max(amplitude, -99.999) + 100)))
                log_reference = f32(math.log(f32(reference + 100)))
                C = f32((f32(sum_y / len(selected)) - log_reference) / f32(sum_x / len(selected)))
                Bv = f32(f32(math.exp(f32(-first * C))) * f32(math.exp(log_reference)))
                expected = (min(f32(Bv - 100), -3), min(100, max(20, Bv)), max(-.0009, min(-.00003, C)))
                curve_errors.extend(abs(F.from_address(obj + off).value - value)
                                    for off, value in zip((0x198, 0x1a0, 0x1a8), expected))
    return {"envelope_interpolation": stats(errors), "bit37_phase_consumer": stats(phase_errors),
            "bit22_array_estimator": stats(curve_errors)}


def response(frequency, resonance):
    center, amplitude, bandwidth = resonance
    bandwidth = min(1500, max(20, bandwidth))
    a2 = -math.exp(-2 * math.pi * bandwidth / 22050)
    a1 = 2 * math.exp(-math.pi * bandwidth / 22050) * math.cos(math.pi)
    gain = 1 - a1 - a2
    norm = gain / math.hypot(1 - a1 * math.cos(math.pi) - a2 * math.cos(2 * math.pi),
                             a1 * math.sin(math.pi) + a2 * math.sin(2 * math.pi))
    theta = 2 * math.pi * (11025 + center - frequency) / 22050
    denominator = math.hypot(1 - a1 * math.cos(theta) - a2 * math.cos(2 * theta),
                             a1 * math.sin(theta) + a2 * math.sin(2 * theta))
    # The native setter rounds its logarithm exponent and exp result to float32.
    linear = 32768 * f32(math.exp(f32(f32(amplitude) * f32(math.log(10) / 20))))
    return gain / denominator * linear / norm * math.exp(-((frequency - center) / 2000) ** 2)


def read_frame(path, offset):
    with path.open("rb") as stream:
        stream.seek(offset)
        header = stream.read(8)
        if len(header) != 8 or header[:4] != b"FRM2":
            raise ValueError("Invalid frame location")
        size = struct.unpack_from("<I", header, 4)[0]
        if not 28 <= size <= 1_000_000:
            raise ValueError("Invalid frame size")
        return parse_frame(header + stream.read(size - 8))


def stats(errors):
    if any(not math.isfinite(error) for error in errors):
        raise AssertionError("Non-finite comparison result")
    return {"count": len(errors), "max": max(errors, default=0),
            "mean": sum(errors) / len(errors) if errors else 0,
            "within_1e-4": sum(e < 1e-4 for e in errors)}


def audit(native, root, manifest):
    paths = {p.stem: p for p in root.rglob("*V4X*.ddb")}
    results = []
    for row in manifest:
        frame = read_frame(paths[row["bank"]], int(row["offset"], 16))
        addr = native.frame(frame.frequencies_hz, frame.amplitudes, frame.f0_hz,
                            frame.secondary_header, frame.resonances, frame.secondary_resonances)
        assert frame.residual_envelope.flags == 0x960002
        values = frame.residual_envelope.values
        env = native.env(list(zip(values[::2], values[1::2])))
        P.from_address(addr + 0x1c8).value = env
        Q.from_address(addr + 0x50).value |= 1 << 23
        low, high, formula_errors, threshold_errors, envelope_errors = [], [], [], [], []
        A, Bv, C = frame.secondary_header
        for frequency, amplitude in zip(frame.frequencies_hz, frame.amplitudes):
            if not 0 < frequency < 22050 or amplitude == 10000:
                continue
            actual = native.amplitude(addr, frequency)[0]
            (low if frequency < 5000 else high).append(abs(actual - amplitude))
            threshold_errors.append(abs(actual - native.amplitude(addr, frequency, 0)[0]))
            curve = A + f32(f32(f32(math.exp(f32(frequency * C))) - 1) * Bv)
            residual = native.evaluate(env, f32(frequency / 22050))
            envelope_errors.append(abs(residual - interpolate(list(zip(values[::2], values[1::2])),
                                                               f32(frequency / 22050), regular=True)))
            total = sum(response(frequency, item) for item in (*frame.resonances, *frame.secondary_resonances))
            expected = curve + residual + 20 * math.log10(f32(total) / 32768)
            formula_errors.append(abs(actual - expected))
        phase_values = frame.harmonic_envelope.values
        delta = phase_values[3] - frame.phases_radians[0]
        phase_errors = []
        for k, y in enumerate(phase_values[3:-2:2], 1):
            if k <= len(frame.phases_radians) and frame.frequencies_hz[k - 1] > 0:
                phase_errors.append(abs(math.remainder(y - frame.phases_radians[k - 1] - k * delta, math.tau)))
        results.append({"bank": row["bank"], "offset": row["offset"],
                        "low": stats(low), "high": stats(high), "formula": stats(formula_errors),
                        "threshold_amplitude": stats(threshold_errors), "linear_env": stats(envelope_errors),
                        "phase_modulo_linear_term": stats(phase_errors)})
    return results


def select_frames(root, parser_project):
    from verify_reference_ddi import load_public_parser, pointer, samples
    from probe_frm2 import MAIN_MASK
    model_type = load_public_parser(parser_project)
    rows = []
    for path in sorted(root.rglob("*V4X*.ddi")):
        raw = path.read_bytes()
        model = model_type(raw)
        model.read()
        for category in ("STA", "ART"):
            parts = [part for cat, part in samples(model) if cat == category]
            if not parts:
                continue
            chosen = sorted({round(j * (len(parts) - 1) / 15) for j in range(16)})
            with path.with_suffix(".ddb").open("rb") as stream:
                for index in chosen:
                    part = parts[index]
                    for frame_index in sorted({0, len(part["epr"]) // 2, len(part["epr"]) - 1}):
                        offset = pointer(part["epr"][frame_index], raw)[0]
                        stream.seek(offset)
                        header = stream.read(28)
                        if len(header) == 28 and header[:4] == b"FRM2" and struct.unpack_from("<Q", header, 20)[0] == MAIN_MASK:
                            rows.append({"bank": path.stem, "category": category, "part": index,
                                         "index": frame_index, "offset": hex(offset)})
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--voice-root", type=Path, required=True)
    selection = parser.add_mutually_exclusive_group(required=True)
    selection.add_argument("--manifest", type=Path, help="JSON list of bank stems and hexadecimal offsets")
    selection.add_argument("--parser-project", type=Path, help="External ddb-tools checkout; selects 16 STA and ART parts per bank")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    manifest = (json.loads(args.manifest.read_text(encoding="utf-8")) if args.manifest
                else select_frames(args.voice_root, args.parser_project))
    if not isinstance(manifest, list) or not 1 <= len(manifest) <= 1000:
        raise ValueError("Require 1..1000 frame identifiers")
    native = Native(args.dll)
    synthetic = synthetic_checks(native)
    rows = audit(native, args.voice_root, manifest)
    summary = {key: {"frames": len(rows), "complete_matches": sum(r[key]["max"] < 1e-4 for r in rows),
                     "points": sum(r[key]["count"] for r in rows), "max": max(r[key]["max"] for r in rows)}
               for key in ("low", "high", "formula", "threshold_amplitude", "linear_env", "phase_modulo_linear_term")}
    report = {"dll_sha256": SHA256, "synthetic": synthetic, "summary": summary, "manifest": manifest, "frames": rows}
    args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"synthetic": synthetic, "summary": summary}, indent=2))
    for result in (*synthetic.values(), *(summary[key] for key in ("formula", "threshold_amplitude", "linear_env"))):
        if result["max"] >= 1e-4:
            raise AssertionError("Previously verified numeric contract regressed; inspect the report")


if __name__ == "__main__":
    main()
