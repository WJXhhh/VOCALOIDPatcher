"""Confirm installed triple ART stage-0 source positions and DDB frame reads."""
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
    args = parser.parse_args()
    assert 1 <= args.workers <= 4
    source_bytes = args.inventory.read_bytes()
    source = json.loads(source_bytes)
    assert source['triple_keys'] == len(source['rows']) == 994
    script = Path(__file__).with_name('verify_region_v4x_index_frame_reads.py')

    def check(original):
        bank, first, second, third = (original[key] for key in
                                      ('bank', 'first', 'second', 'third'))
        shape = ''.join('1' if value else '0'
                        for value in original['pair_edges_available'])
        split = 1 if shape == '101' else 2
        stem = 'v4x_triple_stage_positions_' + bank + '_' + '_'.join(
            token.encode('ascii').hex() for token in (first, second, third))
        output = args.output_root / f'{stem}.json'
        command = [sys.executable, '-B', str(script), '--dll', str(args.dll),
                   '--bank', bank, '--first', first, '--second', second,
                   '--third', third, '--note-split', str(split),
                   '--manifest', str(args.manifest), '--voice-root',
                   str(args.voice_root), '--ddb-tools', str(args.ddb_tools),
                   '--output', str(output)]
        finished = subprocess.run(command, capture_output=True, text=True)
        if finished.returncode:
            return dict(bank=bank, first=first, second=second, third=third,
                        status='fixture_incomplete', message=(
                            finished.stderr.strip().splitlines() or ['no error message'])[-1])
        report_bytes = output.read_bytes()
        result = json.loads(report_bytes)
        triple_key = '->'.join(original['triple_key'])
        triple = result['by_edge'][triple_key]
        positions = triple['stage_positions']
        reads = triple['stage_frame_reads']
        assert result['commits'] == 36 and result['note_split'] == split
        assert positions['0'] > 0 and positions['1'] > 0
        assert reads['0'] > 0 and reads['1'] > 0
        assert positions['0'] + positions['1'] == triple['positions']
        assert reads['0'] + reads['1'] == triple['frame_reads']
        return dict(bank=bank, first=first, second=second, third=third,
                    note_split=split, status='passed',
                    stage_positions=positions, stage_frame_reads=reads,
                    report_sha256=hashlib.sha256(report_bytes).hexdigest())

    rows = []
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        for row in pool.map(check, source['rows']):
            rows.append(row)
            if len(rows) % 100 == 0:
                print(json.dumps(dict(completed=len(rows),
                                      passed=sum(item['status'] == 'passed'
                                                 for item in rows))), flush=True)
    passed = [row for row in rows if row['status'] == 'passed']
    result = dict(inventory_sha256=hashlib.sha256(source_bytes).hexdigest(),
                  candidates=len(rows), passed=len(passed),
                  fixture_incomplete=len(rows) - len(passed),
                  stage_positions={str(stage): sum(row['stage_positions'][str(stage)]
                                                   for row in passed)
                                   for stage in (0, 1)},
                  stage_frame_reads={str(stage): sum(row['stage_frame_reads'][str(stage)]
                                                     for row in passed)
                                     for stage in (0, 1)},
                  rows=rows,
                  scope='All installed triple ART keys. Each selected stage-0 and '
                        'stage-1 source position is tied to DDI alignment. The '
                        'native file loader checks the selected frame time and '
                        'bounded PCM window against installed DDB. Adjacent owned '
                        'note events use the path-appropriate split; Editor '
                        'timing and listening are not tested.')
    args.evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps({key: result[key] for key in
                      ('candidates', 'passed', 'fixture_incomplete',
                       'stage_positions', 'stage_frame_reads')}))


if __name__ == '__main__':
    main()
