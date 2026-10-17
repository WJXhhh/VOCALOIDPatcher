"""Post-tracking frequency correction versus a native-constructed context."""
import argparse
from itertools import product
import json
from pathlib import Path
from audit_frm2_numeric import Native,SHA256,F,I,P,Q,stats
from frm2_frequency_correction import correct


def verify(native):
    config=native.alloc(0x35f0);native.fn(0x90de0,P,P)(config)
    analysis=native.alloc(0xce0);native.fn(0xcdde0,P,P,I,P)(analysis,44100,config)
    context=P.from_address(analysis+0xc98).value
    I.from_address(context+0x18).value=0
    link=native.alloc(0x30);P.from_address(context+8).value=native.array(P,[link])
    call=native.fn(0xeff80,Q,P,I,F)
    marks=P.from_address(context+0x21bf0).value
    errors=[];cases=marked_count=changed=0
    patterns=[([],[]),([200.],[-20.]),([200.,220.,450.,460.],[-10.,-50.,-30.,-30.]),
              ([0.,220.,440.,0.,700.,710.],[-20.,-20.,-20.,10000.,-40.,-10.]),
              ([800.,250.,790.,240.],[-20.,-30.,-20.,-20.]),([200.,200.,220.,230.],[-20.,-10.,-20.,-20.])]
    for (frequencies,amplitudes),f0,strength,length,scale,mask in product(patterns,(0,220),(0,.5,1.,1.5),(256,1024),(44100,48000),(0,1,2,3)):
        for x in (0.,1.):native.add(config+0x5d0+0x24*0xa0,x,strength)
        frame=native.alloc(4096);native.frame_ctor(frame)
        I.from_address(frame+0x58).value=len(frequencies)
        Q.from_address(frame+0x50).value=mask
        F.from_address(frame+0xf0).value=f0
        P.from_address(frame+0x60).value=native.array(F,frequencies or [0.])
        P.from_address(frame+0x68).value=native.array(F,amplitudes or [0.])
        P.from_address(link+0x28).value=frame
        for i in range(len(frequencies)):I.from_address(marks+4*i).value=7
        expected,expected_marks=correct(frequencies,amplitudes,f0,strength,length,scale,mask)
        assert call(context,length,scale)==0
        actual=[F.from_address(P.from_address(frame+0x60).value+4*i).value for i in range(len(frequencies))]
        delta=[abs(a-b) for a,b in zip(actual,expected)]
        assert max(delta,default=0.)<1e-5,(frequencies,f0,strength,length,scale,mask,actual,expected)
        actual_marks=[I.from_address(marks+4*i).value for i in range(len(frequencies))]
        assert actual_marks==(expected_marks if expected_marks is not None else [7]*len(frequencies))
        assert [F.from_address(P.from_address(frame+0x68).value+4*i).value for i in range(len(frequencies))]==amplitudes
        errors.extend(delta);cases+=1
        marked_count+=sum(expected_marks or [])
        changed+=sum(a!=b for a,b in zip(actual,frequencies))
    return dict(cases=cases,parameters=stats(errors),marked=marked_count,changed=changed,
                scope='eff80 native constructor ENV; ordinary finite arrays; mask gating, two scales/window sizes, zero F0 and strengths 0..1.5; production parameter selection not inferred')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args();report=dict(dll_sha256=SHA256,**verify(Native(args.dll)))
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8');print(json.dumps(report))


if __name__=='__main__':main()
