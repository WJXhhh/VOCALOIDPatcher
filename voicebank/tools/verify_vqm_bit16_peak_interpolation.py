"""Compare saved VQM bit16 with independently interpolated FFT peak power.

Installed PCM stays in memory. Reports contain aggregate errors, never source
windows, frame values or spectra. The candidate family has no fitted parameter.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import statistics
import struct

from audit_frm2_numeric import Native, SHA256, F, I, P, Q, D, B, f32
from verify_frm2_analysis_windows import fft, prepare, window
from verify_vqm_bit16_spectral_power import load_sequences, predict
from compare_aux_vqm_drs_fields import material
from audit_aux_vqm_vdb import ENCODED_VQM_MASK, PLAIN_VQM_MASK
import probe_frm2


def metrics(errors):
    return dict(frames=len(errors), mean_absolute_error=statistics.fmean(abs(x) for x in errors),
                max_absolute_error=max(abs(x) for x in errors),
                within_1e_6=sum(abs(x) <= 1e-6 for x in errors))


def auxiliary_sequences(path, *, include_component_phases=False):
    data = path.read_bytes()
    sequences = []
    for category in ('TENSE', 'BRWSP'):
        pcm, saved = material(path, category)
        cursor = 0
        section = None
        while (cursor := data.find(b'VQMs', cursor)) >= 0:
            size = struct.unpack_from('<I', data, cursor + 4)[0]
            if data[cursor - 34:cursor - 29].decode('ascii') == category:
                section = (cursor, cursor + size)
                break
            cursor += size
        assert section
        frames = []
        cursor = section[0]
        while (cursor := data.find(b'FRM2', cursor, section[1])) >= 0:
            size = struct.unpack_from('<I', data, cursor + 4)[0]
            mask = struct.unpack_from('<Q', data, cursor + 20)[0]
            if mask == ENCODED_VQM_MASK:
                raw = bytearray(data[cursor:cursor + size])
                struct.pack_into('<Q', raw, 20, PLAIN_VQM_MASK)
                frame = probe_frm2.parse_frame(raw)
                assert isinstance(frame, probe_frm2.VqmFrame)
                features = saved[len(frames)]
                frames.append(dict(f0=440 * 2 ** (frame.f0_hz / 1200),
                                   bit6=features[6], bit16=features[16],
                                   features=features,
                                   first_component_frequency=frame.component_frequencies_hz[0],
                                   first_component_amplitude=frame.component_amplitudes[0],
                                   first_component_phase=frame.component_phases_radians[0],
                                   components=tuple(zip(frame.component_frequencies_hz,
                                                        frame.component_amplitudes))))
                if include_component_phases:
                    frames[-1]['component_phases'] = frame.component_phases_radians
            cursor += size
        assert len(frames) == len(saved)
        sequences.append(dict(category=category, pcm=pcm, frames=frames))
    return sequences


def native_peak_checks(native, sequences, variants=None):
    config, context = native.alloc(0x35f0), native.alloc(0x400)
    native.fn(0x90de0, P, P)(config)
    native.fn(0x49790, P, P, I, P, I)(context, 0, config, 0)
    analyze = native.fn(0x4e8e0, Q, P, P, I, B, F, P, P, P, B, P, F, I, I, I, I)
    peak_call = native.fn(0x51e60, Q, P, P, I, P, P, F, I, F, D, F)
    output = native.alloc(30000)
    rows = []
    for size, mean_mode in (variants or [(size, mode) for size in (2048, 2049)
                                       for mode in ('raw', 'window_mean')]):
        per_sample = []
        for sample_index, sequence in enumerate(sequences):
            feature_errors, model_errors, frequency_errors = [], [], []
            independent_saved_errors, ulps = [], []
            weights = window(native, size, 10)
            for index in range(4, len(sequence['frames']) - 4):
                start = index * 256 - size // 2
                signal = list(sequence['pcm'][start:start + size])
                assert len(signal) == size
                if mean_mode == 'window_mean':
                    mean = statistics.fmean(signal)
                    signal = [f32(value - mean) for value in signal]
                scalar, packed, half = P(), P(), I()
                assert analyze(context, native.array(F, signal), size, 10, 1.,
                               c.byref(scalar), 0, c.byref(half), 2,
                               c.byref(packed), 2., 0, 0, 0, 0) == 0
                assert half.value == 2048
                pointer, count = P(output), I()
                assert peak_call(packed, scalar, half.value, c.byref(pointer),
                                 c.byref(count), 44100, 0, 0., 0., 0.) == 0
                assert 0 < count.value <= 1500
                frame = sequence['frames'][index]
                at = min(range(count.value), key=lambda j: abs(
                    F.from_address(output + j * 20).value - frame['f0']))
                frequency = F.from_address(output + at * 20).value
                feature = f32(F.from_address(output + at * 20 + 4).value / 10.)
                feature_errors.append(feature - frame['bit16'])
                bits = lambda value: struct.unpack('<I', struct.pack('<f', value))[0]
                ulps.append(abs(bits(feature) - bits(frame['bit16'])))
                frequency_errors.append(frequency - frame['f0'])
                bins = fft(prepare(signal, weights, 2., False))
                center = I.from_address(output + at * 20 + 16).value
                a, b, cc = [abs(bins[center + delta]) for delta in (-1, 0, 1)]
                curvature = a - 2 * b + cc
                shift = .5 * (a - cc) / curvature if curvature else 0.
                height = b - .25 * (a - cc) * shift
                independent = 2 * math.log10(height / 32768)
                model_errors.append(independent - feature)
                independent_saved_errors.append(independent - frame['bit16'])
            per_sample.append(dict(case=sequence.get('category', sample_index),
                                   saved_bit16={**metrics(feature_errors),
                                                'float32_exact': sum(error == 0 for error in feature_errors),
                                                'max_ulp_difference': max(ulps),
                                                'within_two_ulp': sum(value <= 2 for value in ulps)},
                                   independent_to_native=metrics(model_errors),
                                   independent_to_saved=metrics(independent_saved_errors),
                                   native_frequency_to_saved_f0=metrics(frequency_errors)))
        rows.append(dict(window_size=size, pcm_centering=mean_mode, per_sample=per_sample))
    return rows


def run(args):
    parser_hash, ddi_hash, sequences = load_sequences(
        args.manifest, args.voice_root, args.ddb_tools)
    native = Native(args.dll)
    weights = window(native, 2048, 10)
    candidates = {(padding, scale, target): [[] for _ in sequences]
                  for padding in (1., 2., 4.)
                  for scale in ('linear', 'log')
                  for target in ('vertex', 'saved_frequency')}
    direct = [[] for _ in sequences]
    for sample_index, sequence in enumerate(sequences):
        for index in range(4, len(sequence['frames']) - 4):
            frame = sequence['frames'][index]
            samples = sequence['pcm'][index * 256 - 1024:index * 256 + 1024]
            assert len(samples) == 2048
            mean = statistics.fmean(samples)
            signal = [value - mean for value in samples]
            direct[sample_index].append(predict(
                sequence['pcm'], index, frame['f0'], weights) - frame['bit16'])
            for padding in (1., 2., 4.):
                bins = fft(prepare(signal, weights, padding, False))
                position = frame['f0'] * len(bins) / 44100
                at = round(position)
                assert 1 < at < len(bins) // 2 - 2
                for scale in ('linear', 'log'):
                    values = [abs(bins[at + delta]) for delta in (-1, 0, 1)]
                    assert min(values) > 0
                    a, b, c = ([math.log(value) for value in values]
                               if scale == 'log' else values)
                    curvature = a - 2 * b + c
                    assert curvature != 0
                    for target in ('vertex', 'saved_frequency'):
                        shift = .5 * (a - c) / curvature if target == 'vertex' else position - at
                        height = b + .5 * (c - a) * shift + .5 * curvature * shift * shift
                        amplitude = math.exp(height) if scale == 'log' else height
                        assert amplitude > 0 and math.isfinite(amplitude)
                        predicted = 2 * math.log10(amplitude / 32768)
                        candidates[padding, scale, target][sample_index].append(
                            predicted - frame['bit16'])
        print(f'Checked sample {sample_index}', flush=True)
    rows = [dict(padding=key[0], ordinate_scale=key[1], target=key[2],
                 per_sample=[metrics(errors) for errors in values],
                 combined=metrics([error for errors in values for error in errors]))
            for key, values in candidates.items()]
    report = dict(dse_sha256=SHA256, ddi_sha256=ddi_hash, parser_sha256=parser_hash,
                direct_projection=dict(per_sample=[metrics(errors) for errors in direct],
                                       combined=metrics([x for errors in direct for x in errors])),
                candidates=rows,
                native_peak_checks=native_peak_checks(native, sequences),
                best=min(rows, key=lambda row: row['combined']['mean_absolute_error']),
                scope='Three independent installed V4X VQM samples. Initial complete '
                      '2048-point kind-10 windows use 1/2/4 FFT padding and linear/log '
                      'three-bin interpolation at vertex or saved F0. Native comparisons '
                      'then cover 2048/2049 raw/mean-centered windows; optional auxiliary '
                      'TENSE/BRWSP checks use 2049 raw samples. No fitted offsets, gains '
                      'or time shifts. Peak-bin inputs in this report are native; report '
                      '385 independently selects peaks from an independent FFT.')
    if args.aux_vdb:
        report['auxiliary_sha256'] = hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest()
        report['auxiliary_native_checks'] = native_peak_checks(
            native, auxiliary_sequences(args.aux_vdb), [(2049, 'raw')])
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'output'):
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--aux-vdb', type=Path)
    args = parser.parse_args()
    report = run(args)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(direct=report['direct_projection']['combined'], best=report['best'])))


if __name__ == '__main__':
    main()
