"""Verify outer window metadata from native analysis of generated PCM.

No source frames are inserted or rewritten. Config IDs and both frame layouts
were reviewed against the hash-pinned DSE in Ghidra before this experiment.
"""
from __future__ import annotations

import argparse
from collections import Counter
from itertools import product
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import B, F, I, P, Q, SHA256
from probe_frm2_batch_outputs import run as analyze_batch


KINDS = (0, 10, 11, 15)
LENGTHS = (513, 1025, 2049)


def child(args):
    holder = []
    def setup(native, context):
        config = P.from_address(context + 0xad0).value
        # Mode 2 selects fixed length; curve 38 stores length minus one.
        for key, value in ((0, 2), (1, args.kind), (38, args.length - 1)):
            curve = config + 0x5d0 + key * 0xa0
            for x in (0., 1.):
                native.add(curve, x, value)
            assert native.fn(0x2b050, F, P, I, F)(config, key, 0) == value
        # IDs 105/109 are fixed integer members, not ENV array entries.
        I.from_address(config + 0x554).value = args.save
        I.from_address(config + 0x564).value = args.reuse
        holder.append((native, context))
    result = analyze_batch(args.dll, 0, 1, 'steady', 440,
                           analysis_mode=4, setup_observer=setup)
    native, context = holder[0]
    reused = P.from_address(context + 0xca0).value
    assert reused
    reused_kind = B.from_address(reused + 0x3b8).value
    reused_length = I.from_address(reused + 0x418).value
    expected_kind = reused_kind if args.reuse else args.kind
    expected_length = reused_length if args.reuse else args.length
    assert I.from_address(context + 0xbb4).value == expected_length
    track = P.from_address(context + 0xcd0).value
    regions = P.from_address(track + 0x148).value
    actual = Counter()
    checked = 0
    for region_index in range(I.from_address(track + 0x140).value):
        region = P.from_address(regions + 8 * region_index).value
        frames = P.from_address(region + 0x20).value
        for index in range(I.from_address(region + 0x18).value):
            frame = P.from_address(frames + 8 * index).value
            mask = Q.from_address(frame + 0x50).value & 0xc0000
            kind = B.from_address(frame + 0x178).value
            length = I.from_address(frame + 0x17c).value
            assert (mask, kind, length) == (
                (0xc0000, expected_kind, expected_length) if args.save
                else (0, 255, 0)), (index, mask, kind, length)
            actual[f'{mask:#x}/{kind}/{length}'] += 1
            checked += 1
    assert checked == result['frames'] > 0
    return dict(requested_kind=args.kind, requested_length=args.length,
                save_metadata=args.save, reuse_spectrum_window=args.reuse,
                reused_kind=reused_kind, reused_length=reused_length,
                effective_kind=expected_kind, effective_length=expected_length,
                observed=dict(actual), checked_frames=checked)


def run(args):
    cases = []
    for kind, length, save, reuse in product(KINDS, LENGTHS, (0, 1), (0, 1)):
        process = subprocess.run([sys.executable, '-B', str(Path(__file__).resolve()),
            '--dll', str(args.dll), '--child', '--kind', str(kind), '--length',
            str(length), '--save', str(save), '--reuse', str(reuse)],
            capture_output=True, text=True, timeout=60)
        if process.returncode:
            raise RuntimeError(f'{kind}/{length}/{save}/{reuse}: {process.stderr[-1600:]}')
        cases.append(json.loads(process.stdout))
    return dict(dse_sha256=SHA256, cases=cases,
                checked_frames=sum(case['checked_frames'] for case in cases),
                code_contract=dict(config_reader_rva='0x2b050',
                    window_length_rva='0xd0250', analysis_step_rva='0xd2cc0',
                    fft_call_rva='0xd4d79', metadata_save_rva='0xd4d82..0xd4df9',
                    drs_frame_reader_rva='0x87750',
                    drs_offsets=dict(kind='0x178', length='0x17c'),
                    dse5_offsets=dict(kind='0x258', length='0x25c'),
                    config_ids=dict(kind_fixed=1, kind_pitch_dependent=29,
                        fixed_length_minus_one=38, save_metadata=105,
                        reuse_spectrum_window=109)),
                scope='Owned 440 Hz harmonic PCM; complete native batch, mode4, '
                    'external pitch. Four reviewed window kinds and three lengths '
                    'cross metadata saving and spectrum-window reuse. Config inputs '
                    'only: no frame injection, scalar rewrite, fitted transform or '
                    'serialization of commercial PCM. Identifies window metadata '
                    'and producer conditions in this binary; does not identify the '
                    'original commercial authoring executable or its full settings.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--child', action='store_true')
    parser.add_argument('--kind', type=int, choices=KINDS)
    parser.add_argument('--length', type=int, choices=LENGTHS)
    parser.add_argument('--save', type=int, choices=(0, 1))
    parser.add_argument('--reuse', type=int, choices=(0, 1))
    args = parser.parse_args()
    if args.child:
        print(json.dumps(child(args)))
    else:
        if args.output is None:
            parser.error('--output is required')
        report = run(args)
        args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        print(json.dumps(dict(cases=len(report['cases']),
                              checked_frames=report['checked_frames'])))


if __name__ == '__main__':
    main()
