"""Check which installed pair ART routes are selected in indexed V4X events."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import subprocess
import sys


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--inventory', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--output-root', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--workers', type=int, default=3)
    parser.add_argument('--entry-edges', type=int, choices=(1, 2), required=True)
    parser.add_argument('--finish-edges', type=int, choices=(0, 1), required=True)
    parser.add_argument('--note-split', type=int, choices=(1, 2))
    parser.add_argument('--offset', type=int, default=0)
    parser.add_argument('--limit', type=int, default=0)
    args = parser.parse_args()
    assert 1 <= args.workers <= 4 and args.offset >= 0 and args.limit >= 0

    source_bytes = args.inventory.read_bytes()
    source = json.loads(source_bytes)
    assert source['pair_keys'] == len(source['rows']) == 4680
    routes = [row for row in source['rows'] if row['category'] == 'pair_route'
              and len(row['entry_path']) - 1 == args.entry_edges
              and len(row['pair_finish']) - 1 == args.finish_edges]
    candidates = routes[args.offset:args.offset + args.limit if args.limit else None]
    script = Path(__file__).with_name('verify_region_v4x_index_frame_reads.py')

    def check(original):
        bank, left, right = (original[key] for key in ('bank', 'left', 'right'))
        tokens = original['entry_path'][1:] + [right] + original['pair_finish'][1:]
        assert 3 <= len(tokens) <= 4 and tokens[-1] == original['pair_finish'][-1]
        stem = ('v4x_pair_route_' + (f'split{args.note_split}_' if args.note_split else '') +
                bank + '_' + left.encode('ascii').hex() +
                '_' + right.encode('ascii').hex())
        reports = []
        errors = []
        for repeat in (1, 2):
            output = args.output_root / f'{stem}_r{repeat}.json'
            command = [sys.executable, '-B', str(script), '--dll', str(args.dll),
                       '--bank', bank, '--pair-only', '--target-left', left,
                       '--target-right', right, '--manifest', str(args.manifest),
                       '--voice-root', str(args.voice_root), '--ddb-tools',
                       str(args.ddb_tools), '--full-loop-limit', '36',
                       '--output', str(output)]
            if args.note_split is not None:
                command.extend(('--note-split', str(args.note_split)))
            for name, token in zip(('first', 'second', 'third', 'fourth'), tokens):
                command.extend(('--' + name, token))
            finished = subprocess.run(command, capture_output=True, text=True)
            if finished.returncode:
                errors.append(dict(code=finished.returncode,
                                   message=(finished.stderr.strip().splitlines() or
                                            ['no error message'])[-1]))
            else:
                reports.append(output.read_bytes())
        if errors:
            assert len(errors) == 2 and errors[0] == errors[1], (
                bank, left, right, errors)
            return dict(bank=bank, left=left, right=right, tokens=tokens,
                        status='fixture_incomplete', error=errors[0])
        assert reports[0] == reports[1], (bank, left, right)
        result = json.loads(reports[0])
        loop = result['full_loop']
        target_name = f'{left}->{right}'
        assert result['commits'] == loop['events'] == 36
        assert result['by_edge'][target_name]['work_items'] >= result[
            'selected_transition_cases']
        assert loop['pcm_observer_checks'] == loop['pcm_observer_matches'] == result['art_positions']
        assert loop['sta_pcm_checks'] == loop['sta_pcm_matches']
        assert all(not item['first_pcm_differences'] and
                   not item['sta_first_differences'] for item in loop['rows'])
        return dict(bank=bank, left=left, right=right, tokens=tokens,
                    status='passed', selected_target_cases=result['selected_transition_cases'],
                    target_positions=result['by_edge'][target_name]['positions'],
                    art_work_items=result['art_work_items'],
                    art_positions=result['art_positions'],
                    frame_reads=result['frame_reads'],
                    full_loop_blocks=loop['blocks'],
                    full_loop_art_pcm_matches=loop['pcm_observer_matches'],
                    full_loop_sta_pcm_matches=loop['sta_pcm_matches'],
                    nonzero_events=sum(item['nonzero_blocks'] > 0 for item in loop['rows']),
                    by_edge=result['by_edge'],
                    report_sha256=hashlib.sha256(reports[0]).hexdigest())

    rows = []
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        for row in pool.map(check, candidates):
            rows.append(row)
            if len(rows) % 50 == 0:
                print(json.dumps(dict(completed=len(rows),
                                      passed=sum(item['status'] == 'passed'
                                                 for item in rows),
                                      selected=sum(item['status'] == 'passed' and
                                                   item['selected_target_cases'] > 0
                                                   for item in rows))), flush=True)
    passed = [row for row in rows if row['status'] == 'passed']
    failed = [row for row in rows if row['status'] != 'passed']
    evidence = dict(inventory_sha256=hashlib.sha256(source_bytes).hexdigest(),
                    entry_edges=args.entry_edges, finish_edges=args.finish_edges,
                    note_split=args.note_split,
                    available_routes=len(routes), offset=args.offset,
                    candidates=len(rows), passed=len(passed),
                    fixture_incomplete=len(failed),
                    target_selected_routes=sum(row['selected_target_cases'] > 0
                                               for row in passed),
                    commits=len(passed) * 36,
                    selected_target_cases=sum(row['selected_target_cases'] for row in passed),
                    target_positions=sum(row['target_positions'] for row in passed),
                    art_work_items=sum(row['art_work_items'] for row in passed),
                    art_positions=sum(row['art_positions'] for row in passed),
                    frame_reads=sum(row['frame_reads'] for row in passed),
                    full_loop_blocks=sum(row['full_loop_blocks'] for row in passed),
                    full_loop_art_pcm_matches=sum(
                        row['full_loop_art_pcm_matches'] for row in passed),
                    full_loop_sta_pcm_matches=sum(
                        row['full_loop_sta_pcm_matches'] for row in passed),
                    nonzero_events=sum(row['nonzero_events'] for row in passed),
                    repeat_runs=2, rows=rows,
                    scope='Installed V4X DDI/DDB. Synthetic indexed events use '
                          'the shortest listed pair ART route to a final STA. '
                          'Only routes with selected target ART provide target '
                          'input-window evidence; other event PCM matches do '
                          'not prove target selection. Selected ART and final '
                          'STA PCM windows are compared with DDB. Editor timing '
                          'and listening are not tested.')
    args.evidence.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps({key: evidence[key] for key in
                      ('candidates', 'passed', 'fixture_incomplete',
                       'target_selected_routes', 'selected_target_cases')}))


if __name__ == '__main__':
    main()
