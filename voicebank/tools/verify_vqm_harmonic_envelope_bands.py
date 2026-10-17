"""Independently check the mode-4 harmonic ENV overwrite and interval reader.

Native PCM production is unmodified. Natural comparisons reconstruct pre-gain
ordinates and absolute frequencies from observed normalized harmonics/F0;
those inverses lose float bits and
is explicitly not an independent PCM-to-harmonics production model.
"""
from __future__ import annotations

import argparse
import bisect
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import random
import struct

from audit_frm2_numeric import D, F, I, Native, P, Q, SHA256, f32
from probe_frm2_batch_outputs import owned_pcm, run as analyze
from verify_vqm_array_production_stages import block, dct, final_digest, profile_geometry, values
from verify_vqm_bit16_spectral_power import load_sequences


def harmonic_points(frequencies, amplitudes, nyquist):
    """d6a60 insertion, including initial endpoint ownership and clipping."""
    points = {0.: 0., 1.: 0.}
    for hz, db in zip(frequencies, amplitudes):
        if hz > 0:
            x = min(1., max(0., float(f32(hz / nyquist))))
            points[x] = min(0., max(-200., f32(db)))
    ordered = sorted(points.items())
    points[0.] = min(0., max(-200., f32(ordered[1][1] - 3.)))
    # The final endpoint already exists; d6aca reads that endpoint, not the
    # penultimate harmonic. Thus it is normally still the initial zero.
    points[1.] = ordered[-1][1]
    return sorted(points.items())


def env_value(points, x, mode=3):
    """Type-3 DRS reader with its two-node fallback and float coefficients."""
    if x <= points[0][0]:
        return points[0][1]
    if x >= points[-1][0]:
        return points[-1][1]
    i = bisect.bisect_right([p[0] for p in points], x) - 1
    if x == points[i][0]:
        return points[i][1]
    t = (x - points[i][0]) / (points[i + 1][0] - points[i][0])
    if mode == 1 or len(points) == 2:
        return f32(points[i][1] + t * f32(points[i + 1][1] - points[i][1]))
    assert mode == 3
    u = 1. - t
    v = t + 1.
    w = 2. - t
    weights = (f32(u * u * u / 6. - u / 6.),
        f32(w * w * w / 6. - ((u + u) * u) * u / 3. - t / 6. + u / 3.),
        f32(v * v * v / 6. - ((t + t) * t) * t / 3. - u / 6. + t / 3.),
        f32(t * t * t / 6. - t / 6.))
    result = 0.
    for index, weight in zip(range(i - 1, i + 3), weights):
        y = points[min(len(points) - 1, max(0, index))][1]
        result = f32(result + f32(y * weight))
    return result


def interval_mean(points, low, high, mode=3):
    """59260's type-3 mean, including its beyond-high knot and correction.

    Mode 3 evaluates partial endpoints cubically but integrates complete knot
    intervals as trapezoids. It is not an analytic integral of a cubic spline.
    Return status and value; a rejected range leaves the native output intact.
    """
    if high < low:
        return 1, None
    if high == low:
        return 0, env_value(points, low, mode)
    xs = [x for x, _ in points]
    first = max(1, bisect.bisect_right(xs, low))
    if first >= len(points):
        return 1, None
    last = bisect.bisect_right(xs, high)
    if last >= len(points):
        last = len(points) - 1
        high = 1.
    at = lambda x: env_value(points, x, mode)
    if first >= last:
        area = float(f32(at(low) + at(high))) * .5 * (high - low)
    else:
        area = float(f32(at(low) + points[first][1])) * .5 * (points[first][0] - low)
        # Native includes the first knot strictly above high, then adds a
        # negative-width correction back to high. Preserve this arithmetic.
        for k in range(first, last):
            width = points[k + 1][0] - points[k][0]
            ordinate = float(f32(points[k][1] + points[k + 1][1]))
            area = area + width * ordinate * .5
        if last < len(points) - 1:
            area = area + float(f32(at(high) + points[last][1])) * .5 * (high - points[last][0])
    return 0, f32(area / (high - low))


