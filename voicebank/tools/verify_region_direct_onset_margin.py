"""Measure direct-tree onset ART work across a bounded note-start margin."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import itertools
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256


OFFSETS = (0, 1, 2, 9, 10, 20, 50, 99, 100, 199, 200, 201, 400)
CONTROLS = (1, 32, 64, 127)


def probe(dll: Path, workdir: Path, condition: tuple[bool, int, int, int]) -> dict:
    onset, submode, control, offset = condition
    stem = f'onset{int(onset)}_sub{submode}_control{control}_offset{offset}'
    detail = workdir / f'{stem}.json'
    command = [sys.executable, '-B', str(Path(__file__).with_name(
        'verify_region_direct_material_commit.py').resolve()),
        '--dll', str(dll), '--variant', '1' if onset else '0',
        '--tokens', 'a', '--control3', str(control),
        '--ring-submode', str(submode), '--note-start-offset', str(offset),
        '--inspect-allocation', '--output', str(detail)]
    finished = subprocess.run(command, capture_output=True, text=True)
    assert finished.returncode == 0, (stem, finished.stderr[-1500:])
    report = json.loads(detail.read_text(encoding='utf-8'))
    assert report['commits'] == 36
    shapes = []
    for row in report['cases']:
        commit = row['commit']
        diagnostics = commit['diagnostics']
        expected_allocation = [offset // 2, offset - offset // 2]
        assert diagnostics['allocated_sides'] == expected_allocation, (stem, diagnostics)
        if onset:
            assert diagnostics['candidate_count'] == 1
            assert diagnostics['selected_part'] == 1
            if offset:
                assert commit['material_indices'] == [1, 0]
                assert commit['item_ranges'] == [[0, offset], [offset, offset + 200]]
                assert len(diagnostics['art_positions']) == 1
                art = diagnostics['art_positions'][0]
                assert art['item'] == 0 and art['frames'] == offset
                assert art['left'] == offset // 2 and art['reverse'] == 0
                assert art['max_error'] < 1e-5, (stem, art)
            else:
                assert commit['material_indices'] == [0]
                assert commit['item_ranges'] == [[0, 200]]
                assert diagnostics['art_positions'] == []
        else:
            assert diagnostics['candidate_count'] == 0
            assert diagnostics['selected_part'] == -1
            assert commit['material_indices'] == [0]
            assert commit['item_ranges'] == [[0, 200]]
            assert diagnostics['art_positions'] == []
        shapes.append(json.dumps(dict(parts=commit['material_indices'],
                                      ranges=commit['item_ranges'],
                                      allocation=diagnostics['allocated_sides'],
                                      art_positions=diagnostics['art_positions']), sort_keys=True))
    assert len(set(shapes)) == 1, (stem, len(set(shapes)))
    digest = hashlib.sha256(json.dumps(report['cases'], sort_keys=True).encode()).hexdigest()
    return dict(onset_edge=onset, submode=submode, control3=control,
                note_start_offset=offset, commits=report['commits'],
                result=json.loads(shapes[0]), cases_sha256=digest)


def verify(dll: Path, workdir: Path, workers: int) -> dict:
    workdir.mkdir(parents=True, exist_ok=True)
    conditions = ([(True, submode, control, offset) for submode, control, offset
                   in itertools.product((0, 1), CONTROLS, OFFSETS)] +
                  [(False, 0, 64, offset) for offset in OFFSETS])
    with ThreadPoolExecutor(max_workers=workers) as pool:
        results = list(pool.map(lambda item: probe(dll, workdir, item), conditions))
    return dict(dse_sha256=SHA256, scenarios=len(results),
                commits=sum(row['commits'] for row in results),
                articulation_positions=36 * len(CONTROLS) * 2 * sum(OFFSETS),
                results=results,
                scope='Owned direct-tree event fixture. The native-produced note start is '
                      'adjusted after event production while final commit time stays at 100; '
                      'this isolates the onset margin and is not evidence of Editor timing. '
                      'Only self-generated material is used; no acoustic rendering.')


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
    print(json.dumps(dict(scenarios=report['scenarios'], commits=report['commits'],
                          articulation_positions=report['articulation_positions'])))
