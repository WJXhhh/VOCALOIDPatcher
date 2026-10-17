"""Test non-uniqueness of BRWSP bit0/15 from saved quantized PCM.

Per-window inputs are selected using bit0 only and round to the original PCM16.
They are existence witnesses, not recovered samples or a production correction.
Only aggregate differences and frame indices are serialized.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from audit_frm2_numeric import Native, F, I, P, Q, SHA256, f32
from frm2_crossing_distribution import crossing_distribution
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences


SHIFT = .5 - 1 / 256


def run(args):
    previous = json.loads(args.ring_report.read_text(encoding='utf-8'))
    assert previous['dse_sha256'] == SHA256
    assert len(previous['exceptions']) == 6
    source_hash = hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest()
    assert previous['auxiliary_sha256'] == source_hash
    sequence = auxiliary_sequences(args.aux_vdb)[-1]
    assert sequence['category'] == 'BRWSP'
    native = Native(args.dll)
    config, context = native.alloc(0x35f0), native.alloc(0xce0)
    native.fn(0x90de0, P, P)(config)
    for at in (0., 1.):
        native.add(config + 0x5d0 + 23 * 0xa0, at, 1.)
    P.from_address(context + 0xad0).value = config
    I.from_address(context + 0xaf0).value = 514
    I.from_address(context + 0xbb0).value = 256
    I.from_address(context + 0xad8).value = 44100
    I.from_address(context + 0xb00).value = 257
    frame, feature = native.alloc(0x1d8), native.alloc(0x228)
    P.from_address(frame + 0x128).value = feature
    Q.from_address(frame + 0x50).value = 1 << 13
    call = native.fn(0xd92e0, Q, P, P)
    rows = []
    for exception in previous['exceptions']:
        index = exception['index']
        raw = list(sequence['pcm'][index * 256 - 257:index * 256 + 257])
        assert len(raw) == 514
        saved = sequence['frames'][index]['features']
        total = 0.
        for value in raw[1:-1]:
            total = f32(total + value)
        mean = f32(total / 512)
        candidates = [at for at in range(1, 513) if abs(raw[at] - mean) < 2 * SHIFT]
        selected = None
        for at in candidates:
            for direction in (-1, 1):
                altered = [f32(value - direction * SHIFT) for value in raw]
                altered[at] = f32(raw[at] + direction * SHIFT)
                assert all(round(value) == original for value, original in zip(altered, raw))
                prediction = crossing_distribution(altered, 1, 256)
                if prediction['crossings'] == round(saved[0] * 510):
                    selected = altered, prediction
                    break
            if selected is not None:
                break
        assert selected is not None, index
        altered, prediction = selected
        summaries = {}
        for label, signal in (('saved_pcm', raw), ('quantization_witness', altered)):
            P.from_address(context + 0xae0).value = native.array(F, signal)
            assert call(context, frame) == 0
            values = {bit: F.from_address(feature + offset).value
                      for bit, offset in ((0, 4), (2, 12), (15, 0x214))}
            independent = crossing_distribution(signal, 1, 256)
            assert values[15] == independent['imbalance']
            assert values[0] == f32(independent['crossings'] / 510)
            summaries[label] = dict(
                bit0_error=values[0] - saved[0], bit15_error=values[15] - saved[15],
                bit2_error=values[2] - saved[2])
        assert abs(summaries['quantization_witness']['bit0_error']) <= 1e-6
        assert abs(summaries['quantization_witness']['bit15_error']) <= 1e-6
        assert abs(summaries['saved_pcm']['bit15_error']) > 1e-6
        rows.append(dict(index=index, near_mean_candidates=len(candidates),
                         pcm16_roundtrip_samples=len(raw),
                         maximum_input_difference_lsb=max(
                             abs(a - b) for a, b in zip(raw, altered)), comparisons=summaries))
    return dict(dse_sha256=SHA256, auxiliary_sha256=source_hash,
                ring_report_sha256=hashlib.sha256(args.ring_report.read_bytes()).hexdigest(),
                cases=rows, quantization_step_lsb=SHIFT,
                native_checks=2 * len(rows),
                scope='Independent per-window existence witnesses within strictly '
                      'less than half an LSB. Every altered float32 sample rounds '
                      'back to its saved integer; selecting the first bit0-compatible '
                      'candidate precedes the separate native bit15 check. Proves '
                      'non-uniqueness of the producer inputs from PCM16, not an '
                      'identified original quantizer, recovered PCM, a single global '
                      'waveform, or simultaneous reproduction of other saved fields. '
                      'Bit2 residuals are reported without fitting them.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'aux-vdb', 'ring-report', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    report = run(args)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(cases=len(report['cases']), native_checks=report['native_checks'],
                          pcm16_roundtrip_samples=sum(row['pcm16_roundtrip_samples']
                                                      for row in report['cases']))))


if __name__ == '__main__':
    main()
