"""Test the saved pitch/amplitude temporal kernel as a bit17 phase producer.

This is a bounded hypothesis check, not an original maker entry. Commercial
phase, feature and Fourier arrays remain in memory; only metrics are written.
"""
from __future__ import annotations

import argparse
from collections import Counter
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import struct

from audit_frm2_numeric import SHA256, f32
from audit_vqm_compact_authoring_context import OwnedLowpass, selected
from audit_vqm_compact_load_identity import frames_in_interval
from audit_vqm_legacy_live_features import RESOURCE_SHA256
from audit_vqm_legacy_phase_relations import INVALID, circular, metrics
from verify_vqm_compact_runtime_decomposition import blend_strength, mix, raw_float


def continuous_unwrap(sequence):
    result = [sequence[0]]
    for previous, current in zip(sequence, sequence[1:]):
        result.append(result[-1]+circular(current-previous))
    return result


def owned_checks(lowpass):
    reports = []
    for phase in (0., .25, 3., 6.):
        sequence = [f32(phase)]*19
        center = sequence[9]
        result = lowpass.smooth(sequence, True)[9]
        assert raw_float(result) == raw_float(center)
        reports.append(dict(case='constant_phase_'+str(phase), passed=True))
    unwrapped = continuous_unwrap([6., .1, .3])
    assert max(abs(a-b) for a, b in zip(unwrapped, [6., math.tau+.1, math.tau+.3])) < 1e-14
    reports.append(dict(case='known_wrap_crossing', passed=True))
    assert circular(6.-.1) < 0. and abs(circular(.1-(math.tau-.1))-.2) < 1e-14
    reports.append(dict(case='short_arc_direction', passed=True))
    return reports


def run(dll, directory, reference):
    data = (directory/'dvqm.dat').read_bytes()
    assert hashlib.sha256(data).hexdigest() == RESOURCE_SHA256
    prior = json.loads(reference.read_text(encoding='utf-8'))
    assert prior['samples'] == 119 and prior['resource_sha256'] == RESOURCE_SHA256
    lowpass = OwnedLowpass(dll)
    checks = owned_checks(lowpass)
    names = ['current_phase_control']+[domain+suffix for domain in (
        'linear', 'center_unwrapped', 'continuous_unwrapped', 'unit_circle') for suffix in ('', '_mixed')]
    errors = {name:[] for name in names}
    optional_errors = {name:[] for name in names}
    exact = Counter()
    counts, cases = Counter(), []
    for case in prior['cases']:
        start = int(case['track_offset'], 16)
        rows = []
        for _, _, wire in frames_in_interval(data, start, start+case['track_bytes']):
            n = wire['count']
            rows.append(dict(n=n, m=wire['spectrum_count'], pitch=struct.unpack('<f', wire['pitch'])[0],
                time=wire['time'], feature=wire['feature'],
                frequency=struct.unpack_from('<f', wire['components'][0], 4)[0] if n > 1 else None,
                phase=struct.unpack_from('<f', wire['components'][2], 4)[0] if n > 1 else None,
                bit17=struct.unpack_from('<f', wire['feature'], 0x21c)[0]))
        percase = Counter(source_frames=len(rows))
        for index, row in enumerate(rows):
            if index < 9 or len(rows)-1-index < 9:
                percase['clipped_edge_frames_excluded'] += 1
                continue
            if row['pitch'] <= INVALID or row['n'] <= 1:
                percase['unvoiced_or_missing_phase_excluded'] += 1
                continue
            pool = selected(rows, index)
            if any(other['phase'] is None for _, other in pool):
                percase['incomplete_phase_window_excluded'] += 1
                continue
            assert all(other['frequency'] > 0. for _, other in pool)
            center = next(k for k, (j, _) in enumerate(pool) if j == index)
            phases = [other['phase'] for _, other in pool]
            strength = blend_strength(rows, index, pool, 9)
            continuous = continuous_unwrap(phases)
            predicted = dict(linear=lowpass.smooth(phases, True)[center],
                center_unwrapped=row['phase']+lowpass.smooth([
                    f32(circular(phase-row['phase'])) for phase in phases], True)[center],
                continuous_unwrapped=row['phase']+lowpass.smooth([
                    f32(phase-continuous[center]) for phase in continuous], True)[center])
            cosine = lowpass.smooth([f32(math.cos(phase)) for phase in phases], True)[center]
            sine = lowpass.smooth([f32(math.sin(phase)) for phase in phases], True)[center]
            predicted['unit_circle'] = math.atan2(sine, cosine)
            alpha = min(1., f32(strength/2.5))
            candidates = dict(current_phase_control=row['phase'])
            for domain, value in predicted.items():
                candidates[domain] = f32(value % math.tau)
                mixed = mix(value, row['phase'], strength) if domain == 'linear' else f32(
                    value+alpha*circular(row['phase']-value))
                candidates[domain+'_mixed'] = f32(mixed % math.tau)
            for name, prediction in candidates.items():
                error = circular(prediction-row['bit17'])
                errors[name].append(error)
                exact[name] += raw_float(prediction) == raw_float(row['bit17'])
                if row['m']:
                    optional_errors[name].append(error)
            percase['compared_phase_frames'] += 1
            percase['compared_optional_frames'] += row['m'] > 0
            percase['nonzero_strength_frames'] += strength > 0.
        counts.update(percase)
        cases.append(dict(sample_index=case['sample_index'], counts=dict(percase)))
    assert counts['source_frames'] == 26842 and counts['compared_phase_frames'] == 22438
    assert counts['compared_optional_frames'] == 20798
    assert c.string_at(lowpass.harness.bank_base, 0x400+64) == lowpass.harness.bank_before
    assert c.string_at(lowpass.harness.context_base, 0x2de8+64) == lowpass.harness.context_before
    sources = set(prior['source_sha256']) | {
        'audit_vqm_compact_phase_lowpass.py', 'audit_vqm_compact_authoring_context.py',
        'verify_vqm_compact_runtime_decomposition.py', 'verify_vqm_temporal_lowpass.py'}
    dependency = json.loads(Path(__file__).parents[2].joinpath(
        'voicebank_doc/evidence/431a_vqm_compact_saved_baseline.json').read_text())
    sources.update(dependency['source_sha256'])
    return dict(schema=1, dse_sha256=SHA256, resource_sha256=RESOURCE_SHA256,
        reference_sha256=hashlib.sha256(reference.read_bytes()).hexdigest(),
        fft_identity=lowpass.harness.fft_identity, samples=119, owned_checks=checks,
        counts=dict(counts), cases=cases,
        candidates={name:dict(circular_error_radians=metrics(values),
            float32_bytes_exact=exact[name], optional_circular_error_radians=metrics(optional_errors[name]))
            for name, values in errors.items()},
        source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                       for name in sorted(sources)},
        scope='Fixed radius9/passband3/width5, suppressed Nyquist from431; four angular representations '
              'with no blend or runtime strength blend. Source phase1 and saved-positive-pitch interior windows only. '
              'Genuine FFTs shared; no fit, native maker call, PCM production or native audio. '
              'Failure excludes these stated producers, not other phase inputs/kernels or nonlocal state. '
              'bit17 formal definition/production and full original VQM/Growl requirements remain unresolved.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'directory', 'reference', 'output'):
        parser.add_argument('--'+name, type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll, args.directory, args.reference)
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(counts=result['counts'], candidates=result['candidates'])))


if __name__ == '__main__':
    main()
