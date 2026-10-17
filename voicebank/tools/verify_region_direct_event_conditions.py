"""Compare direct-tree two-note boundary conditions using isolated native processes."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import itertools
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256


DELTAS = (-1, 0, 1)
CONTROLS = (0, 1, 32, 64, 127)
GAPS = (0, 20, 21)


def probe(dll: Path, workdir: Path, condition: tuple[int, int, int, int, bool]) -> dict:
    submode, delta, control, gap, self_edge = condition
    stem = f'sub{submode}_delta{delta:+d}_control{control}_gap{gap}_edge{int(self_edge)}'
    detail = workdir / f'{stem}.json'
    command = [sys.executable, '-B', str(Path(__file__).with_name(
        'verify_region_direct_continuation.py').resolve()),
        '--dll', str(dll), '--second-control3', str(control),
        '--gap', str(gap), '--ring-submode', str(submode), '--pitch-delta', str(delta),
        '--output', str(detail)]
    if self_edge:
        command.append('--self-edge')
    finished = subprocess.run(command, capture_output=True, text=True)
    assert finished.returncode == 0, (stem, finished.stderr[-1500:])
    report = json.loads(detail.read_text(encoding='utf-8'))
    assert report['sequences'] == 36
    shapes = sorted({json.dumps(dict(
        parts=row['commit']['followup']['material_indices'],
        ranges=row['commit']['followup']['ranges'],
        duration=row['commit']['followup']['second_duration']), sort_keys=True)
        for row in report['cases']})
    assert len(shapes) == 1, (stem, shapes)
    for row in report['cases']:
        row['commit']['followup'].pop('pitch_delta', None)
        row['commit']['followup'].pop('ring_submode_before', None)
        row['commit']['followup'].pop('ring_submode_after', None)
    cases_hash = hashlib.sha256(json.dumps(report['cases'], sort_keys=True).encode()).hexdigest()
    return dict(submode=submode, pitch_delta=delta, control3=control, gap=gap,
                self_edge=self_edge, sequences=report['sequences'],
                shape=json.loads(shapes[0]), cases_sha256=cases_hash)


def verify(dll: Path, workdir: Path, workers: int) -> dict:
    workdir.mkdir(parents=True, exist_ok=True)
    conditions = list(itertools.product((0, 1), DELTAS, CONTROLS, GAPS, (False, True)))
    with ThreadPoolExecutor(max_workers=workers) as pool:
        results = list(pool.map(lambda item: probe(dll, workdir, item), conditions))
    by_condition = {(row['submode'], row['pitch_delta'], row['control3'], row['gap'],
                     row['self_edge']): row for row in results}
    edge_differences = []
    for submode, delta, control, gap in itertools.product((0, 1), DELTAS, CONTROLS, GAPS):
        absent = by_condition[submode, delta, control, gap, False]
        present = by_condition[submode, delta, control, gap, True]
        if absent['shape'] != present['shape'] or absent['cases_sha256'] != present['cases_sha256']:
            edge_differences.append([submode, delta, control, gap])
    submode_differences = []
    for delta, control, gap, edge in itertools.product(DELTAS, CONTROLS, GAPS, (False, True)):
        zero = by_condition[0, delta, control, gap, edge]
        one = by_condition[1, delta, control, gap, edge]
        if zero['shape'] != one['shape'] or zero['cases_sha256'] != one['cases_sha256']:
            submode_differences.append([delta, control, gap, edge])
    return dict(dse_sha256=SHA256, scenarios=len(results), sequences=sum(
        row['sequences'] for row in results), native_commits=2*sum(
        row['sequences'] for row in results), edge_differences=edge_differences,
        submode_differences=submode_differences, results=results,
        scope='Owned two-note direct-tree fixture. Pitch, note parameter 3, timing, '
              'ring branch and a→a material are varied; the Editor host and acoustic '
              'output are not included.')


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
    print(json.dumps(dict(scenarios=report['scenarios'], sequences=report['sequences'],
                          edge_differences=len(report['edge_differences']),
                          submode_differences=len(report['submode_differences']))))
