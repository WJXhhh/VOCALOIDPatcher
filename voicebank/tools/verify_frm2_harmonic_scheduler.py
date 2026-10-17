"""Whole mode-1 harmonic matching, history and output against native DSE."""
from __future__ import annotations

import argparse
import copy
from itertools import product
import json
from pathlib import Path
import random

from audit_frm2_numeric import Native, SHA256, F, I, P, Q, B, f32, stats
from frm2_peak_selection import advance_harmonics
from verify_frm2_peak_conflicts import put, check, track


def verify(native):
    config = native.alloc(0x35f0)
    native.fn(0x90de0, P, P)(config)
    context = native.alloc(0x21bf8)
    P.from_address(context).value = config
    B.from_address(context + 0x21b34).value = 1
    link = native.alloc(0x30)
    P.from_address(context + 8).value = native.array(P, [link])
    call = native.fn(0xf0690, Q, P)
    rng = random.Random(580)
    rows, errors = [], []
    for count, score, alternate, smoothing, neighbors, retry in product(
            (0, 1, 8), (0, 1, 2), (0, 1), (0, .7), (1, 5), (0, 1)):
        values = {2: 4000, 8: score, 10: retry, 0xd: 220, 0x12: smoothing,
                  0x13: neighbors, 0x21: .7, 0x22: .1, 0x27: 20,
                  0x28: 80, 0x29: .04, 0x33: alternate}
        for key, value in values.items():
            for x in (0., 1.):
                native.add(config + 0x5d0 + key * 0xa0, x, value)
        tracks = [track(i) for i in range(count)]
        for i, item in enumerate(tracks):
            item.update(center=float((i + 1) * 220), amplitude=-30.,
                        distance=[0.] * 3, metric=[0.] * 3)
        I.from_address(context + 0x1c).value = count
        F.from_address(context + 0x21bd8).value = 0
        history = 0.
        for step in range(8):
            # Both positive and negative peak values exercise the exact
            # reciprocal weighting branch without guessing the detector unit.
            peaks = sorted((f32(220 * (i + 1) + rng.uniform(-70, 70)),
                            f32(rng.uniform(1, 30) * (1 if step % 2 else -1)),
                            f32(rng.uniform(-3, 3))) for i in range(12))
            if step == 3:
                peaks = []
            if step == 5:
                peaks = [(f32(220 * (i + 1) + 10), 10., .5) for i in range(4)]
            f0 = 0. if step == 4 else 220.
            owners = [0] * len(peaks)
            put(context, tracks, [0] * len(peaks), owners)
            for i, item in enumerate(tracks):
                base = context + 0x20 + i * 0x54
                F.from_address(base).value = item['amplitude']
                for name, offset in (('distance', 32), ('metric', 44)):
                    for j, value in enumerate(item[name]):
                        F.from_address(base + offset + j * 4).value = value
            frame = native.alloc(4096)
            native.frame_ctor(frame)
            F.from_address(frame + 0xf0).value = f0
            P.from_address(link + 0x28).value = frame
            I.from_address(link + 0x18).value = len(peaks)
            P.from_address(link + 0x20).value = native.array(F, [v for p in peaks for v in (*p, 0, 0)] or [0] * 5)
            expected = copy.deepcopy(tracks)
            result = advance_harmonics(expected, peaks, f0, history, values, owners)
            assert call(context) == 0
            check(context, expected, result['claimed'], result['owners'])
            for i, item in enumerate(expected):
                base = context + 0x20 + i * 0x54
                for name, offset, ty in [('amplitude', 0, F), ('candidates', 20, I),
                                       ('distance', 32, F), ('metric', 44, F), ('cost', 56, F), ('order', 68, I)]:
                    want = item[name] if isinstance(item[name], list) else [item[name]]
                    got = [ty.from_address(base + offset + j * 4).value for j in range(len(want))]
                    delta = [abs(a - b) for a, b in zip(got, want)]
                    assert max(delta) <= 1e-5, (count, score, alternate, smoothing, neighbors, retry, step, i, name, got, want)
                    errors.extend(delta)
            assert F.from_address(context + 0x21bd8).value == result['history']
            if f0 > 0:
                assert I.from_address(frame + 0x58).value == count
                assert Q.from_address(frame + 0x50).value & 7 == 7
                for i, output in enumerate(result['output']):
                    if output is not None:
                        got = tuple(F.from_address(P.from_address(frame + offset).value + i * 4).value for offset in (0x60, 0x68, 0x70))
                        assert got == tuple(output), (got, output)
            rows.append(dict(count=count, score=score, alternate=alternate, smoothing=smoothing,
                             neighbors=neighbors, retry=retry, step=step, assigned=sum(t['current'] >= 0 for t in expected),
                             collisions=result['collisions']))
            tracks, history = expected, result['history']
    return dict(cases=len(rows), parameters=stats(errors), sequences=len(rows)//8,
                collisions=sum(r['collisions'] for r in rows), rows=rows,
                scope='Mode 1 supplied sorted peaks, static configuration, 8 consecutive calls per sequence; unmatched frame defaults excluded')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = dict(dll_sha256=SHA256, **verify(Native(args.dll)))
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in report.items() if k != 'rows'}))


if __name__ == '__main__':
    main()
