"""Check owned phase interpolation and compact saved pitch-correction candidates.

Installed resources are read only and remain in memory. The evidence contains
only synthetic examples, identifiers, hashes and aggregate numerical errors.
No candidate fit is treated as a recovered authoring parameter.
"""
from __future__ import annotations

import argparse
from collections import Counter
import ctypes as c
import hashlib
import itertools
import json
import math
from pathlib import Path
import struct

from audit_frm2_numeric import B, F, I, Native, P, Q, SHA256, f32
from audit_vqm_compact_load_identity import frames_in_interval
from audit_vqm_legacy_live_features import RESOURCE_SHA256
from audit_vqm_legacy_phase_relations import circular, metrics, physical_f0
from verify_region_frame_blend import mix


LAYOUTS = (
    ('DRS', 0x7ec30, 0x80b70, 0x50, 0x58, 0x70, 0x128),
    ('DSE5', 0x12a670, 0x1307b0, 0x150, 0x158, 0x170, 0x208),
)
PAIRS = ((.1, .4), (.4, .1), (6.1, .1), (.1, 6.1), (0., 0.),
         (0., 6.), (6., 0.), (1., 4.), (4., 1.), (0., math.pi),
         (math.pi, 0.), (0., math.pi-1e-6), (0., math.pi+1e-6),
         (math.pi-1e-6, 0.), (math.pi+1e-6, 0.))
FRACTIONS = (0., .125, .25, .499, .5, .501, .75, .875, 1.)


def phase_mix(a, b, fraction, layout='DRS'):
    a, b, fraction = map(f32, (a, b, fraction))
    delta = f32(b-a)
    if delta < -math.pi:
        value = f32(((b+math.tau)-a)*fraction+a)
    elif delta >= math.pi:
        value = f32(((b-math.tau)-a)*fraction+a)
    else:
        value = mix(a, b, fraction)
    wrap = f32(math.tau) if layout == 'DSE5' else math.tau
    if value < 0.:
        value = f32(value+wrap)
    if value >= math.tau:
        value = f32(value-wrap)
    return value


def guarded(native, size):
    base = native.alloc(size+64)
    c.memset(base, 0x5a, 32)
    c.memset(base+size+32, 0xa5, 32)
    return base+32


def guards(pointer, size):
    return (c.string_at(pointer-32, 32) == b'\x5a'*32 and
            c.string_at(pointer+size, 32) == b'\xa5'*32)


