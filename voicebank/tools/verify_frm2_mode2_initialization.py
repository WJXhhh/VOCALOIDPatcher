"""Owned mode-2 outer-entry sorting, clipping and initial output matrices."""
from __future__ import annotations
import argparse
from itertools import product
import json
from pathlib import Path
import random
from audit_frm2_numeric import Native, SHA256, F, D, I, P, Q, B, f32, stats
from frm2_mode2_tracks import initialize, reject_crossing


def verify_crossing(native):
    context=native.alloc(0x21bf8)
    B.from_address(context+0x21b34).value=2
    I.from_address(context+0x18).value=1
    links=[native.alloc(0x30),native.alloc(0x30)]
    P.from_address(context+8).value=native.array(P,links)
    call=native.fn(0xf18a0,Q,P,I,I)
    rng=random.Random(641)
    cases=rejected=0
    for count,shape in product((3,5,8),range(200)):
        previous=[[float(rng.randrange(1,9)*100),0,0,0,0] for _ in range(count)]
        current=[[float(rng.randrange(1,9)*100),0,0,0,0] for _ in range(count)]
        permutation=list(range(count));rng.shuffle(permutation)
        tracks=[dict(current=permutation[i],previous=rng.randrange(-1,count)) for i in range(count)]
        claimed=[rng.randrange(2) for _ in range(count)]
        index=rng.randrange(count)
        for i,t in enumerate(tracks):
            I.from_address(context+0x28+i*0x54).value=t['previous']
            I.from_address(context+0x2c+i*0x54).value=t['current']
            I.from_address(context+0x1ec50+i*4).value=claimed[i]
        for link,peaks in zip(links,(previous,current)):
            P.from_address(link+0x20).value=native.array(F,[v for p in peaks for v in p])
        reject_crossing(tracks,claimed,previous,current,index)
        assert call(context,index,count)==0
        assert I.from_address(context+0x2c+index*0x54).value==tracks[index]['current']
        cases+=1
        rejected+=tracks[index]['current']==-1
    return dict(cases=cases,rejected=rejected,mismatches=0,
                scope='Mode-2 f18a0 positive queue index, valid current peak indices, missing prior assignments and tied frequencies; invalid negative-current memory access excluded')


def verify(native):
    config=native.alloc(0x35f0)
    native.fn(0x90de0,P,P)(config)
    context=native.alloc(0x21bf8)
    P.from_address(context).value=config
    P.from_address(context+0x21be0).value=native.alloc(30000)
    P.from_address(context+0x21be8).value=native.alloc(6000)
    link=native.alloc(0x30)
    queue=native.array(P,[link])
    call=native.fn(0xef9f0,Q,P,P,D)
    rng=random.Random(640)
    errors,rows=[],[]
    for count,capacity,lower,shape,f0 in product((0,1,2,3,4,9),(0,1,3,8),(0,100),range(4),(0,220)):
        for key,value in ((0,2),(4,capacity),(0x27,lower),(2,800)):
            for x in (0.,1.):
                native.add(config+0x5d0+key*0xa0,x,value)
        peaks=[[f32(rng.uniform(0,1100)),f32(rng.uniform(-70,0)),f32(i*.3),f32(i),f32(-i)] for i in range(count)]
        if shape==1:
            for i,p in enumerate(peaks):
                p[1]=-20.
                p[0]=float((i%4)*100)
        elif shape==2:
            for i,p in enumerate(peaks):
                p[1]=f32(-3e38) if i%2 else -3.4028234663852886e38
        elif shape==3:
            for i,p in enumerate(peaks):
                p[0]=(0.,100.,800.,801.)[i%4]
        retained,indices=initialize(peaks,capacity,lower,800)
        I.from_address(context+0x21b30).value=0
        I.from_address(context+0x1c).value=0
        frame=native.alloc(4096)
        native.frame_ctor(frame)
        F.from_address(frame+0xf0).value=f0
        D.from_address(frame+0x48).value=0
        P.from_address(link+0x28).value=frame
        I.from_address(link+0x18).value=count
        data=native.array(F,[v for p in peaks for v in p] or [0.]*5)
        P.from_address(link+0x20).value=data
        assert call(context,queue,0.)==0
        assert I.from_address(link+0x18).value==len(retained),(count,capacity,lower,shape,f0)
        assert I.from_address(context+0x1c).value==len(indices)
        assert I.from_address(context+0x21b30).value==1
        assert F.from_address(context+0x21bd8).value==f0
        assert I.from_address(frame+0x58).value==len(indices)
        for i,p in enumerate(retained):
            actual=[F.from_address(data+i*20+j*4).value for j in range(5)]
            assert actual==p,(actual,p)
        for i,peak in enumerate(indices):
            base=context+0x20+i*0x54
            want=retained[peak]
            assert I.from_address(base+12).value==peak
            assert I.from_address(base+0x50).value==0
            for at,value in ((0,want[1]),(4,want[0])):
                actual=F.from_address(base+at).value
                errors.append(abs(actual-value))
                assert actual==value
            for j,at in enumerate((0x60,0x68,0x70)):
                actual=F.from_address(P.from_address(frame+at).value+i*4).value
                errors.append(abs(actual-want[j]))
                assert actual==want[j]
        rows.append(dict(count=count,capacity=capacity,lower=lower,shape=shape,f0=f0,
                         retained=len(retained),tracks=len(indices)))
    return dict(cases=len(rows),parameters=stats(errors),rows=rows,
                scope='Mode-2 first ef9f0 call at queue index zero; sorting, capacity, frequency clipping, initialization and frame output; later lifecycle excluded')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    report=dict(dll_sha256=SHA256,**verify(Native(args.dll)))
    report['crossing']=verify_crossing(Native(args.dll))
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k!='rows'}))


if __name__=='__main__':
    main()
