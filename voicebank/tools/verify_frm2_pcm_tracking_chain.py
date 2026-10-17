"""Owned PCM through independent FFT/peaks/filter/tracking/correction models.

Native comparison calls the corresponding real primitives in production order.
F0 is supplied; this is not the complete DRS batch entry or serialized FRM2.
"""
import argparse
import ctypes as c
from itertools import product
import json
import math
from pathlib import Path
from audit_frm2_numeric import Native,SHA256,F,D,I,P,Q,B,f32,stats
from verify_frm2_analysis_windows import window,prepare,fft
from frm2_fft_peaks import detect,filter_slopes,filter_adaptive
from frm2_peak_selection import advance_outer
from frm2_frequency_correction import correct


def verify(native):
    config=native.alloc(0x35f0);native.fn(0x90de0,P,P)(config)
    analysis=native.alloc(0xce0);native.fn(0xcdde0,P,P,I,P)(analysis,44100,config)
    tracker=P.from_address(analysis+0xc98).value
    I.from_address(tracker+0x18).value=0
    fft_context=native.alloc(0x400);native.fn(0x49790,P,P,I,P,I)(fft_context,0,config,0)
    fft_call=native.fn(0x4e8e0,Q,P,P,I,B,F,P,P,P,B,P,F,I,I,I,I)
    detector=native.fn(0xd04c0,Q,P,P,P,D,D,F)
    track_call=native.fn(0xef9f0,Q,P,P,D)
    correction=native.fn(0xeff80,Q,P,I,F)
    source=native.alloc(24);link=native.alloc(0x30);queue=native.array(P,[link])
    P.from_address(link+0x20).value=native.alloc(30000)
    errors=[[],[],[]];rows=[];assignment_mismatches=0;mutated_records=0
    baselines={}
    for size,kind,post,logarithmic,strength,mutate in product((256,512),(0,10,14),(0,1,2),(0,1),(0,.7),(False,True)):
        values={0:1,2:10000,3:-40,4:6,8:0,9:.4,10:1,0xd:430,0x11:.6,0x12:0,0x13:3,
                0x21:.7,0x22:.1,0x24:strength,0x27:20,0x28:80,0x29:.04,0x33:0,0x38:int(post==2)}
        for key,value in values.items():
            for x in (0.,1.):native.add(config+0x5d0+key*0xa0,x,value)
        I.from_address(config+0x520).value=logarithmic
        I.from_address(config+0x588).value=int(post!=0)
        F.from_address(config+0x584).value=5000
        I.from_address(tracker+0x21b30).value=0
        F.from_address(tracker+0x21bd8).value=0
        state=dict(tracks=[],history=0.,initialized=False)
        for step in range(8):
            f0=0. if step==4 else 430.
            signal=[f32((7000*math.sin(math.tau*431.3*(i+step*221)/44100+.7)
                        +3100*math.sin(math.tau*862.6*(i+step*221)/44100-1.2))
                        *(0 if step==4 else (.2 if step==0 else 1.))) for i in range(size)]
            scalar,packed,half=P(),P(),I()
            assert fft_call(fft_context,native.array(F,signal),size,kind,1.,c.byref(scalar),0,c.byref(half),2,c.byref(packed),1.,0,0,0,0)==0
            transformed=fft(prepare(signal,window(native,size,kind),1.,0))
            bins=[complex(f32(z.real),f32(z.imag)) for z in transformed[:half.value+1]]
            power=[abs(bins[0].real)]+[f32(f32(z.real*z.real)+f32(z.imag*z.imag)) for z in bins[1:half.value]]
            threshold=f32(32768*math.exp(-40*.1151292546497))
            peaks=detect(power,bins,44100,logarithmic,threshold,step*.005,10000)
            if post==1:peaks=filter_slopes(peaks,1)
            if post==2:peaks=filter_adaptive(peaks,power,5000)
            I.from_address(source).value=half.value
            P.from_address(source+8).value=scalar.value;P.from_address(source+16).value=packed.value
            assert detector(analysis,source,link+0x18,step*.005,step*.005,10000)==0
            assert I.from_address(link+0x18).value==len(peaks)
            if mutate:
                pointer=P.from_address(link+0x20).value
                for i in range(len(peaks)):
                    I.from_address(pointer+20*i+12).value=-2147483000
                    I.from_address(pointer+20*i+16).value=2147483000
                mutated_records+=len(peaks)
            frame=native.alloc(4096);native.frame_ctor(frame)
            D.from_address(frame+0x48).value=step*.005;F.from_address(frame+0xf0).value=f0
            P.from_address(link+0x28).value=frame
            result=advance_outer(state,peaks,f0,values,[0]*len(peaks),timestamp=step*.005)
            output=[list(p) if p is not None else [0.,10000.,0.] for p in result['output']]
            frequencies,_=correct([p[0] for p in output],[p[1] for p in output],f0,strength,size,44100)
            for p,frequency in zip(output,frequencies):p[0]=frequency
            assert track_call(tracker,queue,step*.005)==0
            assert correction(tracker,size,44100)==0
            assert I.from_address(frame+0x58).value==len(output)
            for i,t in enumerate(state['tracks']):
                native_index=I.from_address(tracker+0x2c+i*0x54).value
                assignment_mismatches+=native_index!=t['current']
                assert native_index==t['current'],(size,kind,post,logarithmic,strength,step,i)
            native_output=[]
            for i,want in enumerate(output):
                actual=[F.from_address(P.from_address(frame+at).value+4*i).value for at in (0x60,0x68,0x70)]
                native_output.append(actual)
                for j,(a,b) in enumerate(zip(actual,want)):
                    error=abs(a-b)
                    if j==2:error=abs((a-b+math.pi)%math.tau-math.pi)
                    assert error<(.01,.001,.001)[j],(size,kind,post,logarithmic,strength,step,i,j,a,b,error)
                    errors[j].append(error)
            key=(size,kind,post,logarithmic,strength,step)
            if mutate:
                assert native_output==baselines[key], ('metadata mutation changed native output',key)
            else:
                baselines[key]=native_output
            rows.append(dict(size=size,window=kind,post=post,logarithmic=logarithmic,strength=strength,mutate=mutate,step=step,peaks=len(peaks),slots=len(output)))
    return dict(cases=len(rows),sequences=len(rows)//8,frequency_hz=stats(errors[0]),amplitude_db=stats(errors[1]),
                phase_radians=stats(errors[2]),assignment_mismatches=assignment_mismatches,mutated_records=mutated_records,rows=rows,
                scope='Independent owned PCM -> window/FFT -> detector/post-filter -> mode1 tracker -> eff80; supplied F0, fixed queue index0, no DRS batch entry or serialization')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args();report=dict(dll_sha256=SHA256,**verify(Native(args.dll)))
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8');print(json.dumps({k:v for k,v in report.items() if k!='rows'}))


if __name__=='__main__':main()
