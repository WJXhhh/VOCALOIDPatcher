"""Check the direct-tree articulation candidate helper with owned units."""
import argparse
import ctypes as c
import json
from pathlib import Path

from audit_frm2_numeric import P, I, Q, F, B, SHA256
from region_commit_material import connected
from verify_region_event_production import run


def verify(dll: Path, self_edge: bool, budget: int = 100) -> dict:
    checks = []
    edges = [('Sil', 'a'), ('a', 'Sil')]
    if self_edge:
        edges.append(('a', 'a'))

    def build(n, state, context, voice, table, sta, art):
        material = connected(n, state, context, voice, table, sta, art,
                             edges=edges, tree=True)
        descriptor, flags = n.alloc(0x1c80), n.alloc(2)
        n.fn(0x198250, None, P)(descriptor)
        n.fn(0x1afc30, None, P)(descriptor)
        c.memmove(descriptor + 0x10, b'a\0', 2)
        c.memmove(descriptor + 0x210, b'a\0', 2)
        I.from_address(descriptor + 0xc).value = 1
        I.from_address(descriptor + 0x1c40).value = 0
        I.from_address(descriptor + 0x1c44).value = 0
        n.fn(0x1b8120, P, P, P, P, I, I, B, I)(state, flags, descriptor, 0, 1, 0, 0)
        unit = P.from_address(descriptor + 0x410).value
        expected = material.get('tree_units', {}).get(('a', 'a'), 0)
        selected_part = False
        selected_length = 0
        if self_edge:
            selected_length = n.fn(0x1b8f40, Q, P, P, I, F, F, B)(
                state, descriptor, budget, 0., .6, 0)
            selected_part = P.from_address(descriptor + 0x810).value == material['parts'][-1]
        checks.append(dict(self_edge=self_edge, selected=unit == expected if self_edge else not unit,
                           has_unit=bool(unit), flags=[B.from_address(flags + k).value for k in range(2)],
                           count=I.from_address(descriptor + 0xc).value,
                           selected_part=selected_part, selected_length=selected_length))
        assert checks[-1]['selected'] and (not self_edge or selected_part), checks[-1]
        return material

    report = run(dll, True, False, True, build, [['a']], material_modes=(1,))
    assert len(checks) == 72
    return dict(dse_sha256=SHA256, self_edge=self_edge, budget=budget, probes=len(checks),
                shapes=[json.loads(value) for value in sorted({json.dumps(row, sort_keys=True)
                                                                for row in checks})],
                scope='Owned direct-tree unit selection descriptor; no acoustic output or Editor.')


def verify_matrix(dll: Path) -> dict:
    absent, present = verify(dll, False), verify(dll, True)
    assert absent['shapes'] == [dict(self_edge=False, selected=True, has_unit=False,
                                    flags=[0, 0], count=0, selected_part=False,
                                    selected_length=0)]
    assert present['shapes'] == [dict(self_edge=True, selected=True, has_unit=True,
                                     flags=[1, 1], count=1, selected_part=True,
                                     selected_length=100)]
    return dict(dse_sha256=SHA256, probes=absent['probes'] + present['probes'],
                no_edge=absent['shapes'][0], with_self_edge=present['shapes'][0],
                scope='Owned a→a direct-tree candidate construction and part selection; '
                      'the full two-note event path remains a separate condition.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--self-edge', action='store_true')
    parser.add_argument('--budget', type=int, default=100)
    parser.add_argument('--matrix', action='store_true')
    args = parser.parse_args()
    result = verify_matrix(args.dll) if args.matrix else verify(args.dll, args.self_edge, args.budget)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(probes=result['probes'])))
