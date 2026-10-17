"""Compare full detector plus slope post-filter at d04c0 on owned spectra."""
from __future__ import annotations
import argparse
from itertools import product
import json
import math
from pathlib import Path
import random
from audit_frm2_numeric import Native, SHA256, F, D, I, P, Q, f32, stats
from frm2_fft_peaks import detect, filter_slopes, filter_adaptive


def verify(native):
    config=native.alloc(0x35f0)
    native.fn(0x90de0,P,P)(config)
    context=native.alloc(0xce0)
    P.from_address(context+0xad0).value=config
    source=native.alloc(24)
    destination=native.alloc(16)
    output=native.alloc(30000)
    P.from_address(destination+8).value=output
    call=native.fn(0xd04c0,Q,P,P,P,D,D,F)
    for x in (0.,1.):
        native.add(config+0x5d0+3*0xa0,x,-80)
        native.add(config+0x5d0+0x38*0xa0,x,0)
    threshold=f32(32768*math.exp(f32(-80)*.1151292546497))
    rows,errors=[],[]
    rng=random.Random(620)
    for size,rate,logarithmic,mode,enabled,shape,adaptive,reference_limit in product((64,257),(1000,44100),(0,1),(0,1),(0,1),range(5),(0,1),(1500,5000,20000)):
        I.from_address(context+0xad8).value=rate
        I.from_address(config+0x520).value=logarithmic
        I.from_address(config+0x588).value=enabled
        F.from_address(config+0x584).value=reference_limit
        for x in (0.,1.):
            native.add(config+0x5d0,x,mode)
            native.add(config+0x5d0+0x38*0xa0,x,adaptive)
        power=[f32(10**rng.uniform(2,8)) for _ in range(size)]
        if shape==0:
            power=[100.]*size
        elif shape==1:
            power=[f32((100,1000000,100,10000,100)[i%5]) for i in range(size)]
        elif shape==2:
            power=[f32((10,100,100000,100,10,10000000,100)[i%7]) for i in range(size)]
        bins=[complex(f32(math.sqrt(p)),0) for p in power]
        raw=detect(power,bins,rate,logarithmic,threshold,.003)
        diagnostic={}
        expected=(filter_adaptive(raw,power,reference_limit,diagnostic) if adaptive else filter_slopes(raw,mode)) if enabled else raw
        I.from_address(source).value=size
        P.from_address(source+8).value=native.array(F,power)
        P.from_address(source+16).value=native.array(F,[v for z in bins for v in (z.real,z.imag)])
        assert call(context,source,destination,0.,.003,0.)==0
        count=I.from_address(destination).value
        assert count==len(expected),(size,rate,logarithmic,mode,enabled,shape,count,len(expected))
        for i,want in enumerate(expected):
            actual=[F.from_address(output+20*i+4*j).value for j in range(3)]
            assert [I.from_address(output+20*i+4*j).value for j in (3,4)]==want[3:]
            diff=[abs(a-b) for a,b in zip(actual,want)]
            if max(diff)>=1e-4:
                env=native.env(diagnostic['points'],mode=1,regular=1,bounds=(-20000,20000,0))
                raise AssertionError((size,rate,logarithmic,mode,enabled,shape,adaptive,actual,want,
                    native.evaluate(env,want[0]/22050),diagnostic['points'][-3:]))
            errors.extend(diff)
        rows.append(dict(size=size,rate=rate,logarithmic=logarithmic,mode=mode,enabled=enabled,
                         shape=shape,adaptive=adaptive,reference_limit=reference_limit,raw=len(raw),retained=count,removed=len(raw)-count,
                         rewritten=sum(a[1]!=b[1] for a,b in zip(raw,expected)) if enabled and adaptive else 0))
    return dict(cases=len(rows),parameters=stats(errors),removed=sum(r['removed'] for r in rows),
                rewritten=sum(r['rewritten'] for r in rows),rows=rows,
                scope='Full d04c0 detector plus slope/adaptive branches, cfg584=1500/5000/20000; optional enable and harmonic low-frequency rule')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    report=dict(dll_sha256=SHA256,**verify(Native(args.dll)))
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k!='rows'}))


if __name__=='__main__':
    main()
