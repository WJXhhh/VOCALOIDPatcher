"""Compare the fixed Nyquist-suppressed decomposition with saved compact arrays.

No learned coefficients are used. Both FFT candidates operate on owned reusable
workspaces and share only the real forward/inverse transforms. Bank payloads
and all predicted arrays remain in memory; reports contain aggregate errors.
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
from audit_vqm_legacy_phase_relations import metrics
from verify_vqm_compact_runtime_decomposition import amplitude_strength, blend_strength, mix, pitch_hz, raw_float


def summarize(errors,exact):
    return dict(error=metrics(errors),float32_bytes_exact=exact)


def run(dll,directory,reference,limit):
    data = (directory/'dvqm.dat').read_bytes()
    assert hashlib.sha256(data).hexdigest() == RESOURCE_SHA256
    prior = json.loads(reference.read_text(encoding='utf-8'))
    assert prior['samples'] == 119 and prior['resource_sha256'] == RESOURCE_SHA256
    lowpass = OwnedLowpass(dll)
    names = ('retained_pitch_ratio','suppressed_pitch_ratio','suppressed_frequency_ratio')
    errors = {name:{key:[] for key in ('baseline','residual','pitch_baseline','tail_baseline')}
              for name in names}
    exact = {name:Counter() for name in names}
    counts,cases = Counter(),[]
    for case in prior['cases'][:limit]:
        start = int(case['track_offset'],16)
        rows = []
        for _,_,wire in frames_in_interval(data,start,start+case['track_bytes']):
            n,m = wire['count'],wire['spectrum_count']
            rows.append(dict(n=n,m=m,pitch=struct.unpack('<f',wire['pitch'])[0],time=wire['time'],
                feature=wire['feature'],amplitude=struct.unpack('<'+str(n)+'f',wire['components'][1]),
                frequency1=struct.unpack_from('<f',wire['components'][0],4)[0] if n>1 else 0.,
                first=struct.unpack('<'+str(m)+'f',wire['spectrum'][0]) if m else (),
                second=struct.unpack('<'+str(m)+'f',wire['spectrum'][1]) if m else ()))
        percase = Counter()
        for index,row in enumerate(rows):
            if not row['m']:
                continue
            if index<9 or len(rows)-1-index<9:
                percase['edge_frames_not_reconstructed'] += 1
                continue
            pool = selected(rows,index)
            center = next(k for k,(j,_) in enumerate(pool) if j==index)
            assert len(pool)>=2 and max(x['n'] for _,x in pool)==row['m']
            strength = blend_strength(rows,index,pool,9)
            ratio_pitch = [f32(pitch_hz(row['pitch'])/pitch_hz(x['pitch'])) for _,x in pool]
            ratio_frequency = [f32(row['frequency1']/x['frequency1']) for _,x in pool]
            percase['interior_frames'] += 1
            for harmonic in range(row['m']):
                if harmonic==0:
                    sequence_pitch = tuple(x['pitch'] for _,x in pool)
                    sequence_frequency = sequence_pitch
                    original = row['pitch']
                    amount_pitch = amount_frequency = strength
                else:
                    sequences=[]
                    for ratios in (ratio_pitch,ratio_frequency):
                        sequence=[]
                        for ratio,(_,other) in zip(ratios,pool):
                            at = min(other['n']-1,max(1,int(f32(f32(f32(harmonic)*ratio)+.5))))
                            sequence.append(other['amplitude'][at])
                        sequences.append(tuple(sequence))
                    sequence_pitch,sequence_frequency = sequences
                    original = row['amplitude'][harmonic] if harmonic<row['n'] else None
                    amount_pitch = amplitude_strength(sequence_pitch,strength)
                    amount_frequency = amount_pitch if sequence_pitch==sequence_frequency else amplitude_strength(sequence_frequency,strength)
                retained,suppressed = lowpass.pair(sequence_pitch,center)
                suppressed_frequency = suppressed if sequence_pitch==sequence_frequency else lowpass.smooth(sequence_frequency,True)[center]
                percase['different_mapping_sequences'] += sequence_pitch!=sequence_frequency
                percase['array_values'] += 1
                percase['tail_values'] += harmonic>=row['n']
                candidates = dict(retained_pitch_ratio=(retained,amount_pitch),
                    suppressed_pitch_ratio=(suppressed,amount_pitch),
                    suppressed_frequency_ratio=(suppressed_frequency,amount_frequency))
                for name,(smoothed,amount) in candidates.items():
                    baseline = mix(smoothed,original,amount) if original is not None else smoothed
                    residual = f32(original-baseline) if original is not None else 0.
                    baseline = -200. if baseline==0. else baseline
                    saved_a,saved_b = row['first'][harmonic],row['second'][harmonic]
                    errors[name]['baseline'].append(baseline-saved_a)
                    errors[name]['residual'].append(residual-saved_b)
                    exact[name]['baseline'] += raw_float(baseline)==raw_float(saved_a)
                    exact[name]['residual'] += raw_float(residual)==raw_float(saved_b)
                    if harmonic==0:
                        errors[name]['pitch_baseline'].append(baseline-saved_a)
                        exact[name]['pitch_baseline'] += raw_float(baseline)==raw_float(saved_a)
                    elif harmonic>=row['n']:
                        errors[name]['tail_baseline'].append(baseline-saved_a)
                        exact[name]['tail_baseline'] += raw_float(baseline)==raw_float(saved_a)
        counts.update(percase)
        cases.append(dict(sample_index=case['sample_index'],counts=dict(percase)))
        if case['sample_index']%10==0:
            print(json.dumps(cases[-1]),flush=True)
    if limit==119:
        assert counts['interior_frames']==20798 and counts['edge_frames_not_reconstructed']==1240
    assert c.string_at(lowpass.harness.bank_base,0x400+64)==lowpass.harness.bank_before
    assert c.string_at(lowpass.harness.context_base,0x2de8+64)==lowpass.harness.context_before
    result=dict(schema=1,dse_sha256=SHA256,resource_sha256=RESOURCE_SHA256,
        reference_sha256=hashlib.sha256(reference.read_bytes()).hexdigest(),samples=limit,
        fft_identity=lowpass.harness.fft_identity,counts=dict(counts),cases=cases,
        models={name:{key:summarize(values,exact[name][key]) for key,values in parts.items()}
                for name,parts in errors.items()},
        scope='Full saved arrays on visible interiors only; pitch metadata and amplitude/tails explicitly separate. '
              'Fixed radius9, runtime strength/mapping, fixed passband3/width5, retained versus suppressed Nyquist. '
              'The frequency-ratio alternative uses serialized frequency1, not a fitted parameter. '
              'Shared genuine FFTs, no native audio or source-object mutations; banks stay read only. '
              'Clipped edge inputs and original producer identity/rounding remain unrecovered. '
              'No bank payload or model arrays exported; original VQM/Growl scope preserved.')
    sources=('verify_vqm_compact_saved_baseline.py','audit_vqm_compact_authoring_context.py',
        'verify_vqm_compact_runtime_decomposition.py','audit_frm2_numeric.py',
        'audit_vqm_compact_load_identity.py','audit_vqm_legacy_live_features.py',
        'audit_vqm_legacy_phase_relations.py','verify_vqm_temporal_lowpass.py',
        'verify_frm2_analysis_windows.py','verify_vqm_spectrum_stage_precision.py',
        'verify_vqm_compact_amplitude_split.py','verify_vqm_legacy_bit17_consumption.py',
        'verify_vqm_legacy_seeded_consumption.py','audit_vqm_v4_module_leads.py',
        'probe_frm2.py','verify_region_frame_blend.py')
    result['source_sha256']={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                             for name in sources}
    return result


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('dll','directory','reference','output'):
        parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--limit',type=int,default=119)
    args=parser.parse_args()
    assert 1<=args.limit<=119
    result=run(args.dll,args.directory,args.reference,args.limit)
    args.output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(counts=result['counts'],models=result['models'])))


if __name__=='__main__':
    main()
