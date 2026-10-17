"""Compare full native resonance refinement to the independent optimizer model."""
import argparse
from itertools import product
import json
import math
from pathlib import Path

from audit_frm2_numeric import Native, SHA256, F, D, I, P, Q, B, f32, stats
from frm2_resonance_optimizer import refine


def verify(native):
    call = native.fn(0x70f10, Q, P, B, F)
    rows, errors = [], []
    for count, spacing, shift, offset in product((1, 3, 6), (200, 500), (0, 80, 160), (0, 6)):
        abc = tuple(map(f32, (-20, 60, -.0002)))
        primary = [(250. + i * spacing, 0., 240. + i * 20) for i in range(count)]
        secondary = [(200., 0., 200.)]
        points = [(i / 200, f32(-20 - i * .15 + 12 * math.sin((i * 110.25 - shift) / 200))) for i in range(201)]
        frame = native.frame([100, 200, 300, 400, 500], [-20, -21, -22, -23, -24], 200, abc, primary, secondary)
        P.from_address(frame + 0x108).value = native.env(points, mode=1, regular=0, bounds=(-20000, 20000, 0))
        Q.from_address(frame + 0x50).value |= 128 | 1024
        F.from_address(frame + 0xf8).value = offset
        expected_primary, expected_secondary, trace = refine(points, 200, abc, primary, secondary, offset=offset)
        if call(frame, 0, 22050):
            raise AssertionError('Native refinement failed')
        delta = []
        for at, expected in ((0x188, expected_primary), (0x1b8, expected_secondary)):
            pointer = P.from_address(frame + at).value
            for k, predicted in enumerate(expected):
                res = P.from_address(pointer + k * 8).value
                actual = tuple(D.from_address(res + field).value for field in (0x40, 0x48, 0x58))
                difference = [abs(a - b) for a, b in zip(actual, predicted)]
                if max(difference) >= 1e-4:
                    raise AssertionError(dict(case=(count, spacing, shift, offset), list=at, index=k, actual=actual, expected=predicted, trace=trace))
                delta.extend(difference)
        errors.extend(delta)
        rows.append(dict(count=count, spacing=spacing, shift=shift, offset=offset, trace=trace, error=max(delta)))
    return dict(cases=rows, parameters=stats(errors), scope='Independent full set-0 E7 refinement; positive Hz F0; curve and secondary present')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = dict(dll_sha256=SHA256, **verify(Native(args.dll)))
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(cases=len(report['cases']), parameters=report['parameters'], restores=sum(c['trace']['restored_pairs'] for c in report['cases']))))


if __name__ == '__main__':
    main()
