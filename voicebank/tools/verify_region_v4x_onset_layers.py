"""Replay owned onset events with the installed V4X Sil→a layer metadata."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from analyze_reference_layers import walk_art
from audit_frm2_numeric import SHA256
from verify_planned_ddi import load_public_parser
from verify_region_direct_material_commit import verify as verify_direct


OFFSETS = (0, 1, 20, 100, 400)


def bank_layers(bank: str, ddi_sha256: str, voice_root: Path, ddb_tools: Path) -> list[dict]:
    matches = list(voice_root.rglob(f'{bank}.ddi'))
    assert len(matches) == 1, (bank, len(matches))
    raw = matches[0].read_bytes()
    assert hashlib.sha256(raw).hexdigest() == ddi_sha256, bank
    model = load_public_parser(ddb_tools)(raw)
    model.read()
    assert 'a' in model.ddi_data_dict['sta'], bank
    matches = [parts for root in model.art_data.values()
               for key, parts in walk_art(root) if key == ('Sil', 'a')]
    assert len(matches) == 1 and 1 <= len(matches[0]) <= 4, bank
    layers = [dict(pitch1=part['pitch1'], pitch2=part['pitch2'],
                   dynamics=part['dynamics'], frames=len(part['epr']),
                   groups=[tuple(group[field] for field in
                       ('start', 'end', 'start2', 'end2'))
                           for group in part['frame_align']]) for part in matches[0]]
    assert all(len(layer['groups']) == 2 for layer in layers)
    return layers


def probe(dll: Path, bank: str, ddi_sha256: str, offset: int,
          voice_root: Path, ddb_tools: Path) -> dict:
    layers = bank_layers(bank, ddi_sha256, voice_root, ddb_tools)
    report = verify_direct(dll, 1, ['a'], None, 64, note_start_offset=offset,
                           inspect_allocation=True, edge_layers={('Sil', 'a'): layers})
    assert report['commits'] == 36
    cases = [row for row in report['cases'] if 'commit' in row]
    assert len(cases) == 36
    selected_by_pitch = {}
    allocation_by_pitch = {}
    art_frames = 0
    max_error = 0.0
    for row in cases:
        commit = row['commit']
        diagnostic = commit['diagnostics']
        assert diagnostic['candidate_count'] == 1
        assert all(value >= 0 for value in diagnostic['allocated_sides'])
        assert sum(diagnostic['allocated_sides']) == offset
        selected = diagnostic['selected_part']
        assert 1 <= selected <= len(layers), (bank, row['pitch'], selected)
        selected_by_pitch.setdefault(str(row['pitch']), set()).add(selected - 1)
        allocation_by_pitch.setdefault(str(row['pitch']), set()).add(
            tuple(diagnostic['allocated_sides']))
        if offset:
            assert commit['material_indices'] == [selected, 0]
            assert commit['item_ranges'] == [[0, offset], [offset, offset + 200]]
            assert len(diagnostic['art_positions']) == 1
            position = diagnostic['art_positions'][0]
            assert position['frames'] == offset
            assert position['max_error'] < 1e-5
            art_frames += position['frames']
            max_error = max(max_error, position['max_error'])
        else:
            assert commit['material_indices'] == [0]
            assert commit['item_ranges'] == [[0, 200]]
            assert not diagnostic['art_positions']
    assert all(len(indices) == 1 for indices in selected_by_pitch.values())
    assert all(len(values) == 1 for values in allocation_by_pitch.values())
    for midi, indices in selected_by_pitch.items():
        target = (int(midi) - 69) * 100
        nearest = min(range(len(layers)), key=lambda index: abs(layers[index]['pitch1'] - target))
        assert next(iter(indices)) == nearest, (bank, offset, midi, indices, nearest)
    return dict(bank=bank, ddi_sha256=ddi_sha256, dse_sha256=SHA256,
                note_start_offset=offset, layer_count=len(layers), layers=layers,
                commits=len(cases), selected_layer_by_midi={key: next(iter(value))
                    for key, value in sorted(selected_by_pitch.items())},
                allocation_by_midi={key: list(next(iter(value)))
                    for key, value in sorted(allocation_by_pitch.items())},
                articulation_work_items=36 if offset else 0,
                articulation_positions=art_frames, max_position_error=max_error,
                nearest_pitch1_checks=len(selected_by_pitch),
                cases_sha256=hashlib.sha256(json.dumps(cases, sort_keys=True).encode()).hexdigest(),
                scope='Installed DDI layer metadata in an owned direct-tree event fixture; '
                      'no installed DDB payload, Editor timing or acoustic output.')


def matrix(dll: Path, manifest: Path, voice_root: Path, ddb_tools: Path,
           workdir: Path, workers: int) -> dict:
    workdir.mkdir(parents=True, exist_ok=True)
    source = json.loads(manifest.read_text(encoding='utf-8'))
    banks = source['banks']
    conditions = [(bank['bank'], bank['ddi_sha256'], offset)
                  for bank in banks for offset in OFFSETS]
    def child(condition):
        bank, ddi_sha256, offset = condition
        detail = workdir / f'{bank}_{offset}.json'
        command = [sys.executable, '-B', str(Path(__file__).resolve()),
                   '--dll', str(dll), '--manifest', str(manifest),
                   '--voice-root', str(voice_root), '--ddb-tools', str(ddb_tools),
                   '--bank', bank, '--offset', str(offset), '--output', str(detail)]
        finished = subprocess.run(command, capture_output=True, text=True)
        assert finished.returncode == 0, (bank, offset, finished.stderr[-2000:])
        result = json.loads(detail.read_text(encoding='utf-8'))
        assert result['ddi_sha256'] == ddi_sha256
        return result
    with ThreadPoolExecutor(max_workers=workers) as pool:
        results = list(pool.map(child, conditions))
    return dict(dse_sha256=SHA256, parser_sha256=source['parser_sha256'],
                banks=len(banks), scenarios=len(results),
                commits=sum(row['commits'] for row in results),
                articulation_work_items=sum(row['articulation_work_items'] for row in results),
                articulation_positions=sum(row['articulation_positions'] for row in results),
                nearest_pitch1_checks=sum(row['nearest_pitch1_checks'] for row in results),
                results=results,
                scope='Hash-pinned installed V4X DDI Sil→a layer metadata in owned direct-tree '
                      'events. DDB frames/PCM and Editor timing are outside this experiment.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--bank')
    parser.add_argument('--offset', type=int, choices=OFFSETS)
    parser.add_argument('--workdir', type=Path)
    parser.add_argument('--workers', type=int, default=4)
    args = parser.parse_args()
    source = json.loads(args.manifest.read_text(encoding='utf-8'))
    parser_file = args.ddb_tools / 'utils' / 'ddi_utils.py'
    assert hashlib.sha256(parser_file.read_bytes()).hexdigest() == source['parser_sha256']
    if args.bank:
        assert args.offset is not None
        bank = next(row for row in source['banks']
                    if row['bank'] == args.bank)
        report = probe(args.dll, args.bank, bank['ddi_sha256'], args.offset,
                       args.voice_root, args.ddb_tools)
    else:
        assert args.offset is None and args.workdir is not None
        assert 1 <= args.workers <= 8
        report = matrix(args.dll, args.manifest, args.voice_root, args.ddb_tools,
                        args.workdir, args.workers)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n',
                           encoding='utf-8')
    print(json.dumps(dict(bank=report.get('bank'), scenarios=report.get('scenarios'),
                          commits=report['commits'],
                          articulation_work_items=report['articulation_work_items'])))