def native_contracts(dll):
    native = Native(dll)
    rows, examples, source_checks, phase_values = [], [], 0, 0
    slot_zero_cases = Counter()
    for label, ctor, rva, mask_offset, count_offset, phase_offset, feature_offset in LAYOUTS:
        initialize = native.fn(ctor, P, P)
        interpolate = native.fn(rva, Q, P, P, P, Q, F, B, I, I, F, I, I)
        for count in (2, 4, 5, 9):
            frames = [guarded(native, 0x400) for _ in range(3)]
            for frame in frames:
                initialize(frame)
            source_features = [guarded(native, 0x228) for _ in range(2)]
            source_phases = [guarded(native, count*4) for _ in range(2)]
            for frame, feature, phase in zip(frames, source_features, source_phases):
                Q.from_address(frame+mask_offset).value = 0x2004
                I.from_address(frame+count_offset).value = count
                P.from_address(frame+phase_offset).value = phase
                P.from_address(frame+feature_offset).value = feature
                I.from_address(feature).value = 1 << 17
            errors, noncircular, bit_exact, dc_exact = [], 0, 0, 0
            for (a, b), fraction in itertools.product(PAIRS, FRACTIONS):
                dc_pair = ((0., 0.), (0., 1.), (1., 0.), (1., 1.))[FRACTIONS.index(fraction) % 4]
                a, b, fraction = map(f32, (a, b, fraction))
                for side, value in enumerate((a, b)):
                    F.from_address(source_features[side]+0x21c).value = value
                    for slot in range(count):
                        F.from_address(source_phases[side]+4*slot).value = (
                            dc_pair[side] if slot == 0 else value)
                before = [(c.string_at(frame, 0x400), c.string_at(feature, 0x228),
                           c.string_at(phase, count*4))
                          for frame, feature, phase in zip(frames, source_features, source_phases)]
                assert interpolate(*frames, 0x2004, fraction, 0, 44100, 0x80, 1., 0, 0) == 0
                output = frames[2]
                assert Q.from_address(output+mask_offset).value == 0x2004
                assert I.from_address(output+count_offset).value == count
                feature = P.from_address(output+feature_offset).value
                phases = P.from_address(output+phase_offset).value
                assert feature and phases and I.from_address(feature).value == 1 << 17
                actual = F.from_address(feature+0x21c).value
                expected = mix(a, b, fraction)
                assert struct.pack('<f', actual) == struct.pack('<f', expected)
                bit_exact += 1
                assert F.from_address(phases).value == min(dc_pair)
                slot_zero_cases[str(tuple(map(int, dc_pair)))] += 1
                dc_exact += 1
                circular_value = phase_mix(a, b, fraction, label)
                for slot in range(1, count):
                    value = F.from_address(phases+slot*4).value
                    error = abs(value-circular_value)
                    assert error == 0., (label, count, a, b, fraction, slot, value, circular_value)
                    errors.append(error)
                    phase_values += 1
                noncircular += abs(circular(actual-circular_value)) > 1e-6
                for side, (frame, feature_pointer, phase_pointer) in enumerate(
                        zip(frames, source_features, source_phases)):
                    assert before[side] == (c.string_at(frame, 0x400),
                        c.string_at(feature_pointer, 0x228), c.string_at(phase_pointer, count*4))
                    assert guards(frame, 0x400) and guards(feature_pointer, 0x228)
                    assert guards(phase_pointer, count*4)
                    source_checks += 1
                assert all(guards(frame, 0x400) for frame in frames)
                if count == 2 and a == f32(6.1) and b == f32(.1) and fraction == .5:
                    examples.append(dict(layout=label, first=a, second=b, fraction=fraction,
                        bit17=actual, harmonic_phase=F.from_address(phases+4).value))
            rows.append(dict(layout=label, rva=hex(rva), slots=count,
                calls=bit_exact, bit17_linear_float32_exact=bit_exact,
                dc_slot_minimum_exact=dc_exact, nonzero_index_phase_values=len(errors),
                circular_phase_exact_values=sum(x == 0. for x in errors),
                bit17_differs_from_circular_phase=noncircular,
                source_objects_and_owned_guards_preserved=True))
    return dict(cases=rows, examples=examples, native_calls=sum(x['calls'] for x in rows),
        checked_nonzero_index_phase_values=phase_values, cumulative_source_checks=source_checks,
        slot_zero_endpoint_counts=dict(slot_zero_cases),
        scope='Owned endpoints with matching phase/bit17, equal component counts, phase mask4 '
              'and feature mask13, internal bit17 active, mode0. Both generic layouts. '
              'Slot0 is a minimum operation; slots with index>0 use wrapped interpolation. '
              'No original authoring invocation, temporal resampler or source endpoint recovery.')


def correction_models(rows):
    # The zero model is evaluated on exactly the same rows as each comparison.
    corrections = [row['phase_error'] for row in rows]
    results = {'unmodified_fundamental': metrics(corrections)}
    models = {}
    for rate, portion, sign in itertools.product((22050, 44100), (.5, 1.), (-1, 1)):
        key = f'window_rate_{rate}_portion_{portion}_sign_{sign}'
        models[key] = [sign*math.tau*row['frequency_difference']*row['window']*portion/rate
                       for row in rows]
    for interval, portion, sign in itertools.product(('previous_dt', 'next_dt'), (.5, 1.), (-1, 1)):
        key = f'{interval}_portion_{portion}_sign_{sign}'
        # Missing adjacent timestamp means that row is not eligible for this model.
        selected = [(row, sign*math.tau*row['frequency_difference']*row[interval]*portion)
                    for row in rows if row[interval] is not None]
        results[key] = dict(candidate=metrics([circular(row['phase_error']-value)
                                              for row, value in selected]),
            same_rows_zero_control=metrics([row['phase_error'] for row, _ in selected]))
    for key, values in models.items():
        results[key] = dict(candidate=metrics([circular(actual-value)
                                              for actual, value in zip(corrections, values)]),
            same_rows_zero_control=metrics(corrections))
    train = [row for row in rows if row['eligible_index'] < 8]
    holdout = [row for row in rows if row['eligible_index'] >= 8]
    def predictor(row):
        return math.tau*row['frequency_difference']*row['window']/44100
    denominator = sum(predictor(row)**2 for row in train)
    assert denominator > 0. and train and holdout
    coefficient = sum(predictor(row)*row['phase_error'] for row in train)/denominator
    fitted = dict(model='One global scalar multiplying frequency difference * window / 44100 * tau',
        coefficient=coefficient, training_rows=len(train), holdout_rows=len(holdout),
        selection='First eight eligible frames per catalog item train; remainder holdout',
        heldout=metrics([circular(row['phase_error']-coefficient*predictor(row)) for row in holdout]),
        same_rows_zero_control=metrics([row['phase_error'] for row in holdout]),
        scope='Diagnostic fit only; not an identified original phase coefficient or independent recording split')
    zero = [row for row in rows if row['frequency_difference'] == 0.]
    return dict(candidates=results, heldout_global_coefficient=fitted,
        zero_frequency_difference_counterexample=dict(frames=len(zero),
            catalog_item_count=len({row['sample_index'] for row in zero}),
            phase_difference=metrics([row['phase_error'] for row in zero]),
            scope='At identical stored and baseline pitch, any finite instantaneous coefficient '
                  'multiplying their frequency difference is zero. This excludes that restricted '
                  'phase-correction family as an exact explanation, not cumulative history, '
                  'an additive phase state, earlier input or another baseline.'))


