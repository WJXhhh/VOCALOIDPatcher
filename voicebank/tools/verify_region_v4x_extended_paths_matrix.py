"""Check installed V4X two-phoneme paths beyond the first 232 onset examples."""
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
    parser.add_argument('--limit', type=int, default=0)
    args = parser.parse_args()
    assert 1 <= args.workers <= 4
    assert args.limit >= 0

    source_bytes = args.inventory.read_bytes()
    source = json.loads(source_bytes)
    candidates = [row for row in source['rows']
                  if not row['previously_tested'] and not row['same_token']]
    assert len(candidates) == source['new_paths'] - source['same_token'] == 1616
    if args.limit:
        candidates = candidates[:args.limit]
    script = Path(__file__).with_name('verify_region_v4x_index_frame_reads.py')

    def check(original):
        bank, first, second = (original[key]
                               for key in ('bank', 'first', 'following'))
        prefix = f'v4x_extended_{bank}_{first.encode("ascii").hex()}_{second.encode("ascii").hex()}'
        reports = []
        errors = []
        for repeat in (1, 2):
            output = args.output_root / f'{prefix}_r{repeat}.json'
            command = [sys.executable, '-B', str(script), '--dll', str(args.dll),
                       '--bank', bank, '--first', first, '--second', second,
                       '--manifest', str(args.manifest), '--voice-root',
                       str(args.voice_root), '--ddb-tools', str(args.ddb_tools),
                       '--full-loop-limit', '36', '--output', str(output)]
            finished = subprocess.run(command, capture_output=True, text=True)
            if finished.returncode:
                errors.append(dict(code=finished.returncode,
                                   message=(finished.stderr.strip().splitlines() or
                                            ['no error message'])[-1]))
            else:
                reports.append(output.read_bytes())
        if errors:
            assert len(errors) == 2 and errors[0] == errors[1], (
                bank, first, second, errors)
            return dict(bank=bank, first=first, following=second,
                        first_has_sta=original['first_has_sta'],
                        status='not_materialized_in_fixture',
                        error=errors[0])
        assert reports[0] == reports[1], (bank, first, second)
        result = json.loads(reports[0])
        loop = result['full_loop']
        assert result['commits'] == loop['events'] == 36
        assert result['art_work_items'] == 72
        assert loop['pcm_observer_checks'] == loop['pcm_observer_matches'] == result['art_positions']
        assert set(result['by_edge']) == {f'Sil->{first}', f'{first}->{second}'}
        assert all(not item['first_pcm_differences'] for item in loop['rows'])
        return dict(bank=bank, first=first, following=second,
                    first_has_sta=original['first_has_sta'], status='passed',
                    art_positions=result['art_positions'],
                    frame_reads=result['frame_reads'],
                    nonzero_pcm_windows=result['nonzero_pcm_windows'],
                    full_loop_blocks=loop['blocks'],
                    full_loop_nonzero_blocks=loop['nonzero_blocks'],
                    full_loop_pcm_matches=loop['pcm_observer_matches'],
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
                                                 for item in rows))), flush=True)
    passed = [row for row in rows if row['status'] == 'passed']
    failed = [row for row in rows if row['status'] != 'passed']
    evidence = dict(inventory_sha256=hashlib.sha256(source_bytes).hexdigest(),
                    candidates=len(rows), passed=len(passed),
                    not_materialized_in_fixture=len(failed),
                    commits=len(passed) * 36,
                    art_positions=sum(row['art_positions'] for row in passed),
                    full_loop_pcm_matches=sum(row['full_loop_pcm_matches']
                                              for row in passed),
                    full_loop_blocks=sum(row['full_loop_blocks'] for row in passed),
                    full_loop_nonzero_blocks=sum(row['full_loop_nonzero_blocks']
                                                 for row in passed),
                    nonzero_events=sum(row['nonzero_events'] for row in passed),
                    repeat_runs=2, rows=rows,
                    scope='Installed V4X DDI/DDB. New non-repeating paths with '
                          'Sil-to-first and first-to-second ART plus final STA are '
                          'tested in synthetic indexed mode-0 events. Every ART-position '
                          'PCM input window is compared with DDB SND bytes. Fixture '
                          'failures are retained separately; Editor timing and final '
                          'listening are not tested.')
    args.evidence.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps({key: evidence[key] for key in
                      ('candidates', 'passed', 'not_materialized_in_fixture',
                       'art_positions', 'full_loop_blocks')}))


if __name__ == '__main__':
    main()
