"""Run audio probes with a process timeout; retain failures as evidence."""
import argparse
import json
from pathlib import Path
import subprocess
import sys


def run(args):
    args.work.mkdir(parents=True, exist_ok=True)
    rows = []
    for index in [0, 1]:
        child_output = args.work / f'sample_{index}.json'
        if child_output.exists():
            raise FileExistsError(child_output)
        command = [sys.executable, '-B', str(Path(__file__).with_name('verify_region_dvqm_segments.py')),
                   '--audio', '--audio-start', str(index), '--audio-blocks', '4',
                   '--audio-harmonics', str(args.harmonics),
                   '--dll', str(args.dll), '--directory', str(args.directory),
                   '--output', str(child_output)]
        if args.no_gate:
            command.append('--audio-no-gate')
        try:
            result = subprocess.run(command, capture_output=True, text=True, timeout=args.timeout)
            row = dict(sample_index=index, status='exited', exit_code=result.returncode,
                       stdout=result.stdout, stderr=result.stderr)
            if result.returncode == 0:
                row['audio'] = json.loads(child_output.read_text(encoding='utf-8'))['audio']
        except subprocess.TimeoutExpired as error:
            # subprocess.run kills and reaps the child on timeout.
            output = error.stdout or b''
            row = dict(sample_index=index, status='timeout', timeout_seconds=args.timeout,
                       stdout=output.decode(errors='replace') if isinstance(output, bytes) else output)
        rows.append(row)
        print(json.dumps({k: v for k, v in row.items() if k not in ['stdout', 'stderr', 'audio']}), flush=True)
    return dict(harmonics=args.harmonics, no_gate=args.no_gate, rows=rows, scope='Four-block probes on first two samples with process isolation; '
                'timeouts are unresolved failures, not evidence of valid synthesis or an engine bug')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ['dll', 'directory', 'work', 'output']:
        parser.add_argument('--' + option, type=Path, required=True)
    parser.add_argument('--harmonics', type=int, default=0)
    parser.add_argument('--no-gate', action='store_true')
    parser.add_argument('--timeout', type=float, default=20.)
    args = parser.parse_args()
    assert args.timeout > 0
    result = run(args)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
