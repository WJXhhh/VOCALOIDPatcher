"""Compare complete secondary fitting with an independent Python optimizer."""
from __future__ import annotations

import argparse
from itertools import product
import json
import math
from pathlib import Path

from audit_frm2_numeric import Native, SHA256, F, D, I, P, Q, B, f32, stats
from frm2_resonance_optimizer import fit, grid_response


def verify(native):
    call = native.fn(0x6de40, Q, P, B, I, P, P, I, P, P, F, I, I, F)
    errors, rows = [], []
    matrix = [(f0, center, width, mixture, offset, optimize, 0, 'slope')
              for f0, center, width, mixture, offset, optimize in product((100, 400), (200, 750), (250, 400), (0, 1), (-6, 0, 6), (0, 1))]
    matrix += [(f0, center, width, 1, offset, 1, index, 'peak')
               for f0, center, width, offset, index in product((100, 400), (350, 850), (180, 350, 550), (0, 6), (0, 1))]
    for f0, center, width, mixture, offset, optimize, index, shape in matrix:
        abc = tuple(map(f32, (-20, 60, -.0002)))
        primary = [(900., -12., 230.)] if mixture else []
        secondary = [(center, 0., width)] + ([(1200., -18., 200.)] if mixture else [])
        if index:
            secondary.reverse()
        points = [(i / 100, f32(-20 - i * .5 + 7 * math.exp(-((i * 220.5 - 650) / 300) ** 2))) for i in range(101)]
        if shape == 'peak':
            # Dense, owned peaks force interior searches and bandwidth limits.
            # Input synthesis is independent of the unknown output parameters.
            points = [(i / 200, f32(-30 + 20 * math.log10(max(1e-15,
                       grid_response(i * 110.25, center + 70, 4, width + 30) / 32768))))
                      for i in range(201)]
        frame = native.frame([100, 200, 300, 400, 500], [-20, -21, -22, -23, -24], f0, abc, primary, secondary)
        P.from_address(frame + 0x108).value = native.env(points, mode=1, regular=0, bounds=(-20000, 20000, 0))
        Q.from_address(frame + 0x50).value |= 128 | 1024
        F.from_address(frame + 0xf8).value = offset
        expected, trace = fit(points, f0, abc, primary, secondary, index, optimize=optimize, movement=100, offset=offset)
        primary_ptr = P.from_address(frame + 0x188).value
        secondary_ptr = P.from_address(frame + 0x1b8).value
        if call(frame, 0, bool(primary), frame + 0x180, primary_ptr, 1, frame + 0x1b0, secondary_ptr, 22050, index, optimize, 100):
            raise AssertionError('Native secondary fit failed')
        resonance = P.from_address(secondary_ptr + index * 8).value
        actual = tuple(D.from_address(resonance + at).value for at in (0x40, 0x48, 0x58))
        delta = [abs(a - b) for a, b in zip(actual, expected)]
        if max(delta) >= 1e-4:
            raise AssertionError(dict(case=(f0, center, width, mixture, offset, optimize), expected=expected, actual=actual, trace=trace))
        errors.extend(delta)
        rows.append(dict(f0=f0, center=center, width=width, mixture=mixture, offset=offset, optimize=optimize,
                         index=index, shape=shape, result=actual, trace=trace, error=max(delta)))
    return dict(cases=rows, parameters=stats(errors), scope='Set 0, supplied linear nonregular E7, indices 0/1, curve present, bit10 offsets and both lists; no native calls in prediction')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = dict(dll_sha256=SHA256, **verify(Native(args.dll)))
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(cases=len(report['cases']), parameters=report['parameters'])))


if __name__ == '__main__':
    main()