def compare(actual, expected):
    assert len(actual) == len(expected) and all(math.isfinite(x) for x in actual + expected)
    return dict(values=len(actual), exact=sum(struct.pack('<f', a) == struct.pack('<f', b)
        for a, b in zip(actual, expected)), maximum=max((abs(a - b) for a, b in zip(actual, expected)), default=0.))


def merge(target, source):
    target['values'] += source['values']
    target['exact'] += source['exact']
    target['maximum'] = max(target['maximum'], source['maximum'])


def empty():
    return dict(values=0, exact=0, maximum=0.)


def owned_contracts(dll):
    native = Native(dll)
    mean = native.fn(0x59260, Q, P, D, D, P)
    rng = random.Random(410)
    rows = []
    fixtures = [('two_nodes', [], []),
        ('single_harmonic', [2205.], [-30.]),
        ('out_of_order_duplicates', [15435., 2205., 8820., 2205., 0., -1.],
                                   [-40., -30., -10., -70., -50., -60.]),
        ('clipped_endpoints', [22050., 23000., 100., 4000.], [-300., 20., 10000., -500.])]
    for count in (3, 17, 65):
        xs = sorted(rng.uniform(.001, .999) for _ in range(count))
        fixtures.append((f'random_{count}', [f32(x * 22050.) for x in xs],
                         [f32(rng.uniform(-199., 0.)) for _ in xs]))
    for name, frequencies, amplitudes in fixtures:
        points = harmonic_points(frequencies, amplitudes, 22050.)
        for mode in (1, 3):
            guarded = native.alloc(224)
            c.memset(guarded, 0xa5, 32)
            c.memset(guarded + 192, 0x5a, 32)
            env = guarded + 32
            native.env_ctor(env)
            native.cfg(env, 3, 1, mode, -200., 0., 0., 1, 0, 0)
            for hz, db in zip(frequencies, amplitudes):
                if hz > 0:
                    assert native.add(env, float(f32(hz / 22050.)), db) == 0
            pointer = P.from_address(env + 0x78).value
            assert native.add(env, 0., f32(F.from_address(pointer + 24).value - 3.)) == 0
            pointer = P.from_address(env + 0x78).value
            count = I.from_address(env + 0x5c).value
            assert native.add(env, 1., F.from_address(pointer + 16 * (count - 1) + 8).value) == 0
            assert I.from_address(env + 0x54).value == 0
            assert I.from_address(env + 0x58).value == 0
            pointer = P.from_address(env + 0x78).value
            actual_points = [(D.from_address(pointer + 16 * k).value,
                F.from_address(pointer + 16 * k + 8).value) for k in range(count)]
            assert actual_points == points, ('insertion', name, mode)
            original_knots = c.string_at(pointer, count * 16)
            ordinates = empty()
            for x in [k / 200. for k in range(201)] + [x for x, _ in points]:
                merge(ordinates, compare([native.evaluate(env, x)], [env_value(points, x, mode)]))
            assert ordinates['exact'] == ordinates['values'], ('evaluate', name, mode, ordinates)
            ranges = [(0., 1.), (.001, .002), (.05, .31), (.25, .8), (.99, 1.),
                      (.25, 1.5), (.4, .4), (1., 1.), (.7, .3), (1., 1.1)]
            ranges.extend((x, points[k + 1][0]) for k, (x, _) in enumerate(points[:-1]))
            ranges.extend((x / 200., (x + 1) / 200.) for x in range(200))
            integrals = empty()
            rejected = 0
            for low, high in ranges:
                result = F(777.)
                expected_code, expected = interval_mean(points, low, high, mode)
                code = mean(env, low, high, c.byref(result))
                assert code == expected_code, ('status', name, mode, low, high)
                if code:
                    assert result.value == 777., 'Rejected call modified output'
                    rejected += 1
                else:
                    merge(integrals, compare([result.value], [expected]))
            assert integrals['exact'] == integrals['values'], ('mean', name, mode, integrals)
            assert c.string_at(pointer, count * 16) == original_knots
            assert c.string_at(guarded, 32) == b'\xa5' * 32
            assert c.string_at(guarded + 192, 32) == b'\x5a' * 32
            rows.append(dict(name=name, mode=mode, knot_count=count, ordinates=ordinates,
                means=integrals, rejected_unchanged_output=rejected,
                insertion_nodes_exact=True, knots_and_guards_preserved=True))
    return rows


