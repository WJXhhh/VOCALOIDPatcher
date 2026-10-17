"""Compare complete DRS pitch branches on owned PCM, in bounded child processes.

This records native behavior, not independent F0 algorithm equivalence. Each
child owns its native allocations; Windows TEMP is not used for experiment files.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256
from probe_frm2_batch_outputs import run


def matrix():
    cases = []
    for mode in range(6):
        for automatic in (0, 1):
            for frequency in (110, 220, 440):
                cases.append(dict(group='mode', analysis_mode=mode,
                                  automatic=automatic, frequency=frequency))
    for frequency in (110, 220, 440):
        cases.append(dict(group='variant', analysis_mode=4, automatic=1,
                          frequency=frequency, estimator_variant=4))
    for seed in (75, 110, 220, 440):
        cases.append(dict(group='seed', analysis_mode=4, automatic=1,
                          frequency=220, pitch_seed=seed))
    return cases


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', required=True, type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--worker-json', help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.worker_json:
        case = json.loads(args.worker_json)
        options = {key: value for key, value in case.items()
                   if key in ('analysis_mode', 'estimator_variant', 'pitch_seed')}
        result = run(args.dll, case['automatic'], 0, 'steady',
                     case['frequency'], **options)
        trace = [row['f0'] for row in result.pop('rows')]
        result['pitch_trace_sha256'] = hashlib.sha256(
            json.dumps(trace, separators=(',', ':')).encode('ascii')).hexdigest()
        print(json.dumps(dict(case=case, result=result)))
        return
    if args.output is None:
        parser.error('--output is required outside worker mode')
    rows = []
    for case in matrix():
        child = subprocess.run(
            [sys.executable, str(Path(__file__).resolve()), '--dll',
             str(args.dll.resolve()), '--worker-json', json.dumps(case)],
            capture_output=True, text=True, timeout=30)
        if child.returncode:
            raise RuntimeError((case, child.returncode, child.stderr))
        rows.append(json.loads(child.stdout))
    # Determinism checks within this matrix: alternate seeds must be compared
    # against the matching mode4 baseline, not against an assumed correct pitch.
    reference = next(x['result'] for x in rows if x['case'] == dict(
        group='mode', analysis_mode=4, automatic=1, frequency=220))
    seed_equal = all(x['result'] == reference for x in rows if x['case']['group'] == 'seed')
    pitch_equal = all(x['result']['pitch_trace_sha256'] == reference['pitch_trace_sha256']
                      for x in rows if x['case']['group'] == 'seed')
    report = dict(dll_sha256=SHA256, cases=rows,
                  frames=sum(x['result']['frames'] for x in rows),
                  seed_sweep_aggregate_equal=seed_equal,
                  seed_sweep_pitch_trace_equal=pitch_equal,
                  scope='Native full batch observations; steady owned five-harmonic PCM; '
                        'mode is config+558, variant is config+5c4, seed is dynamic0d. '
                        'No independent F0 equivalence or complete production proof.')
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(cases=len(rows), frames=report['frames'],
                          seed_sweep_aggregate_equal=seed_equal,
                          seed_sweep_pitch_trace_equal=pitch_equal)))


if __name__ == '__main__':
    main()
