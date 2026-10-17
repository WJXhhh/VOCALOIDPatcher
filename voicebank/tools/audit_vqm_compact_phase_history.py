"""Bound temporal phase hypotheses using serialized compact DVQM fields.

Installed arrays stay in memory. Evidence retains counts, hashes and aggregate
errors only. Conditional offsets use saved training frames and are diagnostic,
not a recovered PCM producer, authoring state or independent recording split.
"""
from __future__ import annotations

import argparse
from collections import Counter
import cmath
import hashlib
import json
import math
from pathlib import Path
import struct

from audit_vqm_compact_load_identity import frames_in_interval
from audit_vqm_legacy_live_features import RESOURCE_SHA256
from audit_vqm_legacy_phase_relations import circular, metrics, physical_f0


TOLERANCE = 1e-5
LAGS = (-2, -1, 0, 1, 2)


def interval_distance(value, low, high):
    return max(0., low-value, value-high)


def hull_violation(previous, actual, source, domain):
    step, innovation = actual-previous, source-previous
    if domain == 'circular':
        step, innovation = circular(step), circular(innovation)
    return interval_distance(step, min(0., innovation), max(0., innovation))


def modular_intervals(low, high):
    assert high >= low
    if high-low >= math.tau:
        return [(0., math.tau)]
    start = low % math.tau
    stop = start+high-low
    return [(start, stop)] if stop <= math.tau else [(start, math.tau), (0., stop-math.tau)]


def intersect(first, second):
    return [(max(a, c), min(b, d)) for a, b in first for c, d in second
            if max(a, c) <= min(b, d)]


def allowed_offsets(previous, actual, phase, domain, tolerance=TOLERANCE):
    if domain == 'circular':
        step, innovation = circular(actual-previous), circular(phase-previous)
        if step > tolerance:
            return modular_intervals(step-tolerance-innovation, math.pi-innovation)
        if step < -tolerance:
            return modular_intervals(-math.pi-innovation, step+tolerance-innovation)
    else:
        step = actual-previous
        if step > tolerance:
            return modular_intervals(actual-tolerance-phase, math.tau-phase)
        if step < -tolerance:
            return modular_intervals(-phase, actual+tolerance-phase)
    return [(0., math.tau)]


def contains(intervals, value):
    value %= math.tau
    return any(low-1e-12 <= value <= high+1e-12 for low, high in intervals)


def owned_checks():
    rows = []
    # Verify the analytic necessary-set calculation against independent direct
    # hull tests across rotations; these are synthetic angles, never bank data.
    for previous, actual, phase in ((0., .5, 1.), (1., .5, 0.),
            (6.1, .1, .2), (.1, 6.1, 6.), (2., 2., .1),
            (.25, .75, .2), (5., 4., 5.5), (2., 3., 2.1)):
        for domain in ('circular', 'linear'):
            intervals = allowed_offsets(previous, actual, phase, domain)
            for index in range(721):
                rotation = math.tau*(index+.123)/721
                target = (phase+rotation) % math.tau
                direct = hull_violation(previous, actual, target, domain) <= TOLERANCE
                assert contains(intervals, rotation) == direct, (previous, actual, phase, domain, index)
            rows.append(dict(domain=domain, rotation_checks=721, passed=True))
    assert modular_intervals(0., math.tau) == [(0., math.tau)]
    assert not intersect([(0., 1.)], [(2., 3.)])
    assert intersect([(0., 2.)], [(1., 3.)]) == [(1., 2.)]
    assert hull_violation(0., .5, 1., 'circular') == 0.
    assert hull_violation(0., -.5, 1., 'circular') == .5
    for domain in ('circular', 'linear'):
        fixed = 1.25
        available, previous = [(0., math.tau)], .3
        for at in range(24):
            phase = (1.7+.73*at) % math.tau
            target = (phase+fixed) % math.tau
            innovation = target-previous
            if domain == 'circular':
                innovation = circular(innovation)
            current = previous+(.125+.25*(at % 4))*innovation
            if domain == 'circular':
                current %= math.tau
            available = intersect(available, allowed_offsets(previous, current, phase, domain))
            assert contains(available, fixed)
            previous = current
        rows.append(dict(case='known_variable_gain_trajectory', domain=domain, steps=24, passed=True))
    conflict = intersect(allowed_offsets(0., 1., 0., 'circular'),
        allowed_offsets(0., math.tau-1., math.tau-.5, 'circular'))
    assert not conflict
    rows.append(dict(case='synthetic_empty_offset_intersection', passed=True))
    generated = [dict(time=0., difference=1.), dict(time=.5, difference=3.),
                 dict(time=1.5, difference=5.)]
    for method, cycles in (('left', (0., .5, 3.5)), ('right', (0., 1.5, 6.5)),
                           ('trapezoid', (0., 1., 5.))):
        assert all(math.isclose(value, expected*math.tau, rel_tol=1e-15, abs_tol=1e-15)
                   for value, expected in zip(integrate(generated, method), cycles))
        rows.append(dict(case='owned_integral', method=method, values=3, passed=True))
    return rows


