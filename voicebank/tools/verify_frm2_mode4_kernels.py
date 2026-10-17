"""Independent ed230 kind10 kernels and output, on owned signal matrices."""
import argparse
import ctypes as c
import json
import math
from pathlib import Path
import random

from audit_frm2_numeric import Native,SHA256,F,I,P,Q,B,f32
from frm2_mode4_spectrum import build_kernel_bank,convolve_packed
from verify_frm2_analysis_windows import prepare


def verify(dll):
    rows=[];excluded=[]
    for rate in (8000,11025,16000):
        # A fresh transform per rate: ed230's native cache key does not include
        # sample rate, so changing rate on a populated cache would test reuse.
        native=Native(dll)
        config=native.alloc(0x35f0);native.fn(0x90de0,P,P)(config)
        analysis=native.alloc(0xce0);native.fn(0xcdde0,P,P,I,P)(analysis,44100,config)
        estimator=P.from_address(analysis+0xc80).value
        if not estimator:raise AssertionError('Missing default mode4 estimator')
        transform=P.from_address(estimator+8).value
        call=native.fn(0xed230,Q,P,P,I,B,F,P,P,P,B,P,F,I,F)
        for size in (257,353,513,705):
            if rate==16000 and size==257:
                excluded.append(dict(rate=rate,size=size,reason='371-point kernel exceeds NFFT256+1; native centering buffer contract not established'))
                continue
            model=build_kernel_bank(size,rate,1.)
            rng=random.Random(size+rate)
            signals=dict(zero=[0.]*size,constant=[1.]*size,
                         impulse=[float(i==size//3) for i in range(size)],
                         random=[f32(rng.uniform(-1000,1000)) for _ in range(size)],
                         harmonic=[f32(5000*sum(math.sin(math.tau*220*k*i/rate+k*.23)/k for k in range(1,6))) for i in range(size)])
            for name,signal in signals.items():
                scalar,packed,half=P(),P(),I()
                code=call(transform,native.array(F,signal),size,10,
                          F.from_address(native.lib._handle+0x5bffa4).value,
                          c.byref(scalar),0,c.byref(half),2,c.byref(packed),1.,1,rate)
                if code or half.value*2!=model['nfft']:raise AssertionError('Unexpected native transform size')
                groups=I.from_address(transform+0x1c8).value
                width=I.from_address(transform+0x1c0).value
                center=I.from_address(transform+0x1c4).value
                if (groups,width,center)!=(len(model['lengths']),model['width'],model['center']):
                    raise AssertionError('Kernel dimensions differ')
                lengths=P.from_address(transform+0x1a0).value
                mapping=P.from_address(transform+0x198).value
                kernels=P.from_address(transform+0x1a8).value
                if [I.from_address(lengths+4*i).value for i in range(groups)]!=model['lengths']:
                    raise AssertionError('Kernel window lengths differ')
                if [I.from_address(mapping+4*i).value for i in range(half.value+1)]!=model['selections']:
                    raise AssertionError('Kernel selections differ')
                kernel_error=max(abs(v-F.from_address(kernels+4*(g*width+j)).value)
                                 for g,row in enumerate(model['kernels']) for j,v in enumerate(row))
                predicted=convolve_packed(prepare(signal,model['initial_window'],1.,True),
                                          model['selections'],model['kernels'],model['center'])
                packed_error=max(abs(v-F.from_address(packed.value+4*i).value) for i,v in enumerate(predicted))
                values=[abs(predicted[0])]+[f32(f32(predicted[2*i]**2)+f32(predicted[2*i+1]**2)) for i in range(1,half.value)]+[abs(predicted[1])]
                scalar_error=max(abs(v-F.from_address(scalar.value+4*i).value) for i,v in enumerate(values))
                if max(kernel_error,packed_error,scalar_error)!=0:
                    raise AssertionError((rate,size,name,kernel_error,packed_error,scalar_error))
                rows.append(dict(rate=rate,size=size,signal=name,kernel_values=groups*width,
                                 packed_values=len(predicted),scalar_values=len(values),
                                 kernel_max_error=kernel_error,packed_max_error=packed_error,scalar_max_error=scalar_error))
    return dict(dll_sha256=SHA256,cases=rows,excluded_configs=excluded,scope='Independent centered kind10 ed230 kernels and outputs; factor1; no native arrays supplied to predictions')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',required=True,type=Path);parser.add_argument('--output',required=True,type=Path)
    args=parser.parse_args();report=verify(args.dll)
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(cases=len(report['cases']),**{key:sum(x[key] for x in report['cases']) for key in ('kernel_values','packed_values','scalar_values')})))


if __name__=='__main__':main()
