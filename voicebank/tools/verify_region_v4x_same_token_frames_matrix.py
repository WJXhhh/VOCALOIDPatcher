"""Check DDB frame reads for ART parts actually selected by repeated-token events."""
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
    parser.add_argument('--source-evidence', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--output-root', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--workers', type=int, default=2)
    parser.add_argument('--full-loop', action='store_true')
    args = parser.parse_args()
    assert 1 <= args.workers <= 4

    source_bytes = args.source_evidence.read_bytes()
    source = json.loads(source_bytes)
    assert source['scenarios'] == len(source['rows']) == 93
    script = Path(__file__).with_name('verify_region_v4x_index_frame_reads.py')

    def check(original):
        bank, token = original['bank'], original['token']
        reports = []
        for repeat in (1, 2):
            prefix = ('v4x_same_token_output' if args.full_loop else
                      'v4x_same_token_frames')
            output = args.output_root / (
                f'{prefix}_{bank}_{token.encode("ascii").hex()}_r{repeat}.json')
            command = [sys.executable, '-B', str(script), '--dll', str(args.dll),
                       '--bank', bank, '--first', token, '--second', token,
                       '--manifest', str(args.manifest), '--voice-root',
                       str(args.voice_root), '--ddb-tools', str(args.ddb_tools),
                       '--output', str(output)]
            if args.full_loop:
                command.extend(('--full-loop-limit', '36'))
            finished = subprocess.run(command, capture_output=True, text=True)
            assert finished.returncode == 0, (bank, token, finished.stderr[-1500:])
            reports.append(output.read_bytes())
        assert reports[0] == reports[1], (bank, token)
        result = json.loads(reports[0])
        assert result['commits'] == 36
        assert result['selected_transition_cases'] == original[
            'transition_materialized_cases']
        assert result['art_positions'] == original['checked_art_positions']
        assert result['art_work_items'] == sum(
            edge['work_items'] for edge in result['by_edge'].values())
        row = dict(bank=bank, token=token,
                    selected_transition_cases=result['selected_transition_cases'],
                    art_work_items=result['art_work_items'],
                    art_positions=result['art_positions'],
                    frame_reads=result['frame_reads'],
                    nonzero_pcm_windows=result['nonzero_pcm_windows'],
                    by_edge=result['by_edge'],
                    report_sha256=hashlib.sha256(reports[0]).hexdigest())
        if args.full_loop:
            loop = result['full_loop']
            assert loop['events'] == 36
            assert loop['pcm_observer_checks'] == loop['pcm_observer_matches'] == result['art_positions']
            assert loop['sta_pcm_checks'] == loop['sta_pcm_matches']
            assert all(not item['first_pcm_differences'] for item in loop['rows'])
            assert all(not item['sta_first_differences'] for item in loop['rows'])
            row['full_loop'] = dict(
                events=loop['events'], blocks=loop['blocks'],
                pcm_observer_checks=loop['pcm_observer_checks'],
                pcm_observer_matches=loop['pcm_observer_matches'],
                sta_pcm_checks=loop['sta_pcm_checks'],
                sta_pcm_matches=loop['sta_pcm_matches'],
                nonzero_blocks=loop['nonzero_blocks'],
                nonzero_events=sum(item['nonzero_blocks'] > 0
                                   for item in loop['rows']))
        return row

    rows = []
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        for row in pool.map(check, source['rows']):
            rows.append(row)
            if len(rows) % 20 == 0:
                print(json.dumps(dict(completed=len(rows))), flush=True)
    evidence = dict(source_evidence_sha256=hashlib.sha256(source_bytes).hexdigest(),
                    scenarios=len(rows), commits=len(rows) * 36,
                    selected_transition_cases=sum(row['selected_transition_cases']
                                                  for row in rows),
                    art_work_items=sum(row['art_work_items'] for row in rows),
                    art_positions=sum(row['art_positions'] for row in rows),
                    frame_reads=sum(row['frame_reads'] for row in rows),
                    nonzero_pcm_windows=sum(row['nonzero_pcm_windows'] for row in rows),
                    repeat_runs=2, rows=rows,
                    scope='Installed V4X DDI/DDB. Synthetic indexed repeated-token '
                          'events select entry and repeated-token ART according to '
                          'their event condition. Every distinct selected frame is '
                          'read through the native file loader and compared with DDB '
                          'FRM2 time and bounded SND PCM window. Full native output '
                          'loop and Editor timing are not verified.')
    if args.full_loop:
        evidence['full_loop_blocks'] = sum(row['full_loop']['blocks'] for row in rows)
        evidence['full_loop_pcm_checks'] = sum(
            row['full_loop']['pcm_observer_checks'] for row in rows)
        evidence['full_loop_pcm_matches'] = sum(
            row['full_loop']['pcm_observer_matches'] for row in rows)
        evidence['full_loop_sta_pcm_checks'] = sum(
            row['full_loop']['sta_pcm_checks'] for row in rows)
        evidence['full_loop_sta_pcm_matches'] = sum(
            row['full_loop']['sta_pcm_matches'] for row in rows)
        evidence['full_loop_nonzero_blocks'] = sum(
            row['full_loop']['nonzero_blocks'] for row in rows)
        evidence['full_loop_nonzero_events'] = sum(
            row['full_loop']['nonzero_events'] for row in rows)
        evidence['scope'] = ('Installed V4X DDI/DDB. Synthetic indexed '
                             'repeated-token events replay original mode-0 records '
                             'through the native output loop. Actual ART selections '
                             'are checked with forward-ceil and reverse-floor frame '
                             'indexes against DDB SND PCM windows. Installed STA '
                             'layers are supplied for events with stationary work; '
                             'their input PCM windows are separately compared with '
                             'DDB SND bytes. Editor timing and final listening are '
                             'not tested.')
    args.evidence.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps({key: evidence[key] for key in
                      ('scenarios', 'selected_transition_cases',
                       'art_work_items', 'art_positions', 'frame_reads')}))


if __name__ == '__main__':
    main()
