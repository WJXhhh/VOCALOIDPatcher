"""Check indexed V4X Silence-ending events with installed frame and PCM data."""
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
    assert 1 <= args.workers <= 4 and args.limit >= 0

    source_bytes = args.inventory.read_bytes()
    source = json.loads(source_bytes)
    assert source['exits'] == len(source['rows']) == 352
    candidates = source['rows'][:args.limit] if args.limit else source['rows']
    script = Path(__file__).with_name('verify_region_v4x_index_frame_reads.py')

    def check(original):
        bank, first = original['bank'], original['first']
        reports = []
        errors = []
        for repeat in (1, 2):
            output = args.output_root / (
                f'v4x_silence_exit_{bank}_{first.encode("ascii").hex()}_r{repeat}.json')
            command = [sys.executable, '-B', str(script), '--dll', str(args.dll),
                       '--bank', bank, '--first', first, '--second', 'Sil',
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
                bank, first, errors)
            return dict(bank=bank, first=first,
                        first_has_sta=original['first_has_sta'],
                        status='fixture_incomplete', error=errors[0])
        assert reports[0] == reports[1], (bank, first)
        result = json.loads(reports[0])
        loop = result['full_loop']
        assert result['commits'] == loop['events'] == 36
        assert result['by_edge'][f'Sil->{first}']['work_items'] == 36
        assert result['by_edge'][f'{first}->Sil']['work_items'] == result[
            'selected_transition_cases']
        assert loop['pcm_observer_checks'] == loop['pcm_observer_matches'] == result['art_positions']
        assert all(not item['first_pcm_differences'] for item in loop['rows'])
        if original['first_has_sta']:
            assert loop['sta_pcm_checks'] == loop['sta_pcm_matches']
            assert all(not item['sta_first_differences'] for item in loop['rows'])
        else:
            assert 'sta_pcm_checks' not in loop
        return dict(bank=bank, first=first,
                    first_has_sta=original['first_has_sta'], status='passed',
                    selected_transition_cases=result['selected_transition_cases'],
                    art_work_items=result['art_work_items'],
                    art_positions=result['art_positions'],
                    frame_reads=result['frame_reads'],
                    nonzero_pcm_windows=result['nonzero_pcm_windows'],
                    full_loop_blocks=loop['blocks'],
                    full_loop_art_pcm_matches=loop['pcm_observer_matches'],
                    full_loop_sta_pcm_matches=loop.get('sta_pcm_matches', 0),
                    nonzero_events=sum(item['nonzero_blocks'] > 0 for item in loop['rows']),
                    by_edge=result['by_edge'],
                    report_sha256=hashlib.sha256(reports[0]).hexdigest())

    rows = []
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        for row in pool.map(check, candidates):
            rows.append(row)
            if len(rows) % 44 == 0:
                print(json.dumps(dict(completed=len(rows),
                                      passed=sum(item['status'] == 'passed'
                                                 for item in rows))), flush=True)
    passed = [row for row in rows if row['status'] == 'passed']
    failed = [row for row in rows if row['status'] != 'passed']
    evidence = dict(inventory_sha256=hashlib.sha256(source_bytes).hexdigest(),
                    candidates=len(rows), passed=len(passed),
                    fixture_incomplete=len(failed),
                    commits=len(passed) * 36,
                    selected_transition_cases=sum(
                        row['selected_transition_cases'] for row in passed),
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
                    scope='Installed V4X DDI/DDB. Synthetic indexed two-phoneme '
                          'Silence-ending events with both ART edges are replayed '
                          'through the native mode-0 output loop. Actual selected '
                          'ART and STA input PCM windows are compared with DDB SND '
                          'bytes. Fixture exceptions are retained separately. '
                          'Editor timing and final listening are not tested.')
    args.evidence.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps({key: evidence[key] for key in
                      ('candidates', 'passed', 'fixture_incomplete',
                       'selected_transition_cases', 'art_positions')}))


if __name__ == '__main__':
    main()
