"""Verify the raw bit15 branch against native kernels and installed PCM.

Reports contain aggregate errors and exceptional frame indices, not commercial
PCM, frames or histogram payloads. A saved field is never a production input.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import random

from audit_frm2_numeric import Native, F, I, P, Q, SHA256, f32
from frm2_crossing_distribution import crossing_distribution
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences, metrics


def kernel_checks(native):
    config = native.alloc(0x35f0)
    native.fn(0x90de0, P, P)(config)
    for at in (0., 1.):
        native.add(config + 0x5d0 + 23 * 0xa0, at, 1.)
    context = native.alloc(0xce0)
    P.from_address(context + 0xad0).value = config
    frame = native.alloc(0x1d8)
    features = native.alloc(0x228)
    P.from_address(frame + 0x128).value = features
    Q.from_address(frame + 0x50).value = 1 << 13
    call = native.fn(0xd92e0, Q, P, P)
    rng = random.Random(391)
    cases = 0
    for size, hop in ((64, 8), (1024, 256), (2048, 400)):
        signals = [[0.] * size, [123.] * size, [10000.] + [0.] * (size - 1),
                   [1000. if i % 2 else -1000. for i in range(size)],
                   [f32(i * .3 - 50) for i in range(size)]]
        signals += [[f32(rng.uniform(-amplitude, amplitude)) for _ in range(size)]
                    for amplitude in (.5, 10., 5000.)]
        signals += [[f32(5000 * sum(math.sin(math.tau * frequency * k * i / 44100
                        + k * .23) / k for k in range(1, 6))) for i in range(size)]
                    for frequency in (110, 440, 5000)]
        for signal in signals:
            P.from_address(context + 0xae0).value = native.array(F, signal)
            I.from_address(context + 0xaf0).value = size
            I.from_address(context + 0xbb0).value = hop
            I.from_address(context + 0xad8).value = 44100
            for start in (0, 1, size - hop):
                I.from_address(context + 0xb00).value = (start + hop) % size
                assert call(context, frame) == 0
                actual = F.from_address(features + 0x214).value
                predicted = crossing_distribution(signal, start, hop)
                assert actual == predicted['imbalance'], (size, hop, start, actual, predicted)
                assert F.from_address(features + 4).value == f32(predicted['crossings'] / (2 * hop - 2))
                cases += 1
    return dict(cases=cases, bit15_float32_exact=cases,
                scope='Generated zero, constant, impulse, alternating, ramp, noise '
                      'and harmonics; three ring sizes/hops and three physical starts.')


def saved_checks(sequences):
    rows = []
    for case, sequence in enumerate(sequences):
        errors, crossing_errors, exceptions = [], [], []
        for index in range(4, len(sequence['frames']) - 4):
            ring = sequence['pcm'][index * 256 - 257:index * 256 + 257]
            assert len(ring) == 514
            predicted = crossing_distribution(ring, 1, 256)
            saved = sequence['frames'][index]['features']
            error = predicted['imbalance'] - saved[15]
            crossing_error = predicted['crossings'] - round(saved[0] * 510)
            errors.append(error)
            crossing_errors.append(crossing_error)
            if abs(error) > 1e-6:
                exceptions.append(dict(index=index, bit15_absolute_error=abs(error),
                                       crossing_count_delta=crossing_error))
        rows.append(dict(case=sequence.get('category', case),
                         raw_bit15_to_saved=metrics(errors),
                         crossing_counts_match=sum(error == 0 for error in crossing_errors),
                         bit15_failures_with_crossing_count_difference=sum(
                             item['crossing_count_delta'] != 0 for item in exceptions),
                         exceptions=exceptions))
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    parser_hash, ddi_hash, sequences = load_sequences(args.manifest, args.voice_root,
                                                      args.ddb_tools)
    sequences += auxiliary_sequences(args.aux_vdb)
    native = Native(args.dll)
    report = dict(dse_sha256=SHA256, parser_sha256=parser_hash, ddi_sha256=ddi_hash,
                  auxiliary_sha256=hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest(),
                  native_kernel=kernel_checks(native), cases=saved_checks(sequences),
                  formula='float32(float32(sum(abs(histogram[k]-crossings/10))/10)/(crossings/10)); '
                          'zero when crossings=0. Ten bins floor(float32(float32(offset/(2*hop-1))*10)).',
                  scope='Raw d92e0 bit15 before optional estimator overwrite. Installed '
                        '512-sample windows use contiguous physical layout; no fitted '
                        'mean, input precision correction or saved-value substitution. '
                        'Agreement does not identify original ring geometry or authoring config.')
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(native=report['native_kernel'],
                         saved=[dict(case=row['case'], **row['raw_bit15_to_saved'])
                                for row in report['cases']])))


if __name__ == '__main__':
    main()
