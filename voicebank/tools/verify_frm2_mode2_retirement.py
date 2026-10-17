"""Mode-2 delayed retirement with explicitly allocated extra queue slot."""
import argparse
from itertools import product
import json
from pathlib import Path
from audit_frm2_numeric import Native,SHA256,F,D,I,P,Q,B
from frm2_mode2_tracks import retire_from_history


def queue_layout(native):
    rows=[]
    for mode in range(6):
        config=native.alloc(0x35f0);native.fn(0x90de0,P,P)(config)
        B.from_address(config+0x558).value=mode
        for key,value in ((0,2),(6,.03),(7,.03)):
            for x in (0.,1.):native.add(config+0x5d0+key*0xa0,x,value)
        context=native.alloc(0xce0);native.fn(0xcdde0,P,P,I,P)(context,44100,config)
        tracker=P.from_address(context+0xc98).value
        base=P.from_address(context+0xc20).value
        queue=P.from_address(context+0xc50).value
        count=I.from_address(context+0xc18).value
        index=I.from_address(tracker+0x18).value
        offset=(queue-base)//8
        rows.append(dict(analysis_mode=mode,count=count,offset=offset,index=index,
                         extra_slot_allocated=offset+index+1<count))
    return rows


def verify(native):
    config=native.alloc(0x35f0);native.fn(0x90de0,P,P)(config)
    context=native.alloc(0x21bf8);P.from_address(context).value=config
    B.from_address(context+0x21b34).value=2
    I.from_address(context+0x1c).value=1
    call=native.fn(0xf1a80,Q,P)
    cases=retired=extra_active=0
    for index,rate,gap,current,absent in product((0,1,2,5,9),(100,200),(0.,.01,.02,.03001),(0,-1),(False,True)):
        for last in range(-1,index+2):
            for key,value in ((3,-50),(6,0),(7,gap)):
                for x in (0.,1.):native.add(config+0x5d0+key*0xa0,x,value)
            F.from_address(config+0x18).value=rate
            I.from_address(context+0x18).value=index
            I.from_address(context+0x2c).value=current
            F.from_address(context+0x20).value=-20
            F.from_address(context+0x24).value=440
            I.from_address(context+0x70).value=0
            links=[];amplitudes=[]
            for i in range(index+2):
                active=i<=last
                amplitude=-20. if active else (None if absent else 10000.)
                amplitudes.append(amplitude)
                frame=native.alloc(4096);native.frame_ctor(frame)
                D.from_address(frame+0x48).value=i/rate
                if amplitude is not None:
                    I.from_address(frame+0x58).value=1
                    for at,value in ((0x60,440. if active else 0.),(0x68,amplitude),(0x70,0.)):
                        P.from_address(frame+at).value=native.array(F,[value])
                link=native.alloc(0x30);P.from_address(link+0x28).value=frame;links.append(link)
            P.from_address(context+8).value=native.array(P,links)
            track=dict(current=current,retired=0,amplitude=-20.,center=440.)
            changed=retire_from_history(track,amplitudes,index,rate,-50,gap)
            assert call(context)==0
            actual=[F.from_address(context+0x20).value,F.from_address(context+0x24).value,I.from_address(context+0x70).value]
            assert actual==[track['amplitude'],track['center'],track['retired']],(index,rate,gap,current,absent,last,actual,track)
            cases+=1;retired+=changed;extra_active+=last==index+1
    return dict(cases=cases,retired=retired,extra_slot_active_cases=extra_active,mismatches=0,
                scope='Mode-2 f1a80 with contiguous active prefixes and missing tails; short-track deletion disabled; explicit extra queue slot; production availability of extra slot not yet proven')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args();native=Native(args.dll)
    report=dict(dll_sha256=SHA256,**verify(native),queue_layout=queue_layout(native))
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8');print(json.dumps(report))


if __name__=='__main__':main()
