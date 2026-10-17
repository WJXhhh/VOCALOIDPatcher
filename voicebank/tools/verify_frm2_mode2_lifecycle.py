"""Mode-2 repeated outer calls, reuse and immediate retirement at queue zero."""
import argparse
from itertools import product
import json
from pathlib import Path
import random
from audit_frm2_numeric import Native,SHA256,F,D,I,P,Q,f32,stats
from frm2_mode2_tracks import advance_zero


def verify(native):
    config=native.alloc(0x35f0);native.fn(0x90de0,P,P)(config)
    context=native.alloc(0x21bf8);P.from_address(context).value=config
    P.from_address(context+0x21be0).value=native.alloc(30000)
    P.from_address(context+0x21be8).value=native.alloc(6000)
    link=native.alloc(0x30);queue=native.array(P,[link])
    call=native.fn(0xef9f0,Q,P,P,D)
    rng=random.Random(650)
    totals=dict(added=0,reused=0,retired=0,collisions=0)
    errors=[];cases=0
    for capacity,score,alternate,retry in product((1,3,8),(0,1,2),(0,1),(0,1)):
        values={0:2,2:3000,4:capacity,8:score,9:0,10:retry,0x12:0,0x13:2,
                0x27:20,0x28:120,0x29:.05,0x33:alternate}
        for key,value in values.items():
            for x in (0.,1.):native.add(config+0x5d0+key*0xa0,x,value)
        I.from_address(context+0x21b30).value=0
        state=dict(initialized=False,tracks=[])
        for step in range(10):
            peaks=[[f32(180+230*i+step*17+rng.uniform(-80,80)),f32(-10-rng.random()*40),f32(i*.1),0.,0.] for i in range(1+step%9)]
            if step in (3,7):peaks=[]
            if step==5:
                for p in peaks:p[0]=f32(600+rng.uniform(-50,50))
            frame=native.alloc(4096);native.frame_ctor(frame)
            D.from_address(frame+0x48).value=step*.01
            P.from_address(link+0x28).value=frame
            I.from_address(link+0x18).value=len(peaks)
            P.from_address(link+0x20).value=native.array(F,[v for p in peaks for v in p] or [0.]*5)
            output,trace=advance_zero(state,peaks,values)
            assert call(context,queue,step*.01)==0
            assert I.from_address(context+0x1c).value==len(state['tracks'])
            assert I.from_address(frame+0x58).value==len(output)
            for i,t in enumerate(state['tracks']):
                base=context+0x20+i*0x54
                for name,at,ty in [('amplitude',0,F),('center',4,F),('previous',8,I),('current',12,I),('rank',16,I),('retired',80,I),
                                    ('candidates',20,I),('distance',32,F),('metric',44,F),('cost',56,F),('order',68,I)]:
                    want=t[name] if isinstance(t[name],list) else [t[name]]
                    got=[ty.from_address(base+at+j*4).value for j in range(len(want))]
                    diff=[abs(a-b) for a,b in zip(got,want)]
                    assert max(diff)<1e-5,(capacity,score,alternate,retry,step,i,name,got,want)
                    errors.extend(diff)
                actual=[F.from_address(P.from_address(frame+at).value+i*4).value for at in (0x60,0x68,0x70)]
                assert actual==output[i]
            for key,value in trace.items():totals[key]+=value
            cases+=1
    return dict(cases=cases,sequences=cases//10,parameters=stats(errors),**totals,
                scope='Mode-2 queue-index-zero repeated ef9f0 calls; matching, recursive conflicts, append/reuse and immediate retirement; growing queue delayed retirement excluded')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args();report=dict(dll_sha256=SHA256,**verify(Native(args.dll)))
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8');print(json.dumps(report))


if __name__=='__main__':main()