def saved_metadata(resource, reference):
    data = resource.read_bytes()
    assert hashlib.sha256(data).hexdigest() == RESOURCE_SHA256
    report = json.loads(reference.read_text(encoding='utf-8'))
    assert report['resource_sha256'] == RESOURCE_SHA256 and report['samples'] == 119
    rows, counts, groups = [], Counter(), []
    for case in report['cases']:
        start = int(case['track_offset'], 16)
        disk = frames_in_interval(data, start, start+case['track_bytes'])
        group = []
        eligible = 0
        for at, (_, _, wire) in enumerate(disk):
            counts['all_frames'] += 1
            if not wire['spectrum_count']:
                continue
            counts['frames_with_spectrum'] += 1
            stored = struct.unpack('<f', wire['pitch'])[0]
            f0 = physical_f0(stored, wire['mask'])
            baseline = struct.unpack_from('<f', wire['spectrum'][0])[0]
            baseline_hz = physical_f0(baseline, wire['mask'])
            if f0 <= 0. or baseline_hz <= 0.:
                counts['invalid_pitch_or_baseline'] += 1
                continue
            phase = struct.unpack_from('<f', wire['components'][2], 4)[0]
            bit17 = struct.unpack_from('<f', wire['feature'], 0x21c)[0]
            row = dict(sample_index=case['sample_index'], eligible_index=eligible,
                phase_error=circular(bit17-phase), frequency_difference=f0-baseline_hz,
                window=int.from_bytes(wire['control19'], 'little'),
                previous_dt=wire['time']-disk[at-1][2]['time'] if at > 0 else None,
                next_dt=disk[at+1][2]['time']-wire['time'] if at+1 < len(disk) else None)
            assert all(math.isfinite(row[k]) for k in ('phase_error', 'frequency_difference'))
            assert all(row[k] is None or row[k] > 0. for k in ('previous_dt','next_dt'))
            group.append(row); rows.append(row); eligible += 1
        groups.append(dict(sample_index=case['sample_index'], eligible_frames=len(group)))
    assert counts['all_frames'] == 26842 and len(rows) == counts['frames_with_spectrum'] == 22038
    return dict(counts=dict(counts), eligible_frames=len(rows), catalog_items=len(groups),
        groups=groups, **correction_models(rows),
        scope='Serialized compact fields bound by425 offsets. Baseline/residual relation is verified '
              'but original filter/sample rate/window centre is not identified. Only positive-F0 '
              'frames with optional arrays. Hypothetical frequency-difference phase corrections, '
              'not PCM phase production. No commercial array values persisted.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'resource', 'reference', 'output'):
        parser.add_argument('--'+name, type=Path, required=True)
    args = parser.parse_args()
    result = dict(schema=1, dse_sha256=SHA256, resource_sha256=RESOURCE_SHA256,
        reference_sha256=hashlib.sha256(args.reference.read_bytes()).hexdigest(),
        native_interpolation=native_contracts(args.dll),
        saved_pitch_corrections=saved_metadata(args.resource, args.reference))
    dependencies = ('verify_vqm_compact_phase_contracts.py', 'audit_frm2_numeric.py',
        'audit_vqm_compact_load_identity.py', 'audit_vqm_legacy_live_features.py',
        'audit_vqm_legacy_phase_relations.py', 'probe_frm2.py', 'verify_region_frame_blend.py')
    result['source_sha256'] = {name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                              for name in dependencies}
    result['scope'] = ('Two native generic interpolation contracts plus a bounded saved-metadata '
        'candidate comparison. Does not establish the original authoring path, bit17 definition '
        'or production, BRWSP rules, V4X/Growl full-host behavior. Installed files unchanged; '
        'owned native objects in this independent process only. Aggregate evidence only.')
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(native_calls=result['native_interpolation']['native_calls'],
        nonzero_index_phase_values=result['native_interpolation']['checked_nonzero_index_phase_values'],
        saved_frames=result['saved_pitch_corrections']['eligible_frames'],
        interpolation_examples=result['native_interpolation']['examples'],
        fit=result['saved_pitch_corrections']['heldout_global_coefficient'])))


if __name__ == '__main__':
    main()
