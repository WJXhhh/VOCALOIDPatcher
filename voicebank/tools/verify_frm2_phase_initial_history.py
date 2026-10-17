"""Measure phase-history dependence using controlled owned harmonic inputs."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import struct
import subprocess
import sys
from types import SimpleNamespace

from audit_frm2_numeric import SHA256, f32
from verify_frm2_phase_pipeline import run_case


PATTERNS = ('active', 'late_low', 'late_high', 'reactivated_low')


def case(dll, pattern, seed):
    frames = []
    for i in range(87):
        hz = [220. * (k + 1) for k in range(30)]
        if pattern == 'late_low' and i < 10:
            hz[0] = 0.
        if pattern == 'late_high' and i < 10:
            hz[20] = 0.
        if pattern == 'reactivated_low' and 5 <= i < 15:
            hz[0] = 0.
        frames.append(SimpleNamespace(
            frequencies_hz=hz, amplitudes=[-20.] * 30,
            phases_radians=[f32(2.9 * math.sin(k + i * .31)) for k in range(30)]))
    digests = []

    def capture(index, f0, xs, ys):
        payload = b''.join(struct.pack('<df', x, y) for x, y in zip(xs, ys))
        digests.append(dict(index=index, sha256=hashlib.sha256(payload).hexdigest()))

    result = run_case(dll, 'stable', 256, 0, 220, source_frames=frames,
                      source_controls=True, initial_delta=seed, phase_sink=capture)
    return dict(pattern=pattern, seed=seed, result=result, env_digests=digests)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', required=True, type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--case', choices=PATTERNS)
    parser.add_argument('--seed', type=float, default=0.)
    args = parser.parse_args()
    if args.case:
        print(json.dumps(case(args.dll, args.case, args.seed)))
        return
    if args.output is None:
        parser.error('--output required')
    rows, comparisons = [], []
    for pattern in PATTERNS:
        group = []
        for seed in (0., -12., 12.):
            child = subprocess.run(
                [sys.executable, __file__, '--dll', str(args.dll), '--case', pattern,
                 '--seed', str(seed)], capture_output=True, text=True, timeout=60)
            if child.returncode:
                raise RuntimeError(f'{pattern}/{seed}: {child.stderr[-5000:]}')
            group.append(json.loads(child.stdout))
        for row in group[1:]:
            different = [a['index'] for a, b in zip(group[0]['env_digests'], row['env_digests'])
                         if a != b]
            if len(row['env_digests']) != 87:
                raise AssertionError('Missing phase frames')
            if pattern == 'late_low':
                if not different or min(different) != 10:
                    raise AssertionError(('Expected late activation dependence', different))
            elif different:
                raise AssertionError(('Unexpected seed dependence', pattern, different))
            comparisons.append(dict(pattern=pattern, seed=row['seed'],
                                    changed_frames=len(different), first_changed=min(different, default=None)))
        rows.extend(group)
        print(pattern, flush=True)
    args.output.write_text(json.dumps(dict(
        dll_sha256=SHA256, cases=rows, comparisons=comparisons,
        scope='Controlled native initial delta and independent matching initial state; no harmonic prediction uses native phase outputs. ENV digests are compared only after native/model equality. This is not a commercial production initialization guarantee.'), indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
