"""Read-only phase relations in all 119 compact DVQM loaded samples.

This measures loaded FRM2 objects, not PCM analysis or the original maker.
DC slots are explicitly excluded from physical phase candidates. Only counts,
hashes and aggregate errors are persisted, never installed arrays or PCM.
"""
from __future__ import annotations

import argparse
from collections import Counter
import cmath
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import statistics
import struct

from audit_frm2_numeric import D, F, I, Native, P, Q, SHA256
from audit_vqm_legacy_live_features import RESOURCE_SHA256, catalog, loaded_regions, pointer_items


NAMES = ('first_positive_frequency', 'nearest_physical_f0', 'strongest_near_physical_f0')
INVALID = -3.4028234663852886e38


def circular(value):
    return math.remainder(value, math.tau)


def metrics(values):
    values = [abs(value) for value in values]
    return dict(compared=len(values), mean_absolute_error=statistics.fmean(values) if values else None,
                median_absolute_error=statistics.median(values) if values else None,
                max_absolute_error=max(values) if values else None,
                within_1e_6=sum(value <= 1e-6 for value in values),
                within_1e_3=sum(value <= 1e-3 for value in values),
                within_1e_2=sum(value <= 1e-2 for value in values))


def phase_indices(frequencies, amplitudes, f0):
    valid = [k for k, hz in enumerate(frequencies) if hz > 0]
    result = {'first_positive_frequency': valid[0]} if valid else {}
    if f0 <= 0 or not valid:
        return result
    result['nearest_physical_f0'] = min(valid, key=lambda k: abs(frequencies[k]-f0))
    nearby = [k for k in valid if .5*f0 <= frequencies[k] <= 1.5*f0]
    if nearby:
        result['strongest_near_physical_f0'] = max(nearby, key=lambda k: amplitudes[k])
    return result


def physical_f0(stored, mask):
    if stored <= INVALID:
        return 0.
    return 440.*math.exp2(stored/1200.) if mask & 0x80000000 else max(0., stored)


def owned_checks():
    assert phase_indices([0., 210., 220., 300.], [999., -30., -40., -20.], 220.) == {
        'first_positive_frequency': 1, 'nearest_physical_f0': 2, 'strongest_near_physical_f0': 3}
    assert phase_indices([0., -1.], [0., 1.], 220.) == {}
    assert phase_indices([0., 210.], [999., -30.], 0.) == {'first_positive_frequency': 1}
    assert phase_indices([0., 210.], [999., -30.], 1000.) == {
        'first_positive_frequency': 1, 'nearest_physical_f0': 1}
    assert physical_f0(INVALID, 0x80000000) == 0.
    assert physical_f0(-1200., 0x80000000) == 220.
    assert physical_f0(440., 0) == 440.
    assert abs(circular(.1-(math.tau-.1))-.2) < 1e-14
    return 8


def snapshot(control):
    """Hash relevant loaded state so purely numerical comparisons cannot edit it."""
    digest = hashlib.sha256()
    counts = Counter()
    regions = loaded_regions(control)
    for region in regions:
        for frame in pointer_items(region, 0x18, 0x20):
            counts['frames'] += 1
            digest.update(c.string_at(frame+0x48, 0x10))
            digest.update(c.string_at(frame+0xf0, 4))
            feature = P.from_address(frame+0x128).value
            assert feature
            digest.update(c.string_at(feature, 0x228))
            count = I.from_address(frame+0x58).value
            assert 0 < count <= 10000
            digest.update(struct.pack('<I', count))
            for offset in (0x60, 0x68, 0x70):
                pointer = P.from_address(frame+offset).value
                assert pointer
                digest.update(c.string_at(pointer, 4*count))
            counts['spectral_slots'] += count
    return dict(counts=counts, sha256=digest.hexdigest())


