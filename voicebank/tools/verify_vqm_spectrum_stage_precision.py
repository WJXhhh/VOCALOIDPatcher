"""Separate window/FFT precision from VQM peak and band feature arithmetic.

Hash-pinned installed material stays in memory. Reports contain aggregate
counts/errors only. Shared native spectra are a diagnostic input, never a
replacement for the independent PCM production path.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import random
import struct

from audit_frm2_numeric import Native, SHA256, F, I, P, Q, D, B, f32
from frm2_fft_peaks import detect
from verify_frm2_analysis_windows import fft, prepare, window
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences
from verify_vqm_bit6_component_ratio import predict
from verify_vqm_bit18_band_ratio import replay


AUX_SHA256 = 'a629b36e0b4d571c37f947809d34dcfa283a5af42f12d635cd5f75143ee6203c'
DFT_SHA256 = '7cae07fabcede5a9aa6ddbef596eafb73220d25ff7e343943322c93a4db60860'


def fft_library_identity():
    kernel = c.WinDLL('kernel32', use_last_error=True)
    kernel.GetModuleHandleW.argtypes = [c.c_wchar_p]
    kernel.GetModuleHandleW.restype = P
    kernel.GetModuleFileNameW.argtypes = [P, c.c_wchar_p, I]
    kernel.GetModuleFileNameW.restype = I
    handle = kernel.GetModuleHandleW('DSE_DFT.dll')
    if not handle:
        raise ValueError('Expected native FFT dependency is not loaded')
    buffer = c.create_unicode_buffer(32768)
    count = kernel.GetModuleFileNameW(handle, buffer, len(buffer))
    if not 0 < count < len(buffer):
        raise ValueError('Unable to identify the loaded FFT dependency')
    digest = hashlib.sha256(Path(buffer.value).read_bytes()).hexdigest()
    if digest != DFT_SHA256:
        raise ValueError('Loaded FFT dependency differs from the audited module')
    return dict(name='DSE_DFT.dll', sha256=digest,
                identity_source='GetModuleHandleW/GetModuleFileNameW of the loaded module')


def bits(value):
    return struct.unpack('<I', struct.pack('<f', value))[0]


def ordered(value):
    value = bits(value)
    return (~value & 0xffffffff) if value >> 31 else value | 0x80000000


class Comparison:
    def __init__(self):
        self.count = self.exact = self.within = self.max_ulp = 0
        self.total_error = self.maximum = 0.

    def add(self, actual, expected):
        assert math.isfinite(actual) and math.isfinite(expected)
        self.count += 1
        self.exact += bits(actual) == bits(expected)
        error = abs(actual - expected)
        self.within += error <= 1e-6
        self.total_error += error
        self.maximum = max(self.maximum, error)
        self.max_ulp = max(self.max_ulp, abs(ordered(actual) - ordered(expected)))

    def report(self):
        return dict(values=self.count, float32_exact=self.exact,
                    within_1e_6=self.within, max_absolute_error=self.maximum,
                    mean_absolute_error=self.total_error / self.count if self.count else None,
                    max_ulp_difference=self.max_ulp)


def scalar_model(bins, half):
    # Preserve the native reader's unusual endpoints: DC magnitude, then
    # interior squared magnitudes, then abs(CCS slot 1), the DC imaginary slot.
    # The peak/feature callers consume only the first `half` scalar slots.
    return [f32(abs(bins[0].real))] + [
        f32(f32(z.real * z.real) + f32(z.imag * z.imag))
        for z in bins[1:half]] + [f32(abs(bins[0].imag))]


def independent_linearize(db):
    return f32(32768 * math.exp(f32(db) * 0.1151292546497))


def owned_cases():
    size = 2049
    rng = random.Random(406)
    signals = dict(
        zero=[0.] * size,
        dc=[1024.] * size,
        impulse=[4096. if i == 741 else 0. for i in range(size)],
        alternating=[(-1.) ** i * 4096 for i in range(size)],
        tone=[f32(4096 * math.sin(i * math.tau * 220 / 44100)) for i in range(size)],
        two_band=[f32(4096 * math.sin(i * math.tau * 220 / 44100)
                      + 512 * math.sin(i * math.tau * 8000 / 44100)) for i in range(size)],
        noise=[f32(rng.uniform(-4096, 4096)) for _ in range(size)])
    return signals


def run(args):
    parser_hash, ddi_hash, sequences = load_sequences(
        args.manifest, args.voice_root, args.ddb_tools)
    if hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest() != AUX_SHA256:
        raise ValueError('Auxiliary material differs from the verified source')
    sequences += auxiliary_sequences(args.aux_vdb)
    assert [len(s['frames']) for s in sequences] == [72, 87, 87, 148, 389]
    native = Native(args.dll)
    fft_library = fft_library_identity()
    config, context = native.alloc(0x35f0), native.alloc(0x400)
    native.fn(0x90de0, P, P)(config)
    native.fn(0x49790, P, P, I, P, I)(context, 0, config, 0)
    analyze = native.fn(0x4e8e0, Q, P, P, I, B, F, P, P, P, B, P, F, I, I, I, I)
    peaks = native.fn(0x51e60, Q, P, P, I, P, P, F, I, F, D, F)
    linearize = native.fn(0x2af20, F, F)
    logarithmic = I.from_address(config + 0x520).value
    post_filter = I.from_address(config + 0x588).value
    threshold_db = native.fn(0x2b050, F, P, I, F)(config, 3, 0.)
    assert (logarithmic, post_filter, threshold_db) == (0, 0, -300.)
    threshold = linearize(threshold_db)
    output = native.alloc(30000)
    weights = window(native, 2049, 10)
    names = ('window', 'scalar_from_native_complex', 'fft_real', 'fft_imaginary',
             'scalar_from_independent_fft', 'scalar_from_independent_fft_float32_storage',
             'shared_power_peak_frequency', 'shared_power_peak_db', 'db_linearization',
             'shared_power_bit6', 'independent_fft_bit6_to_native',
             'independent_fft_float32_storage_bit6_to_native',
             'native_fft_bit6_to_saved', 'independent_fft_bit6_to_saved',
             'native_fft_bit16_to_saved', 'independent_fft_bit16_to_saved',
             'native_fft_bit18_to_saved', 'independent_fft_bit18_to_saved')

    def check_case(label, windows):
        checks = {key: Comparison() for key in names}
        frame_count = peak_count = shared_count_matches = model_count_matches = 0
        for signal, frame in windows:
            scalar, packed, half = P(), P(), I()
            assert len(signal) == 2049
            assert analyze(context, native.array(F, signal), 2049, 10, 1.,
                           c.byref(scalar), 0, c.byref(half), 2,
                           c.byref(packed), 2., 0, 0, 0, 0) == 0
            assert half.value == 2048
            pointer, count = P(output), I()
            assert peaks(packed, scalar, half.value, c.byref(pointer), c.byref(count),
                         44100, logarithmic, threshold, 0., 0.) == 0
            assert pointer.value == output and 0 <= count.value <= 1500
            components = [(F.from_address(output + j * 20).value,
                           F.from_address(output + j * 20 + 4).value)
                          for j in range(count.value)]
            native_weights = P.from_address(context + 0x180).value
            for j, value in enumerate(weights):
                checks['window'].add(F.from_address(native_weights + 4 * j).value, value)
            power = [F.from_address(scalar.value + 4 * j).value
                     for j in range(half.value + 1)]
            bins = [complex(F.from_address(packed.value + 8 * j).value,
                            F.from_address(packed.value + 8 * j + 4).value)
                    for j in range(half.value + 1)]
            for actual, expected in zip(power, scalar_model(bins, half.value)):
                checks['scalar_from_native_complex'].add(actual, expected)
            independent_bins = fft(prepare(signal, weights, 2., False))[:half.value + 1]
            for actual, expected in zip(bins, independent_bins):
                checks['fft_real'].add(actual.real, f32(expected.real))
                checks['fft_imaginary'].add(actual.imag, f32(expected.imag))
            float_bins = [complex(f32(z.real), f32(z.imag)) for z in independent_bins]
            independent_power = scalar_model(independent_bins, half.value)
            stored_power = scalar_model(float_bins, half.value)
            for key, values in (('scalar_from_independent_fft', independent_power),
                                ('scalar_from_independent_fft_float32_storage', stored_power)):
                for actual, expected in zip(power, values):
                    checks[key].add(actual, expected)
            shared = [tuple(p[:2]) for p in detect(power[:half.value], None, 44100,
                                                 logarithmic, threshold)]
            model = [tuple(p[:2]) for p in detect(independent_power[:half.value], None,
                                                44100, logarithmic, threshold)]
            stored = [tuple(p[:2]) for p in detect(stored_power[:half.value], None,
                                                 44100, logarithmic, threshold)]
            assert len(shared) == len(components), label
            shared_count_matches += 1
            model_count_matches += len(model) == len(components)
            for actual, expected in zip(components, shared):
                checks['shared_power_peak_frequency'].add(actual[0], expected[0])
                checks['shared_power_peak_db'].add(actual[1], expected[1])
                checks['db_linearization'].add(linearize(actual[1]), independent_linearize(actual[1]))
            native_bit6 = predict(components, linearize)
            shared_bit6 = predict(shared, independent_linearize)
            model_bit6 = predict(model, independent_linearize)
            stored_bit6 = predict(stored, independent_linearize)
            checks['shared_power_bit6'].add(native_bit6, shared_bit6)
            checks['independent_fft_bit6_to_native'].add(native_bit6, model_bit6)
            checks['independent_fft_float32_storage_bit6_to_native'].add(native_bit6, stored_bit6)
            if frame is not None:
                checks['native_fft_bit6_to_saved'].add(native_bit6, frame['features'][6])
                checks['independent_fft_bit6_to_saved'].add(model_bit6, frame['features'][6])
                assert components and model
                nearest = min(components, key=lambda p: abs(p[0] - frame['f0']))
                model_nearest = min(model, key=lambda p: abs(p[0] - frame['f0']))
                checks['native_fft_bit16_to_saved'].add(f32(nearest[1] / 10.), frame['features'][16])
                checks['independent_fft_bit16_to_saved'].add(f32(model_nearest[1] / 10.), frame['features'][16])
                checks['native_fft_bit18_to_saved'].add(replay(power[:half.value], frame['f0'], 44100),
                                                       frame['features'][18])
                checks['independent_fft_bit18_to_saved'].add(replay(independent_power[:half.value],
                                                                  frame['f0'], 44100), frame['features'][18])
            frame_count += 1
            peak_count += count.value
        result = dict(case=label, frames=frame_count, peaks=peak_count,
                      shared_power_peak_counts_equal=shared_count_matches,
                      independent_fft_peak_counts_equal=model_count_matches,
                      checks={key: value.report() for key, value in checks.items() if value.count})
        print(json.dumps(dict(case=label, frames=frame_count, peaks=peak_count,
                             shared_peak_db_exact=checks['shared_power_peak_db'].exact)), flush=True)
        # Only require numerical agreement for the well-defined storage and
        # shared-spectrum stages, never require a saved commercial field fit.
        for key in ('window', 'scalar_from_native_complex', 'shared_power_peak_frequency',
                    'shared_power_peak_db', 'db_linearization', 'shared_power_bit6'):
            assert checks[key].count == checks[key].exact, (label, key, checks[key].report())
        return result

    rows = []
    for number, sequence in enumerate(sequences):
        windows = ((sequence['pcm'][index * 256 - 1024:index * 256 + 1025],
                    sequence['frames'][index])
                   for index in range(4, len(sequence['frames']) - 4))
        rows.append(check_case(sequence.get('category', f'V4X_{number}'), windows))
    synthetic = [check_case(label, [(signal, None)]) for label, signal in owned_cases().items()]
    assert sum(row['frames'] for row in rows) == 743
    return dict(dse_sha256=SHA256, parser_sha256=parser_hash, ddi_sha256=ddi_hash,
                fft_library=fft_library,
                auxiliary_sha256=AUX_SHA256,
                manifest_sha256=hashlib.sha256(args.manifest.read_bytes()).hexdigest(),
                source_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                dependencies_sha256={path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                                     for path in (Path(__file__).parent / name for name in (
                                         'frm2_fft_peaks.py', 'verify_frm2_analysis_windows.py',
                                         'verify_vqm_bit16_spectral_power.py',
                                         'verify_vqm_bit16_peak_interpolation.py',
                                         'verify_vqm_bit6_component_ratio.py',
                                         'verify_vqm_bit18_band_ratio.py'))},
                protocol=dict(window_samples=2049, window_kind=10, fft_points=4096,
                              pcm_centering='raw', margin_frames_each_end=4,
                              logarithmic=logarithmic, post_filter=post_filter,
                              threshold_db=threshold_db, peak_phase_checked=False),
                cases=rows, synthetic_cases=synthetic,
                scope='Shared-spectrum frequency/amplitude/linearization/band-ratio audit, '
                      'not an independent FFT replacement or phase verification. Float32 '
                      'storage diagnostic changes only final FFT bin rounding, never fits '
                      'parameters or selects peaks using saved values. Saved fields are '
                      'compared separately; no claim about original commercial FFT/input '
                      'or unobserved production branches. Material and spectra stay in memory.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb', 'output'):
        parser.add_argument('--' + name, required=True, type=Path)
    args = parser.parse_args()
    args.output.write_text(json.dumps(run(args), indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