def source_rows(data, case):
    start = int(case['track_offset'], 16)
    disk = frames_in_interval(data, start, start+case['track_bytes'])
    rows, span, prior_voiced = [], -1, False
    for index, (_, _, wire) in enumerate(disk):
        stored = struct.unpack('<f', wire['pitch'])[0]
        f0 = physical_f0(stored, wire['mask'])
        voiced = f0 > 0.
        if voiced and not prior_voiced:
            span += 1
        phase = struct.unpack_from('<f', wire['components'][2], 4)[0] if voiced else None
        bit17 = struct.unpack_from('<f', wire['feature'], 0x21c)[0]
        difference = None
        if voiced and wire['spectrum_count']:
            baseline = physical_f0(struct.unpack_from('<f', wire['spectrum'][0])[0], wire['mask'])
            if baseline > 0.:
                difference = f0-baseline
        rows.append(dict(index=index, span=span if voiced else None, phase=phase,
                         bit17=bit17, time=wire['time'], difference=difference))
        prior_voiced = voiced
    return rows


def convex_constraints(sequences):
    results = []
    for domain in ('circular', 'linear'):
        for lag in LAGS:
            violations, cases, totals = [], [], Counter()
            for sample, rows in enumerate(sequences):
                available, spans, count = [(0., math.tau)], {}, 0
                relaxed = [(0., math.tau)]
                failures = 0
                for at in range(1, len(rows)):
                    current, previous = rows[at], rows[at-1]
                    target_at = at+lag
                    if current['span'] is None or previous['span'] != current['span']:
                        continue
                    if not 0 <= target_at < len(rows):
                        continue
                    target = rows[target_at]
                    if target['span'] != current['span']:
                        continue
                    violation = hull_violation(previous['bit17'], current['bit17'], target['phase'], domain)
                    violations.append(violation)
                    failures += violation > TOLERANCE
                    allowed = allowed_offsets(previous['bit17'], current['bit17'], target['phase'], domain)
                    available = intersect(available, allowed)
                    relaxed = intersect(relaxed, allowed_offsets(previous['bit17'], current['bit17'],
                        target['phase'], domain, 1e-4))
                    prior_intervals, prior_count = spans.get(current['span'], ([(0., math.tau)], 0))
                    spans[current['span']] = (intersect(prior_intervals, allowed), prior_count+1)
                    count += 1
                if count:
                    totals['catalog_items_with_constraints'] += 1
                    totals['catalog_items_without_any_fixed_rotation'] += not available
                    totals['catalog_items_without_fixed_rotation_at_1e_4_tolerance'] += not relaxed
                    totals['pairs'] += count
                    totals['unrotated_pairs_outside_hull'] += failures
                    totals['voiced_spans_with_constraints'] += len(spans)
                    totals['voiced_spans_without_any_fixed_rotation'] += sum(not bounds for bounds, _ in spans.values())
                cases.append(dict(sample_index=sample, pairs=count, unrotated_pairs_outside_hull=failures,
                    any_fixed_rotation_feasible=bool(available),
                    any_fixed_rotation_feasible_at_1e_4_tolerance=bool(relaxed),
                    voiced_spans_with_constraints=len(spans),
                    voiced_spans_without_any_fixed_rotation=sum(not bounds for bounds, _ in spans.values())))
            results.append(dict(domain=domain, lag_frames=lag, tolerance=TOLERANCE,
                summary=dict(totals), distance_from_unrotated_admissible_hull=metrics(violations), cases=cases))
    return dict(models=results,
        necessary_condition='Every step must lie between zero and the innovation toward the target. '
            'Target is source phase at the stated lag plus one arbitrary fixed circular rotation. '
            'Rotation sets are intersected analytically, not sampled or fitted. Gain may vary freely '
            'within [0,1] each frame; a cap toward the target only narrows this hull.',
        scope='Necessary conditions use the actual previous bit17, thus grant exact prior state. '
            'Only contiguous positive saved-F0 spans. Linear domain uses target modulo tau. '
            'Positive lags use future frames and are diagnostic offline controls, not causal playback. '
            'Item-level rotations and more permissive independently reset span-level rotations are '
            'both reported. This is not an exclusion of higher-order, nonconvex, dynamic-origin '
            'or other-input producers; saved positive F0 is not independent PCM voicing.')


