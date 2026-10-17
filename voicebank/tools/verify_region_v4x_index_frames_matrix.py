"""Repeat installed DDB frame checks for both ART edges in indexed events."""
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
    parser.add_argument('--selector', action='store_true')
    parser.add_argument('--full-loop-limit', type=int, default=0)
    parser.add_argument('--workers', type=int, default=2)
    args = parser.parse_args()
    assert 1 <= args.workers <= 4
    assert 0 <= args.full_loop_limit <= 36
    assert not (args.selector and args.full_loop_limit)

    source_bytes = args.source_evidence.read_bytes()
    source = json.loads(source_bytes)
    assert source['scenarios'] == len(source['rows']) == 232
    script = Path(__file__).with_name('verify_region_v4x_index_frame_reads.py')
    def check(original):
        bank, first, second = (original[key]
                               for key in ('bank', 'first', 'following'))
        reports = []
        for repeat in (1, 2):
            key = first.encode('ascii').hex()
            prefix = ('v4x_index_selector' if args.selector else
                      'v4x_index_full_output' if args.full_loop_limit else
                      'v4x_index_frames')
            output = args.output_root / f'{prefix}_{bank}_{key}_{second}_r{repeat}.json'
            command = [sys.executable, '-B', str(script), '--dll', str(args.dll),
                       '--bank', bank, '--first', first, '--second', second,
                       '--manifest', str(args.manifest), '--voice-root',
                       str(args.voice_root), '--ddb-tools', str(args.ddb_tools),
                       '--output', str(output)]
            if args.selector:
                command.append('--selector')
            if args.full_loop_limit:
                command.extend(('--full-loop-limit', str(args.full_loop_limit)))
            finished = subprocess.run(command, capture_output=True, text=True)
            assert finished.returncode == 0, (bank, first, second, finished.stderr[-2000:])
            reports.append(output.read_bytes())
        assert reports[0] == reports[1], (bank, first, second)
        result = json.loads(reports[0])
        assert result['commits'] == 36 and result['art_work_items'] == 72
        assert result['art_positions'] == original['indexed_summary']['checked_art_positions']
        assert set(result['by_edge']) == {f'Sil->{first}', f'{first}->{second}'}
        row = dict(bank=bank, first=first, following=second,
                   art_positions=result['art_positions'],
                   frame_reads=result['frame_reads'],
                   nonzero_pcm_windows=result['nonzero_pcm_windows'],
                   by_edge=result['by_edge'],
                   report_sha256=hashlib.sha256(reports[0]).hexdigest())
        if args.selector:
            assert result['selector_records'] == result['art_positions']
            assert sum(result['selector_output_masks'].values()) == result['selector_records']
            row.update(selector_records=result['selector_records'],
                       selector_nonzero_pcm_windows=result['selector_nonzero_pcm_windows'],
                       selector_output_masks=result['selector_output_masks'])
        if args.full_loop_limit:
            loop = result['full_loop']
            assert loop['events'] == args.full_loop_limit
            assert loop['pcm_observer_checks'] == loop['pcm_observer_matches']
            if args.full_loop_limit == 36:
                assert loop['pcm_observer_checks'] == row['art_positions']
            assert sum(item['pcm_observer_checks'] for item in loop['rows']) == loop['pcm_observer_checks']
            row['full_loop'] = dict(
                events=loop['events'], blocks=loop['blocks'],
                pcm_observer_checks=loop['pcm_observer_checks'],
                pcm_observer_matches=loop['pcm_observer_matches'],
                nonzero_blocks=loop['nonzero_blocks'],
                analysis_nonzero_blocks=loop['analysis_nonzero_blocks'],
                stream_hashes_sha256=hashlib.sha256(''.join(
                    item['stream_sha256'] for item in loop['rows']).encode()).hexdigest())
        return row

    rows = []
    if args.selector or args.full_loop_limit:
        with ThreadPoolExecutor(max_workers=args.workers) as pool:
            for row in pool.map(check, source['rows']):
                rows.append(row)
                if len(rows) % 29 == 0:
                    print(json.dumps(dict(completed=len(rows))), flush=True)
    else:
        for original in source['rows']:
            rows.append(check(original))
            if len(rows) % 29 == 0:
                print(json.dumps(dict(completed=len(rows))), flush=True)

    evidence = dict(dse_sha256=source['dse_sha256'],
                    source_evidence_sha256=hashlib.sha256(source_bytes).hexdigest(),
                    scenarios=len(rows), commits=len(rows) * 36,
                    art_work_items=len(rows) * 72,
                    art_positions=sum(row['art_positions'] for row in rows),
                    frame_reads=sum(row['frame_reads'] for row in rows),
                    nonzero_pcm_windows=sum(row['nonzero_pcm_windows'] for row in rows),
                    repeat_runs=2, rows=rows,
                    scope='Installed V4X DDI/DDB read-only. Synthetic indexed '
                          'two-phoneme events select both ART edges; frame indexes are '
                          'derived from source positions by ceil and layer-end clamp, '
                          'then read through the native mode-0 file loader and checked '
                          'against DDB time and PCM windows. Complete output selector, '
                          'Editor timing, and final audio output are not tested.')
    if args.selector:
        evidence['selector_records'] = sum(row['selector_records'] for row in rows)
        evidence['selector_nonzero_pcm_windows'] = sum(
            row['selector_nonzero_pcm_windows'] for row in rows)
        evidence['scope'] = ('Installed V4X DDI/DDB read-only. Native indexed ART work '
                             'items enter the frame selector with its no-layer-blend '
                             'event byte set to 1 and restored after each item. Every '
                             'source-position PCM window is checked against DDB SND '
                             'bytes. Original event-mode full output and Editor timing '
                             'remain untested.')
    if args.full_loop_limit:
        evidence['full_loop_limit_per_scenario'] = args.full_loop_limit
        evidence['full_loop_events'] = sum(row['full_loop']['events'] for row in rows)
        evidence['full_loop_blocks'] = sum(row['full_loop']['blocks'] for row in rows)
        evidence['full_loop_pcm_checks'] = sum(
            row['full_loop']['pcm_observer_checks'] for row in rows)
        evidence['full_loop_pcm_matches'] = sum(
            row['full_loop']['pcm_observer_matches'] for row in rows)
        evidence['full_loop_nonzero_blocks'] = sum(
            row['full_loop']['nonzero_blocks'] for row in rows)
        evidence['full_loop_analysis_nonzero_blocks'] = sum(
            row['full_loop']['analysis_nonzero_blocks'] for row in rows)
        evidence['scope'] = ('Installed V4X DDI/DDB read-only. Synthetic indexed '
                             'two-phoneme events replay original mode-0 records through '
                             'the native output loop; ART-position PCM input windows '
                             'are compared with DDB SND bytes. Editor timing and final '
                             'listening remain untested.')
    args.evidence.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps(dict(scenarios=evidence['scenarios'],
                          commits=evidence['commits'],
                          art_positions=evidence['art_positions'],
                          frame_reads=evidence['frame_reads'])))


if __name__ == '__main__':
    main()
