"""Audit clipped context and pitch-baseline model limits in compact DVQM.

Installed payloads stay in memory. Missing-context count witnesses are logical
models, not recovered recordings. Grouped linear fits are diagnostic only;
neither bank arrays nor learned coefficient arrays are serialized.
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
import sys

import numpy as np

from audit_frm2_numeric import F, SHA256, f32
from audit_vqm_compact_load_identity import frames_in_interval
from audit_vqm_legacy_live_features import RESOURCE_SHA256
from audit_vqm_legacy_phase_relations import INVALID, metrics
from verify_vqm_compact_runtime_decomposition import FftLowpass, blend_strength, mix, pitch_hz, raw_float
from verify_vqm_temporal_lowpass import mirrored, weights


RADIUS = 9
MODELS = ('runtime_cents', 'cents_nyquist_suppressed', 'cents_without_mix', 'hz_from_pitch_mix_cents',
          'hz_from_frequency_mix_cents', 'hz_from_pitch_mix_hz', 'hz_from_frequency_mix_hz')


class OwnedLowpass(FftLowpass):
    """Fixed arithmetic candidates on a reusable owned FFT workspace."""
    def __init__(self, dll):
        super().__init__(dll)
        self.base, self.pointer, self.guard = self.harness.buffer([0.]*66)

    def smooth(self, sequence, suppress_nyquist=False):
        self.calls += 1
        length,index = self.prepare(sequence)
        h,pointer = self.harness,self.pointer
        if suppress_nyquist:
            F.from_address(pointer+4*length).value = 0.
            F.from_address(pointer+4*(length+1)).value = 0.
        assert h.compute_backward(h.backward[index],pointer) == 0
        self.check_guard()
        return tuple((F*length).from_address(pointer))

    def prepare(self, sequence):
        n = len(sequence)
        length = 1 << (n.bit_length()+1)
        assert length <= 64
        prepared = mirrored(sequence,length)
        c.memmove(self.pointer,struct.pack('<'+str(len(prepared))+'f',*prepared),len(prepared)*4)
        h, pointer = self.harness, self.pointer
        index = int(math.log2(length))
        assert h.compute_forward(h.forward[index],pointer) == 0
        for k, weight in enumerate(weights(length,min(n,3.),min(n,5.))):
            if weight != 1.:
                for component in (0,1):
                    address = pointer+8*k+4*component
                    F.from_address(address).value = f32(F.from_address(address).value*weight) if weight else 0.
        F.from_address(pointer+4).value = 0.
        return length,index

    def check_guard(self):
        after = c.string_at(self.base,len(self.guard))
        assert self.guard[:32] == after[:32] and self.guard[-32:] == after[-32:]

    def pair(self, sequence, center):
        self.calls += 1
        length,index = self.prepare(sequence)
        h,pointer = self.harness,self.pointer
        prepared = c.string_at(pointer,(length+2)*4)
        assert h.compute_backward(h.backward[index],pointer) == 0
        retained = F.from_address(pointer+4*center).value
        c.memmove(pointer,prepared,len(prepared))
        F.from_address(pointer+4*length).value = 0.
        F.from_address(pointer+4*(length+1)).value = 0.
        assert h.compute_backward(h.backward[index],pointer) == 0
        suppressed = F.from_address(pointer+4*center).value
        self.check_guard()
        return retained,suppressed


def selected(rows, index):
    return [(j, rows[j]) for j in range(max(0,index-RADIUS), min(len(rows),index+RADIUS+1))
            if rows[j]['n'] > 0 and rows[j]['pitch'] > INVALID]


def boundary_constraints(rows, side):
    n = len(rows)
    assert n > 2*RADIUS and side in ('left','right')
    hidden = tuple(range(-RADIUS,0) if side == 'left' else range(n,n+RADIUS))
    indices = range(RADIUS) if side == 'left' else range(n-RADIUS,n)
    constraints = []
    for index in indices:
        if not rows[index]['m']:
            continue
        included = tuple(j for j in hidden if index-RADIUS <= j <= index+RADIUS)
        visible = max(row['n'] for _,row in selected(rows,index))
        constraints.append(dict(index=index, saved=rows[index]['m'], visible=visible, included=included))
    return hidden, constraints


def count_witness(hidden, constraints):
    """Exact max constraints; each demanded level needs a hidden eligible frame."""
    upper = {j:min((row['saved'] for row in constraints if j in row['included']), default=None)
             for j in hidden}
    needs = {row['saved'] for row in constraints if row['saved'] > row['visible']}
    witness = {j:0 for j in hidden}
    if any(row['saved'] < row['visible'] for row in constraints):
        return False, None
    for level in sorted(needs):
        demands = [row for row in constraints if row['saved'] == level and level > row['visible']]
        candidates = [j for j in hidden if upper[j] == level and all(j in row['included'] for row in demands)]
        if not candidates:
            return False, None
        assert all(witness[j] == 0 for j in candidates)
        witness[candidates[0]] = level
    feasible = all(row['saved'] == max([row['visible']]+[witness[j] for j in row['included']])
                   for row in constraints)
    assert sum(value > 0 for value in witness.values()) == len(needs)
    return feasible, witness if feasible else None


def checks(lowpass):
    reports = []
    for side in ('left','right'):
        for pattern in ('constant','rising','falling','alternating'):
            entire = [7 if pattern == 'constant' else 3+j if pattern == 'rising'
                      else 70-j if pattern == 'falling' else 4+(j%7)*2 for j in range(50)]
            crop = entire[9:41]
            rows = [dict(n=value, pitch=0., m=max(entire[max(0,i):i+19]))
                    for i,value in enumerate(crop)]
            hidden, constraints = boundary_constraints(rows,side)
            feasible, witness = count_witness(hidden,constraints)
            assert feasible and witness is not None
            assert all(row['saved'] == max([row['visible']]+[witness[j] for j in row['included']])
                       for row in constraints)
            reports.append(dict(case='known_context_'+side+'_'+pattern, passed=True))
    hidden = (-2,-1)
    for label, constraints in (
        ('visible_count_exceeds_saved',[dict(visible=4,saved=3,included=hidden)]),
        ('inconsistent_nested_hidden_max',[dict(visible=1,saved=2,included=hidden),
                                          dict(visible=1,saved=3,included=(-1,))])):
        feasible,_ = count_witness(hidden,constraints)
        assert not feasible
        reports.append(dict(case=label,passed=True))
    for size in (1,2,16,19):
        for value in (-3600.,0.,2400.):
            result = lowpass.smooth([value]*size)
            assert all(raw_float(x) == raw_float(value) for x in result)
        reports.append(dict(case='constant_pitch_'+str(size),passed=True))
    return reports


def models(rows, index, lowpass):
    row = rows[index]
    pool = selected(rows,index)
    center = next(k for k,(j,_) in enumerate(pool) if j == index)
    strength = blend_strength(rows,index,pool,RADIUS)
    smooth = lowpass.smooth([x['pitch'] for _,x in pool])[center]
    suppressed = lowpass.smooth([x['pitch'] for _,x in pool],True)[center]
    result = dict(runtime_cents=mix(smooth,row['pitch'],strength), cents_without_mix=smooth,
                  cents_nyquist_suppressed=mix(suppressed,row['pitch'],strength))
    for source in ('pitch','frequency'):
        sequence = [pitch_hz(x['pitch']) if source == 'pitch' else x['frequency1'] for _,x in pool]
        assert all(value > 0. for value in sequence)
        hz = lowpass.smooth(sequence)[center]
        assert hz > 0.
        cents = f32(1200.*math.log2(hz/440.))
        result['hz_from_'+source+'_mix_cents'] = mix(cents,row['pitch'],strength)
        current_hz = pitch_hz(row['pitch']) if source == 'pitch' else row['frequency1']
        mixed_hz = mix(hz,current_hz,strength)
        assert mixed_hz > 0.
        result['hz_from_'+source+'_mix_hz'] = f32(1200.*math.log2(mixed_hz/440.))
    result = {key:-200. if value == 0. else value for key,value in result.items()}
    return result, strength, pool


def grouped_linear_audit(rows, native_coefficients, suppressed_coefficients):
    assert rows
    x = np.asarray([row['x'] for row in rows],dtype=np.float64)
    y = np.asarray([row['saved']-row['current'] for row in rows],dtype=np.float64)
    symmetric = np.column_stack([x[:,8-j]+x[:,9+j] for j in range(9)])
    fold_ids = np.asarray([row['fold'] for row in rows],dtype=np.int32)
    results = {}
    for name, design in (('symmetric_19_point',symmetric),('unrestricted_19_point',x)):
        errors, exact = [], 0
        coefficients, folds = [], []
        for fold in range(4):
            train, test = fold_ids != fold, fold_ids == fold
            assert int(train.sum()) > design.shape[1] and int(test.sum()) > 0
            weight, _, rank, singular = np.linalg.lstsq(design[train],y[train],rcond=None)
            predictions = design[test]@weight
            test_rows = [row for row in rows if row['fold'] == fold]
            fold_errors, fold_exact = [], 0
            for row,value in zip(test_rows,predictions):
                prediction = f32(row['current']+float(value))
                error = prediction-row['saved']
                errors.append(error);fold_errors.append(error)
                matched = raw_float(prediction) == raw_float(row['saved'])
                exact += matched;fold_exact += matched
            full = np.zeros(19,dtype=np.float64)
            if name == 'symmetric_19_point':
                for j,value in enumerate(weight):
                    full[8-j] = full[10+j] = value
            else:
                full[:9],full[10:] = weight[:9],weight[9:]
            full[9] = 1.-float(full.sum())
            coefficients.append(full)
            folds.append(dict(fold=fold,training_rows=int(train.sum()),held_out_rows=int(test.sum()),
                rank=int(rank),parameters=design.shape[1],
                singular_condition=float(singular[0]/singular[-1]),
                held_out_error=metrics(fold_errors),held_out_float32_bytes_exact=fold_exact))
        coeff = np.asarray(coefficients)
        results[name] = dict(folds=folds,held_out_error=metrics(errors),held_out_float32_bytes_exact=exact,
            maximum_fold_coefficient_range=float(np.ptp(coeff,axis=0).max()),
            maximum_difference_from_native_impulse_kernel=float(np.abs(coeff-native_coefficients).max()),
            maximum_difference_from_suppressed_nyquist_kernel=float(np.abs(coeff-suppressed_coefficients).max()))
    control_errors = [row['native']-row['saved'] for row in rows]
    return dict(eligible_rows=len(rows),distinct_loaded_state_groups=len({row['group'] for row in rows}),
        fold_rows={str(i):int((fold_ids==i).sum()) for i in range(4)},
        native_control_error=metrics(control_errors),fitted_families=results,
        scope='Four deterministic folds by identical loaded-state hash, full19 valid-pitch interiors '
              'and current model strength0. Sum1/DC-preserving linear filters; no fitted intercept. '
              'Not original authoring parameters or a proven division into independent recordings. '
              'Learned coefficient arrays remain in memory.')


def run(dll, directory, reference):
    data = (directory/'dvqm.dat').read_bytes()
    assert hashlib.sha256(data).hexdigest() == RESOURCE_SHA256
    prior = json.loads(reference.read_text(encoding='utf-8'))
    assert prior['samples'] == 119 and prior['resource_sha256'] == RESOURCE_SHA256
    lowpass = OwnedLowpass(dll)
    own = checks(lowpass)
    native_coeff, suppressed_coeff = [], []
    for j in range(19):
        impulse = [0.]*19;impulse[j] = 1.
        native_coeff.append(lowpass.smooth(impulse)[9])
        suppressed_coeff.append(lowpass.smooth(impulse,True)[9])
    native_coeff = np.asarray(native_coeff,dtype=np.float64)
    suppressed_coeff = np.asarray(suppressed_coeff,dtype=np.float64)
    nyquist_difference = np.asarray([(-1.)**(9+j)*(1. if j in (0,18) else 2.)/64.
                                     for j in range(19)],dtype=np.float64)
    difference_error = float(np.abs(native_coeff-suppressed_coeff-nyquist_difference).max())
    assert difference_error <= 1e-7
    groups = ('all','interior','edge')
    errors = {group:{name:[] for name in MODELS} for group in groups}
    exact = {group:Counter() for group in groups}
    count, boundaries, eligible, identical_windows = Counter(), [], [], {}
    identical_differences = []
    for case in prior['cases']:
        start = int(case['track_offset'],16)
        rows = []
        for _,_,wire in frames_in_interval(data,start,start+case['track_bytes']):
            n,m = wire['count'],wire['spectrum_count']
            rows.append(dict(n=n,m=m,pitch=struct.unpack('<f',wire['pitch'])[0],
                time=wire['time'],feature=wire['feature'],
                saved=struct.unpack_from('<f',wire['spectrum'][0])[0] if m else None,
                frequency1=struct.unpack_from('<f',wire['components'][0],4)[0] if n > 1 else 0.))
        assert len(rows) > 2*RADIUS
        for side in ('left','right'):
            hidden,constraints = boundary_constraints(rows,side)
            feasible,witness = count_witness(hidden,constraints)
            mismatch = sum(row['saved'] != row['visible'] for row in constraints)
            count['boundaries'] += 1
            count['boundaries_with_constraints'] += bool(constraints)
            count['feasible_boundaries'] += feasible
            count['boundaries_needing_hidden'] += mismatch > 0
            count[side+'_length_mismatches'] += mismatch
            count['minimum_eligible_hidden_frames'] += sum(value > 0 for value in witness.values()) if witness else 0
            boundaries.append(dict(sample_index=case['sample_index'],side=side,
                constraints=len(constraints),visible_length_mismatches=mismatch,
                feasible_with_missing_context=feasible,
                minimum_eligible_hidden_frames=sum(value > 0 for value in witness.values()) if witness else 0))
        for index,row in enumerate(rows):
            count['source_frames'] += 1
            if not row['m']:
                continue
            assert row['pitch'] > INVALID
            group = 'interior' if index >= RADIUS and len(rows)-1-index >= RADIUS else 'edge'
            candidates,strength,pool = models(rows,index,lowpass)
            count[group+'_optional_frames'] += 1
            count['nonzero_runtime_strength'] += strength > 0.
            count[group+'_length_matching'] += max(x['n'] for _,x in pool) == row['m']
            for name,prediction in candidates.items():
                for g in ('all',group):
                    errors[g][name].append(prediction-row['saved'])
                    exact[g][name] += raw_float(prediction) == raw_float(row['saved'])
            if group == 'interior' and len(pool) == 19 and strength == 0.:
                values = [x['pitch'] for _,x in pool]
                x = [value-row['pitch'] for k,value in enumerate(values) if k != 9]
                state_group = case['loaded_state_sha256']
                fold = int(hashlib.sha256(state_group.encode('ascii')).hexdigest()[:8],16)%4
                eligible.append(dict(x=x,saved=row['saved'],current=row['pitch'],group=state_group,
                    fold=fold,native=candidates['runtime_cents']))
                key = b''.join(raw_float(value) for value in values)
                if key in identical_windows:
                    identical_differences.append(row['saved']-identical_windows[key])
                else:
                    identical_windows[key] = row['saved']
    assert count['source_frames'] == 26842
    assert count['interior_optional_frames'] == count['interior_length_matching'] == 20798
    assert count['edge_optional_frames'] == 1240
    assert count['left_length_mismatches'] == 79 and count['right_length_mismatches'] == 61
    assert count['feasible_boundaries'] == count['boundaries'] == 238
    assert c.string_at(lowpass.harness.bank_base,0x400+64) == lowpass.harness.bank_before
    assert c.string_at(lowpass.harness.context_base,0x2de8+64) == lowpass.harness.context_before
    result = dict(schema=1,dse_sha256=SHA256,resource_sha256=RESOURCE_SHA256,
        reference_sha256=hashlib.sha256(reference.read_bytes()).hexdigest(),
        python_version=sys.version.split()[0],numpy_version=np.__version__,
        fft_identity=lowpass.harness.fft_identity,owned_checks=own,counts=dict(count),
        boundary_count_feasibility=boundaries,
        pitch_model_errors={group:{name:dict(error=metrics(values),float32_bytes_exact=exact[group][name])
                                  for name,values in entries.items()} for group,entries in errors.items()},
        grouped_linear_audit=grouped_linear_audit(eligible,native_coeff,suppressed_coeff),
        owned_nyquist_impulse_kernel_difference=dict(compared=19,
            maximum_error_to_alternating_1_over64_endpoints_1_over32_interior=difference_error),
        identical_pitch_window_target_differences=metrics(identical_differences),
        scope='Serialized compact resource only,425 bound intervals. No new bank audio/configure calls. '
              'Boundary max-count feasibility does not recover actual hidden frames or prove author identity. '
              'Pitch unit/order and grouped filter fits are diagnostics, not a maker implementation. '
              'Only genuine FFT transforms are shared; installed files read only, payloads/fit arrays in memory. '
              'bit17,vector13 production,BRWSP and other original requirements remain unresolved.')
    sources = ('audit_vqm_compact_authoring_context.py','verify_vqm_compact_runtime_decomposition.py',
        'audit_frm2_numeric.py','audit_vqm_compact_load_identity.py','audit_vqm_legacy_live_features.py',
        'audit_vqm_legacy_phase_relations.py','verify_vqm_temporal_lowpass.py',
        'verify_frm2_analysis_windows.py','verify_vqm_spectrum_stage_precision.py',
        'verify_vqm_compact_amplitude_split.py','verify_vqm_legacy_bit17_consumption.py',
        'verify_vqm_legacy_seeded_consumption.py','audit_vqm_v4_module_leads.py','probe_frm2.py',
        'verify_region_frame_blend.py')
    result['source_sha256'] = {name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                              for name in sources}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll','directory','reference','output'):
        parser.add_argument('--'+name,type=Path,required=True)
    args = parser.parse_args()
    result = run(args.dll,args.directory,args.reference)
    args.output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(counts=result['counts'],pitch_errors=result['pitch_model_errors']['interior'],
        grouped=result['grouped_linear_audit'],identical=result['identical_pitch_window_target_differences'])))


if __name__ == '__main__':
    main()
