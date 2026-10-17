"""Check known DRS bit6/18 kernels on the PCM window validated for VQM bit16.

Installed V4X and auxiliary PCM is read only and remains in memory. Only
aggregate errors and source hashes are saved. No parameters are fitted.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import struct

from audit_frm2_numeric import Native, SHA256, F, I, P, Q, D, B, f32
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences, metrics
from verify_vqm_bit6_component_ratio import predict
from verify_vqm_bit18_band_ratio import replay
from verify_frm2_analysis_windows import fft, prepare, window
from frm2_fft_peaks import detect


def run(args):
    parser_hash, ddi_hash, sequences = load_sequences(
        args.manifest, args.voice_root, args.ddb_tools)
    if args.aux_vdb:
        sequences += auxiliary_sequences(args.aux_vdb)
    native = Native(args.dll)
    config, context = native.alloc(0x35f0), native.alloc(0x400)
    native.fn(0x90de0, P, P)(config)
    native.fn(0x49790, P, P, I, P, I)(context, 0, config, 0)
    analyze = native.fn(0x4e8e0, Q, P, P, I, B, F, P, P, P, B, P, F, I, I, I, I)
    peaks = native.fn(0x51e60, Q, P, P, I, P, P, F, I, F, D, F)
    linearize = native.fn(0x2af20, F, F)
    logarithmic = I.from_address(config + 0x520).value
    post_filter = I.from_address(config + 0x588).value
    threshold_db = native.fn(0x2b050, F, P, I, F)(config, 3, 0.)
    assert logarithmic == 0 and post_filter == 0 and threshold_db == -300.
    threshold = linearize(threshold_db)
    output = native.alloc(30000)
    weights = window(native, 2049, 10)
    rows = []
    for sample_index, sequence in enumerate(sequences):
        errors = {'bit6_all_peaks': [], 'bit16_nearest_peak': [],
                  'bit16_first_peak': [], 'bit16_strongest_peak': [],
                  'bit16_best_amplitude_match_diagnostic': [],
                  'independent_bit18_power_saved_f0': [],
                  'independent_bit6_all_peaks': [],
                  'independent_bit16_nearest_peak': []}
        matched_ranks = []
        actuals = {}
        for scale in ('power', 'amplitude'):
            for frequency in ('saved_f0', 'nearest_peak'):
                errors[f'bit18_{scale}_{frequency}'] = []
        for index in range(4, len(sequence['frames']) - 4):
            start = index * 256 - 1024
            signal = sequence['pcm'][start:start + 2049]
            assert len(signal) == 2049
            scalar, packed, half = P(), P(), I()
            assert analyze(context, native.array(F, signal), 2049, 10, 1.,
                           c.byref(scalar), 0, c.byref(half), 2,
                           c.byref(packed), 2., 0, 0, 0, 0) == 0
            assert half.value == 2048
            pointer, count = P(output), I()
            assert peaks(packed, scalar, half.value, c.byref(pointer),
                         c.byref(count), 44100, logarithmic, threshold, 0., 0.) == 0
            assert 0 < count.value <= 1500
            components = [(F.from_address(output + j * 20).value,
                           F.from_address(output + j * 20 + 4).value)
                          for j in range(count.value)]
            assert all(a[0] <= b[0] for a, b in zip(components, components[1:]))
            frame = sequence['frames'][index]
            nearest = min(components, key=lambda pair: abs(pair[0] - frame['f0']))
            errors['bit6_all_peaks'].append(
                predict(components, linearize) - frame['features'][6])
            errors['bit16_nearest_peak'].append(
                f32(nearest[1] / 10.) - frame['features'][16])
            for label, pair in (('first', components[0]),
                                ('strongest', max(components, key=lambda x: x[1]))):
                errors[f'bit16_{label}_peak'].append(
                    f32(pair[1] / 10.) - frame['features'][16])
            match = min(range(len(components)), key=lambda j: abs(
                f32(components[j][1] / 10.) - frame['features'][16]))
            matched_ranks.append(match)
            errors['bit16_best_amplitude_match_diagnostic'].append(
                f32(components[match][1] / 10.) - frame['features'][16])
            power = [F.from_address(scalar.value + j * 4).value for j in range(2048)]
            assert all(value >= 0 and math.isfinite(value) for value in power)
            bins = fft(prepare(signal, weights, 2., False))
            # The verified DSE scalar layout uses DC magnitude and a zero
            # final scalar slot, with power only in the interior slots.
            independent_power = [f32(abs(bins[0].real))] + [
                f32(f32(z.real * z.real) + f32(z.imag * z.imag))
                for z in bins[1:2047]] + [0.]
            errors['independent_bit18_power_saved_f0'].append(
                replay(independent_power, frame['f0'], 44100) - frame['features'][18])
            independent_components = [tuple(peak[:2]) for peak in detect(
                independent_power, None, 44100, logarithmic, threshold)]
            errors['independent_bit6_all_peaks'].append(
                predict(independent_components, lambda db: f32(
                    32768 * math.exp(db * 0.1151292546497))) - frame['features'][6])
            independent_nearest = min(independent_components,
                                      key=lambda pair: abs(pair[0] - frame['f0']))
            errors['independent_bit16_nearest_peak'].append(
                f32(independent_nearest[1] / 10.) - frame['features'][16])
            for scale, values in (('power', power),
                                  ('amplitude', [f32(math.sqrt(x)) for x in power])):
                for frequency, value in (('saved_f0', frame['f0']),
                                         ('nearest_peak', nearest[0])):
                    errors[f'bit18_{scale}_{frequency}'].append(
                        replay(values, value, 44100) - frame['features'][18])
            for name in errors:
                actuals.setdefault(name, []).append(frame['features'][
                    6 if 'bit6_' in name else 18 if 'bit18_' in name else 16])
        checks = {}
        bits = lambda value: struct.unpack('<I', struct.pack('<f', value))[0]
        for name, values in errors.items():
            ulps = [abs(bits(actual + error) - bits(actual))
                    for actual, error in zip(actuals[name], values)]
            checks[name] = {**metrics(values),
                            'float32_exact': sum(x == 0 for x in values),
                            'max_ulp_difference': max(ulps),
                            'within_two_ulp': sum(x <= 2 for x in ulps)}
        row = dict(case=sequence.get('category', sample_index),
                   checks=checks, amplitude_match_peak_ranks=dict(
                       first=sum(x == 0 for x in matched_ranks),
                       minimum=min(matched_ranks), maximum=max(matched_ranks)))
        rows.append(row)
        print(json.dumps(dict(case=row['case'], frames=len(matched_ranks))), flush=True)
    validated = [row for row in rows if row['case'] != 'BRWSP']
    for row in validated:
        for name in ('bit6_all_peaks', 'bit16_nearest_peak', 'bit18_power_saved_f0',
                     'independent_bit6_all_peaks', 'independent_bit18_power_saved_f0',
                     'independent_bit16_nearest_peak'):
            assert row['checks'][name]['max_absolute_error'] <= 1e-6, (row['case'], name)
    return dict(dse_sha256=SHA256, parser_sha256=parser_hash, ddi_sha256=ddi_hash,
                auxiliary_sha256=(hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest()
                                  if args.aux_vdb else None),
                window=dict(samples=2049, fft_points=4096, kind=10,
                            pcm_centering='raw', margin_frames_each_end=4),
                defaults=dict(logarithmic=logarithmic, post_filter=post_filter,
                              threshold_db=threshold_db), cases=rows,
                validated_voiced_frames=sum(row['checks']['bit6_all_peaks']['frames']
                                            for row in validated),
                scope='Known DRS component and spectral band kernels on the validated '
                      'bit16 input window. Independent FFT and peak detector select '
                      'their own peaks, without native peak-bin inputs. Best amplitude match '
                      'is diagnostic only, never a production selector. No fitted '
                      'smoothing or thresholds. Negative results do not exclude '
                      'other VQM production branches.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'output'):
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--aux-vdb', type=Path)
    args = parser.parse_args()
    args.output.write_text(json.dumps(run(args), indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
