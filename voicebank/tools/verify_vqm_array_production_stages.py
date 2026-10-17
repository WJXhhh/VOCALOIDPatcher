"""Observe classification arrays through natural offline PCM analysis and output.

No frame or harmonic inputs are replaced. Models cover two mode-zero profiles;
a separate active harmonic-envelope branch matrix records later source changes.
Reference PCM and intermediate arrays remain in memory; reports contain errors.
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

from audit_frm2_numeric import D, F, I, Native, P, Q, SHA256, f32
from frm2_classification_bands import bands
from frm2_classification_geometry import geometry
from probe_frm2_batch_outputs import owned_pcm, run as analyze
from verify_frm2_analysis_windows import window
from verify_vqm_indexed_temporal_features import guarded_feature, temporal
from verify_vqm_bit16_spectral_power import load_sequences


def values(pointer, start, count):
    return [F.from_address(pointer + start + 4 * k).value for k in range(count)]


def block(frame):
    pointer = P.from_address(frame + 0x128).value
    assert pointer and Q.from_address(frame + 0x50).value & 8192
    return pointer


def dct(source, initial):
    result = list(initial)
    for k in range(len(initial)):
        for j, value in enumerate(source):
            angle = (j + 0.5) * k * math.pi / len(source)
            result[k] = f32(math.cos(angle) * value + result[k])
    return result


def alternate_bands(power, nyquist, boundaries):
    result = [0.0] * 24
    for k in range(1, len(power)):
        hz = f32(f32(k * nyquist) / len(power))
        if hz > boundaries[25]:
            break
        for j in range(25):
            lo, hi = boundaries[j:j + 2]
            if lo < hz < hi:
                width = f32(hi - lo)
                if j:
                    weight = f32(1 - f32(f32(hz - lo) / width))
                    result[j - 1] = f32(result[j - 1] + f32(weight * power[k]))
                if j < 24:
                    weight = f32(1 - f32(f32(hi - hz) / width))
                    result[j] = f32(result[j] + f32(weight * power[k]))
                break
    return [f32(f32(math.log10(value)) * 10) if value > 0 else -100.0
            for value in result]


def profile_geometry(alternative):
    if not alternative:
        return geometry(44100)
    size = (44100 * 512) // 22050 + 1
    half = int(2 ** math.ceil(f32(math.log(size - 1)) / math.log(2)) * .5)
    low = f32(math.log(1.0)) * 1127.01048
    step = (f32(math.log(8.0)) * 1127.01048 - low) / 25
    boundaries = [f32((math.exp((k * step + low) / 1127.01048) - 1) * 700)
                  for k in range(42)]
    return size, half, window(None, size, 11), boundaries


class Observer:
    def __init__(self, pcm, alternative, estimator_variant=0):
        self.pcm = pcm
        self.alternative = alternative
        self.estimator_variant = estimator_variant
        self.bands, self.ceps = (24, 13) if alternative else (40, 20)
        self.size, self.half, self.weights, self.boundaries = profile_geometry(alternative)
        self.sources = {}
        self.history = {name: [[0.] * length for _ in range(8)]
                        for name, length in (('band', self.bands), ('cep', self.ceps))}
        self.pending = None
        self.totals = dict(classified_frames=0, emitted_frames=0, power_exact_values=0,
                           band_values=0, band_exact_values=0, band_max_absolute=0.0,
                           dct_values=0, dct_exact_values=0, dct_max_absolute=0.0,
                           temporal_exact_values=0, output_source_exact_values=0,
                           output_bit17_nonzero=0, output_array_nonzero=0,
                           source_changed_band_frames=0, source_changed_cep_frames=0,
                           temporal_compared_values=0, emitted_nonzero_harmonic_frames=0)
        self.guard_profile = None

    def setup(self, native, context):
        analyzer = P.from_address(context + 0xca8).value
        assert analyzer and I.from_address(analyzer + 0x3b0).value == 0
        config = P.from_address(analyzer + 0x3b8).value
        assert I.from_address(config + 0x5b4).value == self.alternative
        assert I.from_address(config + 0x5a0).value == 0
        assert I.from_address(analyzer + 0x698).value == self.size
        assert I.from_address(analyzer + 0x69c).value == self.half
        assert values(analyzer, 0x3e0, 42) == self.boundaries
        coefficient_pointer = P.from_address(analyzer + 0x6a8).value
        assert values(coefficient_pointer, 0, self.size) == self.weights
        assert I.from_address(analyzer + 0x6d0).value == 8
        overwrite = native.fn(0x2b050, F, P, I, F)(config, 0x85, 0.)
        assert overwrite == float(self.estimator_variant)
        self.guard_profile = dict(mode=0, alternative=self.alternative,
            window_size=self.size, bins=self.half, kind=11,
            history_slots=8, harmonic_envelope_overwrite_id133=overwrite)

    def before(self, native, context):
        count = I.from_address(context + 0xc18).value
        assert 3 <= count <= 128
        queue = P.from_address(context + 0xc20).value
        frames = [P.from_address(P.from_address(queue + k * 8).value + 0x28).value
                  for k in range(count)]
        analyzer = P.from_address(context + 0xca8).value
        token = dict(fresh=frames[-1], center=I.from_address(context + 0xb00).value,
                     fresh_mask=Q.from_address(frames[-1] + 0x50).value,
                     target=frames[0], source_frames=frames[:3], sources=None)
        if D.from_address(frames[0] + 0x48).value >= 0:
            # With the three-link mode-0 queue, the second lookahead is the
            # fresh frame. Its source arrays are produced during this step.
            source = [block(frame) if Q.from_address(frame + 0x50).value & 8192 else None
                      for frame in frames[:3]]
            token['sources'] = {name: [values(p, offset, length) if p else None for p in source]
                for name, offset, length in (('band', 0x2c, self.bands),
                                             ('cep', 0x16c, self.ceps))}
            for name, offset, length in (('band', 0x6c0, self.bands),
                                         ('cep', 0x6b8, self.ceps)):
                vector = P.from_address(analyzer + offset).value
                for k in range(8):
                    actual = values(P.from_address(vector + 8 * k).value, 0, length)
                    assert struct.pack('<' + 'f' * length, *actual) == struct.pack(
                        '<' + 'f' * length, *self.history[name][k]), 'History model differs'
        self.pending = token

    def after(self, native, context, status):
        if status == 1:
            return
        assert status == 0
        output = P.from_address(context + 0xcc8).value
        token = self.pending
        assert token is not None
        if output:
            assert output == token['target']
        fresh = token['fresh']
        assert not token['fresh_mask'] & 8192
        feature = block(fresh)
        analyzer = P.from_address(context + 0xca8).value
        rate = I.from_address(analyzer + 0x3c0).value
        ring_size = I.from_address(analyzer + 0x3d0).value
        ring = P.from_address(analyzer + 0x3c8).value
        absolute = round(D.from_address(fresh + 0x48).value * rate)
        start = token['center'] - self.size // 2
        prepared = [0.] * (2 * self.half)
        # e3640 passes window_size-1 to 519a0. The copied last sample does
        # not participate, and may be at the exclusive streaming push end.
        for k in range(self.size - 1):
            at = absolute - self.size // 2 + k
            sample = f32(self.pcm[at]) if 0 <= at < len(self.pcm) else 0.
            assert F.from_address(ring + ((start + k) % ring_size) * 4).value == sample, (
                'PCM availability', self.alternative, absolute, at,
                D.from_address(context + 0xaf8).value * rate)
            prepared[k] = f32(sample * self.weights[k])
        power = [f32(prepared[0] * prepared[0])] + [
            f32(f32(prepared[k * 2] ** 2) + f32(prepared[k * 2 + 1] ** 2))
            for k in range(1, self.half)]
        pointer = P.from_address(analyzer + 0x6b0).value
        assert values(pointer, 0, self.half) == power
        assert F.from_address(pointer + 4 * self.half).value == f32(prepared[1] ** 2)
        self.totals['power_exact_values'] += self.half + 1
        predicted_bands = (alternate_bands(power, float(rate // 2), self.boundaries)
                           if self.alternative else bands(power, float(rate // 2), self.boundaries))
        actual_bands = values(feature, 0x2c, self.bands)
        error = max(abs(a - b) for a, b in zip(actual_bands, predicted_bands))
        assert error <= 1e-4, ('bands', self.alternative, absolute, error)
        self.totals['band_values'] += self.bands
        self.totals['band_exact_values'] += sum(struct.pack('<f', a) == struct.pack('<f', b)
            for a, b in zip(actual_bands, predicted_bands))
        self.totals['band_max_absolute'] = max(self.totals['band_max_absolute'], error)
        # The shared-band comparison isolates DCT rounding from logf differences.
        predicted_ceps = dct(actual_bands, [0.] * self.ceps)
        actual_ceps = values(feature, 0x16c, self.ceps)
        error = max(abs(a - b) for a, b in zip(actual_ceps, predicted_ceps))
        assert error <= 1e-4, ('dct', self.alternative, absolute, error)
        self.totals['dct_values'] += self.ceps
        self.totals['dct_exact_values'] += sum(struct.pack('<f', a) == struct.pack('<f', b)
            for a, b in zip(actual_ceps, predicted_ceps))
        self.totals['dct_max_absolute'] = max(self.totals['dct_max_absolute'], error)
        timestamp = round(D.from_address(fresh + 0x48).value * 44100 / 256)
        assert timestamp not in self.sources
        self.sources[timestamp] = dict(band=actual_bands, cep=actual_ceps)
        self.totals['classified_frames'] += 1
        if not output:
            return
        if token['sources'] is None:
            raise AssertionError('Emitted target has no observed source history')
        target = block(output)
        at = round(D.from_address(output + 0x48).value * 44100 / 256)
        assert at in self.sources
        for name, destination, source, length in (('band', 0xcc, 0x2c, self.bands),
                                                  ('cep', 0x1bc, 0x16c, self.ceps)):
            old, recent = self.history[name][-2:]
            _, next1, next2 = token['sources'][name]
            if next1 is None:
                next1 = values(block(token['source_frames'][1]), source, length)
            if next2 is None:
                next2 = values(block(token['source_frames'][2]), source, length)
            expected = [temporal(a, b, c1, c2, name == 'cep')
                        for a, b, c1, c2 in zip(old, recent, next1, next2)]
            actual = values(target, destination, length)
            matching = sum(struct.pack('<f', a) == struct.pack('<f', b)
                           for a, b in zip(actual, expected))
            if not self.estimator_variant:
                assert matching == length, ('temporal', name, at)
            actual_source = values(target, source, length)
            source_matching = sum(struct.pack('<f', a) == struct.pack('<f', b)
                for a, b in zip(actual_source, self.sources[at][name]))
            if not self.estimator_variant:
                assert source_matching == length, ('source changed', name, at)
            self.totals['source_changed_' + name + '_frames'] += source_matching != length
            self.totals['temporal_exact_values'] += matching
            self.totals['temporal_compared_values'] += length
            self.totals['output_source_exact_values'] += source_matching
            self.totals['output_array_nonzero'] += sum(x != 0 for x in actual + actual_source)
            self.history[name] = self.history[name][1:] + [actual_source]
        self.totals['emitted_frames'] += 1
        self.totals['output_bit17_nonzero'] += F.from_address(target + 0x21c).value != 0
        self.totals['emitted_nonzero_harmonic_frames'] += I.from_address(output + 0x58).value > 0


def final_digest(native, context, collection):
    track = P.from_address(context + 0xcd0).value
    regions = P.from_address(track + 0x148).value
    digest = hashlib.sha256()
    count = 0
    for k in range(I.from_address(track + 0x140).value):
        region = P.from_address(regions + 8 * k).value
        frames = P.from_address(region + 0x20).value
        for j in range(I.from_address(region + 0x18).value):
            frame = P.from_address(frames + j * 8).value
            digest.update(c.string_at(block(frame), 0x228))
            count += 1
    return dict(frames=count, feature_sha256=digest.hexdigest())


def case(dll, name, pcm, alternative, mode, estimator_variant=0):
    observer = Observer(pcm, alternative, estimator_variant)
    final = []
    def collect(native, context, collection):
        final.append(final_digest(native, context, collection))
    options = dict(analysis_mode=mode, config_5b4=alternative, pcm_override=pcm,
                   estimator_variant=estimator_variant,
                   streaming=True, result_observer=collect)
    observed = analyze(dll, 0, 1, 'steady', 440., setup_observer=observer.setup,
                       before_step_observer=observer.before, after_step_observer=observer.after, **options)
    baseline = analyze(dll, 0, 1, 'steady', 440., **options)
    assert observed == baseline and final[0] == final[1], 'Observation changed final results'
    assert observer.totals['emitted_frames'] == observed['frames']
    assert observer.totals['output_bit17_nonzero'] == 0
    return dict(name=name, alternative=alternative, analysis_mode=mode,
                estimator_variant=estimator_variant,
                profile=observer.guard_profile, checks=observer.totals,
                observer_matches_unobserved_output=True,
                final_feature_sha256=final[0]['feature_sha256'])


def additive_checks(dll):
    native = Native(dll)
    rows = []
    rng = random.Random(409)
    for alternative in (0, 1):
        cfg = native.alloc(0x35f0)
        native.fn(0x90de0, P, P)(cfg)
        I.from_address(cfg + 0x54c).value = 1
        I.from_address(cfg + 0x5b4).value = alternative
        ctx = native.alloc(0xce0)
        native.fn(0xcdde0, P, P, I, P)(ctx, 44100, cfg)
        analyzer = P.from_address(ctx + 0xca8).value
        size = I.from_address(analyzer + 0x3d0).value
        assert 4096 <= size <= 100000
        ring = P.from_address(analyzer + 0x3c8).value
        band_count, cep_count = (24, 13) if alternative else (40, 20)
        invoke = native.fn(0xe3640, Q, P, P, I)
        for shape in ('zero', 'tone', 'noise'):
            pcm = ([0.] * size if shape == 'zero' else
                   [f32(5000 * math.sin(math.tau * 440 * k / 44100)) for k in range(size)]
                   if shape == 'tone' else [f32(rng.uniform(-5000, 5000)) for _ in range(size)])
            c.memmove(ring, struct.pack('<' + 'f' * size, *pcm), size * 4)
            for center in (0, 512, size - 32, 12345):
                for seeded in (False, True):
                    frame = native.alloc(0x230)
                    native.frame_ctor(frame)
                    base, feature = guarded_feature(native)
                    P.from_address(frame + 0x128).value = feature
                    Q.from_address(frame + 0x50).value = 8192
                    if not seeded:
                        c.memset(feature + 0x16c, 0, cep_count * 4)
                    exact = 0
                    bands_prior = None
                    for repeat in range(2):
                        before = c.string_at(base, 0x228 + 64)
                        initial = values(feature, 0x16c, cep_count)
                        assert invoke(analyzer, frame, center) == 0
                        source = values(feature, 0x2c, band_count)
                        expected_ceps = dct(source, initial)
                        actual_ceps = values(feature, 0x16c, cep_count)
                        assert struct.pack('<' + 'f' * cep_count, *expected_ceps) == struct.pack(
                            '<' + 'f' * cep_count, *actual_ceps), 'Additive DCT mismatch'
                        after = c.string_at(base, 0x228 + 64)
                        allowed = set(range(32 + 0x2c, 32 + 0x2c + band_count * 4))
                        allowed.update(range(32 + 0x16c, 32 + 0x16c + cep_count * 4))
                        assert all(a == b for k, (a, b) in enumerate(zip(before, after))
                                   if k not in allowed), 'Classification write outside arrays'
                        if bands_prior is not None:
                            assert source == bands_prior, 'Band overwrite depends on old values'
                        bands_prior = source
                        exact += cep_count
                    rows.append(dict(alternative=alternative, shape=shape, physical_center=center,
                        seeded_cepstrum=seeded, calls=2, exact_cepstral_values=exact,
                        band_overwrite_repeatable=True, dct_uses_initial_values=True,
                        outside_source_arrays_and_bit17_preserved=True, guards_preserved=True))
    return dict(cases=rows, calls=sum(row['calls'] for row in rows),
                exact_cepstral_values=sum(row['exact_cepstral_values'] for row in rows),
                scope='Owned ring and preexisting guarded feature block at genuine '
                      'classifier entry; explicit shared-band additive DCT contract. '
                      'Repeated calls on the same frame are boundary fixtures, '
                      'not a commercial PCM production protocol.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'output'):
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--quick', action='store_true')
    args = parser.parse_args()
    rng = random.Random(409)
    signals = dict(zero=[0.] * 11025, harmonic=owned_pcm('steady', 440),
                   gap=owned_pcm('gap', 440), noise=[f32(rng.uniform(-5000, 5000))
                                                  for _ in range(11025)])
    parser_hash, ddi_hash, sequences = load_sequences(args.manifest, args.voice_root, args.ddb_tools)
    if not args.quick:
        signals.update({f'v4x_{k}': sequence['pcm'] for k, sequence in enumerate(sequences)})
    rows = [case(args.dll, name, pcm, alternative, mode)
            for name, pcm in signals.items() for alternative in (0, 1)
            for mode in ((4,) if args.quick else (0, 4))]
    overwrite_rows = [case(args.dll, name, pcm, alternative, 4, 1)
                      for name, pcm in signals.items() for alternative in (0, 1)]
    assert all(row['checks']['source_changed_band_frames'] > 0
               and row['checks']['source_changed_cep_frames'] > 0
               for row in overwrite_rows if row['name'] != 'zero')
    names = ('verify_vqm_array_production_stages.py', 'probe_frm2_batch_outputs.py',
             'verify_vqm_indexed_temporal_features.py', 'frm2_classification_bands.py',
             'frm2_classification_geometry.py', 'verify_frm2_analysis_windows.py',
             'audit_frm2_numeric.py', 'verify_vqm_bit16_spectral_power.py')
    report = dict(dse_sha256=SHA256, parser_sha256=parser_hash, ddi_sha256=ddi_hash,
        source_sha256={name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                       for name in names}, cases=rows, harmonic_overwrite_cases=overwrite_rows,
        additive=additive_checks(args.dll),
        scope='Natural offline streaming PCM analysis, two preconstructed profiles '
              'and modes 0/4, no frame/harmonic replacement. Scalar preparation '
              'here is the reviewed legacy no-FFT path. Bands independently '
              'computed from PCM; DCT comparison explicitly shares native bands; '
              'temporal comparison uses observed source arrays and independently '
              'maintained history, verifying all eight native history slots. '
              'ID133=1 mode-4 matrix observes the identified later overwrite '
              'and counts source/history differences; it does not independently '
              'reproduce the harmonic-envelope integral. Not all overwrite branches, original maker '
              'configuration or PCM-to-bit17 production.')
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(cases=len(rows), frames=sum(row['checks']['emitted_frames'] for row in rows),
                         overwrite_cases=len(overwrite_rows),
                         overwrite_frames=sum(row['checks']['emitted_frames'] for row in overwrite_rows),
                         checks={key: sum(row['checks'][key] for row in rows) for key in
                                 ('power_exact_values', 'band_values', 'band_exact_values',
                                  'dct_values', 'dct_exact_values', 'temporal_exact_values')})))


if __name__ == '__main__':
    main()
