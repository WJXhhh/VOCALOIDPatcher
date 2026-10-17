"""Verify initial PHDC shaping, enable gates, direction and stage using owned data."""
import argparse
import ctypes as c
import itertools
import json
from pathlib import Path
from audit_frm2_numeric import Native, P, I, B, F, SHA256, f32


def run(dll):
    n = Native(dll)
    shape = n.fn(0x1b2260, None, P, P, F, I, F)
    rows = []
    categories = [None, 'UnvoicedFricatives', 'VoicedFricatives',
                  'Semivowels', 'Nasals', 'Liquids', 'Silence']
    for category, coefficient, marker, enabled, join, reverse, stage in itertools.product(
            categories, [0., .5, 1., 1.5, 2.], [-1, 0], [0, 1], [0, 1], [0, 1], [0, 1]):
        work, table, classes = (n.alloc(s) for s in [0x1d00, 0x180, 0x160])
        P.from_address(table + 0x158).value = classes
        P.from_address(work + 0x1c38).value = table
        I.from_address(work + 4).value = marker
        B.from_address(work + 8).value = enabled
        B.from_address(work + 0x1b15).value = join
        I.from_address(work + 12).value = 2
        def text(value):
            raw = value.encode() + b'\0'
            ptr = n.alloc(len(raw))
            c.memmove(ptr, raw, len(raw))
            return ptr
        if category:
            record = n.alloc(0x30)
            I.from_address(classes + 0x140).value = 1
            P.from_address(classes + 0x148).value = record
            I.from_address(record).value = 1
            P.from_address(record + 0x10).value = n.array(P, [text('a')])
            P.from_address(record + 0x18).value = text(category)
        for offset, name in [(0x10, 'a'), (0x210, 'b'), (0x20, 'b'), (0x220, 'c')]:
            c.memmove(work + offset, name.encode() + b'\0', 2)
        # Two distinct stages, unequal sides, and nonzero outer margins.
        groups = [(0, 7, 1, 4), (7, 20, 9, 18),
                  (20, 31, 21, 26), (31, 48, 33, 46)]
        for slot in range(2):
            part = n.alloc(0x280)
            alignment = n.array(I, [v for group in groups for v in group])
            P.from_address(part + 0x1d8).value = alignment
            P.from_address(part + 0x1e0).value = alignment + 64
            P.from_address(work + 0x810 + slot * 0x68).value = part
            I.from_address(work + 0x818 + slot * 0x68).value = stage
            B.from_address(work + 0x1c18 + slot).value = reverse
        selected = groups[2*stage:2*stage+2][::(-1 if reverse else 1)] * 2
        expected_inner = [g[3]-g[2] for g in selected]
        expected_outer = [g[1]-g[0] for g in selected]
        weight = f32((102 if category in ['Semivowels', 'Nasals', 'Liquids']
                      else 64 if category == 'Silence' else 77) / 127)
        factor = f32(1 - f32(f32(1-f32(coefficient**2))*weight))
        affected = ([0] if join else [1, 2]) if marker >= 0 and enabled and factor != 1 else []
        for i in affected:
            value = int(f32(max(1., f32(expected_inner[i]*factor)) + .5))
            expected_inner[i] = expected_outer[i] = value
        shape(0, work, coefficient, -1, .5)
        actual_inner = [I.from_address(work+0x1810+i*4).value for i in range(4)]
        actual_outer = [I.from_address(work+0x1910+i*4).value for i in range(4)]
        actual_margin = [I.from_address(work+0x1a10+i*4).value for i in range(4)]
        assert (actual_inner, actual_outer) == (expected_inner, expected_outer), (
            category, coefficient, marker, enabled, join, reverse, stage,
            actual_inner, expected_inner, actual_outer, expected_outer)
        assert actual_margin == [o-i for o, i in zip(actual_outer, actual_inner)]
        rows.append(dict(category=category, coefficient=coefficient, marker=marker,
                         enabled=enabled, join=join, reverse=reverse, stage=stage,
                         factor=factor, affected=affected, inner=actual_inner, outer=actual_outer))
    return dict(dll_sha256=SHA256, cases=len(rows), mismatches=0, rows=rows,
                scope='Initial shaping only; later junction shaping disabled by start=-1. '
                      'Owned PHDC records and alignments; no host parameter naming claim.')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dll', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    a = p.parse_args()
    result = run(a.dll)
    a.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({k: result[k] for k in ['cases', 'mismatches']}))
