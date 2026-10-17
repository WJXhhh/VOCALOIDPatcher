"""Check V4X consonant-to-vowel ART material in owned indexed events."""
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
            key = first.encode('ascii').hex()
            output = args.output_root / f'v4x_index_transition_{bank}_{key}_{second}_r{repeat}.json'
            command = [sys.executable, '-B', str(script), '--dll', str(args.dll),
                       '--bank', bank, '--first', first, '--second', second,
                       '--manifest', str(args.manifest), '--voice-root',
                       str(args.voice_root), '--ddb-tools', str(args.ddb_tools),
                       '--mode', '0', '--offset', '0', '--output', str(output)]
            finished = subprocess.run(command, capture_output=True, text=True)
            assert finished.returncode == 0, (bank, first, second, finished.stderr[-2000:])
            reports.append(output.read_bytes())
        assert reports[0] == reports[1], (bank, first, second)
        result = json.loads(reports[0])
        assert result['mode'] == 0 and result['commits'] == 36
        rows.append(dict(bank=bank, first=first, following=second,
                         indexed_summary=result['indexed_summary'],
                         by_pitch=result['by_pitch'],
                         report_sha256=hashlib.sha256(reports[0]).hexdigest()))
        if len(rows) % 29 == 0:
            print(json.dumps(dict(completed=len(rows))), flush=True)

    evidence = dict(dse_sha256=result['dse_sha256'],
                    source_evidence_sha256=hashlib.sha256(source_bytes).hexdigest(),
                    scenarios=len(rows), commits=len(rows) * 36,
                    transition_materialized_cases=sum(
                        row['indexed_summary']['transition_materialized_cases']
                        for row in rows),
                    checked_art_positions=sum(row['indexed_summary']['checked_art_positions']
                                              for row in rows),
                    max_position_error=max(row['indexed_summary']['max_position_error']
                                           for row in rows),
                    repeat_runs=2, rows=rows,
                    scope='Installed V4X DDI layer metadata in synthetic indexed '
                          'two-phoneme events. Both Silence-to-consonant and consonant-to-vowel '
                          'ART are available; selected work items and source positions checked '
                          'against an independent alignment model. No DDB payload, Editor '
                          'timing, or final audio output.')
    args.evidence.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps(dict(scenarios=evidence['scenarios'],
                          commits=evidence['commits'],
                          transition_materialized_cases=evidence['transition_materialized_cases'],
                          checked_art_positions=evidence['checked_art_positions'])))


if __name__ == '__main__':
    main()
