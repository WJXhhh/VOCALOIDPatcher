"""Observe a two-phoneme V4X onset event with installed layer metadata."""
import argparse
import hashlib
import json
from pathlib import Path

from analyze_reference_layers import walk_art
from audit_frm2_numeric import SHA256, P, I, B
from region_commit_material import connected
from verify_planned_ddi import load_public_parser
from verify_region_event_production import run


def verify(dll: Path, bank: str, first: str, second: str,
           manifest: Path, voice_root: Path, ddb_tools: Path,
           offset: int, control3: int = 64, ring_submode: int = 0,
           mode: int = 1, include_cases: bool = False) -> dict:
    assert mode in (0, 1) and (mode == 1 or offset == 0)
    source = json.loads(manifest.read_text(encoding='utf-8'))
    identity = next(row for row in source['banks'] if row['bank'] == bank)
    assert hashlib.sha256((ddb_tools / 'utils' / 'ddi_utils.py').read_bytes()).hexdigest() == source['parser_sha256']
    matches = list(voice_root.rglob(f'{bank}.ddi'))
    assert len(matches) == 1
    raw = matches[0].read_bytes()
    assert hashlib.sha256(raw).hexdigest() == identity['ddi_sha256']
    model = load_public_parser(ddb_tools)(raw)
    model.read()
    sta_anchor = (second if second in model.ddi_data_dict['sta'] else
                  first if first in model.ddi_data_dict['sta'] else 'a')
    assert sta_anchor in model.ddi_data_dict['sta']
    assert second in model.ddi_data_dict['sta'] or second == 'Sil'
    wanted = (('Sil', first), (first, second))
    found = {key: parts for root in model.art_data.values()
             for key, parts in walk_art(root) if key in wanted}
    assert set(found) == set(wanted) and all(found.values())
    layers = {}
    for edge in wanted:
        layers[edge] = [dict(pitch1=part['pitch1'], pitch2=part['pitch2'],
                             dynamics=part['dynamics'], frames=len(part['epr']),
                             groups=[tuple(group[field] for field in
                                     ('start', 'end', 'start2', 'end2'))
                                     for group in part['frame_align']])
                        for part in found[edge]]

    def build(n, state, context, voice, table, sta, art):
        material = connected(n, state, context, voice, table, sta, art,
                             edges=wanted, tree=bool(mode), edge_layers=layers,
                             stationary_phoneme=sta_anchor)
        if mode:
            def inspect(n, state, ring, items, material):
                return [dict(part_null=not P.from_address(items + index * 0x140 + 0x20).value,
                             stage=I.from_address(items + index * 0x140 + 0x28).value,
                             reverse=B.from_address(items + index * 0x140 + 0x12c).value)
                        for index in range(I.from_address(ring + 0x34).value)]
            material['post_direct_commit'] = inspect
        return material

    result = run(dll, True, False, True, build, [[first, second]],
                 material_modes=(mode,), commit_control3=control3,
                 ring_submode=ring_submode,
                 commit_note_start_offset=offset)
    cases = [row for row in result['rows'] if row['mode'] == mode and 'commit' in row]
    assert len(cases) == 36
    by_pitch = {}
    for case in cases:
        commit = case['commit']
        key = str(case['pitch'])
        summary = dict(work_items=commit['work_items'],
                       material_indices=commit['material_indices'],
                       item_ranges=commit['item_ranges'],
                       tokens=commit['tokens'])
        if mode:
            summary['diagnostics'] = commit['diagnostics']
        else:
            summary['position_checks'] = len(commit['position_checks'])
            summary['position_ranges'] = [[item['min_position'], item['max_position']]
                                          for item in commit['position_checks']]
        by_pitch.setdefault(key, []).append(summary)
    assert all(len(items) == 12 for items in by_pitch.values())
    report = dict(bank=bank, first=first, second=second, offset=offset,
                control3=control3, ring_submode=ring_submode,
                ddi_sha256=identity['ddi_sha256'], dse_sha256=SHA256,
                edge_layer_counts={f'{left}->{right}': len(layers[(left, right)])
                                   for left, right in wanted},
                commits=len(cases), by_pitch={key: items[0] for key, items in by_pitch.items()},
                consistent_by_pitch={key: len({json.dumps(item, sort_keys=True)
                                               for item in items}) == 1
                                     for key, items in by_pitch.items()},
                scope='Installed V4X DDI layer metadata in a synthetic two-phoneme '
                      'direct-tree event. No DDB frame reading, Editor timing, or final audio.')
    if mode == 0:
        report['mode'] = 0
        transition_start = 1 + len(layers[wanted[0]])
        transition_end = transition_start + len(layers[wanted[1]])
        observed = []
        for case in cases:
            commit = case['commit']
            transition_items = [index for index, part_index in
                                enumerate(commit['material_indices'])
                                if transition_start <= part_index < transition_end]
            checks = commit['position_checks']
            observed.append(dict(pitch=case['pitch'], milliseconds=case['milliseconds'],
                                 slot=case['slot'], split=case['split'],
                                 transition_items=transition_items,
                                 material_indices=commit['material_indices'],
                                 item_ranges=commit['item_ranges'],
                                 checked_art_positions=sum(item['frames'] for item in checks),
                                 max_position_error=max((item['unit_model_max_error']
                                                         for item in checks), default=0.)))
        report['indexed_summary'] = dict(
            transition_materialized_cases=sum(bool(item['transition_items'])
                                              for item in observed),
            checked_art_positions=sum(item['checked_art_positions'] for item in observed),
            max_position_error=max(item['max_position_error'] for item in observed),
            cases_sha256=hashlib.sha256(json.dumps(observed, sort_keys=True).encode()).hexdigest(),
            distinct_work_lists=len({json.dumps((item['material_indices'], item['item_ranges']))
                                     for item in observed}))
        if include_cases:
            report['indexed_cases'] = observed
        report['scope'] = ('Installed V4X DDI layer metadata in an owned mode-0 '
                           'indexed two-phoneme event; source positions checked against '
                           'independent alignment model. No DDB frame or final audio read.')
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--bank', required=True)
    parser.add_argument('--first', required=True)
    parser.add_argument('--second', required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--offset', type=int, default=400)
    parser.add_argument('--control3', type=int, default=64)
    parser.add_argument('--ring-submode', type=int, choices=(0, 1), default=0)
    parser.add_argument('--mode', type=int, choices=(0, 1), default=1)
    parser.add_argument('--include-cases', action='store_true')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = verify(args.dll, args.bank, args.first, args.second,
                    args.manifest, args.voice_root, args.ddb_tools, args.offset,
                    args.control3, args.ring_submode, args.mode, args.include_cases)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n',
                           encoding='utf-8')
    print(json.dumps(dict(commits=report['commits'], by_pitch=report['by_pitch'])))
