"""Compare raw feature arithmetic against d92e0 using owned ring buffers."""
import argparse
import json
import math
from pathlib import Path
import random
from audit_frm2_numeric import Native,F,I,P,Q,SHA256,f32
from frm2_raw_features import raw_features


def verify(dll):
    native=Native(dll)
    config=native.alloc(0x35f0);native.fn(0x90de0,P,P)(config)
    for t in (0.,1.):native.add(config+0x5d0+0x17*0xa0,t,1.)
    context=native.alloc(0xce0);P.from_address(context+0xad0).value=config
    frame=native.alloc(0x1a0);features=native.alloc(0x228)
    P.from_address(frame+0x128).value=features
    call=native.fn(0xd92e0,Q,P,P)
    rng=random.Random(7801);rows=[]
    for size,hop in ((64,8),(1024,256),(2048,400)):
        signals={'zero':[0.]*size,'constant':[f32(123.4)]*size,
                 'impulse':[10000.]+[0.]*(size-1),
                 'alternating':[1000. if i%2 else -1000. for i in range(size)],
                 'ramp':[f32(i*.3-50) for i in range(size)]}
        for amplitude in (.5,10.,5000.):
            signals['noise_'+str(amplitude)]=[f32(rng.uniform(-amplitude,amplitude)) for _ in range(size)]
        for frequency in (110,440,5000):
            signals['harmonic_'+str(frequency)]=[f32(5000*sum(math.sin(math.tau*frequency*k*i/44100+k*.23)/k for k in range(1,6))) for i in range(size)]
        for name,ring in signals.items():
            P.from_address(context+0xae0).value=native.array(F,ring)
            I.from_address(context+0xaf0).value=size;I.from_address(context+0xbb0).value=hop
            for start in (0,1,size-hop):
                I.from_address(context+0xb00).value=(start+hop)%size
                for rate in (16000,44100,48000):
                    I.from_address(context+0xad8).value=rate
                    call(context,frame)
                    actual={name:F.from_address(features+offset).value for name,offset in [('feature4',4),('feature8',8),('feature12',12)]}
                    predicted=raw_features(ring,start,hop,rate)
                    errors={key:abs(actual[key]-predicted[key]) for key in actual}
                    if any(errors.values()):raise AssertionError((size,hop,name,start,rate,actual,predicted,errors))
                    rows.append(dict(size=size,hop=hop,signal=name,start=start,sample_rate=rate,actual=actual,max_error=0.))
    return dict(dll_sha256=SHA256,cases=len(rows),field_values=3*len(rows),max_error=0.,rows=rows,
                scope='Enabled raw-feature arithmetic on owned ring buffers; upstream PCM loading and later overrides excluded.')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',required=True,type=Path);parser.add_argument('--output',required=True,type=Path)
    args=parser.parse_args();report=verify(args.dll)
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:report[k] for k in ('cases','field_values','max_error')}))


if __name__=='__main__':main()
