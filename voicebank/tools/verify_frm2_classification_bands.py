"""Compare owned power spectra with the native forty-band producer."""
import argparse,json,random
from pathlib import Path
from audit_frm2_numeric import Native,P,F,I,Q,SHA256,f32,stats
from frm2_classification_bands import bands


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--dll',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
    n=Native(a.dll);rng=random.Random(90);cases=[]
    floor=F.from_address(n.lib._handle+0x5bff98).value
    assert floor == -100.
    for size in (32,128,512,2048):
        for spacing in ('linear','curved'):
            boundaries=[f32(22050*(i/41)**(1 if spacing=='linear' else 2)) for i in range(42)]
            for shape in ('zero','flat','random','single'):
                power=[0. if shape=='zero' else 1234. if shape=='flat' else f32(10**rng.uniform(-8,9)) if shape=='random' else (1e7 if i==size//3 else 0.) for i in range(size)]
                frame=n.alloc(0x230);feature=n.alloc(0x228);P.from_address(frame+0x128).value=feature;Q.from_address(frame+0x50).value=8192
                n.fn(0x7b4b0,Q,P,P,I,F,P)(frame,n.array(F,power),size,22050.,n.array(F,boundaries))
                predicted=bands(power,22050.,boundaries)
                error=stats([abs(v-F.from_address(feature+0x2c+4*k).value) for k,v in enumerate(predicted)])
                cases.append(dict(size=size,spacing=spacing,shape=shape,errors=error))
                if error['max']>1e-4:raise AssertionError(cases[-1])
    a.output.write_text(json.dumps(dict(dll_sha256=SHA256,floor=floor,cases=cases,scope='Owned power spectra and explicit boundaries; band producer only, not source FFT or live classification chain'),indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(cases=len(cases),values=sum(x['errors']['count'] for x in cases),max_error=max(x['errors']['max'] for x in cases))))


if __name__=='__main__':main()
