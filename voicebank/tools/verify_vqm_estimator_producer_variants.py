"""Compare reviewed mode4 estimator variants with saved VQM scalar producers.

Each case runs in a disposable process. Installed PCM stays in memory; reports
contain only geometry, hashes and aggregate comparisons, never source payloads.
"""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import statistics
import subprocess
import sys

from audit_frm2_numeric import Native, SHA256, F, I, P, D, B, Q
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences, metrics
from probe_frm2_batch_outputs import run as analyze_batch


VARIANTS = (0, 1, 3, 4, 10)
OFFSETS = {2: 0x0c, 4: 0x14, 6: 0x1c, 14: 0x20c, 15: 0x214,
           16: 0x218, 17: 0x21c, 18: 0x220}


def geometry(native, estimator):
    return dict(sample_rate=D.from_address(estimator + 0x188).value,
                decimation_factor=I.from_address(estimator + 0x150).value,
                candidate_mode=I.from_address(estimator + 0x44).value,
                voicing_mode=I.from_address(estimator + 0x48).value,
                special_pcm=B.from_address(estimator + 0x1c).value,
                nominal_size=I.from_address(estimator + 0x34).value,
                window_size=I.from_address(estimator + 0x2c).value,
                half_window=I.from_address(estimator + 0x30).value,
                window_kind=B.from_address(estimator + 0x144).value,
                fft_factor=F.from_address(estimator + 0x140).value,
                queue_index=I.from_address(estimator + 0x18).value)


def child(args):
    parser_hash, ddi_hash, sequences = load_sequences(args.manifest, args.voice_root,
                                                      args.ddb_tools)
    sequences += auxiliary_sequences(args.aux_vdb)
    sequence = sequences[args.sample]
    reference = sequence.get('pitch1', statistics.median(
        frame['f0'] for frame in sequence['frames']))
    holder = []
    def setup(native, context):
        estimator = P.from_address(context + 0xc80).value
        assert estimator
        holder.append((native, context, geometry(native, estimator)))
    result = analyze_batch(args.dll, args.automatic, 1, 'steady', reference,
                           analysis_mode=4, estimator_variant=args.variant,
                           special_pcm=getattr(args, 'special_pcm', 0),
                           pcm_override=sequence['pcm'], setup_observer=setup)
    native, context, contract = holder[0]
    track = P.from_address(context + 0xcd0).value
    regions = P.from_address(track + 0x148).value
    produced = {}
    flags = Counter()
    outer_flags = Counter()
    outer_values = Counter()
    for region_index in range(I.from_address(track + 0x140).value):
        region = P.from_address(regions + 8 * region_index).value
        frames = P.from_address(region + 0x20).value
        for index in range(I.from_address(region + 0x18).value):
            frame = P.from_address(frames + 8 * index).value
            feature = P.from_address(frame + 0x128).value
            assert feature and Q.from_address(frame + 0x50).value & (1 << 13)
            at = round(D.from_address(frame + 0x48).value * 44100 / 256)
            assert 0 <= at < len(sequence['frames']) and at not in produced
            values = {bit: F.from_address(feature + offset).value
                      for bit, offset in OFFSETS.items()}
            assert all(math.isfinite(value) for value in values.values())
            produced[at] = values
            flags[hex(I.from_address(feature).value)] += 1
            outer_flags[hex(Q.from_address(frame + 0x50).value & 0xc0000)] += 1
            outer_values[f'{B.from_address(frame + 0x178).value}/'
                         f'{I.from_address(frame + 0x17c).value}'] += 1
    assert len(produced) == len(sequence['frames']) == result['frames']
    comparisons = {}
    for bit in OFFSETS:
        indices = range(12, len(sequence['frames']) - 12)
        errors = [produced[index][bit] - sequence['frames'][index]['features'][bit]
                  for index in indices]
        nonzero_errors = [produced[index][bit] - sequence['frames'][index]['features'][bit]
                          for index in indices
                          if sequence['frames'][index]['features'][bit] != 0]
        values = [produced[index][bit] for index in produced]
        comparisons[str(bit)] = {**metrics(errors),
            'saved_nonzero_comparisons': metrics(nonzero_errors) if nonzero_errors else None,
            'saved_zero_comparisons': len(errors) - len(nonzero_errors),
            'produced_nonzero': sum(value != 0 for value in values),
            'produced_range': [min(values), max(values)]}
    if args.variant == 10:
        assert contract['voicing_mode'] == 1
    elif args.variant == 1:
        assert contract['candidate_mode'] == 1
    else:
        assert contract['candidate_mode'] == contract['voicing_mode'] == 0
    return dict(case=sequence.get('category', args.sample), variant=args.variant,
                special_pcm=getattr(args, 'special_pcm', 0),
                automatic=args.automatic, frames=result['frames'], contract=contract,
                flags=dict(flags), outer_mask_bits18_19=dict(outer_flags),
                outer_values=dict(outer_values),
                comparisons=comparisons, parser_sha256=parser_hash, ddi_sha256=ddi_hash)


def run(args):
    rows = []
    for sample in range(5):
        for variant in VARIANTS:
            for automatic in (0, 1):
                command = [sys.executable, '-B', str(Path(__file__).resolve()),
                    '--dll', str(args.dll), '--manifest', str(args.manifest),
                    '--voice-root', str(args.voice_root), '--ddb-tools', str(args.ddb_tools),
                    '--aux-vdb', str(args.aux_vdb), '--child', '--sample', str(sample),
                    '--variant', str(variant), '--automatic', str(automatic)]
                process = subprocess.run(command, capture_output=True, text=True, timeout=90)
                if process.returncode:
                    raise RuntimeError(f'sample={sample}, variant={variant}, automatic={automatic}: '
                                       f'{process.stderr[-1600:]}')
                row = json.loads(process.stdout)
                rows.append(row)
                print(json.dumps(dict(case=row['case'], variant=variant, automatic=automatic,
                                      bit17_nonzero=row['comparisons']['17']['produced_nonzero'])),
                      flush=True)
    return dict(dse_sha256=SHA256,
                auxiliary_sha256=hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest(),
                cases=rows, frame_count=sum(row['frames'] for row in rows),
                scope='Five native constructor branches selected by config+0x5c4: '
                      'generic 0, candidate variant 1, long window 3, short window 4, '
                      'voicing variant 10. Fixed mode4, automatic/external pitch, five '
                      'installed sequences; scalar comparisons exclude 12 frames at '
                      'each edge. No history seeding, fitted transforms or field injection. '
                      'Saved-zero comparisons are separated from saved-nonzero values: '
                      'a zero coincidence does not verify the commercial producer. '
                      'Config value 2 follows the generic constructor branch and is not '
                      'claimed as a distinct producer here.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--child', action='store_true')
    parser.add_argument('--sample', type=int, choices=range(5))
    parser.add_argument('--variant', type=int, choices=VARIANTS)
    parser.add_argument('--automatic', type=int, choices=(0, 1))
    parser.add_argument('--special-pcm', type=int, choices=(0, 1), default=0)
    args = parser.parse_args()
    if args.child:
        print(json.dumps(child(args)))
    else:
        if args.output is None:
            parser.error('--output is required for the matrix')
        if args.special_pcm:
            parser.error('Use verify_vqm_special_pcm_protocol.py for the matched special-PCM matrix')
        args.output.write_text(json.dumps(run(args), indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
