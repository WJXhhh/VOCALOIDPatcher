"""Reconstruct runtime compact amplitude decomposition from its actual inputs.

Installed data remain read only. Optional arrays may be released on independently
loaded owned objects using the reviewed native field-release routine. Native
audio and FFT implementations are unchanged. Only aggregates are persisted.
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

from audit_frm2_numeric import F, I, Native, P, Q, SHA256, f32
from audit_vqm_compact_load_identity import frames_in_interval
from audit_vqm_legacy_live_features import RESOURCE_SHA256, catalog, children, name, pointer_items
from audit_vqm_legacy_phase_relations import INVALID, metrics
from verify_vqm_compact_amplitude_split import compact_audio, frames
from verify_vqm_legacy_bit17_consumption import audio, load_configure
from verify_vqm_legacy_seeded_consumption import random_reset
from verify_vqm_temporal_lowpass import Harness, mirrored, weights


def raw_float(value):
    return struct.pack('<f', value)


def capture(frame):
    n, m = I.from_address(frame+0x58).value, I.from_address(frame+0xa8).value
    assert 1 <= n <= 10000 and 0 <= m <= 100000
    feature = c.string_at(P.from_address(frame+0x128).value, 0x228)
    return dict(n=n, m=m, pitch=F.from_address(frame+0xf0).value,
        time=c.c_double.from_address(frame+0x48).value, feature=feature,
        amplitude=tuple((F*n).from_address(P.from_address(frame+0x68).value)),
        frequency=c.string_at(P.from_address(frame+0x60).value, n*4),
        phase=c.string_at(P.from_address(frame+0x70).value, n*4),
        first=tuple((F*m).from_address(P.from_address(frame+0xb0).value)) if m else (),
        second=tuple((F*m).from_address(P.from_address(frame+0xb8).value)) if m else ())


def pitch_hz(value):
    return f32(f32(2.**f32(value/1200.))*440.) if value > INVALID else 0.


class FftLowpass:
    """Independent mirroring/masking arithmetic sharing only the native FFTs."""
    def __init__(self, dll):
        self.harness = Harness(dll)
        self.calls = 0

    def smooth(self, sequence):
        self.calls += 1
        n = len(sequence)
        length = 1 << (n.bit_length()+1)
        start, width = min(float(n), 3.), min(float(n), 5.)
        h = self.harness
        _, pointer, before = h.buffer(mirrored(sequence, length))
        index = int(math.log2(length))
        assert h.compute_forward(h.forward[index], pointer) == 0
        for k, weight in enumerate(weights(length, start, width)):
            if weight != 1.:
                for component in (0, 1):
                    address = pointer+8*k+4*component
                    F.from_address(address).value = f32(F.from_address(address).value*weight) if weight else 0.
        F.from_address(pointer+4).value = 0.
        assert h.compute_backward(h.backward[index], pointer) == 0
        after = c.string_at(pointer-32, len(before))
        assert before[:32] == after[:32] and before[-32:] == after[-32:]
        return tuple((F*length).from_address(pointer))


def blend_strength(rows, index, selected, radius):
    current = rows[index]
    first, second = 0., 0.
    for _, row in selected:
        elapsed = f32(abs(current['time']-row['time']))
        a = abs(struct.unpack_from('<f', row['feature'], 0x28)[0])
        first = max(first, f32(a*max(0., f32(1.-f32(elapsed/f32(.02))))))
        total = 0.
        for offset in range(0x1c0, 0x1f0, 4):
            total = f32(total+abs(struct.unpack_from('<f', row['feature'], offset)[0]))
        second = max(second, f32(total*max(0., f32(1.-f32(elapsed/f32(.1))))))
    second = f32(max(0., f32(f32(f32(second/260.)*2.5)-1.5))*2.5)
    strength = max(0., f32(max(first, second)-1.5))
    nearest = f32(1e10)
    for row in reversed(rows[max(0, index-radius):index+radius+1]):
        if row['pitch'] <= INVALID:
            distance = abs(current['time']-row['time'])
            if distance < nearest:
                nearest = f32(distance)
    if nearest < f32(1e10):
        strength = max(strength, f32(max(0., f32(1.-f32(nearest/f32(.05))))*10.))
    return strength


def amplitude_strength(sequence, starting):
    n = len(sequence)
    assert n >= 2
    gradient = []
    for index in range(n-1):
        total = 0.
        for j in range(max(0, index-1), min(n-2, index+1)+1):
            total = f32(total+f32(sequence[j+1]-sequence[j]))
        gradient.append(f32(total/3.))
    half = n//2
    strength = starting
    for step in range(half+1):
        weight = f32(math.sqrt(f32((half-step)/f32(half+1))))
        for i in (half-step, half+step):
            if 0 <= i < len(gradient) and abs(gradient[i]) > 10.:
                candidate = f32(f32(f32(f32(abs(gradient[i])-10.)*weight)*.25)*2.5)
                strength = max(strength, candidate)
    return strength


def mix(smoothed, original, strength):
    if strength <= 0.:
        return smoothed
    coefficient = min(1., f32(strength/2.5))
    return f32(smoothed+f32(f32(original-smoothed)*coefficient))


def predict(rows, index, radius, lowpass):
    current = rows[index]
    selected = [(j, rows[j]) for j in range(max(0, index-radius), min(len(rows), index+radius+1))
                if rows[j]['n'] > 0 and rows[j]['pitch'] > INVALID]
    assert len(selected) >= 2
    center = next(k for k, (j, _) in enumerate(selected) if j == index)
    count = max(row['n'] for _, row in selected)
    strength = blend_strength(rows, index, selected, radius)
    ratios = [f32(pitch_hz(current['pitch'])/pitch_hz(row['pitch'])) for _, row in selected]
    first, second = [], []
    for harmonic in range(count):
        if harmonic == 0:
            sequence = [row['pitch'] for _, row in selected]
            original = current['pitch']
            amount = strength
        else:
            sequence = []
            for ratio, (_, row) in zip(ratios, selected):
                at = int(f32(f32(f32(harmonic)*ratio)+.5))
                at = min(row['n']-1, max(1, at))
                sequence.append(row['amplitude'][at])
            original = current['amplitude'][harmonic] if harmonic < current['n'] else None
            amount = amplitude_strength(sequence, strength)
        smoothed = lowpass.smooth(sequence)[center]
        value = mix(smoothed, original, amount) if original is not None else smoothed
        residual = f32(original-value) if original is not None else 0.
        first.append(-200. if value == 0. else value)
        second.append(residual)
    return tuple(first), tuple(second), dict(selected=len(selected), generated_count=count,
        tail_slots=max(0, count-current['n']), pitch_blend_nonzero=strength > 0.,
        pitch_blend_saturated=strength >= 2.5)


def raw_counts(directory, reference):
    data = (directory/'dvqm.dat').read_bytes()
    assert hashlib.sha256(data).hexdigest() == RESOURCE_SHA256
    prior = json.loads(reference.read_text(encoding='utf-8'))
    counts, differences = Counter(), Counter()
    for case in prior['cases']:
        start = int(case['track_offset'], 16)
        rows = [wire for _, _, wire in frames_in_interval(data, start, start+case['track_bytes'])]
        for index, row in enumerate(rows):
            if not row['spectrum_count']:
                continue
            selected = [other for other in rows[max(0,index-9):index+10]
                        if other['count'] > 0 and struct.unpack('<f',other['pitch'])[0] > INVALID]
            predicted = max(other['count'] for other in selected)
            counts['optional_frames'] += 1
            counts['count_equals_runtime_radius9_max'] += predicted == row['spectrum_count']
            differences[str(row['spectrum_count']-predicted)] += 1
    return dict(counts=dict(counts), saved_minus_predicted_count=dict(differences),
        scope='Raw original count comparison only; a mismatch excludes a universal identity. '
              'Not proof the original author used the current runtime window or eligibility rule.')


def run(dll, directory, selected_indices, variant, feature_slot=None, feature_value=520., bit9_value=None, blocks=0):
    native = Native(dll)
    lowpass = FftLowpass(dll)
    reset, random_identity = random_reset(dll, 430)
    root = native.fn(0xfea0, P, c.c_char_p)(str(directory.resolve()).encode('utf-8'))
    assert root
    counts, cases = Counter(), []
    errors_a, errors_b, original_a, original_b = [], [], [], []
    try:
        entries = catalog(native, root)
        for index in selected_indices:
            owned_root = native.fn(0xfea0, P, c.c_char_p)(str(directory.resolve()).encode('utf-8'))
            assert owned_root and owned_root != root
            side, descriptor, label, _ = entries[index]
            node = native.fn(0x23f00, P, P, I, P)(owned_root, side, descriptor)
            sample = {name(item):item for item in children(node)}[label]
            control = None
            try:
                control, _ = load_configure(native, owned_root, (side, descriptor, label, sample), 'baseline')
                frame_objects = frames(control)
                original = [capture(frame) for frame in frame_objects]
                feature_changes = 0
                if feature_slot is not None or bit9_value is not None:
                    for frame in frame_objects:
                        feature = P.from_address(frame+0x128).value
                        offset = 0x1bc+4*feature_slot if feature_slot is not None else 0x28
                        value = feature_value if feature_slot is not None else bit9_value
                        F.from_address(feature+offset).value = value
                        feature_changes += 1
                removed = 0
                if variant == 'missing_optional_arrays':
                    for frame, row in zip(frame_objects, original):
                        if row['m']:
                            native.fn(0x88f40, None, P, Q, I)(frame, 0x30, 0)
                            assert I.from_address(frame+0xa8).value == 0
                            assert not P.from_address(frame+0xb0).value and not P.from_address(frame+0xb8).value
                            removed += 1
                before = [capture(frame) for frame in frame_objects]
                result = compact_audio(audio(native, control, blocks, reset))
                context = P.from_address(P.from_address(control).value+0x60).value
                assert context and I.from_address(context+0x2df0).value == 1
                source = P.from_address(context+0x2e88).value
                assert pointer_items(source, 0x18, 0x20) == frame_objects
                assert not P.from_address(context+0x21d8).value and not P.from_address(context+0x2208).value
                radius = I.from_address(context+0xac94).value-1
                assert radius == 9
                after = [capture(frame) for frame in frame_objects]
                summary, frame_reports = Counter(), []
                generated_digest = hashlib.sha256()
                for i, (old, new, saved) in enumerate(zip(before, after, original)):
                    summary['source_frames'] += 1
                    summary['main_amplitude_changed_in_preparation'] += old['amplitude'] != new['amplitude']
                    assert old['frequency'] == new['frequency'] and old['phase'] == new['phase']
                    assert raw_float(old['pitch']) == raw_float(new['pitch'])
                    assert struct.pack('<d', old['time']) == struct.pack('<d', new['time'])
                    assert old['feature'][0x28:0x20c] == new['feature'][0x28:0x20c]
                    assert old['feature'][0x21c:0x220] == new['feature'][0x21c:0x220]
                    changed_metadata = old['feature'][0x20:0x24] != new['feature'][0x20:0x24] or old['feature'][0x220:0x224] != new['feature'][0x220:0x224]
                    summary['metadata_adapted_frames'] += changed_metadata
                    if changed_metadata and not new['m'] and new['pitch'] > INVALID:
                        assert new['feature'][0x20:0x24] == raw_float(0.)
                        assert new['feature'][0x220:0x224] == raw_float(new['pitch'])
                        selected = [(j, after[j]) for j in range(max(0,i-radius), min(len(after),i+radius+1))
                                    if after[j]['n'] > 0 and after[j]['pitch'] > INVALID]
                        assert blend_strength(after, i, selected, radius) >= 2.5
                        summary['adapted_no_arrays_saturated_strength'] += 1
                    if old['m'] or not new['m']:
                        continue
                    predicted_a, predicted_b, shape = predict(after, i, radius, lowpass)
                    summary['generated_frames'] += 1
                    summary['count_matching'] += len(predicted_a) == new['m']
                    summary['tail_slots'] += shape['tail_slots']
                    summary['pitch_blend_nonzero_frames'] += shape['pitch_blend_nonzero']
                    summary['pitch_blend_saturated_frames'] += shape['pitch_blend_saturated']
                    assert len(predicted_a) == new['m']
                    exact_a, exact_b = 0, 0
                    for a, b, pa, pb in zip(new['first'], new['second'], predicted_a, predicted_b):
                        errors_a.append(a-pa); errors_b.append(b-pb)
                        exact_a += raw_float(a) == raw_float(pa)
                        exact_b += raw_float(b) == raw_float(pb)
                    summary['array_values'] += new['m']
                    summary['baseline_float32_bytes_exact'] += exact_a
                    summary['residual_float32_bytes_exact'] += exact_b
                    summary['bit18_equals_first_zero'] += new['feature'][0x220:0x224] == raw_float(new['first'][0])
                    summary['bit7_equals_second_zero'] += new['feature'][0x20:0x24] == raw_float(new['second'][0])
                    summary['tail_residual_zero'] += sum(value == 0. for value in new['second'][new['n']:])
                    generated_digest.update(struct.pack('<II', i, new['m']))
                    for values in (new['first'], new['second']):
                        generated_digest.update(struct.pack('<'+str(len(values))+'f', *values))
                    if saved['m']:
                        summary['original_arrays_rebuilt'] += 1
                        summary['original_count_matching'] += saved['m'] == new['m']
                        for a, b, oa, ob in zip(new['first'][1:], new['second'][1:], saved['first'][1:], saved['second'][1:]):
                            original_a.append(a-oa); original_b.append(b-ob)
                    frame_reports.append(dict(source_index=i, observed_count=new['m'], shape=shape,
                        exact_baseline=exact_a, exact_residual=exact_b))
                counts.update(summary)
                cases.append(dict(sample_index=index, variant=variant, released_optional_frames=removed,
                    owned_feature_changes=feature_changes, radius=radius,
                    summary=dict(summary), frames=frame_reports,
                    generated_arrays_sha256=generated_digest.hexdigest(), audio=result))
                if len(selected_indices) <= 8 or index % 10 == 0:
                    print(json.dumps(dict(sample_index=index, variant=variant, summary=dict(summary))), flush=True)
            finally:
                if control:
                    native.fn(0xb480, None, P)(control)
                assert native.fn(0x10080, Q, P)(owned_root) == 0
    finally:
        assert native.fn(0x10080, Q, P)(root) == 0
    assert c.string_at(lowpass.harness.bank_base, 0x400+64) == lowpass.harness.bank_before
    assert c.string_at(lowpass.harness.context_base, 0x2de8+64) == lowpass.harness.context_before
    assert counts['array_values'] == counts['baseline_float32_bytes_exact'] == counts['residual_float32_bytes_exact']
    assert counts['generated_frames'] == counts['count_matching'] == counts['bit18_equals_first_zero'] == counts['bit7_equals_second_zero']
    assert counts['tail_slots'] == counts['tail_residual_zero']
    return dict(random_identity=random_identity, shared_fft_identity=lowpass.harness.fft_identity,
        max_additional_audio_blocks=blocks,
        owned_feature_control=dict(vector13_slot=feature_slot, vector13_value=feature_value if feature_slot is not None else None,
                                   bit9_value=bit9_value),
        counts=dict(counts), baseline_error=metrics(errors_a), residual_error=metrics(errors_b),
        original_nonfirst_baseline_error=metrics(original_a), original_nonfirst_residual_error=metrics(original_b),
        lowpass_sequences=lowpass.calls, cases=cases,
        scope='Established initial native audio call plus the specified additional chunks per source. Model uses post-preparation main arrays, '
              'timestamps and feature inputs, independently reconstructs scheduling/mapping/mixing and '
              'shares only genuine FFT transforms. Original main-array preparation is not modeled. '
              'Native field release is confined to owned loaded objects; no installed file/code changes. '
              'No whole-host, original authoring or bit17 production claim.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'directory', 'reference', 'output'):
        parser.add_argument('--'+name, type=Path, required=True)
    parser.add_argument('--samples', default='3,10')
    parser.add_argument('--variant', choices=('baseline','missing_optional_arrays'), default='baseline')
    parser.add_argument('--feature-slot', type=int, choices=(0,1,12,13,19))
    parser.add_argument('--feature-value', type=float, default=520.)
    parser.add_argument('--bit9-value', type=float)
    parser.add_argument('--blocks', type=int, default=0)
    parser.add_argument('--feature-matrix', action='store_true')
    args = parser.parse_args()
    selected = list(range(119)) if args.samples == 'all' else [int(value) for value in args.samples.split(',')]
    assert selected and len(set(selected)) == len(selected) and all(0 <= x < 119 for x in selected)
    assert args.feature_slot is None or args.bit9_value is None
    assert math.isfinite(args.feature_value) and abs(args.feature_value) <= 1000.
    assert args.bit9_value is None or math.isfinite(args.bit9_value) and abs(args.bit9_value) <= 1000.
    assert 0 <= args.blocks <= 64
    result = dict(schema=1, dse_sha256=SHA256, resource_sha256=RESOURCE_SHA256,
        reference_sha256=hashlib.sha256(args.reference.read_bytes()).hexdigest(),
        raw_count_diagnostic=raw_counts(args.directory, args.reference))
    if args.feature_matrix:
        assert args.feature_slot is None and args.bit9_value is None and len(selected) <= 8
        matrix = [('unchanged_features',None,260.,None)]
        matrix += [('vector13_slot'+str(slot),slot,260.,None) for slot in (0,1,12,13,19)]
        matrix += [('vector13_saturated',1,520.,None),('bit9_partial',None,260.,2.5),('bit9_saturated',None,260.,4.)]
        result['feature_matrix'] = {label:run(args.dll, args.directory, selected,
            'missing_optional_arrays',slot,value,bit9,args.blocks) for label,slot,value,bit9 in matrix}
        baseline = result['feature_matrix']['unchanged_features']['cases']
        result['feature_array_effects'] = {label:[dict(sample_index=b['sample_index'],
            arrays_changed=b['generated_arrays_sha256'] != row['generated_arrays_sha256'],
            generated_frames=row['summary'].get('generated_frames',0),
            saturated_metadata_only_frames=row['summary'].get('adapted_no_arrays_saturated_strength',0))
            for b,row in zip(baseline,report['cases'])] for label,report in result['feature_matrix'].items()}
    else:
        result['runtime'] = run(args.dll, args.directory, selected, args.variant,
            args.feature_slot, args.feature_value, args.bit9_value, args.blocks)
    sources = ('verify_vqm_compact_runtime_decomposition.py','verify_vqm_temporal_lowpass.py',
        'verify_frm2_analysis_windows.py','verify_vqm_spectrum_stage_precision.py',
        'verify_vqm_compact_amplitude_split.py','audit_vqm_compact_array_lifecycle.py',
        'audit_frm2_numeric.py','audit_vqm_compact_load_identity.py','audit_vqm_legacy_live_features.py',
        'audit_vqm_legacy_phase_relations.py','verify_vqm_legacy_bit17_consumption.py',
        'verify_vqm_legacy_seeded_consumption.py','audit_vqm_v4_module_leads.py','probe_frm2.py',
        'verify_region_frame_blend.py')
    result['source_sha256'] = {name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                              for name in sources}
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(result['feature_array_effects'] if args.feature_matrix else
        {key:result['runtime'][key] for key in ('counts','baseline_error','residual_error')}))


if __name__ == '__main__':
    main()
