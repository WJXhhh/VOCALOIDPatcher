#!/usr/bin/env python3
"""Check DRS setter amplitude precision and independently predicted responses."""
import argparse
import json
import math
from pathlib import Path

from audit_frm2_numeric import Native, SHA256, D, f32, stats
from frm2_resonance_optimizer import FLOOR, linear, response


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    native = Native(args.dll)
    resonance = native.alloc(256)
    levels = [FLOOR, -200., -100., 0., 20., 40.]
    levels += [f32(-80 + i * .371) for i in range(401)]
    amplitude_errors, old_errors, response_errors = [], [], []
    for i, level in enumerate(levels):
        center, width = 100. + (i * 137) % 21000, 20. + (i * 31) % 1480
        native.res_set(resonance, center, level, width)
        actual = D.from_address(resonance + 0x50).value
        amplitude_errors.append(abs(actual - linear(level)))
        old = 0. if level <= FLOOR else 32768 * f32(math.exp(f32(f32(level) * f32(math.log(10) / 20))))
        old_errors.append(abs(actual - old))
        # Read the setter's cached coefficients only on the reference side.
        rate, gain, a1, a2, norm = [D.from_address(resonance + off).value
                                    for off in (0x60, 0x68, 0x70, 0x78, 0x80)]
        for frequency in (0., 100., 1000., 5000., 10000., 22050., center):
            theta = ((rate * .5 + center - frequency) * math.tau) / rate
            real = 1 - a1 * math.cos(theta) - a2 * math.cos(theta + theta)
            imaginary = a1 * math.sin(theta) + a2 * math.sin(theta + theta)
            delta = frequency - center
            reference = gain / math.sqrt(imaginary * imaginary + real * real) * actual / norm * math.exp((-delta * delta / 2000) / 2000)
            predicted = response(frequency, (center, level, width))
            response_errors.append(abs(reference - predicted) / max(1., abs(reference)))
    report = dict(dll_sha256=SHA256, setter_cases=len(levels), amplitude_errors=stats(amplitude_errors),
                  previous_conversion_errors=stats(old_errors), response_relative_errors=stats(response_errors),
                  scope='DRS 0x62760 cached amplitude and response coefficients; no claim about a separate DSE consumer setter.')
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report))
    assert max(amplitude_errors) == 0
    assert max(response_errors) < 1e-10


if __name__ == '__main__':
    main()
