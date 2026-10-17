"""Verify the later bit7 log-frequency regression stage of natural DRS output.

Natural analysis is unmodified; the model shares output harmonic frequencies
and dB values, rather than claiming independent PCM-to-harmonics production.
Commercial data stays in memory. Reports contain only aggregate comparisons.
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

from audit_frm2_numeric import F, I, Native, P, Q, SHA256, f32
from audit_vqm_v4_module_leads import PE
from probe_frm2_batch_outputs import owned_pcm, run as analyze
from verify_vqm_array_production_stages import final_digest, values
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_indexed_temporal_features import guarded_feature


def divide(a, b):
    if b:
        return f32(a/b)
    if a == 0 or math.isnan(a):
        return float('nan')
    return math.copysign(float('inf'), a*math.copysign(1., b))


def regression(x, y):
    assert len(x)==len(y)
    total_x=total_y=0.
    for xx,yy in zip(x,y):
        if yy!=10000.:
            total_x=f32(total_x+xx)
            total_y=f32(total_y+yy)
    mean=divide(total_x,len(x))
    covariance=variance=0.
    for xx,yy in zip(x,y):
        if yy!=10000.:
            delta=f32(xx-mean)
            covariance=f32(covariance+f32(delta*yy))
            variance=f32(variance+f32(delta*delta))
    slope=divide(covariance,variance)
    intercept=divide(f32(total_y-f32(slope*total_x)),len(x))
    return intercept,slope


def stage(frequencies, amplitudes, logarithm=None):
    assert len(frequencies)==len(amplitudes)
    if not frequencies or frequencies[0]<=0:
        return dict(value=0.,selected=0,regression=False)
    selected=0
    for k,(frequency,amplitude) in enumerate(zip(frequencies,amplitudes)):
        if frequency>0 and amplitude>f32(amplitudes[0]-50.):
            selected=k
    # The second selection loop overwrites the first loop's last index. The
    # value is an index used as a count, so that last selected slot is excluded.
    for k,frequency in enumerate(frequencies):
        if frequency<10000.:
            selected=k
    selected=min(20,selected)
    if selected<=4:
        return dict(value=0.,selected=selected,regression=False)
    log=logarithm or (lambda x: f32(math.log10(x)) if x>0 else float('-inf') if x==0 else float('nan'))
    denominator=log(2.)
    x=[divide(log(divide(hz,frequencies[0])),denominator) for hz in frequencies[:selected]]
    _,slope=regression(x,amplitudes[:selected])
    return dict(value=f32(slope/10.),selected=selected,regression=True)


def same(a,b):
    if math.isnan(a) or math.isnan(b):
        return math.isnan(a) and math.isnan(b)
    return struct.pack('<f',a)==struct.pack('<f',b)


def log_import(dll):
    pe=PE(dll.read_bytes())
    assert hashlib.sha256(pe.data).hexdigest()==SHA256
    # Confirm the reviewed thunk target against the bounded PE import table.
    thunk=pe.offset(0x217e0e)
    assert pe.data[thunk:thunk+2]==bytes.fromhex('ff25')
    target=0x217e0e+6+struct.unpack_from('<i',pe.data,thunk+2)[0]
    assert target==0x2248b0
    rva,size=struct.unpack_from('<II',pe.data,pe.directories+8)
    assert 0<size<=0x10000
    for at in range(pe.offset(rva),pe.offset(rva)+size,20):
        lookup,timestamp,forwarder,name,iat=struct.unpack_from('<IIIII',pe.data,at)
        if not any((lookup,timestamp,forwarder,name,iat)):
            break
        for k in range(2048):
            slot=struct.unpack_from('<Q',pe.data,pe.offset(lookup or iat)+8*k)[0]
            if not slot:
                break
            if iat+8*k==target:
                assert slot<1<<63
                imported=pe.text(slot+2)
                assert imported=='log10f'
                return dict(module=pe.text(name),name=imported,iat_rva=hex(target))
        else:
            raise ValueError('Import descriptor exceeds bound')
    raise ValueError('Reviewed logarithm import not found')


def owned_kernel(dll):
    native=Native(dll)
    invoke=native.fn(0x646a0,Q,P,I,P,P,P,P)
    rows=[]
    for count in (0,1,2,3,4,5,7,8,9,20,21,31,40):
        for shape in ('linear','irregular','missing','all_missing','constant_x'):
            x=[f32(k*.317+(k%3)*.17) for k in range(count)]
            y=[f32(20.-k*1.721+(k%4)*.9) for k in range(count)]
            if shape=='linear':y=[f32(7.-2.5*xx) for xx in x]
            if shape=='missing':y=[10000. if k%3==0 else yy for k,yy in enumerate(y)]
            if shape=='all_missing':y=[10000.]*count
            if shape=='constant_x':x=[1.]*count
            xp,yp=native.array(F,x),native.array(F,y)
            before_x=c.string_at(xp,count*4)
            before_y=c.string_at(yp,count*4)
            output=native.alloc(72)
            c.memset(output,0xa5,32)
            c.memset(output+40,0x5a,32)
            before=c.string_at(output,72)
            assert invoke(0,count,xp,yp,output+32,output+36)==0
            actual=values(output,32,2)
            expected=regression(x,y)
            assert all(same(a,b) for a,b in zip(actual,expected)),(shape,count,actual,expected)
            after=c.string_at(output,72)
            assert before[:32]==after[:32] and before[40:]==after[40:]
            assert before_x==c.string_at(xp,count*4) and before_y==c.string_at(yp,count*4)
            rows.append(dict(shape=shape,count=count,finite_exact_values=sum(math.isfinite(x) for x in actual),
                nonfinite_class_matches=sum(not math.isfinite(x) for x in actual),
                guards_and_sources_unchanged=True))
    return rows


def owned_normalization(dll):
    native=Native(dll)
    normalize=native.fn(0x86cb0,Q,P,c.c_ubyte,F)
    rows=[]
    for pitch,cents in ((0.,False),(440.,False),(1200.,True)):
        for selector in range(8):
            frame=native.alloc(0x328)
            native.frame_ctor(frame)
            base,feature=guarded_feature(native)
            P.from_address(frame+0x128).value=feature
            F.from_address(feature+0x20).value=-0.015800755470991135
            Q.from_address(frame+0x50).value=8192|7|((1<<31) if cents else 0)
            I.from_address(frame+0x58).value=8
            F.from_address(frame+0xf0).value=pitch
            for offset,array in ((0x60,[440.*(k+1) for k in range(8)]),
                                 (0x68,[-20.-k*3. for k in range(8)]),
                                 (0x70,[k*.07 for k in range(8)])):
                P.from_address(frame+offset).value=native.array(F,array)
            before=c.string_at(base,0x228+64)
            assert normalize(frame,selector,22050.)==0
            assert c.string_at(base,0x228+64)==before
            rows.append(dict(stored_pitch=pitch,cents=cents,selector=selector,
                entire_feature_bit7_bit17_and_guards_unchanged=True))
    return rows


class Observer:
    def __init__(self):
        self.checks=dict(frames=0,regression_frames=0,zero_branch_frames=0,
            independent_exact_values=0,independent_max_absolute=0.,
            shared_log_exact_values=0,bit17_nonzero=0,negative_bit7_frames=0,
            nonfinite_bit7_frames=0)
        self.selection={}

    def after(self,native,context,status):
        if status==1:
            return
        frame=P.from_address(context+0xcc8).value
        if not frame:
            return
        feature=P.from_address(frame+0x128).value
        assert feature and Q.from_address(frame+0x50).value&8192
        count=I.from_address(frame+0x58).value
        assert 0<=count<=1024
        freq=P.from_address(frame+0x60).value
        amp=P.from_address(frame+0x68).value
        assert count==0 or freq and amp
        frequencies=values(freq,0,count) if count else []
        amplitudes=values(amp,0,count) if count else []
        actual=F.from_address(feature+0x20).value
        predicted=stage(frequencies,amplitudes)
        shared=stage(frequencies,amplitudes,native.fn(0x217e0e,F,F))
        assert same(actual,shared['value']),('shared-log stage',actual,shared)
        finite=math.isfinite(actual) and math.isfinite(predicted['value'])
        error=abs(actual-predicted['value']) if finite else 0.
        assert same(actual,predicted['value']) or finite and error<=2e-5,('independent stage',actual,predicted,error)
        self.checks['frames']+=1
        self.checks['regression_frames']+=predicted['regression']
        self.checks['zero_branch_frames']+=not predicted['regression']
        self.checks['independent_exact_values']+=same(actual,predicted['value'])
        self.checks['independent_max_absolute']=max(self.checks['independent_max_absolute'],error)
        self.checks['shared_log_exact_values']+=1
        self.checks['negative_bit7_frames']+=actual<0
        self.checks['nonfinite_bit7_frames']+=not math.isfinite(actual)
        self.checks['bit17_nonzero']+=F.from_address(feature+0x21c).value!=0
        key=str(predicted['selected'])
        self.selection[key]=self.selection.get(key,0)+1


def natural(dll,name,pcm,alternative,variant):
    observer=Observer()
    digests=[]
    def collect(native,context,collection):
        digests.append(final_digest(native,context,collection))
    options=dict(analysis_mode=4,estimator_variant=variant,config_5b4=alternative,
        streaming=True,pcm_override=pcm,result_observer=collect)
    observed=analyze(dll,0,1,'steady',440.,after_step_observer=observer.after,**options)
    baseline=analyze(dll,0,1,'steady',440.,**options)
    assert observed==baseline and digests[0]==digests[1]
    assert observer.checks['frames']==observed['frames']
    assert observer.checks['bit17_nonzero']==0
    return dict(name=name,alternative=alternative,estimator_variant=variant,
        checks=observer.checks,selected_counts=observer.selection,observer_matches_baseline=True,
        output_feature_sha256=digests[0]['feature_sha256'])


def reference(dll,sequences):
    native=Native(dll)
    invoke=native.fn(0x646a0,Q,P,I,P,P,P,P)
    log=native.fn(0x217e0e,F,F)
    result=[]
    for index,sequence in enumerate(sequences):
        matches=0
        model_native_exact=0
        produced=[]
        for frame in sequence['frames']:
            frequencies=[f32(hz/frame['f0']) for hz,db in frame['components']]
            amplitudes=[f32(db) for hz,db in frame['components']]
            predicted=stage(frequencies,amplitudes,log)
            count=predicted['selected']
            assert predicted['regression']
            x=[divide(log(divide(hz,frequencies[0])),log(2.)) for hz in frequencies[:count]]
            output=native.alloc(8)
            assert invoke(0,count,native.array(F,x),native.array(F,amplitudes[:count]),output,output+4)==0
            slope=F.from_address(output+4).value
            actual=f32(slope/10.)
            assert same(actual,predicted['value'])
            model_native_exact+=1
            produced.append(actual)
            matches+=same(actual,frame['features'][7])
        result.append(dict(sequence=index,frames=len(sequence['frames']),shared_log_native_exact=model_native_exact,
            saved_bit7_exact_matches=matches,predicted_min=min(produced),predicted_max=max(produced),
            max_saved_absolute_error=max(abs(x-frame['features'][7]) for x,frame in zip(produced,sequence['frames']))))
    return result


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('dll','manifest','voice-root','ddb-tools','output'):
        parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--quick',action='store_true')
    args=parser.parse_args()
    parser_hash,ddi_hash,sequences=load_sequences(args.manifest,args.voice_root,args.ddb_tools)
    rng=random.Random(415)
    signals=dict(zero=[0.]*11025,harmonic=owned_pcm('steady',440.),gap=owned_pcm('gap',440.),
        noise=[f32(rng.uniform(-5000,5000)) for _ in range(11025)])
    if not args.quick:
        signals.update({f'v4x_{k}':s['pcm'] for k,s in enumerate(sequences)})
    rows=[natural(args.dll,name,pcm,alternative,variant) for name,pcm in signals.items()
          for alternative in (0,1) for variant in (0,1)]
    sources=('verify_vqm_bit7_spectral_slope.py','audit_frm2_numeric.py','audit_vqm_v4_module_leads.py','probe_frm2_batch_outputs.py',
        'verify_vqm_array_production_stages.py','verify_vqm_bit16_spectral_power.py',
        'verify_vqm_indexed_temporal_features.py')
    report=dict(dse_sha256=SHA256,parser_sha256=parser_hash,ddi_sha256=ddi_hash,
        entries=dict(fit='0x646a0',log10f_thunk='0x217e0e',logarithm_import=log_import(args.dll),
                     bit7_write='0xd9018',slope_divisor=10.),
        owned_fit_cases=owned_kernel(args.dll),owned_normalization_cases=owned_normalization(args.dll),
        natural_cases=rows,reference_cases=reference(args.dll,sequences),
        source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        scope='Later natural mode4 output bit7, two reviewed classifier profiles and estimator variants. '
              'Independent scalar regression arithmetic shares produced harmonics; a separate exact '
              'comparison also shares native log10f. No injection or output replacement. Degenerate '
              'owned fits compare NaN classes, not NaN bit payloads. Saved V4X comparison '
              'deliberately reconstructs relative frequencies from physical Hz and does not '
              'recover original float bits or authoring input. Not independent PCM-to-harmonics, '
              'original saved bit7 production, official feature naming or bit17 production.')
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(cases=len(rows),frames=sum(x['checks']['frames'] for x in rows),
        shared_exact=sum(x['checks']['shared_log_exact_values'] for x in rows),
        independent_exact=sum(x['checks']['independent_exact_values'] for x in rows))))


if __name__=='__main__':
    main()
