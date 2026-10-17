"""Classify repeated-token indexed events against installed V4X ART layers."""
import argparse
from collections import Counter
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
    parser.add_argument('--workers', type=int, default=2)
    args = parser.parse_args()
    assert 1 <= args.workers <= 4

    source_bytes = args.inventory.read_bytes()
    source = json.loads(source_bytes)
    candidates = [row for row in source['rows'] if row['same_token']]
    assert len(candidates) == source['same_token'] == 93
    script = Path(__file__).with_name('verify_region_v4x_two_phoneme_onset.py')

    def check(original):
        bank, first = original['bank'], original['first']
        assert first == original['following']
        reports = []
        for repeat in (1, 2):
            output = args.output_root / (
                f'v4x_same_token_{bank}_{first.encode("ascii").hex()}_r{repeat}.json')
            command = [sys.executable, '-B', str(script), '--dll', str(args.dll),
                       '--bank', bank, '--first', first, '--second', first,
                       '--manifest', str(args.manifest), '--voice-root',
                       str(args.voice_root), '--ddb-tools', str(args.ddb_tools),
                       '--mode', '0', '--offset', '0', '--output', str(output)]
            command.append('--include-cases')
            finished = subprocess.run(command, capture_output=True, text=True)
            assert finished.returncode == 0, (bank, first, finished.stderr[-1500:])
            reports.append(output.read_bytes())
        assert reports[0] == reports[1], (bank, first)
        result = json.loads(reports[0])
        assert result['mode'] == 0 and result['commits'] == 36
        cases = result['indexed_cases']
        assert len(cases) == 36
        transition_cases = sum(bool(case['transition_items']) for case in cases)
        assert transition_cases == result['indexed_summary'][
            'transition_materialized_cases']
        selected = {pitch: dict(Counter(
            json.dumps(case['material_indices']) for case in cases
            if str(case['pitch']) == pitch))
            for pitch in result['by_pitch']}
        selected_transition = {pitch: sum(bool(case['transition_items'])
                                          for case in cases
                                          if str(case['pitch']) == pitch)
                               for pitch in result['by_pitch']}
        return dict(bank=bank, token=first,
                    entry_layers=result['edge_layer_counts'][f'Sil->{first}'],
                    repeat_layers=result['edge_layer_counts'][f'{first}->{first}'],
                    transition_materialized_cases=result['indexed_summary'][
                        'transition_materialized_cases'],
                    checked_art_positions=result['indexed_summary']['checked_art_positions'],
                    selected_part_indices_by_pitch=selected,
                    transition_cases_by_pitch=selected_transition,
                    report_sha256=hashlib.sha256(reports[0]).hexdigest())

    rows = []
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        for row in pool.map(check, candidates):
            rows.append(row)
            if len(rows) % 20 == 0:
                print(json.dumps(dict(completed=len(rows))), flush=True)
    evidence = dict(inventory_sha256=hashlib.sha256(source_bytes).hexdigest(),
                    scenarios=len(rows), commits=len(rows) * 36,
                    transition_materialized_cases=sum(
                        row['transition_materialized_cases'] for row in rows),
                    checked_art_positions=sum(row['checked_art_positions'] for row in rows),
                    repeat_runs=2, rows=rows,
                    scope='Installed V4X DDI layer metadata in synthetic indexed '
                          'two-phoneme same-token events. Work-item part selection and '
                          'source-position model are checked; DDB audio, Editor timing '
                          'and final output are not tested.')
    args.evidence.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps(dict(scenarios=evidence['scenarios'],
                          transition_materialized_cases=evidence[
                              'transition_materialized_cases'])))


if __name__ == '__main__':
    main()
