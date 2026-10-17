"""Bound VQM saved-PCM energy errors and test fixed half-LSB midpoint controls.

Midpoint controls are hypotheses, not reconstructed original PCM. No correction
is fitted to a saved feature. Installed source material remains in memory.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import statistics

from audit_frm2_numeric import Native, SHA256, F, I, P, Q, D, B, f32
from frm2_raw_features import raw_features
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences, metrics
from verify_vqm_bit6_component_ratio import predict
from verify_vqm_bit18_band_ratio import replay


def midpoint(samples, direction):
    return [f32(x + direction * math.copysign(.5, x)) if x else 0. for x in samples]


def run(args):
    native = Native(args.dll)
    cfg, ctx = native.alloc(0x35f0), native.alloc(0x400)
    native.fn(0x90de0, P, P)(cfg)
    native.fn(0x49790, P, P, I, P, I)(ctx, 0, cfg, 0)
    analyze = native.fn(0x4e8e0, Q, P, P, I, B, F, P, P, P, B, P, F, I, I, I, I)
    peak_call = native.fn(0x51e60, Q, P, P, I, P, P, F, I, F, D, F)
    linearize = native.fn(0x2af20, F, F)
    output = native.alloc(30000)
    rows = []
    for sequence in auxiliary_sequences(args.aux_vdb):
        pcm = sequence['pcm']
        modes = {'raw': pcm, 'toward_zero_half_lsb': midpoint(pcm, -1),
                 'away_zero_half_lsb': midpoint(pcm, 1)}
        case = dict(category=sequence['category'], controls=[])
        bounds, rms_delta, ratios = [], [], []
        raw_errors = []
        for name, source in modes.items():
            errors = {key: [] for key in ('bit2', 'bit6', 'bit16_nearest_peak',
                                         'bit16_best_peak_diagnostic', 'bit18')}
            for index in range(4, len(sequence['frames']) - 4):
                frame = sequence['frames'][index]
                ring = source[index * 256 - 257:index * 256 + 257]
                actual_energy = frame['features'][2]
                energy = raw_features(ring, 1, 256)['feature12']
                errors['bit2'].append(energy - actual_energy)
                if name == 'raw':
                    window = ring[1:-1]
                    mean = statistics.fmean(window)
                    deviations = [x - mean for x in window]
                    deviations += [deviations[-1]]
                    # For input quantization <= q=0.5 LSB, mean removal gives
                    # |delta deviation| <= 2q=1. The duplicated end sample
                    # and /511 normalization are part of the native contract.
                    bound = sum(2 * abs(x) + 1 for x in deviations) / 511 / 32768 ** 2
                    bounds.append(bound)
                    raw_errors.append(energy - actual_energy)
                    rms_delta.append((math.sqrt(actual_energy) - math.sqrt(energy)) * 32768)
                    ratios.append(actual_energy / energy)
                signal = source[index * 256 - 1024:index * 256 + 1025]
                scalar, packed, half = P(), P(), I()
                assert analyze(ctx, native.array(F, signal), 2049, 10, 1.,
                               c.byref(scalar), 0, c.byref(half), 2,
                               c.byref(packed), 2., 0, 0, 0, 0) == 0
                assert half.value == 2048
                pointer, count = P(output), I()
                assert peak_call(packed, scalar, half.value, c.byref(pointer),
                                 c.byref(count), 44100, 0, 0., 0., 0.) == 0
                components = [(F.from_address(output + j * 20).value,
                               F.from_address(output + j * 20 + 4).value)
                              for j in range(count.value)]
                assert components
                nearest = min(components, key=lambda x: abs(x[0] - frame['f0']))
                errors['bit6'].append(predict(components, linearize) - frame['features'][6])
                errors['bit16_nearest_peak'].append(f32(nearest[1] / 10.) - frame['features'][16])
                errors['bit16_best_peak_diagnostic'].append(min(
                    (f32(x[1] / 10.) - frame['features'][16] for x in components), key=abs))
                power = [F.from_address(scalar.value + j * 4).value for j in range(2048)]
                errors['bit18'].append(replay(power, frame['f0'], 44100) - frame['features'][18])
            case['controls'].append(dict(
                pcm_mapping=name, checks={key: {**metrics(values),
                    'mean_signed_error': statistics.fmean(values)} for key, values in errors.items()}))
        case['energy_quantization_diagnostic'] = dict(
            frames=len(bounds), assumed_sample_error_max_lsb=.5,
            bound_mean=statistics.fmean(bounds), bound_min=min(bounds), bound_max=max(bounds),
            outside_bound=sum(abs(e) > b for e, b in zip(raw_errors, bounds)),
            saved_energy_below_reconstructed=sum(e > 0 for e in raw_errors),
            saved_over_reconstructed_range=[min(ratios), max(ratios)],
            rms_equivalent_lsb_range=[min(rms_delta), max(rms_delta)],
            qualification='Bound describes real-valued energy sensitivity, not the exact '
                          'native float32 quantizer or proof of the original source. '
                          'No observed error exceeded the bound; no floating-point '
                          'roundoff allowance was needed to classify these samples.')
        rows.append(case)
        print(json.dumps(dict(category=case['category'], quantization=case[
            'energy_quantization_diagnostic'])), flush=True)
    return dict(dse_sha256=SHA256,
                auxiliary_sha256=hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest(),
                cases=rows, scope='Fixed raw and +/-0.5 signed LSB hypotheses, with '
                      'kind-10 2049 PCM windows. A within-bound result shows compatibility '
                      'with missing quantization detail, not recovered PCM, an identified '
                      'quantizer, or a completed VQM producer. Diagnostic amplitude matching '
                      'consumes saved bit16 and must not be used to select production peaks.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'aux-vdb', 'output'):
        parser.add_argument('--' + name, required=True, type=Path)
    args = parser.parse_args()
    args.output.write_text(json.dumps(run(args), indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
