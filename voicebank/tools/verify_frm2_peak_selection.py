"""Compare owned peak candidate matrices against the hash-pinned DSE selector."""
from __future__ import annotations

import argparse
from itertools import product
import json
from pathlib import Path
import random

from audit_frm2_numeric import Native, SHA256, F, I, P, Q, B, f32, stats
from frm2_peak_selection import select


def verify(native):
    config = native.alloc(0x35f0)
    native.fn(0x90de0, P, P)(config)
    context = native.alloc(0x21bf8)
    P.from_address(context).value = config
    track = context + 0x20
    call = native.fn(0xf0ec0, Q, P, P, P, I, I)
    rng = random.Random(571)
    errors, cases = [], []
    for mode, score, alternate, index, history, shape in product(
            (1, 2), (0, 1, 2), (0, 1), (-1, 0, 3), (0, 220), range(8)):
        values = {2: 2000, 8: score, 0xd: 200, 0x21: .6, 0x22: .1,
                  0x27: 20, 0x28: 80, 0x29: .04, 0x33: alternate}
        center = 440.
        previous = -30. if shape % 2 else -3.4028234663852886e38
        peaks = [(center + rng.uniform(-250, 250), rng.uniform(-90, 5)) for _ in range(19)]
        if shape == 0:
            peaks = []
        elif shape == 1:
            peaks = [(440, -30)]
        elif shape == 2:
            peaks = [(400, -20), (420, -20), (460, -20), (480, -20)]
        elif shape == 3:
            peaks = [(20, -1000), (440, -1000), (2000, -1000)]
        elif shape == 4:
            center = 5
        elif shape == 5:
            center = 2200
        elif shape == 6:
            peaks = [(320, -10), (380, 0), (400, 5), (440, -30), (500, -40), (560, -20)]
        if mode == 1:
            peaks.sort()
        peaks = [(f32(x), f32(y)) for x, y in peaks]
        for key, value in values.items():
            env = config + 0x5d0 + key * 0xa0
            native.add(env, 0., value)
            native.add(env, 1., value)
        # Start with known sentinels: early returns leave ranking untouched.
        for j in range(21):
            I.from_address(track + j * 4).value = 0
        F.from_address(track).value = previous
        F.from_address(track + 4).value = center
        F.from_address(context + 0x21bd8).value = history
        B.from_address(context + 0x21b34).value = mode
        data = native.array(F, [v for x, y in peaks for v in (x, y, 0, 0, 0)] or [0] * 5)
        expected = select(peaks, center, previous, history, index, mode, values)
        status = call(context, track, data, len(peaks), index)
        actual = dict(status=status, current=I.from_address(track + 12).value,
                      rank=I.from_address(track + 16).value,
                      history=F.from_address(context + 0x21bd8).value)
        for name, offset, ty in [('candidates', 20, I), ('distance', 32, F),
                                 ('metric', 44, F), ('cost', 56, F), ('order', 68, I)]:
            actual[name] = [ty.from_address(track + offset + j * 4).value for j in range(3)]
        case = (mode, score, alternate, index, history, shape)
        for name in ('status', 'current', 'rank', 'candidates', 'order'):
            if actual[name] != expected[name]:
                raise AssertionError(dict(case=case, field=name, actual=actual, expected=expected))
        delta = [abs(actual['history'] - expected['history'])]
        for name in ('distance', 'metric', 'cost'):
            delta.extend(abs(a - b) for a, b in zip(actual[name], expected[name]))
        if max(delta) > 1e-5:
            raise AssertionError(dict(case=case, actual=actual, expected=expected))
        errors.extend(delta)
        cases.append(dict(case=case, selected=actual['current'], error=max(delta)))
    return dict(cases=cases, parameters=stats(errors),
                scope='Owned finite peak arrays; mode 1 sorted, mode 2 unsorted; static configuration; candidate selection only, no FFT peak detector')


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
