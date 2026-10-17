"""Owned spectral matrices against the actual native FFT peak detector."""
from __future__ import annotations
import argparse
import ctypes
from itertools import product
import json
import math
from pathlib import Path
import random
from audit_frm2_numeric import Native, SHA256, F, D, I, P, Q, B, f32, stats
from frm2_fft_peaks import detect
from verify_frm2_analysis_windows import window, prepare, fft


def verify_pcm(native):
    config = native.alloc(0x35f0)
    native.fn(0x90de0,P,P)(config)
    context = native.alloc(0x400)
    native.fn(0x49790,P,P,I,P,I)(context,0,config,0)
    analyze = native.fn(0x4e8e0,Q,P,P,I,B,F,P,P,P,B,P,F,I,I,I,I)
    peak_call = native.fn(0x51e60,Q,P,P,I,P,P,F,I,F,D,F)
    output = native.alloc(30000)
    errors = [[],[],[]]
    peak_count = cases = span_mismatches = 0
    span_cases=[]
    for size, kind, logarithmic, centered, factor in product((256,512),(0,10,14),(0,1),(0,1),(1.,2.)):
        signal = [f32(7000*math.sin(math.tau*431.3*i/44100+.7)+3100*math.sin(math.tau*1193.7*i/44100-1.2)) for i in range(size)]
        scalar, packed, half = P(),P(),I()
        assert analyze(context,native.array(F,signal),size,kind,1.,ctypes.byref(scalar),0,
                       ctypes.byref(half),2,ctypes.byref(packed),factor,0,0,0,centered)==0
        transformed = fft(prepare(signal,window(native,size,kind),factor,centered))
        bins = [complex(f32(z.real),f32(z.imag)) for z in transformed[:half.value+1]]
        power = [abs(bins[0].real)] + [f32(f32(z.real*z.real)+f32(z.imag*z.imag)) for z in bins[1:half.value]]
        predicted = detect(power,bins,44100,logarithmic,500,.013)
        pointer,count=P(output),I()
        assert peak_call(packed,scalar,half.value,ctypes.byref(pointer),ctypes.byref(count),44100,logarithmic,500,.013,0)==0
        assert count.value==len(predicted),(size,kind,logarithmic,centered,factor,count.value,len(predicted))
        native_power=[F.from_address(scalar.value+4*k).value for k in range(half.value)]
        native_bins=[complex(F.from_address(packed.value+8*k).value,F.from_address(packed.value+8*k+4).value) for k in range(half.value+1)]
        same_input=detect(native_power,native_bins,44100,logarithmic,500,.013)
        for i,want in enumerate(predicted):
            actual=[F.from_address(output+i*20+j*4).value for j in range(3)]
            discrete=[I.from_address(output+i*20+j*4).value for j in (3,4)]
            assert discrete==same_input[i][3:]
            assert max(abs(a-b) for a,b in zip(actual,same_input[i]))<1e-4
            assert discrete[1]==want[4]
            span_mismatches+=discrete[0]!=want[3]
            if discrete[0]!=want[3]:
                span_cases.append(dict(size=size,window=kind,logarithmic=logarithmic,
                    centered=centered,factor=factor,peak=i,native=discrete[0],independent=want[3]))
            for j,(a,b) in enumerate(zip(actual,want)):
                error=abs(a-b)
                assert error<(.01,.001,.001)[j],(size,kind,logarithmic,centered,factor,i,j,a,b)
                errors[j].append(error)
        peak_count+=count.value
        cases+=1
    return dict(cases=cases,peaks=peak_count,frequency_hz=stats(errors[0]),amplitude_db=stats(errors[1]),
                independent_fft_span_mismatches=span_mismatches,
                span_cases=span_cases,
                phase_radians=stats(errors[2]),scope='Owned PCM -> analytic window -> independent FFT -> independent peak model vs native FFT+51e60; 3 windows, centering and padding; excludes d04c0 post-filter')


def verify(native):
    call = native.fn(0x51e60,Q,P,P,I,P,P,F,I,F,D,F)
    output = native.alloc(30000)
    rng = random.Random(610)
    errors, rows = [], []
    for n, logarithmic, time, cutoff, shape in product((16,65,257),(0,1),(0.,.013), (0.,4000.),range(8)):
        power = [f32(100+rng.random()*900) for _ in range(n)]
        if shape == 0:
            power = [0.] * n
        elif shape == 1:
            power = [100.] * n
        elif shape == 2:
            power = [f32(i*10) for i in range(n)]
        elif shape == 3:
            power = [f32(1000*math.exp(-((i-n*.4)/2)**2)) for i in range(n)]
        elif shape == 4:
            power = [0.,100.,100.,0.] * (n//4) + [0.] * (n%4)
        elif shape == 5:
            power = [f32(10+(i%5)*100) for i in range(n)]
        phases = [f32((-1 if i%2 else 1)*(3.05-.01*(i%4))) for i in range(n)]
        bins = [complex(f32(math.sqrt(p)*math.cos(ph)), f32(math.sqrt(p)*math.sin(ph))) for p,ph in zip(power,phases)]
        threshold = 15. if shape == 6 else 0.
        expected = detect(power,bins,44100,logarithmic,threshold,time,cutoff)
        pointer, count = P(output), I()
        code = call(native.array(F,[v for z in bins for v in (z.real,z.imag)]),
                    native.array(F,power),n,ctypes.byref(pointer),ctypes.byref(count),44100,
                    logarithmic,threshold,time,cutoff)
        assert code == 0 and count.value == len(expected), (n,logarithmic,time,cutoff,shape,count.value,len(expected))
        delta = []
        for i,want in enumerate(expected):
            actual = [F.from_address(output+i*20+j*4).value for j in range(3)]
            actual += [I.from_address(output+i*20+j*4).value for j in (3,4)]
            assert actual[3:] == want[3:], (i,actual,want)
            diff = [abs(a-b) for a,b in zip(actual[:3],want[:3])]
            assert max(diff) <= .0001, (n,logarithmic,time,cutoff,shape,i,actual,want,diff)
            delta.extend(diff)
        errors.extend(delta)
        rows.append(dict(n=n,logarithmic=logarithmic,time=time,cutoff=cutoff,shape=shape,
                         peaks=count.value,max_error=max(delta,default=0.)))
    return dict(cases=len(rows),parameters=stats(errors),peaks=sum(r['peaks'] for r in rows),rows=rows,
                scope='51e60 supplied nonnegative power and complex bins, 44100 Hz, both interpolation scales, cutoff and phase-origin correction; upstream FFT-to-spectrum wrapper not yet compared')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    report=dict(dll_sha256=SHA256,**verify(Native(args.dll)))
    report['pcm_pipeline']=verify_pcm(Native(args.dll))
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k!='rows'}))


if __name__=='__main__':
    main()
