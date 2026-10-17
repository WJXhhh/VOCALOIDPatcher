"""Observe valid and missing ART material in the direct-tree event path."""
import argparse
import hashlib
import json
import subprocess
import sys
from pathlib import Path

from audit_frm2_numeric import SHA256, P, I, F, B
from region_commit_material import connected
from verify_region_event_production import run


EDGE_SETS = [(), (('Sil', 'a'),), (('a', 'Sil'),),
             (('Sil', 'a'), ('a', 'Sil')), (('a', 'a'),),
             (('Sil', 'a'), ('a', 'Sil'), ('a', 'a'))]


def verify(dll: Path, variant: int, tokens: list[str], lookahead: list[str] | None,
           control3: int, ring_submode: int = 0, note_start_offset: int = 0,
           inspect_allocation: bool = False, edge_layers=None) -> dict:
    edges = EDGE_SETS[variant]
    def build(*args):
        material = connected(*args, edges=edges, tree=True, edge_layers=edge_layers)
        if inspect_allocation:
            def post(n, state, ring, items, material):
                descriptor = n.lib._handle + 0xa971c0
                part = P.from_address(descriptor + 0x810).value
                count = I.from_address(ring + 0x34).value
                frames = P.from_address(ring).value
                art_positions = []
                for j in range(count):
                    item = items + j * 0x140
                    selected = P.from_address(item + 0x20).value
                    if selected not in material['parts'][1:]:
                        continue
                    from verify_region_output_mapping import predict
                    begin, end = (I.from_address(item + offset).value for offset in (0, 4))
                    left = I.from_address(item + 8).value
                    reverse = B.from_address(item + 0x12c).value
                    stage = I.from_address(item + 0x28).value
                    source_groups = material['groups_by_part'][selected]
                    groups = source_groups[2 * stage:2 * stage + 2][::(-1 if reverse else 1)]
                    assert len(groups) == 2
                    expected = predict(groups[0], left, 0, reverse) + predict(
                        groups[1], end - begin - left, 1, reverse)
                    actual = [F.from_address(frames + t * 0x18 + 8).value
                              for t in range(begin, end)]
                    assert len(actual) == len(expected)
                    art_positions.append(dict(item=j, left=left, reverse=reverse,
                                              frames=len(actual),
                                              max_error=max(abs(a - b) for a, b in zip(actual, expected)),
                                              first=actual[:3], last=actual[-3:]))
                return dict(candidate_count=I.from_address(descriptor + 0xc).value,
                            selected_part=material['parts'].index(part)
                            if part in material['parts'] else -1,
                            allocated_sides=[I.from_address(n.lib._handle + offset).value
                                             for offset in (0xa986d0, 0xa986d4)],
                            art_positions=art_positions)
            material['post_direct_commit'] = post
        return material
    report = run(dll, True, False, True,
                 build, [tokens],
                 material_modes=(1,), lookahead_tokens=lookahead, commit_control3=control3,
                 ring_submode=ring_submode, commit_note_start_offset=note_start_offset)
    cases = [row for row in report['rows'] if row['mode'] == 1 and 'commit' in row]
    assert len(cases) == 36
    return dict(dse_sha256=SHA256, edges=edges, tokens=tokens, lookahead=lookahead,
                ring_submode=ring_submode, note_start_offset=note_start_offset,
                control3=control3,
                commits=len(cases), cases=cases,
                scope='Owned valid voice, stationary and selected ART edges; direct-tree '
                      'native event commit. No Editor process or acoustic rendering.')


def verify_matrix(dll: Path, workdir: Path) -> dict:
    workdir.mkdir(parents=True, exist_ok=True)
    matrix = []
    groups = [('single-note', range(5), None, 64),
              ('lookahead-nonzero', (0, 4), ['a'], 64),
              ('lookahead-zero', (0, 1, 4), ['a'], 0),
              ('lookahead-silence-zero', (0,), ['Sil'], 0)]
    for name, variants, lookahead, control3 in groups:
        for variant in variants:
            detail_path = workdir / f'{name}_{variant}.json'
            command = [sys.executable, '-B', str(Path(__file__).resolve()), '--dll', str(dll),
                       '--variant', str(variant), '--tokens', 'a', '--control3', str(control3),
                       '--output', str(detail_path)]
            if lookahead is not None:
                command.extend(['--lookahead', *lookahead])
            subprocess.run(command, check=True, capture_output=True, text=True)
            report = json.loads(detail_path.read_text(encoding='utf-8'))
            for row in report['cases']:
                commit = row['commit']
                assert commit['new_slot'] == 1 - row['slot']
                assert commit['pending'] == 0
                assert commit['duration'] == 200
                if control3 == 64:
                    assert commit['work_items'] == 1
                    assert commit['material_indices'] == [0]
                    assert commit['item_ranges'] == [[0, 200]]
                    assert commit['tokens'] == [[1, -1]]
                    assert commit['note_aux'] == .5
                else:
                    assert commit['work_items'] == 0
                    assert commit['material_indices'] == []
                    assert commit['note_aux'] == 0
                    assert commit['lookahead_before_commit']['bank_ids'] == [0, 0]
                    assert commit['lookahead_before_commit']['pitch'] == commit['note_pitch']
                    assert commit['lookahead_before_commit']['start'] == 300
            digest = hashlib.sha256(json.dumps(report['cases'], sort_keys=True,
                                               ensure_ascii=False).encode()).hexdigest()
            matrix.append(dict(group=name, edges=report['edges'], lookahead=lookahead,
                               control3=control3, commits=report['commits'],
                               resulting_work_items=1 if control3 == 64 else 0,
                               result_sha256=digest))
    return dict(dse_sha256=SHA256, scenarios=len(matrix), commits=sum(x['commits'] for x in matrix),
                results=matrix,
                scope='Owned first-note direct-tree commits with native event producer. '
                      'The zero-control lookahead work slot is associated with the registered '
                      'bank explicitly. Release-edge variants are excluded from zero-control '
                      'matrix pending their further fixture contract; host scheduling and '
                      'later acoustic output are untested.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--variant', type=int, choices=range(len(EDGE_SETS)))
    parser.add_argument('--tokens', nargs='+', default=['a'])
    parser.add_argument('--lookahead', nargs='+')
    parser.add_argument('--control3', type=int, default=64)
    parser.add_argument('--ring-submode', type=int, choices=(0, 1), default=0)
    parser.add_argument('--note-start-offset', type=int, default=0)
    parser.add_argument('--inspect-allocation', action='store_true')
    parser.add_argument('--matrix', action='store_true')
    parser.add_argument('--workdir', type=Path)
    args = parser.parse_args()
    if args.matrix:
        assert args.variant is None and args.workdir is not None
        result = verify_matrix(args.dll, args.workdir)
    else:
        assert args.variant is not None
        result = verify(args.dll, args.variant, args.tokens, args.lookahead,
                        args.control3, args.ring_submode, args.note_start_offset,
                        args.inspect_allocation)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(commits=result['commits'], scenarios=result.get('scenarios'))))