def metadata_spans(sequences):
    result = []
    for rows in sequences:
        current = []
        for row in rows:
            if row['difference'] is None:
                if current:
                    result.append(current); current = []
                continue
            if current and row['span'] != current[-1]['span']:
                result.append(current); current = []
            current.append(row)
        if current:
            result.append(current)
    return result


def rotation(errors):
    return cmath.phase(sum(cmath.exp(1j*value) for value in errors))


def integrate(rows, method):
    values = [0.]
    for previous, current in zip(rows, rows[1:]):
        dt = current['time']-previous['time']
        assert dt > 0.
        frequency = (previous['difference'] if method == 'left' else current['difference']
                     if method == 'right' else (previous['difference']+current['difference'])/2)
        values.append(values[-1]+math.tau*frequency*dt)
    return values


def cumulative_comparisons(sequences):
    spans = metadata_spans(sequences)
    eligible = [span for span in spans if len(span) > 8]
    assert eligible
    cases = []
    for method in ('left', 'right', 'trapezoid'):
        for sign in (-1, 1):
            errors, controls, train_errors = [], [], []
            for span in eligible:
                integral = integrate(span, method)
                residual = [circular(row['bit17']-row['phase']-sign*value)
                            for row, value in zip(span, integral)]
                zero = [circular(row['bit17']-row['phase']) for row in span]
                offset, control_offset = rotation(residual[:8]), rotation(zero[:8])
                train_errors.extend(circular(x-offset) for x in residual[:8])
                errors.extend(circular(x-offset) for x in residual[8:])
                controls.extend(circular(x-control_offset) for x in zero[8:])
            cases.append(dict(integration=method, sign=sign, training=metrics(train_errors),
                heldout=metrics(errors), same_rows_prefix_rotation_control=metrics(controls)))
    return dict(metadata_frames=sum(len(span) for span in spans), metadata_spans=len(spans),
        eligible_spans=len(eligible), short_spans_excluded=len(spans)-len(eligible),
        first_eight_training_frames=8*len(eligible),
        heldout_frames=sum(len(span)-8 for span in eligible), cases=cases,
        scope='Physical-time left/right/trapezoidal integration of saved F0 minus saved baseline F0, '
            'in Hz; sign+/-1. State resets on absent spectrum/invalid pitch/span boundary. '
            'A circular offset is fitted on the first eight frames per span for each model and its '
            'no-integral control separately. Saved-bit17 training state is conditional evidence, '
            'not PCM production or recovered authoring timing/configuration. Duplicated catalog '
            'items are not independent recordings. No commercial trajectories persisted.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('resource', 'reference', 'output'):
        parser.add_argument('--'+name, type=Path, required=True)
    args = parser.parse_args()
    data = args.resource.read_bytes()
    assert hashlib.sha256(data).hexdigest() == RESOURCE_SHA256
    reference = json.loads(args.reference.read_text(encoding='utf-8'))
    assert reference['samples'] == 119 and reference['resource_sha256'] == RESOURCE_SHA256
    sequences = [source_rows(data, case) for case in reference['cases']]
    assert sum(len(rows) for rows in sequences) == 26842
    fixtures = owned_checks()
    result = dict(schema=1, resource_sha256=RESOURCE_SHA256,
        reference_sha256=hashlib.sha256(args.reference.read_bytes()).hexdigest(), owned_checks=fixtures,
        convex_phase_history=convex_constraints(sequences), cumulative_pitch_difference=cumulative_comparisons(sequences))
    dependencies = ('audit_vqm_compact_phase_history.py','audit_vqm_compact_load_identity.py',
        'audit_vqm_legacy_live_features.py','audit_vqm_legacy_phase_relations.py',
        'audit_frm2_numeric.py','probe_frm2.py','verify_region_frame_blend.py')
    result['source_sha256'] = {name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                              for name in dependencies}
    result['scope'] = ('Serialized compact resources, analytic necessary-state constraints and '
        'conditional cumulative phase candidates. No native authoring/PCM producer, file changes, '
        'installed-array exports, original maker identification or whole-host proof. '
        'The full VQM/Growl research objective remains unfulfilled.')
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    cumulative = result['cumulative_pitch_difference']
    print(json.dumps(dict(convex=[dict(domain=x['domain'], lag=x['lag_frames'],
        catalog_items_without_fixed_rotation=x['summary']['catalog_items_without_any_fixed_rotation'])
        for x in result['convex_phase_history']['models']],
        cumulative_heldout_frames=cumulative['heldout_frames'],
        cumulative_models=[dict(method=x['integration'], sign=x['sign'],
            heldout_mae=x['heldout']['mean_absolute_error'],
            control_mae=x['same_rows_prefix_rotation_control']['mean_absolute_error'])
            for x in cumulative['cases']])))


if __name__ == '__main__':
    main()
