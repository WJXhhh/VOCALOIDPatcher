"""Check the real duration comparator, including four-region trailing groups."""
import argparse
import itertools
import json
from pathlib import Path
from audit_frm2_numeric import Native, P, I, F, B, SHA256, f32


def run(dll):
    n = Native(dll)
    sort = n.fn(0x190f60, P, P, P)
    rows = []
    for left, right, tempo_a, tempo_b, four in itertools.product(
            [10, 16, 20], [10, 16, 20], [60., 120.], [60., 120.], [False, True]):
        parts = []
        for count, tempo, tail in [(left, tempo_a, 1), (right, tempo_b, 40)]:
            part = n.alloc(0x280)
            groups = [0, count//2, 0, count//2,
                      count//2, count, count//2, count]
            if four:
                groups += [count, count+tail, count, count+tail,
                           count+tail, count+2*tail, count+tail, count+2*tail]
            address = n.array(I, groups)
            P.from_address(part+0x1d8).value = address
            P.from_address(part+0x1e0).value = address+4*len(groups)
            F.from_address(part+0x170).value = tempo
            parts.append(part)
        ratio = f32(min(left, right)/max(left, right))
        if ratio >= f32(.8) or tempo_a == tempo_b:
            swap = right < left
        else:
            swap = tempo_b < tempo_a
        pointers = n.array(P, parts)
        sort(pointers, pointers+16)
        actual = [parts.index(P.from_address(pointers+8*i).value) for i in range(2)]
        assert actual == ([1, 0] if swap else [0, 1])
        rows.append(dict(first_two_inner_lengths=[left, right], tempos=[tempo_a, tempo_b],
                         four_regions=four, order=actual))
    return dict(dll_sha256=SHA256, cases=len(rows), mismatches=0, rows=rows,
                scope='Actual two-part sort; later groups do not contribute to comparator; not full budget search')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dll', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    a = p.parse_args()
    result = run(a.dll)
    a.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(cases=result['cases'], mismatches=result['mismatches'])))