def analyze_frames(rows):
    counts = Counter()
    errors = {name: {str(sign): [] for sign in (-1, 1)} for name in NAMES}
    nonzero_errors = {name: {str(sign): [] for sign in (-1, 1)} for name in NAMES}
    step_errors = {name: {str(sign): {mode: [] for mode in ('direct', 'quarter_cap', 'subtract_f0_advance')}
                         for sign in (-1, 1)} for name in NAMES}
    zero_steps, observed_steps, calibrated = [], [], {}
    previous = None
    voiced_steps, nearest_frequency_errors, relative_frequency_errors = [], [], []
    elapsed_values, nominal_hop_steps = [], []
    nearest_slots = Counter()
    prepared = {name: [] for name in NAMES}
    for row in rows:
        counts['frames'] += 1
        counts['nonzero_bit17_frames'] += row['bit17'] != 0.
        counts['voiced_frames'] += row['f0'] > 0.
        counts['voiced_zero_bit17_frames'] += row['f0'] > 0. and row['bit17'] == 0.
        counts['unvoiced_nonzero_bit17_frames'] += row['f0'] == 0. and row['bit17'] != 0.
        if 'nearest_physical_f0' in row['candidates']:
            selected = row['candidates']['nearest_physical_f0']
            nearest_slots[selected['slot']] += 1
            nearest_frequency_errors.append(selected['frequency']-row['f0'])
            relative_frequency_errors.append(selected['frequency']/row['f0']-1)
            counts['nearest_is_first_positive'] += selected['slot'] == row['candidates']['first_positive_frequency']['slot']
        for name, candidate in row['candidates'].items():
            phase = candidate['phase']
            counts[name+'_frames'] += 1
            counts[name+'_uses_slot_zero'] += candidate['slot'] == 0
            prepared[name].append((row['bit17'], phase))
            for sign in (-1, 1):
                error = circular(row['bit17']-sign*phase)
                errors[name][str(sign)].append(error)
                if row['bit17'] != 0.:
                    nonzero_errors[name][str(sign)].append(error)
        if previous is not None:
            observed = circular(row['bit17']-previous['bit17'])
            zero_steps.append(observed)
            observed_steps.append(observed)
            counts['adjacent_pairs'] += 1
            counts['quarter_limit_hits'] += abs(abs(observed)-.25) <= 1e-5
            counts['steps_above_quarter_limit'] += abs(observed) > .25001
            counts['wraps'] += abs(row['bit17']-previous['bit17']) > math.pi
            if row['f0'] > 0. and previous['f0'] > 0.:
                voiced_steps.append(observed)
                counts['both_voiced_pairs'] += 1
                counts['both_voiced_steps_above_quarter_limit'] += abs(observed) > .25001
                counts['both_voiced_steps_above_tenth_limit'] += abs(observed) > .10001
            elapsed = row['time']-previous['time']
            assert elapsed > 0.
            elapsed_values.append(elapsed)
            if row['f0'] > 0. and previous['f0'] > 0.:
                rate_step = observed * (256./44100.)/elapsed
                nominal_hop_steps.append(rate_step)
                counts['both_voiced_nominal_hop_steps_above_quarter'] += abs(rate_step) > .25001
            for name in row['candidates'].keys() & previous['candidates'].keys():
                phase_step = circular(row['candidates'][name]['phase']-previous['candidates'][name]['phase'])
                advance = math.tau * .5 * (row['f0']+previous['f0']) * elapsed
                for sign in (-1, 1):
                    predictions = dict(direct=sign*phase_step,
                        quarter_cap=max(-.25, min(.25, sign*phase_step)),
                        subtract_f0_advance=circular(sign*phase_step-advance))
                    for mode, prediction in predictions.items():
                        step_errors[name][str(sign)][mode].append(circular(observed-prediction))
        previous = row
    # Diagnostic only: use the first eight available phases to estimate one
    # fixed rotation, then evaluate all remaining phases, without refitting.
    for name, pairs in prepared.items():
        calibrated[name] = {}
        if len(pairs) <= 8:
            continue
        for sign in (-1, 1):
            rotation = cmath.phase(sum(cmath.exp(1j*circular(a-sign*b)) for a, b in pairs[:8]))
            calibrated[name][str(sign)] = metrics(circular(a-sign*b-rotation) for a, b in pairs[8:])
    return dict(counts=dict(counts), bit17_range=[min(row['bit17'] for row in rows),
                max(row['bit17'] for row in rows)],
        max_absolute_circular_step=max(map(abs, observed_steps), default=None),
        max_absolute_both_voiced_step=max(map(abs, voiced_steps), default=None),
        loaded_timestamp_delta=dict(compared=len(elapsed_values), minimum=min(elapsed_values),
            median=statistics.median(elapsed_values), maximum=max(elapsed_values)),
        nominal_256_sample_step_rate_diagnostic=metrics(nominal_hop_steps),
        nearest_selected_slot_counts=dict(nearest_slots),
        nearest_frequency_minus_f0=dict(compared=len(nearest_frequency_errors),
            mean_absolute_hz=statistics.fmean(map(abs, nearest_frequency_errors)) if nearest_frequency_errors else None,
            maximum_absolute_hz=max(map(abs, nearest_frequency_errors), default=None),
            maximum_absolute_relative_error=max(map(abs, relative_frequency_errors), default=None)),
        zero_step_control=metrics(zero_steps),
        both_voiced_zero_step_control=metrics(voiced_steps),
        direct_level={name: {sign: metrics(values) for sign, values in group.items()}
                      for name, group in errors.items()},
        nonzero_target_level={name: {sign: metrics(values) for sign, values in group.items()}
                              for name, group in nonzero_errors.items()},
        step_models={name: {sign: {mode: metrics(values) for mode, values in modes.items()}
                           for sign, modes in group.items()} for name, group in step_errors.items()},
        first_eight_rotation_diagnostic=calibrated)


