"""Native indexed lookup/compaction and pitch-dynamics candidate-set verification."""
import argparse
import ctypes as c
import itertools
import json
from pathlib import Path
from audit_frm2_numeric import Native, P, I, Q, B, F, SHA256, f32


def run(dll):
    n = Native(dll)
    build = n.fn(0x18d790, None, P, P)
    query = n.fn(0x1aa0a0, P, P, P, P, I, I, B, I)
    select = n.fn(0x1ab350, B, P, P, I, P, P, I, F, F, F, F, F, F, B, B)
    parents = {}
    callback = c.CFUNCTYPE(P, P)(lambda node: parents.get(node, 0))
    n.keep.append(callback)
    vtable = n.alloc(0x68)
    P.from_address(vtable + 0x60).value = c.cast(callback, P).value
    voice, registry, config, context, outer = (n.alloc(s) for s in [0x300, 0x20, 0x100, 16, 16])
    P.from_address(outer + 8).value = context
    P.from_address(context).value = config
    P.from_address(config + 0x10).value = registry
    I.from_address(registry + 8).value = 1
    P.from_address(registry + 0x10).value = n.array(P, [voice])
    P.from_address(registry + 0x18).value = n.array(P, [n.alloc(8)])
    index, sentinel = voice + 0x2e0, n.alloc(0xa8)
    P.from_address(index).value = sentinel
    for offset in [0, 8, 16]:
        P.from_address(sentinel + offset).value = sentinel
    B.from_address(sentinel + 0x18).value = 1
    B.from_address(sentinel + 0x19).value = 1
    pitches = [-400., 0., 400.]
    dynamics = list(map(f32, [.2, .6, 1.]))
    parts = []
    for pitch, dynamic in zip(pitches, dynamics):
        part = n.alloc(0x280)
        F.from_address(part + 0x160).value = pitch
        F.from_address(part + 0x16c).value = dynamic
        parts.append(part)
    previous = 0
    for name in ['articulation', 'a', 'b']:
        node = n.alloc(0x180)
        P.from_address(node).value = vtable
        c.memmove(node + 0x18, name.encode() + b'\0', len(name) + 1)
        parents[node] = previous
        previous = node
    P.from_address(node + 0x148).value = n.array(P, parts)
    I.from_address(node + 0x150).value = len(parts)
    build(index, node)

    def lookup(keys):
        work, flags = n.alloc(0x1d00), n.alloc(2)
        I.from_address(work + 12).value = len(keys)
        for i, (left, right) in enumerate(keys):
            for base, token in [(0x10, left), (0x210, right)]:
                raw = token.encode() + b'\0'
                c.memmove(work + base + 16*i, raw, len(raw))
        query(outer, flags, work, 0, len(keys), 0, 0)
        return work, list((B * 2).from_address(flags))

    compact = []
    for length in range(1, 5):
        for present in itertools.product([False, True], repeat=length):
            keys = [('a', 'b') if yes else ('x', 'y') for yes in present]
            work, flags = lookup(keys)
            count = I.from_address(work + 12).value
            assert count == sum(present), (present, count)
            for i in range(count):
                assert c.string_at(work + 0x10 + 16*i) == b'a'
                assert c.string_at(work + 0x210 + 16*i) == b'b'
                assert P.from_address(work + 0x510 + 24*i).value != P.from_address(work + 0x518 + 24*i).value
            for i in range(count, length):
                assert c.string_at(work + 0x10 + 16*i) == b''
                assert c.string_at(work + 0x210 + 16*i) == b''
            compact.append(dict(present=present, remaining=count, boundary_flags=flags))

    def subset(values, low, high, width):
        # Float32 distances; a tie retains the lower coordinate.
        near = lambda x: min(range(len(values)), key=lambda i: (abs(f32(values[i]-f32(x))), values[i]))
        lo, hi = values[near(low)], values[near(high)]
        return {i for i, value in enumerate(values)
                if f32(lo-f32(width)) <= value <= f32(hi+f32(width))}

    work, _ = lookup([('a', 'b')])
    output, counts = n.alloc(0x3400), n.alloc(128)
    rows = []
    pitch_bounds = [(v, v) for v in [-600., -200., 0., 200., 600.]] + [(-400., 400.), (-200., 200.), (-600., 0.), (0., 600.)]
    dynamic_bounds = [(v, v) for v in [.1, .4, .6, .8, 1.1]] + [(.2, 1.), (.4, .8), (.1, .6), (.6, 1.1)]
    for pitch, dynamic, pitch_width, dynamic_width in itertools.product(
            pitch_bounds, dynamic_bounds, [0., 400.], [0., .4]):
        pset = subset(pitches, *pitch, pitch_width)
        dset = subset(dynamics, *dynamic, dynamic_width)
        expected = pset & dset or pset | dset
        ok = select(work, 0, 0, output, counts, 0, *pitch, *dynamic,
                    pitch_width, dynamic_width, 0, 0)
        assert ok
        count = I.from_address(counts).value
        assert 0 < count <= len(parts)
        actual = [parts.index(P.from_address(output + i*0x68).value) for i in range(count)]
        assert set(actual) == expected and len(actual) == len(expected), (pitch, dynamic, actual, expected)
        assert all(I.from_address(output + i*0x68 + 8).value == 0 and
                   I.from_address(output + i*0x68 + 12).value == 1 for i in range(count))
        rows.append(dict(pitch=pitch, dynamics=dynamic, pitch_width=pitch_width,
                         dynamics_width=dynamic_width, selected=actual,
                         used_union=not bool(pset & dset)))
    triple_part = n.alloc(0x280)
    F.from_address(triple_part + 0x160).value = 0.
    F.from_address(triple_part + 0x16c).value = .6
    previous = 0
    for name in ['articulation', 'a', '^b', 'c']:
        node = n.alloc(0x180)
        P.from_address(node).value = vtable
        c.memmove(node + 0x18, name.encode() + b'\0', len(name) + 1)
        parents[node] = previous
        previous = node
    P.from_address(node + 0x148).value = n.array(P, [triple_part])
    I.from_address(node + 0x150).value = 1
    build(index, node)
    stages = []
    for pattern, slot in [('full_start', 0), ('full_end', 1), ('only_start', 0),
                          ('only_end', 0), ('previous_end', 0)]:
        for join, first_only, last_only in itertools.product([0, 1], repeat=3):
            keys = [('a', 'b'), ('b', 'c')] if pattern.startswith('full') else (
                [('a', 'b')] if pattern == 'only_start' else [('b', 'c')])
            stage_work, _ = lookup(keys)
            prior = lookup([('a', 'b')])[0] if pattern == 'previous_end' else 0
            B.from_address(stage_work + 0x1b15).value = join
            destination, stage_counts = n.alloc(0x6800), n.alloc(128)
            ok = select(stage_work, prior, slot, destination, stage_counts, 0,
                        0., 0., .6, .6, 10000., 10., first_only, last_only)
            if pattern == 'full_start':
                expected_triple = not join and not last_only
            elif pattern == 'full_end':
                expected_triple = not join and not first_only
            elif pattern == 'previous_end':
                expected_triple = bool(join) and not first_only
            else:
                expected_triple = False
            ordinary = keys[slot] == ('a', 'b')
            expected_count = (3 if ordinary else 0) + int(expected_triple)
            assert bool(ok) == bool(expected_count), (pattern, join, first_only, last_only, ok)
            selected = []
            if ok:
                count = I.from_address(stage_counts + slot*4).value
                assert count == expected_count
                for i in range(count):
                    descriptor = destination + slot*0x3400 + i*0x68
                    part = P.from_address(descriptor).value
                    stage = I.from_address(descriptor + 8).value
                    limit = I.from_address(descriptor + 12).value
                    if part == triple_part:
                        assert expected_triple and stage == (0 if pattern == 'full_start' else 1) and limit == 2
                        selected.append(['triple', stage, limit])
                    else:
                        assert ordinary and part in parts and stage == 0 and limit == 1
                        selected.append([parts.index(part), stage, limit])
                assert sum(x[0] == 'triple' for x in selected) == int(expected_triple)
            stages.append(dict(pattern=pattern, join=join, first_only=first_only,
                               last_only=last_only, accepted=bool(ok), selected=selected))
    return dict(dll_sha256=SHA256, lookup_cases=len(compact), filter_cases=len(rows),
                stage_cases=len(stages), stages=stages,
                mismatches=0, compaction=compact, rows=rows,
                scope='Native index builder, full lookup with silence substitution disabled, '
                      'indexed candidate sets and two-stage eligibility including previous-list linkage; '
                      'not joint final cost selection')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dll', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    a = p.parse_args()
    r = run(a.dll)
    a.output.write_text(json.dumps(r, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: r[k] for k in ['lookup_cases', 'filter_cases', 'stage_cases', 'mismatches']}))
