#!/usr/bin/env python3
"""Compare independent initial ABC on sparse/dense owned E7 inputs."""
import argparse
import itertools
import json
from pathlib import Path

from audit_frm2_numeric import Native, SHA256, F, I, P, B, Q, f32, stats
from frm2_initial_curve import initial_curve
from verify_frm2_source_fit import frame_from_prediction


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    native = Native(args.dll)
    cases, errors = [], []
    for count, f0, offset, level in itertools.product((2, 3, 4, 5, 12, 80), (80., 220., 880.), (0., -20., 15.), (-20., -110.)):
        inside = [(f0 / 22050 + k * (.9 - f0 / 22050) / max(1, count - 3), f32(level - k * .7)) for k in range(count - 2)]
        points = [(0., f32(level - 3))] + inside + [(1., f32(level - count * .7 - 3))]
        frame = frame_from_prediction(native, dict(f0=f0, gain=offset, points=points))
        native.fn(0x759a0, None, P, B, F, I, P, I)(frame, 0, 22050., 1, 0, 1)
        actual = tuple(F.from_address(frame + off).value for off in (0x198, 0x1a0, 0x1a8))
        predicted, fitted = initial_curve(points, f0, offset)
        delta = [abs(a - b) for a, b in zip(actual, predicted)]
        same_flag = bool(Q.from_address(frame + 0x50).value & (1 << 22)) == fitted
        errors.extend(delta)
        cases.append(dict(count=count, f0=f0, offset=offset, level=level, actual=actual,
                          predicted=predicted, fitted=fitted, flag_equal=same_flag, max_error=max(delta)))
    report = dict(dll_sha256=SHA256, cases=cases, errors=stats(errors))
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(cases=len(cases), errors=report['errors'], flags_equal=all(x['flag_equal'] for x in cases))))
    assert max(errors) < 1e-4 and all(x['flag_equal'] for x in cases)


if __name__ == '__main__':
    main()
