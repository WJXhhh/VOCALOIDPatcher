"""Bounded per-sample sweep of the host's sample-info coefficient multiplier."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import time


def run(args):
    assert 0 <= args.start < args.end <= 119
    assert 0 < args.timeout <= 30 and 0 < args.total_seconds <= 900
    factors = [1., .5, 1.5, 2.]
    args.work.mkdir(parents=True, exist_ok=True)
    begun = time.monotonic()
    rows = []
    for index in range(args.start, args.end):
        if time.monotonic() - begun >= args.total_seconds:
            break
        attempts = []
        for factor in factors:
            if time.monotonic() - begun >= args.total_seconds:
                break
            output = args.work / f'sample_{index}_factor_{factor}.json'
            if output.exists():
                raise FileExistsError(output)
            command = [sys.executable, '-B', str(Path(__file__).with_name('verify_region_dvqm_segments.py')),
                       '--audio', '--audio-start', str(index), '--audio-limit', '1',
                       '--audio-blocks', '4', '--audio-native-scalars',
                       '--audio-native-factor', str(factor), '--dll', str(args.dll),
                       '--directory', str(args.directory), '--output', str(output)]
            try:
                child = subprocess.run(command, capture_output=True, text=True, timeout=args.timeout)
                attempt = dict(factor=factor, status='exited', exit_code=child.returncode)
                if child.returncode == 0:
                    row = json.loads(output.read_text(encoding='utf-8'))
                    assert row['audio_contract']['start'] == index
                    assert len(row['audio']) == 1
                    attempt['audio'] = row['audio'][0]
                else:
                    attempt['stderr_tail'] = child.stderr[-300:]
            except subprocess.TimeoutExpired as error:
                # subprocess.run kills and reaps only this child on timeout.
                stdout = error.stdout or b''
                if isinstance(stdout, bytes):
                    stdout = stdout.decode(errors='replace')
                attempt = dict(factor=factor, status='timeout',
                               stdout_tail=stdout[-160:])
            attempts.append(attempt)
            if attempt.get('audio'):
                break
        rows.append(dict(index=index, attempts=attempts))
        result = dict(dse=str(args.dll.name), resource=str((args.directory / 'dvqm.dat').name),
                      factors=factors, start=args.start, end=args.end,
                      timeout_seconds=args.timeout, total_seconds=args.total_seconds,
                      complete=len(rows) == args.end-args.start, rows=rows,
                      scope='Four-block direct API fixture per independent process; a successful factor is not host parameter validation')
        args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
        print(index, [a['factor'] for a in attempts],
              'passed' if attempts and attempts[-1].get('audio') else 'unresolved', flush=True)
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ('dll', 'directory', 'work', 'output'):
        parser.add_argument('--' + option, type=Path, required=True)
    parser.add_argument('--start', type=int, default=0)
    parser.add_argument('--end', type=int, default=119)
    parser.add_argument('--timeout', type=float, default=3.)
    parser.add_argument('--total-seconds', type=float, default=300.)
    args = parser.parse_args()
    result = run(args)
    print('complete', result['complete'], 'cases', len(result['rows']))
