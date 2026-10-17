"""Compare onset ART descriptor state before and after a second owned note."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import itertools
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256


def probe(dll: Path, workdir: Path, condition: tuple[bool, bool, int, int, int]) -> dict:
    onset, self_edge, submode, control, delta = condition
    stem = f'onset{int(onset)}_self{int(self_edge)}_sub{submode}_control{control}_delta{delta}'
    detail = workdir / f'{stem}.json'
    command = [sys.executable, '-B', str(Path(__file__).with_name(
        'verify_region_direct_continuation.py').resolve()),
        '--dll', str(dll), '--second-control3', str(control), '--gap', '21',
        '--ring-submode', str(submode), '--pitch-delta', str(delta),
        '--inspect-global', '--output', str(detail)]
    if not onset:
        command.append('--no-onset-edge')
    if self_edge:
        command.append('--self-edge')
    finished = subprocess.run(command, capture_output=True, text=True)
    assert finished.returncode == 0, (stem, finished.stderr[-1500:])
    report = json.loads(detail.read_text(encoding='utf-8'))
    assert report['sequences'] == 36
    snapshots = []
    for row in report['cases']:
        followup = row['commit']['followup']
        before = followup['global_descriptor_before']
        after = followup['global_descriptor_after']
        assert before == after, (stem, before, after)
        assert all(part == 0 for part in followup['material_indices'])
        assert before == (dict(count=1, left='Sil', right='a', unit_edge='Sil→a', part_index=1)
                          if onset else dict(count=0, left='', right='',
                                             unit_edge=None, part_index=-1)), (stem, before)
        snapshots.append(json.dumps(dict(descriptor=before,
                                         ranges=followup['ranges'],
                                         final_parts=followup['material_indices']), sort_keys=True))
    assert len(set(snapshots)) == 1, (stem, set(snapshots))
    digest = hashlib.sha256(json.dumps(report['cases'], sort_keys=True).encode()).hexdigest()
    return dict(onset_edge=onset, self_edge=self_edge, submode=submode,
                control3=control, pitch_delta=delta, sequences=report['sequences'],
                result=json.loads(snapshots[0]), cases_sha256=digest)


def verify(dll: Path, workdir: Path, workers: int) -> dict:
    workdir.mkdir(parents=True, exist_ok=True)
    conditions = [(onset, self_edge, submode, control, delta)
                  for onset, self_edge, submode, control, delta in itertools.product(
                      (False, True), (False, True), (0, 1), (0, 64), (0, 1))
                  if onset or submode == 0]
    with ThreadPoolExecutor(max_workers=workers) as pool:
        results = list(pool.map(lambda item: probe(dll, workdir, item), conditions))
    for self_edge, control, delta in itertools.product(
            (False, True), (0, 64), (0, 1)):
        submode = 0
        absent = next(row for row in results if (row['onset_edge'], row['self_edge'],
            row['submode'], row['control3'], row['pitch_delta']) ==
            (False, self_edge, submode, control, delta))
        present = next(row for row in results if (row['onset_edge'], row['self_edge'],
            row['submode'], row['control3'], row['pitch_delta']) ==
            (True, self_edge, submode, control, delta))
        assert absent['result']['ranges'] == present['result']['ranges']
        assert absent['result']['final_parts'] == present['result']['final_parts']
    return dict(dse_sha256=SHA256, scenarios=len(results),
                sequences=sum(row['sequences'] for row in results),
                native_commits=2*sum(row['sequences'] for row in results),
                results=results,
                scope='Owned direct-tree first/second note events. Native global candidate '
                      'descriptor is read after each commit. Missing onset ART with submode 1 '
                      'is excluded because this fixture does not satisfy its candidate contract; '
                      'no Editor process or acoustic output.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--workdir', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--workers', type=int, default=4)
    args = parser.parse_args()
    assert 1 <= args.workers <= 8
    report = verify(args.dll, args.workdir, args.workers)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(scenarios=report['scenarios'], sequences=report['sequences'])))
