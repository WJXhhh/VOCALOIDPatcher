"""Probe donor ART candidates while preserving an owned physical phoneme list.

Uses synthetic metadata and a hash-pinned DSE in a disposable process. No voice
bank, PCM, editor process, or persistent native project is read or modified.
The token names are fixture labels, not a language-specific lyric conversion.
"""
import argparse
import ctypes as c
import itertools
import json
from pathlib import Path

from audit_frm2_numeric import Native, P, I, Q, F, B, SHA256
from verify_region_output_mapping import predict


def run(dll):
    n = Native(dll)
    build = n.fn(0x18d790, None, P, P)
    query = n.fn(0x1aa0a0, P, P, P, P, I, I, B, I)
    choose = n.fn(0x1acda0, B, P, I, I, I, P, P, I, B)
    controls, outer, context, config, registry, voice = (
        n.alloc(size) for size in [0x100, 16, 16, 0x100, 0x20, 0x300])
    P.from_address(outer).value = controls
    P.from_address(outer + 8).value = context
    P.from_address(context).value = config
    P.from_address(config + 0x10).value = registry
    I.from_address(registry + 8).value = 1
    P.from_address(registry + 0x10).value = n.array(P, [voice])
    P.from_address(registry + 0x18).value = n.array(P, [n.alloc(8)])
    score = n.alloc(24 * 0x2c)
    P.from_address(controls + 0x10).value = score
    I.from_address(controls + 0x18).value = 24
    table, classes = n.alloc(0x180), n.alloc(0x160)
    P.from_address(table + 0x158).value = classes
    P.from_address(voice + 0x170).value = table
    names = []
    for token in ['u', 'l', 'a', 'e']:
        name = n.alloc(0x40)
        raw = token.encode('ascii') + b'\0'
        c.memmove(name, raw, len(raw))
        names.append(name)
    I.from_address(table + 0x144).value = len(names)
    P.from_address(table + 0x148).value = n.array(P, names)
    index, sentinel = voice + 0x2e0, n.alloc(0xa8)
    P.from_address(index).value = sentinel
    for offset in [0, 8, 16]:
        P.from_address(sentinel + offset).value = sentinel
    B.from_address(sentinel + 0x18).value = 1
    B.from_address(sentinel + 0x19).value = 1
    parents = {}
    callback = c.CFUNCTYPE(P, P)(lambda node: parents.get(node, 0))
    n.keep.append(callback)
    vtable = n.alloc(0x68)
    P.from_address(vtable + 0x60).value = c.cast(callback, P).value
    alignment = n.array(I, [0, 5, 0, 5, 5, 12, 5, 12])
    collections = {}
    for pair in [('u', 'l'), ('e', 'l'), ('l', 'a')]:
        previous = 0
        for name in ('articulation',) + pair:
            node = n.alloc(0x180)
            P.from_address(node).value = vtable
            raw = name.encode('ascii') + b'\0'
            c.memmove(node + 0x18, raw, len(raw))
            parents[node] = previous
            previous = node
        parts = [n.alloc(0x280) for _ in range(3)]
        for part in parts:
            P.from_address(part).value = vtable
            parents[part] = node
            F.from_address(part + 0x16c).value = .6
            F.from_address(part + 0x170).value = 90.
            I.from_address(part + 0x1b0).value = 12
            P.from_address(part + 0x1d8).value = alignment
            P.from_address(part + 0x1e0).value = alignment + 32
        P.from_address(node + 0x148).value = n.array(P, parts)
        I.from_address(node + 0x150).value = len(parts)
        build(index, node)
        collections[pair] = parts

    def new_work(pairs):
        work = n.alloc(0x1d00)
        I.from_address(work + 4).value = -1
        I.from_address(work + 12).value = len(pairs)
        B.from_address(work + 0x1b15).value = 1
        P.from_address(work + 0x1c38).value = table
        for slot, pair in enumerate(pairs):
            for base, token in zip([0x10, 0x210], pair):
                raw = token.encode('ascii') + b'\0'
                c.memmove(work + base + 16 * slot, raw, len(raw))
        flags = n.alloc(2)
        query(outer, flags, work, 0, len(pairs), 0, 0)
        return work

    def index_snapshot():
        pending, seen = [P.from_address(sentinel + 8).value], set()
        while pending:
            entry = pending.pop()
            if entry == sentinel:
                continue
            assert entry and entry not in seen and len(seen) < 9
            seen.add(entry)
            pending.extend([P.from_address(entry).value,
                            P.from_address(entry + 16).value])
        assert len(seen) == 9
        return [(entry, c.string_at(entry, 0xa8)) for entry in sorted(seen)]

    original_index = index_snapshot()
    emit = n.fn(0x1a0450, B, P, P, P, P, I, I, I, I, P, I, I, B)
    ring, emit_outer = n.alloc(0x80), n.alloc(16)
    P.from_address(emit_outer).value = ring
    P.from_address(emit_outer + 8).value = context
    frames, items = n.alloc(256 * 0x18), n.alloc(16 * 0x140)
    P.from_address(ring).value = frames
    I.from_address(ring + 8).value = 256
    for offset in [0x20, 0x38, 0x50]:
        P.from_address(ring + offset).value = items
    for offset in [0x28, 0x2c, 0x40, 0x58]:
        I.from_address(ring + offset).value = 16
    for offset in [0x44, 0x5c]:
        I.from_address(ring + offset).value = 15
    coefficients = n.array(F, [1., 1., 0., 0., 0., 0.])
    rows = []
    for coordinates in [(-400., 0., 400.), (-50., 0., 50.)]:
        for parts in collections.values():
            for part, pitch in zip(parts, coordinates):
                F.from_address(part + 0x160).value = pitch
        for targets in itertools.product([-100., 0., 100.], repeat=2):
            for frame in range(24):
                F.from_address(score + frame * 0x2c).value = targets[frame // 12]
                F.from_address(score + frame * 0x2c + 4).value = .6
            baseline = None
            baseline_descriptor = n.alloc(0x68)
            for mode in ['baseline', 'donor', 'incoming_side', 'missing_fallback', 'baseline_after']:
                current = new_work([('u', 'l'), ('l', 'a')])
                physical = c.string_at(current + 0x10, 0x400)
                applied = False
                if mode in ['donor', 'incoming_side', 'missing_fallback']:
                    pair = ('missing', 'l') if mode == 'missing_fallback' else ('e', 'l')
                    donor = new_work([pair])
                    # The native lookup compacts missing edges. Check the count
                    # and range before copying; an empty donor keeps baseline.
                    if (I.from_address(donor + 12).value == 1 and
                            P.from_address(donor + 0x510).value !=
                            P.from_address(donor + 0x518).value):
                        c.memmove(current + 0x510, donor + 0x510, 24)
                        applied = True
                assert applied == (mode in ['donor', 'incoming_side'])
                assert choose(outer, 0, 24, 24, current, new_work([]), 0, 0)
                assert I.from_address(current + 12).value == 2
                assert c.string_at(current + 0x10, 0x400) == physical
                pair = ('e', 'l') if applied else ('u', 'l')
                winners, descriptors = [], []
                for slot, key in enumerate([pair, ('l', 'a')]):
                    descriptor = current + 0x810 + slot * 0x68
                    winners.append(collections[key].index(P.from_address(descriptor).value))
                    stage = I.from_address(descriptor + 8).value
                    limit = I.from_address(descriptor + 12).value
                    assert (stage, limit) == (0, 1)
                    descriptors.append([stage, limit])
                lengths = [I.from_address(current + 0x1510 + i * 4).value
                           for i in range(4)]
                result = (winners, descriptors, lengths)
                if baseline is None:
                    baseline = result
                    c.memmove(baseline_descriptor, current + 0x810, 0x68)
                assert result == baseline, (mode, targets, result, baseline)
                for offset in [0x34, 0x48, 0x60]:
                    I.from_address(ring + offset).value = 0
                c.memset(frames, 0, 256 * 0x18)
                emitted = []
                start = 0
                plan = [(current + 0x810, 0, 1, lengths[0], lengths[1], pair),
                        (current + 0x878, 1, 2, lengths[2], lengths[3], ('l', 'a'))]
                if mode == 'incoming_side':
                    plan[:1] = [(baseline_descriptor, 0, 1, lengths[0], 0, ('u', 'l')),
                                (current + 0x810, 0, 1, 0, lengths[1], ('e', 'l'))]
                for slot, (descriptor, left_id, right_id, left, right, source_pair) in enumerate(plan):
                    assert left + right > 0
                    assert emit(emit_outer, descriptor, names[left_id], names[right_id],
                                left, right, 0, 0, coefficients, 17, 23, 0)
                    item = items + slot * 0x140
                    assert I.from_address(ring + 0x34).value == slot + 1
                    assert P.from_address(item + 0x20).value == P.from_address(descriptor).value
                    assert [I.from_address(item + offset).value for offset in [0x10, 0x14]] == [left_id, right_id]
                    assert [I.from_address(item + offset).value for offset in [0, 4, 8]] == [start, start + left + right, left]
                    expected = (predict((0, 5, 0, 5), left, 0, False) +
                                predict((5, 12, 5, 12), right, 1, False))
                    actual = [F.from_address(frames + t * 0x18 + 8).value
                              for t in range(start, start + left + right)]
                    assert all(P.from_address(frames + t * 0x18).value == item
                               for t in range(start, start + left + right))
                    assert len(actual) == len(expected)
                    assert max(abs(a - b) for a, b in zip(actual, expected)) < 1e-5
                    emitted.append(dict(physical_ids=[left_id, right_id],
                                        source_pair=source_pair,
                                        begin=start, end=start + left + right,
                                        left_length=left, positions=actual))
                    start += left + right
                assert c.string_at(current + 0x10, 0x400) == physical
                assert index_snapshot() == original_index
                rows.append(dict(coordinates=coordinates, targets=targets, mode=mode,
                                 donor_applied=applied, selected_pairs=[pair, ('l', 'a')],
                                 selected_layers=winners, stage_limits=descriptors,
                                 allocated_lengths=lengths, emitted=emitted,
                                 physical_tokens_unchanged=True,
                                 shared_index_unchanged=True))
    return dict(dll_sha256=SHA256, cases=len(rows),
                donor_cases=sum(row['donor_applied'] for row in rows),
                incoming_side_cases=sum(row['mode'] == 'incoming_side' for row in rows),
                fallback_cases=sum(row['mode'] == 'missing_fallback' for row in rows),
                mismatches=0, rows=rows,
                scope='Synthetic ordinary two-region metadata; native index builder, '
                      'lookup, joint selector and frame work-item emission. Donor '
                      'candidate range replaces only the first physical edge. '
                      'Emission explicitly receives original physical names; source '
                      'positions and work-item IDs are checked. Incoming-side mode '
                      'emits the original left region and donor right region as '
                      'separate contiguous work items. No PCM, complete '
                      'event commit, vocoder output, editor scheduling or perceptual '
                      'equivalence is tested.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: report[key] for key in
                      ['cases', 'donor_cases', 'incoming_side_cases', 'fallback_cases', 'mismatches']}))
