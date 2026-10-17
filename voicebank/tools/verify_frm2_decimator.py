"""Compare FIR state and arbitrary input chunking against pinned DSE."""
import argparse
import ctypes as c
import json
from pathlib import Path
import random
from audit_frm2_numeric import Native,F,D,I,P,Q,SHA256
from frm2_decimator import Decimator,runtime_coefficients,selected_coefficients


def verify(native,rate,factor,chunks):
    obj=native.alloc(0x80);native.fn(0x103b00,P,P,P,I,D)(obj,0,factor,rate)
    actual_factor,coefficients=runtime_coefficients(obj)
    if actual_factor!=factor:raise AssertionError('Wrong factor')
    selected=selected_coefficients(native,rate,factor)
    if selected!=coefficients:raise AssertionError('Constructor coefficient selection')
    coefficients=selected
    model=Decimator(factor,coefficients)
    rng=random.Random(831);signal=[0.]*17+[10000.]+[rng.uniform(-20000,20000) for _ in range(1999)]+[0.]*700
    native_all=[];model_all=[];offset=0;step=0
    while offset<len(signal):
        count=min(chunks[step%len(chunks)],len(signal)-offset)
        source=native.array(F,signal[offset:offset+count]);output=(F*(count+1))();written=I()
        native.fn(0x103f00,Q,P,I,P,P,P)(obj,count,source,c.addressof(output),c.byref(written))
        predicted=model.feed(signal[offset:offset+count]);actual=list(output[:written.value])
        if actual!=predicted:raise AssertionError(('FIR samples differ',rate,factor,offset))
        if I.from_address(obj+0x48).value!=len(model.pending):raise AssertionError('Pending count')
        pointer=P.from_address(obj+0x18).value
        history=[F.from_address(pointer+4*i).value for i in range(len(coefficients))]
        if history!=model.history:raise AssertionError('FIR history')
        native_all.extend(actual);model_all.extend(predicted);offset+=count;step+=1
    # Same coefficients and signal must produce the same sequence in one batch.
    if model_all!=Decimator(factor,coefficients).feed(signal):raise AssertionError('Chunk-dependent model')
    return dict(rate=rate,factor=factor,taps=len(coefficients),chunks=chunks,calls=step,
                input_samples=len(signal),output_samples=len(native_all),max_error=0.,state_matches=True)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--dll',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True);a=p.parse_args();native=Native(a.dll)
    geometries=[(11025,2),(11025,4),(22050,2),(22050,4),(22050,8)]+[(r,f) for r in (44100,48000) for f in (2,4,8,16)]
    cases=[verify(native,r,f,pattern) for r,f in geometries for pattern in ([128],[1,3,7,31,129],[2048,19])]
    report=dict(dll_sha256=SHA256,scope='Independent FIR state/arithmetic and constructor asset selection; embedded coefficient values remain explicit external DLL assets, not independently designed',cases=cases)
    a.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(cases=len(cases),output_samples=sum(x['output_samples'] for x in cases),max_error=0.)))
