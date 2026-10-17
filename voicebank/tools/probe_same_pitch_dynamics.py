"""Distinguish native dynamics-layer selection from actual installed layers.

Owned metadata native fixtures use identical phoneme key, pitch, alignment and
duration. Inventory reads hash-pinned DDI metadata only, never DDB or PCM.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import ctypes as c
import hashlib
import itertools
import json
from pathlib import Path
import struct

from audit_frm2_numeric import Native, P, I, Q, F, B, SHA256, f32
from verify_part_scalar_contract import make_work, select
from analyze_reference_layers import walk_art
from verify_planned_ddi import load_public_parser


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def bits(value):
    return struct.pack('<f', value).hex()


def native_layers(dll):
    n = Native(dll)
    direct, joint = [], []
    targets = [.2, .39, .4, .41, .6, .79, .8, .81, 1.]
    for order in itertools.permutations([.2, .6, 1.]):
        rows = [dict(pitch=-400., dynamic=-9., length=12, tempo=90.)]
        rows += [dict(pitch=0., dynamic=d, length=12, tempo=90.) for d in order]
        rows += [dict(pitch=400., dynamic=9., length=12, tempo=90.)]
        work, pointers = make_work(n, [rows])
        for target in targets:
            expected = min(order, key=lambda d: (abs(f32(f32(d)-f32(target))), d))
            actual, length = select(n, work, pointers, 12, 0., target)
            selected = rows[actual[0]]
            assert selected['pitch'] == 0. and selected['dynamic'] == expected and length == 12
            direct.append(dict(order=order, target=target, selected=selected['dynamic'], pitch=0., length=length))

    controls, outer, context, config, registry, voice = (
        n.alloc(size) for size in [0x100, 16, 16, 0x100, 0x20, 0x300])
    P.from_address(outer).value = controls
    P.from_address(outer+8).value = context
    P.from_address(context).value = config
    P.from_address(config+0x10).value = registry
    I.from_address(registry+8).value = 1
    P.from_address(registry+0x10).value = n.array(P, [voice])
    P.from_address(registry+0x18).value = n.array(P, [n.alloc(8)])
    score = n.alloc(12*0x2c)
    P.from_address(controls+0x10).value = score
    I.from_address(controls+0x18).value = 12
    table, classes = n.alloc(0x180), n.alloc(0x160)
    P.from_address(table+0x158).value = classes
    index, sentinel = voice+0x2e0, n.alloc(0xa8)
    P.from_address(index).value = sentinel
    for offset in [0, 8, 16]:
        P.from_address(sentinel+offset).value = sentinel
    B.from_address(sentinel+0x18).value = B.from_address(sentinel+0x19).value = 1
    parents = {}
    callback = c.CFUNCTYPE(P, P)(lambda node: parents.get(node, 0))
    n.keep.append(callback)
    vtable = n.alloc(0x68)
    P.from_address(vtable+0x60).value = c.cast(callback, P).value
    previous = 0
    for name in ['articulation', 'a', 'b']:
        node = n.alloc(0x180)
        P.from_address(node).value = vtable
        raw = name.encode()+b'\0'
        c.memmove(node+0x18, raw, len(raw))
        parents[node] = previous
        previous = node
    alignment = n.array(I, [0, 5, 0, 5, 5, 12, 5, 12])
    parts = [n.alloc(0x280) for _ in range(3)]
    for part, dynamic in zip(parts, [.2, .6, 1.]):
        F.from_address(part+0x160).value = 0.
        F.from_address(part+0x16c).value = dynamic
        F.from_address(part+0x170).value = 90.
        I.from_address(part+0x1b0).value = 12
        P.from_address(part+0x1d8).value = alignment
        P.from_address(part+0x1e0).value = alignment+32
    P.from_address(node+0x148).value = n.array(P, parts)
    I.from_address(node+0x150).value = 3
    n.fn(0x18d790, None, P, P)(index, node)
    query = n.fn(0x1aa0a0, P, P, P, P, I, I, B, I)
    choose = n.fn(0x1acda0, B, P, I, I, I, P, P, I, B)
    before = [c.string_at(part, 0x280) for part in parts]
    for target in targets:
        for frame in range(12):
            F.from_address(score+frame*0x2c).value = 0.
            F.from_address(score+frame*0x2c+4).value = target
        current, following = n.alloc(0x1d00), n.alloc(0x1d00)
        for work in [current, following]:
            I.from_address(work+4).value = -1
            B.from_address(work+0x1b15).value = 1
            P.from_address(work+0x1c38).value = table
        I.from_address(current+12).value = 1
        c.memmove(current+0x10, b'a\0', 2)
        c.memmove(current+0x210, b'b\0', 2)
        query(outer, n.alloc(2), current, 0, 1, 0, 0)
        assert choose(outer, 0, 12, 12, current, following, 0, 0)
        chosen = parts.index(P.from_address(current+0x810).value)
        expected = min(range(3), key=lambda k: (abs(f32(f32([.2,.6,1.][k])-f32(target))), k))
        assert chosen == expected, (target, chosen, expected)
        assert [I.from_address(current+0x1510+i*4).value for i in range(2)] == [5,7]
        joint.append(dict(target=target, selected=[.2,.6,1.][chosen], pitch=0., lengths=[5,7], stage=0))
    assert before == [c.string_at(part, 0x280) for part in parts]
    return dict(dse_sha256=SHA256, direct=direct, joint=joint, mismatches=0,
        scope='Owned metadata: same key, identical pitch/duration/alignment; dynamics alone varies. No PCM or Editor; not proof of supplied commercial recordings.')


def inventory(reference, roots, parser_root):
    pinned = json.loads(reference.read_text(encoding='utf-8'))['cross_reference']
    assert digest(parser_root/'utils/ddi_utils.py') == pinned['parser_sha256']
    model_type = load_public_parser(parser_root)
    found = defaultdict(set)
    for root in roots:
        for path in root.rglob('*.ddi'):
            found[path.stem].add(path.resolve())
    banks, total, distribution = [], Counter(), Counter()
    for family in ['v5', 'v4x']:
        for identity in pinned[family]:
            matches = found[identity['bank']]
            assert len(matches) == 1, identity['bank']
            path = next(iter(matches))
            assert path.stat().st_size < 256*1024*1024
            assert digest(path) == identity['ddi_sha256']
            model = model_type(path.read_bytes()); model.read()
            groups = [('STA', [name], list(unit['stap'].values())) for name,unit in model.sta_data.items()]
            groups += [('ART'+str(len(key)), list(key), parts) for root in model.art_data.values() for key,parts in walk_art(root)]
            groups += [('VQM', [str(name)], [part]) for name,part in (model.vqm_data or {}).items()]
            counts, dynamics, pitch_windows, voiced_windows = Counter(), Counter(), Counter(), Counter()
            varying, near_examples, examples = [], [], []
            for kind,key,parts in groups:
                if not parts: continue
                counts[kind] += len(parts)
                values = Counter(bits(float(part['dynamics'])) for part in parts)
                dynamics.update(values)
                if len(values)>1: varying.append(dict(kind=kind,key=key,dynamics=dict(values)))
                if kind != 'VQM':
                    coords = sorted(float(part['pitch1']) for part in parts)
                    maximum = max(sum(0 <= q-p <= 75 for q in coords) for p in coords)
                    pitch_windows[maximum] += 1
                    pitched = [p for p in coords if -20000 < p < 20000]
                    if pitched:
                        voiced_windows[max(sum(0 <= q-p <= 75 for q in pitched) for p in pitched)] += 1
                    if maximum >= 3 and len(near_examples)<8:
                        near_examples.append(dict(kind=kind,key=key,pitches=coords,dynamics=[float(p['dynamics']) for p in parts]))
                    if len(examples)<8 and len(parts)>1:
                        examples.append(dict(kind=kind,key=key,pitches=coords,dynamics=[float(p['dynamics']) for p in parts]))
            assert sum(counts.values()) == sum(identity['parts_by_kind'].values())
            assert dict(dynamics) == identity['metadata_float32_bits']['dynamics']
            total.update(counts); distribution.update(dynamics)
            banks.append(dict(bank=identity['bank'],family=family,ddi_sha256=identity['ddi_sha256'],counts=dict(counts),
                dynamics_float32_little_endian=dict(dynamics),keys_with_multiple_dynamics=varying,
                maximum_parts_in_75_cent_window_histogram=dict(pitch_windows),
                pitched_parts_in_75_cent_window_histogram=dict(voiced_windows),
                close_pitch_examples=near_examples,examples=examples))
            print(json.dumps(dict(bank=identity['bank'],parts=sum(counts.values()),dynamics=dict(dynamics))),flush=True)
    return dict(reference_sha256=digest(reference),parser_sha256=pinned['parser_sha256'],bank_count=len(banks),
        counts=dict(total),total_parts=sum(total.values()),dynamics_float32_little_endian=dict(distribution),banks=banks,
        scope='Hash-pinned metadata only. Exact keys preserve suffix/context; 75-cent windows are descriptive, not manufacturer pitch-layer labels. Constant dynamics does not prove identical recorded vocal effort or absence of every other encoding.')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dll',type=Path)
    p.add_argument('--reference',type=Path)
    p.add_argument('--voice-root',type=Path,action='append')
    p.add_argument('--parser-root',type=Path)
    p.add_argument('--output',type=Path,required=True)
    a=p.parse_args()
    if a.dll:
        result=native_layers(a.dll)
    else:
        if not all([a.reference,a.voice_root,a.parser_root]): p.error('Inventory requires reference, voice-root and parser-root')
        result=inventory(a.reference,a.voice_root,a.parser_root)
    a.output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'native_cases':len(result.get('direct',[]))+len(result.get('joint',[])),
                      'banks':result.get('bank_count'),'parts':result.get('total_parts')}))
