#!/usr/bin/env python3
"""Independent full set-0 temporal model for DRS RVA 0xc1610.

Verifies A/B/C and both resonance lists on owned sequences. The final regenerated
residual ENV is excluded here: its distinct contract is tested separately.
Native allocations last until this bounded process exits.
"""
from __future__ import annotations

import argparse
import copy
from itertools import product
import json
import math
from pathlib import Path

from audit_frm2_numeric import Native, SHA256, F, D, I, P, Q, B, f32, stats
from audit_frm2_temporal import smooth_curve


def curve(abc, center):
    return f32(f32(f32(math.exp(f32(f32(center) * abc[2]))) - 1) * abc[1])


def smooth_lists(rows, field, passes=2, endpoint_mode=0):
    """In-place, minimum shared list size; preserve the native summation order."""
    n = len(rows)

    def update(target, indices):
        for k in range(min(len(rows[i]) for i in indices)):
            rows[target][k][field] = sum(rows[i][k][field] / len(indices) for i in indices)

    for _ in range(passes):
        if field == 0 and endpoint_mode and n > 3:
            update(0, [1, 0, 2, 3])
            update(n - 1, [n - 2, n - 1, n - 3, n - 4])
        if field == 2:
            if n > 3:
                update(1, [0, 1, 2, 3])
        elif n > 2:
            update(1, [0, 1, 2])
        if n > 3:
            indices = [n - 1, n - 2, n - 3] + ([n - 4] if field == 2 else [])
            update(n - 2, indices)
        for i in range(2, n - 2):
            order = [i + 1, i - 2, i - 1, i, i + 2] if field == 0 else [i - 1, i - 2, i, i + 1, i + 2]
            update(i, order)


def model(source, endpoint_mode):
    rows = copy.deepcopy(source)
    minimum_width = min([1000.] + [f32(row['primary'][0][2]) for row in rows if row['primary']])
    for row in rows:
        for res in row['primary'] + row['secondary']:
            res[1] = row['abc'][0] + res[1] + curve(row['abc'], res[0])
    smoothed = [smooth_curve([row['abc'][axis] for row in rows]) for axis in range(3)]
    for i, row in enumerate(rows):
        row['abc'] = [values[i] for values in smoothed]
    secondary = [row['secondary'] for row in rows]
    primary = [row['primary'] for row in rows]
    for field in (1, 0, 2):
        smooth_lists(secondary, field)
    smooth_lists(primary, 1)
    smooth_lists(primary, 0, endpoint_mode=endpoint_mode)
    if endpoint_mode:
        for items in primary:
            items[0][2] = f32(items[0][2] * .2 + minimum_width * .8)
    smooth_lists(primary, 2, passes=1)
    for row in rows:
        for res in row['primary'] + row['secondary']:
            res[1] = (res[1] - row['abc'][0]) - curve(row['abc'], res[0])
    return rows


def read(frame):
    row = dict(abc=[F.from_address(frame + offset).value for offset in (0x198, 0x1a0, 0x1a8)])
    for key, count_at, ptr_at in (('primary', 0x180, 0x188), ('secondary', 0x1b0, 0x1b8)):
        row[key] = []
        ptr = P.from_address(frame + ptr_at).value
        for i in range(I.from_address(frame + count_at).value):
            res = P.from_address(ptr + i * 8).value
            row[key].append([D.from_address(res + offset).value for offset in (0x40, 0x48, 0x58)])
    return row


def verify(native):
    errors = {key: [] for key in ('abc', 'primary_center', 'primary_amplitude', 'primary_bandwidth',
                                 'secondary_center', 'secondary_amplitude', 'secondary_bandwidth')}
    cases = 0
    smoother = native.fn(0xc1610, Q, P, B, F, I)
    for count, primaries, secondaries, endpoint_mode in product((1, 2, 3, 4, 5, 9, 25), (1, 3, 22), (1, 2), (0, 1)):
        frames = []
        for i in range(count):
            abc = [f32(v) for v in (-30 + math.sin(i) * 5, 60 + i * i / 100, -.0001 - i * .000005)]
            first = [[min(21000., 350 + k * 900 + math.sin(i + k) * 100),
                      math.cos(i * .31 + k) * 9, 150 + 35 * (i % 6) + k * 10]
                     for k in range(primaries)]
            second = [[200 + 50 * (i % 2) + k * 100, 6 * math.cos(i), 200 + 150 * (i % 2) + k * 25]
                      for k in range(secondaries)]
            frames.append(native.frame([200, 400, 600, 800, 1000], [-20, -25, -30, -35, -40],
                                       200, abc, first, second))
        before = [read(frame) for frame in frames]
        expected = model(before, endpoint_mode)
        region = native.alloc(0x80)
        I.from_address(region + 0x18).value = count
        P.from_address(region + 0x20).value = native.array(P, frames)
        if smoother(region, 0, 22050, endpoint_mode):
            raise AssertionError('Temporal processor failed')
        for frame, predicted in zip(frames, expected):
            actual = read(frame)
            errors['abc'].extend(abs(a - b) for a, b in zip(actual['abc'], predicted['abc']))
            for key in ('primary', 'secondary'):
                for a, b in zip(actual[key], predicted[key]):
                    for axis, field in enumerate(('center', 'amplitude', 'bandwidth')):
                        error = abs(a[axis] - b[axis])
                        if error >= 1e-4:
                            raise AssertionError(f'{count}/{primaries}/{secondaries}/{endpoint_mode} {key} {field}: {a} != {b}')
                        errors[f'{key}_{field}'].append(error)
        cases += 1
    summary = {key: stats(values) for key, values in errors.items()}
    if any(value['max'] >= 1e-4 for value in summary.values()):
        raise AssertionError(summary)
    return dict(cases=cases, errors=summary,
                scope='Set 0, finite amplitudes, fixed list counts within each sequence, endpoint mode 0/1',
                exclusions=['Unequal list lengths', 'Missing model flags', 'Nonfinite inputs', 'Residual ENV regeneration'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = dict(dll_sha256=SHA256, **verify(Native(args.dll)))
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
