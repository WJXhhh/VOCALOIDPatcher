"""Native PCM mean-square and minimum-energy region boundary, owned files only."""
import argparse
import ctypes as c
import itertools
import json
import struct
from pathlib import Path
from audit_frm2_numeric import Native,P,I,Q,F,SHA256,f32


def energy(samples,core,position,hop,window):
    first=core+int(f32(f32(position*hop)+.5))-window//2
    total=0.
    for i in range(first,first+window+1):
        value=samples[i] if 0<=i<len(samples) else 0
        value=f32(value/32768)
        total=f32(total+f32(value*value))
    return f32(total/(window+1))


def run(dll,work):
    work.mkdir(parents=True,exist_ok=True)
    n=Native(dll)
    measure=n.fn(0x173f70,F,P,P,P,F)
    choose=n.fn(0x1ae730,P,P,P,P,P,I)
    context,outer,voice,stream,part,analysis=(n.alloc(s) for s in [0x2b000,16,0x300,0x80,0x280,0x100])
    P.from_address(outer+8).value=context
    P.from_address(voice+0x1a0).value=stream
    P.from_address(context+0x1fc8).value=analysis
    buffer=n.alloc(16+65*2+16)
    P.from_address(context+0x24118).value=buffer+16
    prefix=64
    Q.from_address(part+0x1b8).value=prefix
    count=384
    I.from_address(part+0x1d0).value=count
    groups=[(0,6),(6,12),(12,18),(18,24)]
    align=n.array(I,[v for a,b in groups for v in [a,b,a,b]])
    P.from_address(part+0x1d8).value=align
    P.from_address(part+0x1e0).value=align+64
    crt=c.CDLL('ucrtbase.dll')
    crt._wfopen.argtypes,crt._wfopen.restype=[c.c_wchar_p,c.c_wchar_p],P
    crt.fclose.argtypes,crt.fclose.restype=[P],I
    profiles={
        'silence':[0]*count,
        'constant':[1000]*count,
        'negative_full_scale':[-32768]*count,
        'positive_full_scale':[32767]*count,
        'ramp':[(i*193%40001)-20000 for i in range(count)],
        'valleys':[200 if 80<=i<100 or 136<=i<156 else 12000 for i in range(count)]}
    rows=[];energy_rows=[]
    for name,samples in profiles.items():
        path=work/(name+'.pcm')
        with path.open('xb') as file:
            file.write(b'\x35'*prefix+struct.pack('<'+'h'*count,*samples)+b'\x35'*64)
        handle=crt._wfopen(str(path.resolve()),'rb')
        if not handle:raise OSError('Cannot open owned PCM fixture')
        P.from_address(stream+8).value=handle
        try:
            for core,hop,window in itertools.product([0,32,64],[4,8],[2,8,16,64]):
                Q.from_address(part+0x1c0).value=prefix+core*2
                F.from_address(context+0x1d550).value=hop
                I.from_address(analysis+0x8c).value=window
                expected=[]
                for position in range(24):
                    c.memset(buffer,0xa5,16+65*2+16)
                    result=measure(context,part,voice,float(position))
                    prediction=energy(samples,core,float(position),hop,window)
                    assert result==prediction,(name,core,hop,window,position,result,prediction)
                    assert c.string_at(buffer,16)==b'\xa5'*16
                    assert c.string_at(buffer+16+2*(window+1),16)==b'\xa5'*16
                    expected.append(prediction)
                energy_rows.append(dict(profile=name,core=core,hop=hop,window=window,energies=expected))
                for stage,side in itertools.product([0,1],[0,1]):
                    descriptor=n.alloc(0x68)
                    P.from_address(descriptor).value=part
                    I.from_address(descriptor+8).value=stage
                    I.from_address(descriptor+12).value=2
                    for off in [0x28,0x48]:Q.from_address(descriptor+off).value=15
                    result=n.alloc(8)
                    choose(outer,result,descriptor,voice,side)
                    first,last=groups[stage*2+side]
                    candidates=expected[first:last]
                    minimum=min(1.,min(candidates))
                    if minimum<1:
                        offset=candidates.index(minimum)
                    else:
                        # Assembly initializes the index from descriptor's low 32 address bits.
                        offset=I(descriptor & 0xffffffff).value
                    actual_offset=I.from_address(result).value
                    actual_energy=F.from_address(result+4).value
                    assert actual_offset==offset and actual_energy==minimum,(name,core,hop,window,stage,side,actual_offset,offset,actual_energy,minimum)
                    rows.append(dict(profile=name,core=core,hop=hop,window=window,stage=stage,side=side,
                        valid_index=minimum<1,offset=actual_offset if minimum<1 else None,energy=actual_energy,
                        address_bits_preserved=actual_offset==offset if minimum>=1 else None))
        finally:
            crt.fclose(handle)
    return dict(dll_sha256=SHA256,energy_cases=len(energy_rows)*24,boundary_cases=len(rows),mismatches=0,
        undefined_index_cases=sum(not r['valid_index'] for r in rows),energy_rows=energy_rows,rows=rows,
        scope='Real PCM reader, normalized mean-square, and stage/side minimum search; '
              'owned PCM only; complete final mapping integration remains separate')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dll',type=Path,required=True)
    p.add_argument('--work',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True)
    a=p.parse_args();r=run(a.dll,a.work)
    a.output.write_text(json.dumps(r,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:r[k] for k in ['energy_cases','boundary_cases','undefined_index_cases','mismatches']}))
