"""Ordinary DSE5 runtime model, distinct from DRS's analytic resonance path.

The pinned DLL owns its lookup data. Only four cells are read per independent
interpolation; no lookup table or reference-frame payload is exported.
"""
from __future__ import annotations

import ctypes as c
import math

from audit_frm2_numeric import F, D, I, P, Q, B, f32


class DseModel:
    def __init__(self, native, abc, primary, secondary):
        self.native = native
        self.address = native.alloc(0x328)
        native.fn(0x12a670, P, P)(self.address)
        Q.from_address(self.address + 0x150).value = 0x600000
        for off, value in zip((0x270, 0x274, 0x278), abc):
            F.from_address(self.address + off).value = value
        self.abc = tuple(map(f32, abc))
        self.resonances = []
        for co, po, items in ((0x260, 0x268, primary), (0x27c, 0x280, secondary)):
            pointers = []
            for item in items:
                res = native.alloc(0x160)
                native.fn(0x128770, P, P)(res)
                native.fn(0x137ad0, None, P, D, D, D)(res, *item)
                pointers.append(res)
                self.resonances.append(res)
            I.from_address(self.address + co).value = len(pointers)
            P.from_address(self.address + po).value = native.array(P, pointers)
        self.consume = native.fn(0x12da40, Q, P, F, F, P, P, B, P, B, I)
        self.single = native.fn(0x12d7d0, D, P, D)

    def amplitude(self, hz, threshold=5000):
        amp, phase, cutoff = F(), F(), F(threshold)
        code = self.consume(self.address, hz, 22050, c.byref(amp), c.byref(phase),
                            0, c.byref(cutoff), 0, 0)
        if code:
            raise RuntimeError(f"DSE runtime model returned {code}")
        return amp.value

    def independent_resonance(self, res, hz):
        center = D.from_address(res + 0x140).value
        linear = D.from_address(res + 0x150).value
        bw = D.from_address(res + 0x158).value
        x = min(1599.0, abs(hz - center) / 5)
        y = min(147.0, (bw - 20) / 10)
        if y < 0:
            raise ValueError("Resonance must first pass the native bandwidth setter")
        i, j = int(x), int(y)

        def value(row, column):
            return D.from_address(self.native.lib._handle + 0x5ef130
                                  + 8 * (row * 1601 + column)).value

        low = (value(j, i + 1) - value(j, i)) * (x - i) + value(j, i)
        high = (value(j + 1, i + 1) - value(j + 1, i)) * (x - i) + value(j + 1, i)
        return ((high - low) * (y - j) + low) * linear

    def independent_amplitude(self, hz):
        hz = f32(hz)
        total = f32(sum(self.independent_resonance(res, hz) for res in self.resonances))
        a, b, slope = self.abc
        curve = f32(a + f32(f32(f32(math.exp(f32(hz * slope))) - 1) * b))
        level = f32(20 * math.log10(total / 32768)) if total > 0 else -3.4028234663852886e38
        return f32(curve + level)


def rebuild_residual(model, frame):
    """Recover ordinary 30-Hz residuals from runtime-model harmonic knots."""
    count = len(frame.residual_envelope.values) // 2
    if count != 736:
        raise ValueError("Only the ordinary 22050-Hz / 30-Hz residual grid is covered")
    previous_index = 0
    previous = f32(frame.amplitudes[0] - 3 - model.amplitude(0))
    result = [0.0] * count
    result[0] = min(10, max(-100, previous))
    last_inside = next(amplitude for hz, amplitude in reversed(list(zip(frame.frequencies_hz, frame.amplitudes)))
                       if 0 < hz < 22050 and amplitude != 10000)
    for hz, amplitude in zip(frame.frequencies_hz, frame.amplitudes):
        if hz <= 0 or amplitude == 10000:
            continue
        index = min(count - 1, int(hz / 30 + .5))
        source = f32(last_inside - 3) if hz >= 22050 else amplitude
        residual = f32(source - model.amplitude(hz))
        for i in range(previous_index + 1, index + 1):
            value = f32(previous + (i - previous_index) / (index - previous_index) * (residual - previous))
            result[i] = min(10, max(-100, value))
        previous_index, previous = index, residual
    return result
