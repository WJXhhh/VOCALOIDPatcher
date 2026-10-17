"""Measure native joint-selection costs after native region-length allocation."""
import argparse
import ctypes as c
import itertools
import json
from pathlib import Path
from audit_frm2_numeric import Native, P, I, Q, F, B, SHA256, f32


def run(dll):
    n = Native(dll)
    cost = n.fn(0x19e730, P, P, P, I, I, P, P, P, I, I, B, B, I)
    controls, outer, context, config, registry, voice = (n.alloc(s) for s in
        [0x100, 16, 16, 0x100, 0x20, 0x300])
    P.from_address(outer).value = controls
    P.from_address(outer + 8).value = context
    P.from_address(context).value = config
    P.from_address(config + 0x10).value = registry
    I.from_address(registry + 8).value = 1
    P.from_address(registry + 0x10).value = n.array(P, [voice])
    P.from_address(registry + 0x18).value = n.array(P, [n.alloc(8)])
    score = n.alloc(24 * 0x2c)
    P.from_address(controls + 0x10).value = score
    I.from_address(controls + 0x18).value = 12
    work, part, table, classes = (n.alloc(s) for s in [0x1d00, 0x280, 0x180, 0x160])
    P.from_address(table + 0x158).value = classes
    P.from_address(work + 0x1c38).value = table
    I.from_address(work + 4).value = -1
    I.from_address(work + 12).value = 1
    B.from_address(work + 0x1b15).value = 1
    P.from_address(work + 0x810).value = part
    I.from_address(work + 0x81c).value = 1
    align = n.array(I, [0, 5, 0, 5, 5, 12, 5, 12])
    P.from_address(part + 0x1d8).value = align
    P.from_address(part + 0x1e0).value = align + 32
    I.from_address(part + 0x1b0).value = 12
    result = n.alloc(8)
    rows = []
    for pitch, dynamic, targets, target_dynamics in itertools.product(
            [-400., 0., 400.], [.2, .6, 1.],
            [(-200., -200.), (200., 200.), (-200., 200.), (400., -400.), (0., 0.)],
            [(.6, .6), (.2, 1.), (1., .2)]):
        F.from_address(part + 0x160).value = pitch
        F.from_address(part + 0x16c).value = dynamic
        for frame in range(12):
            side = int(frame >= 5)
            F.from_address(score + frame*0x2c).value = targets[side]
            F.from_address(score + frame*0x2c + 4).value = target_dynamics[side]
        cost(outer, result, 0, 12, work, 0, 0, 0, 2, 0, 0, 0)
        lengths = [I.from_address(work + 0x1510 + i*4).value for i in range(2)]
        assert lengths == [5, 7]
        pitch_sum = dynamic_sum = 0.
        for length, target_pitch, target_dynamic in zip(lengths, targets, target_dynamics):
            total = 0.
            for _ in range(length):
                total = f32(total + f32(target_dynamic))
            mean_dynamic = f32(total / length)
            difference = f32(target_pitch - pitch)
            pitch_distance = -difference if difference < 0 else f32(difference * f32(.85))
            pitch_sum = f32(pitch_sum + f32(pitch_distance * length))
            dynamic_sum = f32(dynamic_sum + f32(abs(f32(mean_dynamic - f32(dynamic))) * length))
        expected = f32(f32(f32(pitch_sum / 12) * f32(.0025)) +
                       f32(f32(dynamic_sum / 12) * f32(1/6)))
        actual, duration = (F.from_address(result + i*4).value for i in range(2))
        assert abs(actual - expected) < 2e-6, (pitch, dynamic, targets, actual, expected)
        assert duration == 0.
        rows.append(dict(pitch=pitch, dynamics=dynamic, targets=targets,
                         target_dynamics=target_dynamics, cost=actual,
                         expected=expected, duration_cost=duration))
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
    previous = 0
    for name in ['articulation', 'a', 'b']:
        node = n.alloc(0x180)
        P.from_address(node).value = vtable
        c.memmove(node + 0x18, name.encode() + b'\0', len(name) + 1)
        parents[node] = previous
        previous = node
    parts = []
    for pitch in [-400., 0., 400.]:
        candidate = n.alloc(0x280)
        F.from_address(candidate + 0x160).value = pitch
        F.from_address(candidate + 0x16c).value = .6
        F.from_address(candidate + 0x170).value = 90.
        P.from_address(candidate + 0x1d8).value = align
        P.from_address(candidate + 0x1e0).value = align + 32
        I.from_address(candidate + 0x1b0).value = 12
        parts.append(candidate)
    P.from_address(node + 0x148).value = n.array(P, parts)
    I.from_address(node + 0x150).value = 3
    n.fn(0x18d790, None, P, P)(index, node)
    query = n.fn(0x1aa0a0, P, P, P, P, I, I, B, I)
    choose = n.fn(0x1acda0, B, P, I, I, I, P, P, I, B)
    winners = []
    scenarios = [([-400., 0., 400.], value) for value in [-600., -220., -200., -180., 0., 180., 200., 220., 600.]]
    scenarios += [([-50., 0., 50.], value) for value in [-100., -50., -26., -25., -24., -22., 0., 22., 24., 25., 26., 50., 100.]]
    for coordinates, target in scenarios:
        for candidate, pitch in zip(parts, coordinates):
            F.from_address(candidate + 0x160).value = pitch
        for frame in range(12):
            F.from_address(score + frame*0x2c).value = target
            F.from_address(score + frame*0x2c + 4).value = .6
        current, following, flags = n.alloc(0x1d00), n.alloc(0x1d00), n.alloc(2)
        for obj in [current, following]:
            I.from_address(obj + 4).value = -1
            B.from_address(obj + 0x1b15).value = 1
            P.from_address(obj + 0x1c38).value = table
        I.from_address(current + 12).value = 1
        c.memmove(current + 0x10, b'a\0', 2)
        c.memmove(current + 0x210, b'b\0', 2)
        query(outer, flags, current, 0, 1, 0, 0)
        ok = choose(outer, 0, 12, 12, current, following, 0, 0)
        assert ok, target
        selected = parts.index(P.from_address(current + 0x810).value)
        nearest = min(range(3), key=lambda i:(abs(coordinates[i]-target), coordinates[i]))
        eligible = [i for i in range(3) if abs(coordinates[i]-coordinates[nearest]) <= 75.]
        def expected_cost(i):
            difference = target - coordinates[i]
            return difference * .85 if difference >= 0 else -difference
        expected = min(eligible, key=expected_cost)
        assert selected == expected, (coordinates, target, selected, expected)
        assert I.from_address(current + 0x818).value == 0
        assert I.from_address(current + 0x81c).value == 1
        winners.append(dict(coordinates=coordinates, target=target, selected=selected,
                            expected=expected, eligible=eligible, nearest=nearest,
                            stage=I.from_address(current + 0x818).value,
                            limit=I.from_address(current + 0x81c).value))
    multiple = []
    I.from_address(controls + 0x18).value = 24
    four_align = n.array(I, [0, 5, 0, 5, 5, 12, 5, 12, 12, 17, 12, 17, 17, 24, 17, 24])
    for candidate in parts:
        P.from_address(candidate + 0x1d8).value = four_align
        P.from_address(candidate + 0x1e0).value = four_align + 64
        I.from_address(candidate + 0x1b0).value = 24
    scenarios = [('different', a, b, da, db) for a, b, da, db in itertools.product(
        [-50., 0., 50.], [-50., 0., 50.], [.2, .6, 1.], [.2, .6, 1.])]
    scenarios += [(kind, 0., 0., .6, .6) for kind in ['consecutive', 'repeated', 'reversed']]
    for kind, pitch0, pitch1, dyn0, dyn1 in scenarios:
        pair = [parts[0], parts[1]] if kind == 'different' else [parts[0], parts[0]]
        stages = [0, 1] if kind == 'consecutive' else [1, 0] if kind == 'reversed' else [0, 0]
        multi = n.alloc(0x1d00)
        I.from_address(multi + 4).value = -1
        I.from_address(multi + 12).value = 2
        B.from_address(multi + 0x1b15).value = 1
        P.from_address(multi + 0x1c38).value = table
        for slot, (candidate, pitch, dynamic, stage) in enumerate(zip(pair, [pitch0, pitch1], [dyn0, dyn1], stages)):
            F.from_address(candidate + 0x160).value = pitch
            F.from_address(candidate + 0x16c).value = dynamic
            descriptor = multi + 0x810 + slot*0x68
            P.from_address(descriptor).value = candidate
            I.from_address(descriptor + 8).value = stage
            I.from_address(descriptor + 12).value = 2 if kind != 'different' else 1
            for frame in range(slot*12, (slot+1)*12):
                F.from_address(score + frame*0x2c).value = pitch
                F.from_address(score + frame*0x2c + 4).value = dynamic
        cost(outer, result, 0, 24, multi, 0, 0, 0, 4, 0, 0, 0)
        actual = F.from_address(result).value
        duration = F.from_address(result + 4).value
        lengths = [I.from_address(multi + 0x1510 + 4*i).value for i in range(4)]
        expected = abs(pitch0-pitch1)*.0025/6 + abs(f32(dyn0)-f32(dyn1))/36
        if kind != 'consecutive':
            expected += 20.
        assert lengths == [5, 7, 5, 7] and duration == 0.
        assert abs(actual-expected) < 4e-6, (kind, pitch0, pitch1, dyn0, dyn1, actual, expected)
        multiple.append(dict(kind=kind, pitches=[pitch0,pitch1], dynamics=[dyn0,dyn1],
                             stages=stages, cost=actual, expected=expected, lengths=lengths))
    coordinates = [-50., 0., 50.]
    for candidate, pitch in zip(parts, coordinates):
        F.from_address(candidate + 0x160).value = pitch
        F.from_address(candidate + 0x16c).value = .6
        P.from_address(candidate + 0x1d8).value = align
        P.from_address(candidate + 0x1e0).value = align + 32
        I.from_address(candidate + 0x1b0).value = 12
    joint_winners = []
    second_parts = []
    for pitch in coordinates:
        candidate = n.alloc(0x280)
        F.from_address(candidate + 0x160).value = pitch
        F.from_address(candidate + 0x16c).value = .6
        F.from_address(candidate + 0x170).value = 90.
        P.from_address(candidate + 0x1d8).value = align
        P.from_address(candidate + 0x1e0).value = align + 32
        I.from_address(candidate + 0x1b0).value = 12
        second_parts.append(candidate)
    previous = 0
    for name in ['articulation', 'b', 'c']:
        node = n.alloc(0x180)
        P.from_address(node).value = vtable
        c.memmove(node + 0x18, name.encode() + b'\0', len(name) + 1)
        parents[node] = previous
        previous = node
    P.from_address(node + 0x148).value = n.array(P, second_parts)
    I.from_address(node + 0x150).value = 3
    n.fn(0x18d790, None, P, P)(index, node)
    for targets in itertools.product([-100., -24., 0., 26., 100.], repeat=2):
        for frame in range(24):
            F.from_address(score + frame*0x2c).value = targets[frame//12]
            F.from_address(score + frame*0x2c + 4).value = .6
        current, following, flags = n.alloc(0x1d00), n.alloc(0x1d00), n.alloc(2)
        for obj in [current, following]:
            I.from_address(obj + 4).value = -1
            B.from_address(obj + 0x1b15).value = 1
            P.from_address(obj + 0x1c38).value = table
        I.from_address(current + 12).value = 2
        for slot, (left, right) in enumerate([(b'a\0', b'b\0'), (b'b\0', b'c\0')]):
            c.memmove(current + 0x10 + slot*16, left, 2)
            c.memmove(current + 0x210 + slot*16, right, 2)
        query(outer, flags, current, 0, 2, 0, 0)
        assert choose(outer, 0, 24, 24, current, following, 0, 0)
        actual = tuple(collection.index(P.from_address(current + 0x810 + slot*0x68).value)
                       for slot, collection in enumerate([parts, second_parts]))
        lo = min(range(3), key=lambda i:(abs(coordinates[i]-min(targets)), coordinates[i]))
        hi = min(range(3), key=lambda i:(abs(coordinates[i]-max(targets)), coordinates[i]))
        eligible = [i for i in range(3) if coordinates[lo]-75 <= coordinates[i] <= coordinates[hi]+75]
        def pair_cost(indices):
            total = 0.
            for target, index in zip(targets, indices):
                difference = target-coordinates[index]
                total += (difference*.85 if difference >= 0 else -difference)*.0025/2
            return total + abs(coordinates[indices[0]]-coordinates[indices[1]])*.0025/6 + 20.
        alternatives = list(itertools.product(eligible, repeat=2))
        best_cost = min(map(pair_cost, alternatives))
        # Check the minimum itself rather than assigning an unverified tie order.
        assert abs(pair_cost(actual)-best_cost) < 2e-6, (targets, actual, best_cost)
        assert all(I.from_address(current + 0x818 + slot*0x68).value == 0 for slot in range(2))
        joint_winners.append(dict(targets=targets, selected=actual, eligible=eligible,
                                  expected_minimum=best_cost, selected_model_cost=pair_cost(actual)))
    triple = n.alloc(0x280)
    F.from_address(triple + 0x16c).value = .6
    F.from_address(triple + 0x170).value = 90.
    P.from_address(triple + 0x1d8).value = four_align
    P.from_address(triple + 0x1e0).value = four_align + 64
    I.from_address(triple + 0x1b0).value = 24
    previous = 0
    for name in ['articulation', 'a', '^b', 'c']:
        node = n.alloc(0x180)
        P.from_address(node).value = vtable
        c.memmove(node + 0x18, name.encode() + b'\0', len(name) + 1)
        parents[node] = previous
        previous = node
    P.from_address(node + 0x148).value = n.array(P, [triple])
    I.from_address(node + 0x150).value = 1
    n.fn(0x18d790, None, P, P)(index, node)
    mixed = []
    for layout, triple_pitch, targets in itertools.product(['same_list', 'split_lists'], [-100., 0., 100.],
            itertools.product([-100., -24., 0., 26., 100.], repeat=2)):
        F.from_address(triple + 0x160).value = triple_pitch
        for frame in range(24):
            F.from_address(score + frame*0x2c).value = targets[frame//12]
            F.from_address(score + frame*0x2c + 4).value = .6
        current, following, flags = n.alloc(0x1d00), n.alloc(0x1d00), n.alloc(2)
        for obj in [current, following]:
            I.from_address(obj + 4).value = -1
            P.from_address(obj + 0x1c38).value = table
        B.from_address(following + 0x1b15).value = 1
        I.from_address(current + 12).value = 2 if layout == 'same_list' else 1
        I.from_address(following + 12).value = int(layout == 'split_lists')
        for slot, (left, right) in enumerate([(b'a\0', b'b\0'), (b'b\0', b'c\0')]):
            obj, position = (following, 0) if layout == 'split_lists' and slot == 1 else (current, slot)
            c.memmove(obj + 0x10 + position*16, left, 2)
            c.memmove(obj + 0x210 + position*16, right, 2)
        query(outer, flags, current, 0, I.from_address(current + 12).value, 0, 0)
        if layout == 'split_lists':
            query(outer, flags, following, 0, 1, 0, 0)
        assert choose(outer, 0, 12 if layout == 'split_lists' else 24, 24, current, following, 0, 0)
        selected = []
        for slot, collection in enumerate([parts, second_parts]):
            obj, position = (following, 0) if layout == 'split_lists' and slot == 1 else (current, slot)
            descriptor = obj + 0x810 + position*0x68
            pointer = P.from_address(descriptor).value
            stage, limit = I.from_address(descriptor+8).value, I.from_address(descriptor+12).value
            if pointer == triple:
                assert stage == slot and limit == 2
                selected.append(3)
            else:
                assert stage == 0 and limit == 1
                selected.append(collection.index(pointer))
        values = coordinates + [triple_pitch]
        lo = min(values, key=lambda v:(abs(v-min(targets)),v))
        hi = min(values, key=lambda v:(abs(v-max(targets)),v))
        eligible = [i for i,v in enumerate(values) if lo-75 <= v <= hi+75]
        def mixed_cost(indices):
            total = 0.
            for target, i in zip(targets, indices):
                difference = target-values[i]
                total += (difference*.85 if difference >= 0 else -difference)*.0025/2
            if layout == 'same_list':
                total += abs(values[indices[0]]-values[indices[1]])*.0025/6
                total += 0. if tuple(indices)==(3,3) else 20.
            return total
        expected = min(mixed_cost(pair) for pair in itertools.product(eligible, repeat=2))
        assert abs(mixed_cost(selected)-expected) < 2e-6, (triple_pitch,targets,selected,expected)
        if layout == 'same_list':
            assert (selected[0]==3)==(selected[1]==3)
        mixed.append(dict(layout=layout,triple_pitch=triple_pitch,targets=targets,selected=selected,
                          eligible=eligible,expected_minimum=expected))
    return dict(dll_sha256=SHA256, cases=len(rows), winner_cases=len(winners), winners=winners,
                multi_cost_cases=len(multiple), multiple=multiple,
                joint_winner_cases=len(joint_winners), joint_winners=joint_winners,
                mixed_stage_cases=len(mixed), mixed=mixed,
                mismatches=0, rows=rows,
                scope='Full native cost evaluator and length allocation for one ordinary part, '
                      'two nonempty regions, requested duration equals inner/outer duration; '
                      'native joint selector tested with one nonempty list and empty following list; '
                      'two-work-item continuity costs including same-part stage succession; '
                      'two ordinary work-item final winners checked against exhaustive independent costs; '
                      'ordinary and two-stage material competition verified; '
                      'same-list and split-list ordinary/two-stage competition; '
                      'not temporal stretching or host sequence construction')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dll', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    a = p.parse_args()
    r = run(a.dll)
    a.output.write_text(json.dumps(r, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k:r[k] for k in ['cases', 'winner_cases', 'multi_cost_cases', 'joint_winner_cases', 'mixed_stage_cases', 'mismatches']}))
