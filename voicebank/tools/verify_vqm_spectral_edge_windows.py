"""Bound missing PCM context at VQM spectral edges with explicit padding controls.

Installed PCM stays in memory. Padding is a tested hypothesis, never evidence
that the original production used that boundary policy.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
from pathlib import Path

from audit_frm2_numeric import Native, SHA256, F, I, P, Q, D, B, f32
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences, metrics
from verify_vqm_bit6_component_ratio import predict
from verify_vqm_bit18_band_ratio import replay
from verify_frm2_analysis_windows import window


def pad_sample(pcm, index, mode):
    if 0 <= index < len(pcm):
        return pcm[index]
    if mode == 'zero':
        return 0.
    if mode == 'repeat':
        return pcm[max(0, min(len(pcm) - 1, index))]
    if mode == 'circular':
        return pcm[index % len(pcm)]
    if mode == 'mirror':
        at = index % (2 * len(pcm))
        return pcm[at if at < len(pcm) else 2 * len(pcm) - 1 - at]
    if mode == 'positive_full_scale':
        return 32767.
    if mode == 'negative_full_scale':
        return -32768.
    raise ValueError(mode)


def run(args):
    parser_hash, ddi_hash, sequences = load_sequences(args.manifest, args.voice_root,
                                                      args.ddb_tools)
    sequences += auxiliary_sequences(args.aux_vdb)
    native = Native(args.dll)
    cfg, ctx = native.alloc(0x35f0), native.alloc(0x400)
    native.fn(0x90de0, P, P)(cfg)
    native.fn(0x49790, P, P, I, P, I)(ctx, 0, cfg, 0)
    analyze = native.fn(0x4e8e0, Q, P, P, I, B, F, P, P, P, B, P, F, I, I, I, I)
    peak_call = native.fn(0x51e60, Q, P, P, I, P, P, F, I, F, D, F)
    linearize = native.fn(0x2af20, F, F)
    output = native.alloc(30000)
    endpoint = window(native, 2049, 10)[-1]
    rows = []
    for case, sequence in enumerate(sequences):
        count = len(sequence['frames'])
        pcm = sequence['pcm']
        results = []
        for mode in ('zero', 'repeat', 'mirror', 'circular',
                     'positive_full_scale', 'negative_full_scale'):
            sides = []
            for side, indices in (('head', range(4)), ('tail', range(count - 4, count))):
                errors = {key: [] for key in ('bit6', 'bit16', 'bit18')}
                first_tail = {}
                for index in indices:
                    frame = sequence['frames'][index]
                    start = index * 256 - 1024
                    signal = [pad_sample(pcm, j, mode) for j in range(start, start + 2049)]
                    scalar, packed, half = P(), P(), I()
                    assert analyze(ctx, native.array(F, signal), 2049, 10, 1.,
                                   c.byref(scalar), 0, c.byref(half), 2,
                                   c.byref(packed), 2., 0, 0, 0, 0) == 0
                    assert half.value == 2048
                    pointer, peaks = P(output), I()
                    assert peak_call(packed, scalar, half.value, c.byref(pointer),
                                     c.byref(peaks), 44100, 0, 0., 0., 0.) == 0
                    components = [(F.from_address(output + j * 20).value,
                                   F.from_address(output + j * 20 + 4).value)
                                  for j in range(peaks.value)]
                    assert components
                    nearest = min(components, key=lambda x: abs(x[0] - frame['f0']))
                    power = [F.from_address(scalar.value + j * 4).value for j in range(2048)]
                    values = dict(bit6=predict(components, linearize),
                                  bit16=f32(nearest[1] / 10.),
                                  bit18=replay(power, frame['f0'], 44100))
                    for key, value in values.items():
                        errors[key].append(value - frame['features'][int(key[3:])])
                    if side == 'tail' and index == count - 4:
                        first_tail = dict(missing_samples=1, errors={key: values[key] -
                            frame['features'][int(key[3:])] for key in values})
                sides.append(dict(side=side, checks={key: metrics(values)
                                                     for key, values in errors.items()},
                                  first_tail_frame=first_tail))
            results.append(dict(padding=mode, sides=sides))
        rows.append(dict(case=sequence.get('category', case), controls=results))
        print(f'Checked edge windows: {sequence.get("category", case)}', flush=True)
    return dict(dse_sha256=SHA256, parser_sha256=parser_hash, ddi_sha256=ddi_hash,
                auxiliary_sha256=hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest(),
                window_endpoint_coefficient=endpoint, cases=rows,
                scope='First and last four spectral frames, four conventional padding '
                      'policies and two full-scale guards. A pass under a guessed policy '
                      'does not prove original context. The first tail frame lacks only '
                      'the final window endpoint; endpoint and guard invariance are '
                      'reported separately. No source PCM or frame payload is saved.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    args.output.write_text(json.dumps(run(args), indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
