"""Compare installed V4X audio-segment selection on either side of a note gap."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--inventory', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--output-root', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--workers', type=int, default=3)
    args = parser.parse_args()
    assert 1 <= args.workers <= 4
    inventory_bytes = args.inventory.read_bytes()
    inventory = json.loads(inventory_bytes)
    routes = [row for row in inventory['rows'] if row['category'] == 'triple_bridge']
    assert inventory['pair_keys'] == 4680 and len(routes) == 174
    verifier = Path(__file__).with_name('verify_region_v4x_index_frame_reads.py')

    def check(route):
        bank, left, right = (route[key] for key in ('bank', 'left', 'right'))
        third = route['triple_finishes'][0]
        assert route['entry_path'] == ['Sil', left]
        edge = left + '->' + right
        triple = left + '->^' + right + '->' + third
        stem = ('v4x_bridge_boundary_' + bank + '_' +
                left.encode('ascii').hex() + '_' + right.encode('ascii').hex())
        outcomes = {}
        for gap in (12, 13):
            reports = []
            for repeat in (1, 2):
                output = args.output_root / f'{stem}_gap{gap}_r{repeat}.json'
                command = [sys.executable, '-B', str(verifier),
                           '--dll', str(args.dll), '--bank', bank,
                           '--first', left, '--second', right, '--third', third,
                           '--note-split', '2', '--note-gap', str(gap),
                           '--manifest', str(args.manifest),
                           '--voice-root', str(args.voice_root),
                           '--ddb-tools', str(args.ddb_tools),
                           '--full-loop-limit', '36', '--output', str(output)]
                finished = subprocess.run(command, capture_output=True, text=True)
                if finished.returncode:
                    raise RuntimeError((bank, left, right, gap, repeat,
                                        finished.stderr.strip().splitlines()[-1:]))
                reports.append(output.read_bytes())
            assert reports[0] == reports[1], (bank, left, right, gap)
            result = json.loads(reports[0])
            assert result['commits'] == result['full_loop']['events'] == 36
            assert result['full_loop']['pcm_observer_checks'] == \
                   result['full_loop']['pcm_observer_matches'] == result['art_positions']
            assert result['full_loop']['sta_pcm_checks'] == \
                   result['full_loop']['sta_pcm_matches']
            assert all(not item['first_pcm_differences'] and
                       not item['sta_first_differences']
                       for item in result['full_loop']['rows'])
            pair_item = result['by_edge'].get(edge, {})
            triple_item = result['by_edge'].get(triple, {})
            outcomes[str(gap)] = dict(
                pair_work_items=pair_item.get('work_items', 0),
                pair_positions=pair_item.get('positions', 0),
                pair_frame_reads=pair_item.get('frame_reads', 0),
                triple_work_items=triple_item.get('work_items', 0),
                triple_positions=triple_item.get('positions', 0),
                triple_frame_reads=triple_item.get('frame_reads', 0),
                art_positions=result['art_positions'],
                pcm_windows_checked=result['full_loop']['pcm_observer_checks'],
                pcm_windows_matched=result['full_loop']['pcm_observer_matches'],
                output_blocks=result['full_loop']['blocks'],
                nonzero_events=sum(item['nonzero_blocks'] > 0
                                   for item in result['full_loop']['rows']),
                report_sha256=hashlib.sha256(reports[0]).hexdigest())
        return dict(bank=bank, left=left, right=right, third=third,
                    gap12=outcomes['12'], gap13=outcomes['13'])

    rows = []
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        for row in pool.map(check, routes):
            rows.append(row)
            if len(rows) % 25 == 0:
                print(json.dumps(dict(completed=len(rows))), flush=True)
    result = dict(inventory_sha256=hashlib.sha256(inventory_bytes).hexdigest(),
                  candidates=len(rows), repeat_runs=2, events_per_gap=36,
                  gap_units=[12, 13], rows=rows,
                  scope='Installed V4X audio-segment selection in owned two-note '
                        'events. Selected frame positions and output PCM input '
                        'windows are checked against the installed DDB. Editor '
                        'natural timing and listening are not tested.')
    for gap in (12, 13):
        key = 'gap' + str(gap)
        result[key] = {field: sum(row[key][field] for row in rows)
                       for field in ('pair_work_items', 'pair_positions',
                                     'pair_frame_reads', 'triple_work_items',
                                     'triple_positions', 'triple_frame_reads',
                                     'art_positions', 'pcm_windows_checked',
                                     'pcm_windows_matched', 'output_blocks',
                                     'nonzero_events')}
        result[key]['pair_positive_routes'] = sum(row[key]['pair_positions'] > 0
                                                  for row in rows)
        result[key]['triple_positive_routes'] = sum(row[key]['triple_positions'] > 0
                                                    for row in rows)
    args.evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps({key: result[key] for key in ('candidates', 'gap12', 'gap13')}))


if __name__ == '__main__':
    main()