def run(dll, directory, limit):
    resource = directory/'dvqm.dat'
    with resource.open('rb') as stream:
        resource_hash = hashlib.file_digest(stream, 'sha256').hexdigest()
    assert resource_hash == RESOURCE_SHA256
    native = Native(dll)
    root = native.fn(0xfea0, P, c.c_char_p)(str(directory.resolve()).encode('utf-8'))
    assert root
    cases, counts = [], Counter()
    frame_masks, dc_phases = Counter(), Counter()
    all_step_errors = {name: {str(sign): {mode: [] for mode in ('direct', 'quarter_cap', 'subtract_f0_advance')}
                             for sign in (-1, 1)} for name in NAMES}
    all_errors = {name: {str(sign): [] for sign in (-1, 1)} for name in NAMES}
    try:
        for index, (side, descriptor, label, sample) in enumerate(catalog(native, root)[:limit]):
            control = native.fn(0xb400, P)()
            assert control
            try:
                assert native.fn(0x100a0, Q, P, I, P, c.c_char_p, P)(
                    root, side, descriptor, label.encode('ascii'), control) == 0
                assert P.from_address(P.from_address(control).value).value == sample
                before = snapshot(control)
                regions = loaded_regions(control)
                assert len(regions) == 1
                rows = []
                for frame in pointer_items(regions[0], 0x18, 0x20):
                    assert c.string_at(frame+8, 4) == b'FRM2'
                    mask = Q.from_address(frame+0x50).value
                    frame_masks[hex(mask)] += 1
                    assert mask & 8192 and mask & 7 == 7
                    feature = P.from_address(frame+0x128).value
                    assert feature and I.from_address(feature).value & (1 << 17)
                    value = F.from_address(feature+0x21c).value
                    stored = F.from_address(frame+0xf0).value
                    f0 = physical_f0(stored, mask)
                    count = I.from_address(frame+0x58).value
                    arrays = [list((F*count).from_address(P.from_address(frame+offset).value))
                              for offset in (0x60, 0x68, 0x70)]
                    frequencies, amplitudes, phases = arrays
                    assert all(math.isfinite(v) for array in arrays for v in array)
                    assert math.isfinite(value) and 0 <= value < math.tau
                    assert frequencies[0] == 0. and phases[0] in (0., 1.)
                    dc_phases[str(int(phases[0]))] += 1
                    selected = phase_indices(frequencies, amplitudes, f0)
                    candidates = {name: dict(slot=k, phase=phases[k], frequency=frequencies[k])
                                  for name, k in selected.items()}
                    for name, candidate in candidates.items():
                        for sign in (-1, 1):
                            all_errors[name][str(sign)].append(circular(value-sign*candidate['phase']))
                    rows.append(dict(bit17=value, f0=f0, time=D.from_address(frame+0x48).value,
                                     candidates=candidates))
                case = analyze_frames(rows)
                # Aggregate only; trajectories are discarded after this sample.
                for before_row, row in zip(rows, rows[1:]):
                    observed = circular(row['bit17']-before_row['bit17'])
                    advance = math.tau*.5*(row['f0']+before_row['f0'])*(row['time']-before_row['time'])
                    for name in row['candidates'].keys() & before_row['candidates'].keys():
                        delta = circular(row['candidates'][name]['phase']-before_row['candidates'][name]['phase'])
                        for sign in (-1, 1):
                            predictions = dict(direct=sign*delta, quarter_cap=max(-.25,min(.25,sign*delta)),
                                               subtract_f0_advance=circular(sign*delta-advance))
                            for mode, prediction in predictions.items():
                                all_step_errors[name][str(sign)][mode].append(circular(observed-prediction))
                after = snapshot(control)
                assert before == after
                case.update(sample_index=index, loaded_sha256=before['sha256'], source_unchanged=True)
                counts.update(case['counts'])
                counts['spectral_slots'] += before['counts']['spectral_slots']
                cases.append(case)
            finally:
                native.fn(0xb480, None, P)(control)
    finally:
        assert native.fn(0x10080, Q, P)(root) == 0
    if limit == 119:
        assert counts['frames'] == 26842 and counts['spectral_slots'] == 2350024
    assert all(counts[name+'_uses_slot_zero'] == 0 for name in NAMES)
    hash_groups = {}
    unique_counts = Counter()
    for case in cases:
        if case['loaded_sha256'] not in hash_groups:
            unique_counts.update(case['counts'])
        hash_groups.setdefault(case['loaded_sha256'], []).append(case['sample_index'])
    paths = [Path(__file__), Path(__file__).with_name('audit_vqm_legacy_live_features.py'),
             Path(__file__).with_name('audit_frm2_numeric.py'),
             Path(__file__).with_name('verify_region_frame_blend.py'),
             Path(__file__).with_name('probe_frm2.py')]
    return dict(schema=1, dse_sha256=SHA256, resource_sha256=resource_hash, owned_checks=owned_checks(),
        samples=len(cases), summary=dict(counts), frame_masks=dict(frame_masks), dc_phase_slot_counts=dict(dc_phases),
        loaded_state_hash_classes=len(hash_groups), duplicate_sample_index_groups=[indices for indices in hash_groups.values() if len(indices)>1],
        unique_loaded_state_counts=dict(unique_counts),
        direct_level={name: {sign: metrics(values) for sign, values in group.items()}
                      for name, group in all_errors.items()},
        step_models={name: {sign: {mode: metrics(values) for mode, values in modes.items()}
                           for sign, modes in group.items()} for name, group in all_step_errors.items()}, cases=cases,
        source_sha256={p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in paths},
        candidate_rules=dict(first_positive_frequency='First slot with frequency > 0',
            nearest_physical_f0='Minimum absolute frequency minus physical F0, excluding DC',
            strongest_near_physical_f0='Maximum saved amplitude among positive frequencies in [0.5*F0,1.5*F0]'),
        scope='All selected compact-DVQM samples after real resource/control/loadSample, before sampleInfo/config/audio; '
              'loaded state hashed before and after read-only comparisons. The DC phase slot is excluded. '
              'Three preset physical phase selectors and both signs, direct or capped steps, and trapezoidal F0 advance '
              'using loaded frame timestamps; the first-eight-phase rotation is a separate diagnostic, not a producer. '
              'The normalized step rate uses an assumed 256-sample/44100-Hz reference only as a diagnostic, '
              'not a recovered maker hop. Loaded timestamps vary and their original time-warp is not recovered. '
              'Catalog entries are not asserted to be independent recordings. '
              'No PCM analysis, original production inputs or formal bit17 name assertion. '
              'Separate from V4X Growl and auxiliary TENSE/BRWSP resources.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'directory', 'output'):
        parser.add_argument('--'+name, type=Path, required=True)
    parser.add_argument('--limit', type=int, default=119)
    args = parser.parse_args()
    assert 1 <= args.limit <= 119
    report = run(args.dll, args.directory, args.limit)
    args.output.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({key: report[key] for key in ('samples', 'summary', 'direct_level')}))


if __name__ == '__main__':
    main()
