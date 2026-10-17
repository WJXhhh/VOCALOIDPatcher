"""Follow owned layer PCM through Growl transition mixing and inverse FFT.

Each differential replays an identical prefix and changes just one record's
PCM at the tested step. Reports contain aggregate values/hashes, never PCM.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path

from audit_frm2_numeric import D, F, I, Native, P, Q, SHA256, f32
import probe_vqm_growl_frequency_mix as fixture
from verify_vqm_growl_pitch_boundaries import Transition, reference_model, reversal_cases
from verify_vqm_growl_pitch_selection import choose


def digest(blob): return hashlib.sha256(blob).hexdigest()


class AudioNative(Native):
    rows = []
    prefix_runs = 0
    native_event_calls = 0

    def fn(self, rva, result, *types):
        original = super().fn(rva,result,*types)
        if rva != 0x158E40: return original

        def invoke(growl, working, hz, arg4, mode, output, gain, arg8):
            descriptor=self.alloc(0x80)
            c.memmove(descriptor,P.from_address(growl+8).value,0x40)
            P.from_address(growl+8).value=descriptor
            original(growl,working,hz,arg4,mode,output,gain,arg8)
            source_record=P.from_address(descriptor+0x28).value
            frame_bytes=c.string_at(working,0x328)
            bins=I.from_address(working+0x1A8).value
            assert bins==2049
            target_arrays=[c.string_at(P.from_address(working+off).value,bins*4) for off in (0x1B0,0x1B8)]
            ctor=super(AudioNative,self).fn(0x155E70,P,P,I,D,F,c.c_ubyte,I,c.c_ubyte,c.c_ubyte,c.c_ubyte)
            mixer=super(AudioNative,self).fn(0x158A60,None,P,P,P,F,F,P,P)
            inverse=super(AudioNative,self).fn(0x14DEA0,Q,P,P,I,I)
            pitches=[220.,440.,660.]
            pcms=[]
            for variant in (False,True):
                group=[]
                for k in range(3):
                    fundamental=(220.+110.*k)+(37. if variant else 0.)
                    phase=.35 if variant else 0.
                    samples=[f32(4200.*math.sin(math.tau*fundamental*t/44100.+phase)
                        +(1700. if variant else 900.)*math.sin(math.tau*(2.*fundamental+53.)*t/44100.+.7+k*.2))
                        for t in range(65536)]
                    group.append(self.array(F,samples))
                pcms.append(group)

            def replay(sequence,limit,perturb=None,mix_gain=1.):
                AudioNative.prefix_runs+=1
                data=self.alloc(3*0x850)
                desc=self.alloc(0x80)
                c.memmove(desc,descriptor,0x80)
                for k,pitch in enumerate(pitches):
                    at=data+k*0x850
                    c.memmove(at,source_record,0x850)
                    F.from_address(at+8).value=pitch
                    P.from_address(at+0x808).value=pcms[0][k]
                P.from_address(desc+0x28).value=data
                P.from_address(desc+0x30).value=data+3*0x850
                obj=self.alloc(0x120)
                assert ctor(obj,4096,.01,22050.,0,1,0,0,0)==obj
                P.from_address(obj+8).value=desc
                # Same documented initialization as the existing owned fixture.
                for k in (0xD,0xF,0x11,0x13,0x15,0x17,0x19,0x1B):
                    pointer=P.from_address(obj+k*8).value
                    count=Q.from_address(obj+(k+1)*8).value
                    assert pointer and 0<count<=0x20000
                    c.memset(pointer,0,count*4)
                work=self.alloc(0x328)
                c.memmove(work,frame_bytes,0x328)
                for off,values in ((0x160,[440.,880.]),(0x168,[-20.,-30.]),(0x170,[0.,0.])):
                    P.from_address(work+off).value=self.array(F,values)
                for off,raw in zip((0x1B0,0x1B8),target_arrays):
                    addr=self.alloc(len(raw));c.memmove(addr,raw,len(raw))
                    P.from_address(work+off).value=addr
                real=self.array(F,[0.]*bins);imag=self.array(F,[0.]*bins)
                third=self.array(F,[1.]*bins);third_ptr=self.array(P,[third])
                packed=self.array(F,[0.]*(4096+2))
                event_output=self.array(F,[0.]*4096)
                model=Transition()
                for step,input_hz in enumerate(sequence[:limit+1]):
                    frequencies=P.from_address(work+0x160).value
                    F.from_address(frequencies).value=input_hz
                    F.from_address(frequencies+4).value=2.*input_hz
                    if step==limit and perturb is not None:
                        P.from_address(data+perturb*0x850+0x808).value=pcms[1][perturb]
                    ref=reference_model([input_hz,2.*input_hz],[-20.,-30.],input_hz)
                    candidate,_=choose(pitches,ref['frequencies'][0],model.previous,2,False)
                    expected,branch,_=model.advance(candidate)
                    original(obj,work,input_hz,arg4,1 if step==0 else 2,event_output,1.,arg8)
                    AudioNative.native_event_calls+=1
                    actual=dict(previous=I.from_address(obj+0x60).value,
                        counter=I.from_address(obj+0x34).value,weight=F.from_address(obj+0x38).value)
                    assert actual==expected,(step,actual,expected)
                    for off,idx in ((0x20,model.a),(0x28,model.b)):
                        if idx is not None:
                            assert I.from_address(P.from_address(obj+off).value+0x5C).value==idx
                    c.memset(real,0,bins*4);c.memset(imag,0,bins*4)
                    for k in range(bins): F.from_address(third+k*4).value=1.
                    mixer(obj,real,imag,mix_gain,input_hz,work,third_ptr)
                    F.from_address(packed).value=F.from_address(real).value
                    F.from_address(packed+4).value=F.from_address(real+(bins-1)*4).value
                    for k in range(1,bins-1):
                        F.from_address(packed+k*8).value=F.from_address(real+k*4).value
                        F.from_address(packed+k*8+4).value=F.from_address(imag+k*4).value
                    assert inverse(P.from_address(obj+0x18).value,packed,4096,1)==0
                window_count=I.from_address(obj+0x48).value
                assert 0<window_count<=8193
                window=c.string_at(P.from_address(obj+0xD8).value,window_count*4)
                pcm=c.string_at(packed,4096*4)
                spectrum=c.string_at(real,bins*4)+c.string_at(imag,bins*4)
                values=list((F*4096).from_buffer_copy(pcm))
                assert all(math.isfinite(v) for v in values)
                active_sources={candidate}
                if branch=='transition': active_sources.add(model.b)
                last_window_source=model.b if branch=='transition' else candidate
                return dict(pcm=pcm,spectrum=spectrum,window=window,
                    active_sources=sorted(active_sources),last_window_source=last_window_source,
                    state=actual,pcm_nonzero=sum(v!=0 for v in values),
                    pcm_max_absolute=max(map(abs,values)),window_count=window_count)

            cases=[('steady_220',[220.,220.]),('steady_440',[440.,440.]),('steady_660',[660.,660.])]+reversal_cases()
            for name,sequence in cases:
                for step in range(len(sequence)):
                    base=replay(sequence,step)
                    repeated=replay(sequence,step)
                    assert base['pcm']==repeated['pcm'] and base['spectrum']==repeated['spectrum'] and base['window']==repeated['window'],(name,step,'baseline_repeat')
                    assert base['pcm_nonzero']>0,(name,step,'no_pcm')
                    real_values=list((F*bins).from_buffer_copy(base['spectrum'][:bins*4]))
                    imag_values=list((F*bins).from_buffer_copy(base['spectrum'][bins*4:]))
                    pcm_values=list((F*4096).from_buffer_copy(base['pcm']))
                    inverse_errors=[]
                    for sample in (0,1,2,5,64,512,1024,2047,4095):
                        expected_sample=real_values[0]+real_values[-1]*(-1 if sample&1 else 1)
                        for k in range(1,bins-1):
                            angle=math.tau*sample*k/4096.
                            expected_sample+=2.*(real_values[k]*math.cos(angle)-imag_values[k]*math.sin(angle))
                        inverse_errors.append(abs(pcm_values[sample]-expected_sample/4096.))
                    inverse_tolerance=max(1e-5,2e-7*base['pcm_max_absolute'])
                    assert max(inverse_errors)<=inverse_tolerance,(name,step,'inverse_dft',max(inverse_errors),inverse_tolerance)
                    comparisons=[]
                    for layer in range(3):
                        other=replay(sequence,step,layer)
                        window_changed=base['window']!=other['window']
                        pcm_changed=base['pcm']!=other['pcm']
                        spectrum_changed=base['spectrum']!=other['spectrum']
                        expected_active=layer in base['active_sources']
                        assert window_changed==(layer==base['last_window_source']),(name,step,layer,'window')
                        assert pcm_changed==expected_active,(name,step,layer,'pcm',base,other)
                        assert spectrum_changed==expected_active,(name,step,layer,'spectrum')
                        a=list((F*4096).from_buffer_copy(base['pcm']))
                        b=list((F*4096).from_buffer_copy(other['pcm']))
                        differences=[abs(x-y) for x,y in zip(a,b)]
                        comparisons.append(dict(layer=layer,expected_active=expected_active,
                            window_changed=window_changed,spectrum_changed=spectrum_changed,
                            pcm_changed=pcm_changed,pcm_sha256=digest(other['pcm']),
                            changed_samples=sum(x!=y for x,y in zip(a,b)),
                            max_absolute_pcm_difference=max(differences)))
                    zero=replay(sequence,step,mix_gain=0.)
                    assert zero['pcm_nonzero']==0,(name,step,'zero_mix_control')
                    self.rows.append(dict(case=name,step=step,input_hz=sequence[step],
                        state=base['state'],active_sources=base['active_sources'],
                        last_window_source=base['last_window_source'],window_count=base['window_count'],
                        baseline_pcm_sha256=digest(base['pcm']),baseline_spectrum_sha256=digest(base['spectrum']),
                        baseline_pcm_nonzero=base['pcm_nonzero'],pcm_max_absolute=base['pcm_max_absolute'],
                        baseline_repeat_exact=True,zero_mix_pcm_zero=True,layer_comparisons=comparisons))
                    self.rows[-1].update(inverse_dft_probe_count=9,
                        inverse_dft_max_error=max(inverse_errors),inverse_dft_tolerance=inverse_tolerance)
        return invoke


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True)
    parser.add_argument('--scratch',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    args.scratch.mkdir(parents=True,exist_ok=True)
    fixture.Native=AudioNative
    fixture.run(args.dll,args.scratch,True,False,1.,.1,native_target=True,growl_buffer_fill=0)
    rows=AudioNative.rows
    summary=dict(trajectories=len({r['case'] for r in rows}),tested_steps=len(rows),
        additional_fixture_baseline_calls=1,
        layer_differentials=3*len(rows),prefix_replays=AudioNative.prefix_runs,
        wrapper_calls=AudioNative.native_event_calls,active_differentials=sum(x['expected_active'] for r in rows for x in r['layer_comparisons']),
        all_baselines_repeat_exact=True,all_source_predictions_match=True,all_zero_mix_controls_zero=True)
    summary.update(inverse_dft_probes=9*len(rows),
        inverse_dft_max_error=max(r['inverse_dft_max_error'] for r in rows))
    result=dict(dse_sha256=SHA256,wrapper_rva='0x158e40',mixer_rva='0x158a60',inverse_rva='0x14dea0',
        script_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        fixture_sha256=hashlib.sha256(Path(fixture.__file__).read_bytes()).hexdigest(),
        transition_model_sha256=hashlib.sha256(Path(__file__).with_name('verify_vqm_growl_pitch_boundaries.py').read_bytes()).hexdigest(),
        summary=summary,cases=rows,limits=['Owned PCM/FRM2 only; original DLL and old fixtures are unchanged.',
            'Differentials change only one source PCM pointer at the tested step after an identical prefix.',
            'Verifies native mix spectrum and 4096-sample inverse FFT output, not Editor scheduling or overlap-add/playback.',
            'Does not claim perceived audio quality or original maker metadata production.'])
    args.output.write_text(json.dumps(result,indent=2,allow_nan=False)+'\n',encoding='utf-8')
    print(json.dumps(summary))


if __name__=='__main__': main()
