"""Verify mode-1 crossing rejection and recursive peak ownership on owned data."""
from __future__ import annotations

import argparse
import copy
from itertools import product
import json
from pathlib import Path
import random

from audit_frm2_numeric import Native, SHA256, F, I, P, Q, B
from frm2_peak_selection import reject_crossing, resolve_collision


def put(context, tracks, claimed, owners):
    for i, track in enumerate(tracks):
        base = context + 0x20 + i * 0x54
        F.from_address(base + 4).value = track['center']
        for name, offset in [('previous', 8), ('current', 12), ('rank', 16)]:
            I.from_address(base + offset).value = track[name]
        for name, offset, ty in [('candidates', 20, I), ('cost', 56, F), ('order', 68, I)]:
            for j, value in enumerate(track[name]):
                ty.from_address(base + offset + j * 4).value = value
    for offset, values in ((0x1ec50, claimed), (0x203c0, owners)):
        for i, value in enumerate(values):
            I.from_address(context + offset + i * 4).value = value


def check(context, tracks, claimed, owners):
    for i, track in enumerate(tracks):
        base = context + 0x20 + i * 0x54
        for name, offset, ty in [('center', 4, F), ('previous', 8, I), ('current', 12, I), ('rank', 16, I)]:
            assert ty.from_address(base + offset).value == track[name], (i, name, track)
    for offset, values in ((0x1ec50, claimed), (0x203c0, owners)):
        assert [I.from_address(context + offset + i * 4).value for i in range(len(values))] == values


def track(current, previous=-1):
    return dict(center=440., previous=previous, current=current, rank=0,
                candidates=[current, -1, -1], cost=[1., 2., 3.], order=[0, 1, 2])


def verify(native):
    context = native.alloc(0x21bf8)
    config = native.alloc(0x35f0)
    native.fn(0x90de0, P, P)(config)
    P.from_address(context).value = config
    B.from_address(context + 0x21b34).value = 1
    cross = native.fn(0xf18a0, Q, P, I, I)
    collide = native.fn(0xf16a0, Q, P, I, I)
    crossing_cases = rejected = 0
    for count in (3, 5, 8):
        for current, neighbor, previous, other in product(range(count), range(count), range(-1, count), range(-1, count)):
            tracks = [track(current, previous), track(neighbor, other)]
            claimed, owners = [0] * count, [0] * count
            claimed[neighbor], owners[neighbor] = 1, 1
            put(context, tracks, claimed, owners)
            reject_crossing(tracks, claimed, owners, 0)
            assert cross(context, 0, count) == 0
            check(context, tracks, claimed, owners)
            crossing_cases += 1
            rejected += tracks[0]['current'] < 0
    link = native.alloc(0x30)
    I.from_address(link + 0x18).value = 8
    P.from_address(context + 8).value = native.array(P, [link])
    rng = random.Random(572)
    rows = []
    for retry, repetition in product((False, True), range(400)):
        env = config + 0x5d0 + 10 * 0xa0
        for x in (0., 1.):
            native.add(env, x, int(retry))
        tracks = [track(2), track(2), track(4)]
        for i, item in enumerate(tracks):
            # Existing assignments at 2 and 4 are unique; the incoming track
            # competes at 2 and can recursively displace the owner at 4.
            choices = [item['current']] + rng.sample([j for j in range(8) if j != item['current']], 2)
            if repetition % 3 == 0 and i < 2:
                choices[1] = 4
                if choices[2] == 4:
                    choices[2] = 6
            if repetition % 7 == 0:
                choices[2] = -1
            item['candidates'] = choices
            item['cost'] = sorted(float(rng.randrange(1, 12)) for _ in range(3))
            item['previous'] = -1 if repetition % 2 else i
        claimed, owners = [0] * 8, [0] * 8
        claimed[2], owners[2], claimed[4], owners[4] = 1, 1, 1, 2
        put(context, tracks, claimed, owners)
        expected = copy.deepcopy(tracks)
        trace = resolve_collision(expected, claimed, owners, 0, 1, retry)
        assert collide(context, 0, 1) == 0
        check(context, expected, claimed, owners)
        rows.append(dict(retry=retry, collisions=len(trace), dropped=sum(t['current'] < 0 for t in expected)))
    return dict(crossing_cases=crossing_cases, crossing_rejections=rejected,
                collision_cases=len(rows), recursive_cases=sum(r['collisions'] > 1 for r in rows),
                max_collisions=max(r['collisions'] for r in rows), mismatches=0,
                scope='Mode 1 sorted peak indices; static retry configuration; owned finite tracks', cases=rows)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = dict(dll_sha256=SHA256, **verify(Native(args.dll)))
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in report.items() if k != 'cases'}))


if __name__ == '__main__':
    main()