class Observer:
    def __init__(self, alternative):
        self.alternative = alternative
        self.boundaries = profile_geometry(alternative)[3]
        self.checks = dict(frames=0, active_harmonic_slots=0, before_harmonic_frames=0,
            no_active_harmonic_frames=0, all_40_bands=empty(), all_20_cepstra=empty(),
            shared_native_bands_dct=empty(), bit17_nonzero=0,
            alternative_zero_delta_tail_values=0)
        self.target = None

    def setup(self, native, context):
        analyzer = P.from_address(context + 0xca8).value
        assert values(analyzer, 0x3e0, 42) == self.boundaries
        assert I.from_address(analyzer + 0x3b0).value == 0
        cfg = P.from_address(analyzer + 0x3b8).value
        assert I.from_address(cfg + 0x5c4).value == 1
        assert I.from_address(cfg + 0x5b4).value == self.alternative
        assert F.from_address(context + 0xbb8).value == 22050.

    def before(self, native, context):
        queue = P.from_address(context + 0xc50).value
        self.target = None
        if queue and P.from_address(queue).value:
            self.target = P.from_address(P.from_address(queue).value + 0x28).value
            self.checks['before_harmonic_frames'] += I.from_address(self.target + 0x58).value > 0

    def after(self, native, context, status):
        frame = P.from_address(context + 0xcc8).value
        if status or not frame:
            return
        assert frame == self.target
        count = I.from_address(frame + 0x58).value
        assert 0 < count <= 1500
        frequencies = values(P.from_address(frame + 0x60).value, 0, count)
        normalized = values(P.from_address(frame + 0x68).value, 0, count)
        mask = Q.from_address(frame + 0x50).value
        pitch = F.from_address(frame + 0xf0).value
        assert mask & (1 << 31) and mask & (1 << 20) and mask & (1 << 10), (
            'Expected observed cents, relative-frequency and gain output protocol')
        f0 = f32(f32(math.pow(2., f32(pitch / 1200.))) * 440.)
        frequencies = [f32(hz * f0) for hz in frequencies]
        gain = F.from_address(frame + 0xf8).value
        # Inverse of 86cb0's f32(db-gain). It is not a bitwise unique inverse.
        amplitudes = [db if db == 10000. else f32(db + gain) for db in normalized]
        positive = sum(hz > 0 for hz in frequencies)
        self.checks['active_harmonic_slots'] += positive
        self.checks['no_active_harmonic_frames'] += positive == 0
        points = harmonic_points(frequencies, amplitudes, 22050.)
        expected = []
        for k in range(40):
            low, high = f32(self.boundaries[k] / 22050.), f32(self.boundaries[k + 2] / 22050.)
            code, value = interval_mean(points, low, high)
            assert code == 0
            expected.append(value)
        feature = block(frame)
        actual = values(feature, 0x2c, 40)
        comparison = compare(actual, expected)
        assert comparison['maximum'] <= 1e-4, ('bands', self.alternative, self.checks['frames'], comparison)
        merge(self.checks['all_40_bands'], comparison)
        actual_ceps = values(feature, 0x16c, 20)
        comparison = compare(actual_ceps, dct(expected, [0.] * 20))
        assert comparison['maximum'] <= 1e-3, ('cepstra', self.alternative, comparison)
        merge(self.checks['all_20_cepstra'], comparison)
        comparison = compare(actual_ceps, dct(actual, [0.] * 20))
        assert comparison['exact'] == 20, ('shared DCT', self.alternative, comparison)
        merge(self.checks['shared_native_bands_dct'], comparison)
        self.checks['frames'] += 1
        self.checks['bit17_nonzero'] += F.from_address(feature + 0x21c).value != 0
        if self.alternative:
            tail = values(feature, 0xcc + 24 * 4, 16) + values(feature, 0x1bc + 13 * 4, 7)
            assert all(x == 0. for x in tail), 'Unexpected unused temporal tail'
            self.checks['alternative_zero_delta_tail_values'] += len(tail)


