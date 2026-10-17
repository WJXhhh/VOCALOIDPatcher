"""Classify the later work item in all V4X consonant-onset test events."""
import argparse
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
    args = parser.parse_args()

    source_bytes = args.source_evidence.read_bytes()
    source = json.loads(source_bytes)
    assert source['scenarios'] == len(source['rows']) == 232
    script = Path(__file__).with_name('verify_region_v4x_two_phoneme_onset.py')
    rows = []
    for original in source['rows']:
        bank, first, second = (original[key]
                               for key in ('bank', 'first', 'following'))
        reports = []
        for repeat in (1, 2):
            name = f'v4x_followup_{bank}_{first.encode("ascii").hex()}_{second}_r{repeat}.json'
            output = args.output_root / name
            command = [sys.executable, '-B', str(script), '--dll', str(args.dll),
                       '--bank', bank, '--first', first, '--second', second,
                       '--manifest', str(args.manifest), '--voice-root',
                       str(args.voice_root), '--ddb-tools', str(args.ddb_tools),
                       '--offset', '400', '--output', str(output)]
            finished = subprocess.run(command, capture_output=True, text=True)
            assert finished.returncode == 0, (bank, first, second, finished.stderr[-2000:])
            reports.append(output.read_bytes())
        assert reports[0] == reports[1], (bank, first, second)
        result = json.loads(reports[0])
        assert result['commits'] == 36
        assert all(result['consistent_by_pitch'].values())
        rows.append(dict(bank=bank, first=first, following=second,
                         by_pitch=result['by_pitch'],
                         report_sha256=hashlib.sha256(reports[0]).hexdigest()))
        if len(rows) % 29 == 0:
            print(json.dumps(dict(completed=len(rows))), flush=True)

    later_null = sum(item['diagnostics'][1]['part_null']
                     for row in rows for item in row['by_pitch'].values()
                     if len(item['diagnostics']) > 1)
    later_observations = sum(len(item['diagnostics']) > 1
                             for row in rows for item in row['by_pitch'].values())
    evidence = dict(dse_sha256=result['dse_sha256'],
                    source_evidence_sha256=hashlib.sha256(source_bytes).hexdigest(),
                    scenarios=len(rows), commits=len(rows) * 36,
                    pitch_summaries=len(rows) * 3,
                    later_null_summaries=later_null,
                    later_observed_summaries=later_observations,
                    repeat_runs=2, rows=rows,
                    scope='Synthetic two-phoneme direct-tree events with installed V4X '
                          'DDI layer metadata. Classifies work-item part presence after '
                          'commit; no later material consumption or Editor timing.')
    args.evidence.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps(dict(scenarios=evidence['scenarios'],
                          commits=evidence['commits'],
                          later_null_summaries=later_null,
                          later_observed_summaries=later_observations)))


if __name__ == '__main__':
    main()
