"""Check installed pair ART when two note events have a measured gap."""
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
    parser.add_argument('--gap', type=int, default=50)
    args = parser.parse_args()
    assert 1 <= args.workers <= 4 and 0 <= args.gap <= 500
    source_bytes = args.inventory.read_bytes()
    source = json.loads(source_bytes)
    routes = [row for row in source['rows'] if row['category'] == 'triple_bridge']
    assert source['pair_keys'] == 4680 and len(routes) == 174
    script = Path(__file__).with_name('verify_region_v4x_index_frame_reads.py')

    def check(route):
        bank, left, right = (route[key] for key in ('bank', 'left', 'right'))
        third = route['triple_finishes'][0]
        assert route['entry_path'] == ['Sil', left]
        stem = ('v4x_bridge_gap' + str(args.gap) + '_' + bank + '_' +
                left.encode('ascii').hex() + '_' + right.encode('ascii').hex())
        reports = []
        errors = []
        for repeat in (1, 2):
            output = args.output_root / f'{stem}_r{repeat}.json'
            command = [sys.executable, '-B', str(script), '--dll', str(args.dll),
                       '--bank', bank, '--first', left, '--second', right,
                       '--third', third, '--note-split', '2',
                       '--note-gap', str(args.gap), '--manifest', str(args.manifest),
                       '--voice-root', str(args.voice_root), '--ddb-tools',
                       str(args.ddb_tools), '--full-loop-limit', '36',
                       '--output', str(output)]
            finished = subprocess.run(command, capture_output=True, text=True)
            if finished.returncode:
                errors.append(dict(code=finished.returncode,
                                   message=(finished.stderr.strip().splitlines() or
                                            ['no error message'])[-1]))
            else:
                reports.append(output.read_bytes())
        if errors:
            assert len(errors) == 2 and errors[0] == errors[1]
            return dict(bank=bank, left=left, right=right, third=third,
                        status='fixture_incomplete', error=errors[0])
        assert reports[0] == reports[1], (bank, left, right)
        result = json.loads(reports[0])
        target = result['by_edge'][left + '->' + right]
        loop = result['full_loop']
        assert result['commits'] == loop['events'] == 36
        assert target['positions'] > 0 and target['frame_reads'] > 0
        assert loop['pcm_observer_checks'] == loop['pcm_observer_matches'] == result['art_positions']
        assert loop['sta_pcm_checks'] == loop['sta_pcm_matches']
        assert all(not item['first_pcm_differences'] and
                   not item['sta_first_differences'] for item in loop['rows'])
        return dict(bank=bank, left=left, right=right, third=third,
                    status='passed', target_work_items=target['work_items'],
                    target_positions=target['positions'],
                    target_frame_reads=target['frame_reads'],
                    art_positions=result['art_positions'],
                    full_loop_blocks=loop['blocks'],
                    full_loop_art_pcm_matches=loop['pcm_observer_matches'],
                    full_loop_sta_pcm_matches=loop['sta_pcm_matches'],
                    nonzero_events=sum(item['nonzero_blocks'] > 0 for item in loop['rows']),
                    report_sha256=hashlib.sha256(reports[0]).hexdigest())

    rows = []
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        for row in pool.map(check, routes):
            rows.append(row)
            if len(rows) % 50 == 0:
                print(json.dumps(dict(completed=len(rows),
                                      passed=sum(item['status'] == 'passed'
                                                 for item in rows))), flush=True)
    passed = [row for row in rows if row['status'] == 'passed']
    result = dict(inventory_sha256=hashlib.sha256(source_bytes).hexdigest(),
                  gap=args.gap, candidates=len(rows), passed=len(passed),
                  fixture_incomplete=len(rows) - len(passed),
                  repeat_runs=2, commits=len(passed) * 36, rows=rows,
                  scope='Installed V4X pair ART on an owned two-note path with '
                        'a measured inter-note gap. Selected target frame times '
                        'and all mode-0 output PCM input windows are checked '
                        'against installed DDB. Editor timing and listening are '
                        'not tested.')
    for field in ('target_work_items', 'target_positions', 'target_frame_reads',
                  'art_positions', 'full_loop_blocks', 'full_loop_art_pcm_matches',
                  'full_loop_sta_pcm_matches', 'nonzero_events'):
        result[field] = sum(row[field] for row in passed)
    args.evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps({key: result[key] for key in
                      ('candidates', 'passed', 'fixture_incomplete',
                       'target_positions', 'target_frame_reads')}))


if __name__ == '__main__':
    main()