def natural_case(dll, name, pcm, alternative):
    observer = Observer(alternative)
    digests = []
    def collect(native, context, collection):
        digests.append(final_digest(native, context, collection))
    options = dict(analysis_mode=4, estimator_variant=1, config_5b4=alternative,
        streaming=True, pcm_override=pcm, result_observer=collect)
    observed = analyze(dll, 0, 1, 'steady', 440., setup_observer=observer.setup,
        before_step_observer=observer.before, after_step_observer=observer.after, **options)
    baseline = analyze(dll, 0, 1, 'steady', 440., **options)
    assert observed == baseline and digests[0] == digests[1]
    assert observer.checks['frames'] == observed['frames']
    assert observer.checks['before_harmonic_frames'] == 0
    assert observer.checks['bit17_nonzero'] == 0
    return dict(name=name, alternative=alternative, checks=observer.checks,
        observer_matches_unobserved=True, feature_sha256=digests[0]['feature_sha256'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'output'):
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--quick', action='store_true')
    args = parser.parse_args()
    rng = random.Random(410)
    signals = dict(zero=[0.] * 11025, harmonic=owned_pcm('steady', 440.),
        gap=owned_pcm('gap', 440.), noise=[f32(rng.uniform(-5000, 5000)) for _ in range(11025)])
    parser_hash, ddi_hash, sequences = load_sequences(args.manifest, args.voice_root, args.ddb_tools)
    if not args.quick:
        signals.update({f'v4x_{k}': sequence['pcm'] for k, sequence in enumerate(sequences)})
    contracts = owned_contracts(args.dll)
    rows = [natural_case(args.dll, name, pcm, alternative)
            for name, pcm in signals.items() for alternative in (0, 1)]
    names = ('verify_vqm_harmonic_envelope_bands.py', 'audit_frm2_numeric.py',
        'probe_frm2_batch_outputs.py', 'verify_vqm_array_production_stages.py',
        'verify_vqm_bit16_spectral_power.py', 'frm2_classification_geometry.py',
        'verify_frm2_analysis_windows.py', 'frm2_classification_bands.py')
    report = dict(dse_sha256=SHA256, parser_sha256=parser_hash, ddi_sha256=ddi_hash,
        entries=dict(env_insert='0x3af0', env_evaluate='0x2b30', env_mean='0x59260',
                     overwrite_range=['0xd696f', '0xd6bea'], normalize='0x86cb0'),
        protocol=dict(analysis_mode=4, estimator_id=133, estimator_variant=1,
            alternatives=[0, 1], overwrite_bands=40, overwrite_cepstra=20,
            env_type=3, env_interpolation=3, env_bounds=[-200., 0., 0.]),
        source_sha256={name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                       for name in names}, owned_contracts=contracts, cases=rows,
        scope='Unmodified natural streaming PCM. Independent insertion, interval arithmetic '
              'and DCT with shared observed normalized harmonics, cents F0 and gain. Inverse '
              'frequency/amplitude normalization cannot recover original harmonic bits uniquely. Shared-band DCT is a '
              'separate exact check. Not independent PCM-to-harmonics, all producer settings '
              'or PCM-to-bit17. Reference arrays remain in memory.')
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(cases=len(rows), frames=sum(r['checks']['frames'] for r in rows))))


if __name__ == '__main__':
    main()
