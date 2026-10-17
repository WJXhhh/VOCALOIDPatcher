"""Owned-PCM integration for Semivowels and VoicedFricatives output mapping."""
import ctypes as c
import itertools
import struct
from audit_frm2_numeric import P,I,Q,F,B,f32
from verify_region_energy_boundary import energy
from region_output_model import positions


def predict_hold(group,count,side,reverse,minimum_offset,coefficient=1.,left_group=None,all_writes=False):
    if count==0:return {} if all_writes else []
    start,end,a,b=group
    width=end-start
    if coefficient<1:
        width=int(f32(max(1.,f32((b-a)*coefficient))+.5))
        if side==0:
            if reverse:end=b=a+width
            else:start=a=b-width
        elif reverse:start=a=left_group[2]-width
        else:end=b=left_group[3]+width
    origin=(end-1 if reverse else start) if side==0 else (b-1 if reverse else a)
    cap=(a if reverse else b-1) if side==0 else (start if reverse else end-1)
    def clip(value):
        return (cap if value<cap else min(origin,value)) if reverse else min(cap,max(origin,value))
    direction=-1 if reverse else 1
    if count<=width:
        values={i:float(clip(origin+direction*i)) for i in range(count)}
        return values if all_writes else list(values.values())
    pivot=width-minimum_offset-1 if reverse else minimum_offset
    tail=width-pivot-1
    values={i:float(clip(origin+direction*i)) for i in range(max(0,pivot))}
    for i in range(pivot,count-tail):values[i]=float(clip(origin+direction*pivot))
    for i in range(count-tail,count):
        j=i-(count-tail)
        value=origin+direction*(pivot+1)+(-direction if reverse and side==0 else direction)*j
        values[i]=float(clip(value))
    return values if all_writes else [values[i] for i in range(count)]


def predict_stop(groups,count,reverse,samples,core,coefficients=(1.,1.)):
    """Return relative output writes, including writes outside the assigned right side."""
    if count==0:return {},None
    oriented=groups[::-1] if reverse else groups
    widths=[g[1]-g[0] for g in oriented]
    margin=min(widths)
    for side,g in enumerate(oriented):
        if coefficients[side]<1:widths[side]=int(f32(max(1.,f32((g[3]-g[2])*coefficients[side]))+.5))
    selected=oriented[1]
    start,end,a,b=selected
    search_start,search_end=start,end
    width=widths[1]
    if coefficients[1]<1:
        if reverse:start=a=oriented[0][2]-width
        else:end=b=oriented[0][3]+width
    pivot=search_start if reverse else search_end-1
    best=1.
    if not reverse:
        for index in range(search_end-1,search_start-margin-1,-1):
            value=energy(samples,core,float(index),8,16)
            if value<best:
                best=value;pivot=index-search_start
    # For positive outer widths the native reverse search guard is false.
    origin=b-1 if reverse else a
    cap=start if reverse else end-1
    def clip(value):return (cap if value<cap else min(origin,value)) if reverse else min(cap,max(origin,value))
    direction=-1 if reverse else 1
    writes={i:float(clip(origin+direction*i)) for i in range(max(0,pivot))}
    tail_begin=max(0,pivot)-width+count
    missing=struct.unpack('<f',struct.pack('<I',0xff7fffff))[0]
    for i in range(pivot,tail_begin):writes[i]=missing
    for i in range(tail_begin,count):writes[i]=float(clip(origin+direction*(i-count+width)))
    return writes,pivot


def run_fixture(fixture,work,stops=False,pcm_coefficients=False):
    n=fixture['n'];context=fixture['context'];part=fixture['part'];voice=fixture['voice']
    classes=fixture['classes'];names=fixture['names'];ring=fixture['ring'];frames=fixture['frames']
    descriptor=fixture['descriptor'];emit=fixture['emit'];outer=fixture['outer'];coefficients=fixture['coefficients']
    work.mkdir(parents=True,exist_ok=True)
    analysis=n.alloc(0x100);destination=n.alloc(2*17)
    I.from_address(analysis+0x8c).value=16
    P.from_address(context+0x1fc8).value=analysis
    P.from_address(context+0x24118).value=destination
    F.from_address(context+0x1d550).value=8
    stream=n.alloc(0x80);P.from_address(voice+0x1a0).value=stream
    record=n.alloc(0x30);category_buffer=n.alloc(64)
    P.from_address(record+0x18).value=category_buffer
    I.from_address(classes+0x140).value=1
    P.from_address(classes+0x148).value=record
    crt=c.CDLL('ucrtbase.dll')
    crt._wfopen.argtypes,crt._wfopen.restype=[c.c_wchar_p,c.c_wchar_p],P
    crt.fclose.argtypes,crt.fclose.restype=[P],I
    profiles={'silence':[0]*640,'ramp':[(i*193%40001)-20000 for i in range(640)],
              'valleys':[200 if 80<=i<110 or 250<=i<290 else 12000 for i in range(640)]}
    groups=fixture['layouts'][0]
    align=n.array(I,[v for g in groups for v in g])
    P.from_address(part+0x1d8).value=align
    P.from_address(part+0x1e0).value=align+64
    Q.from_address(part+0x1b8).value=64
    I.from_address(part+0x1d0).value=640
    F.from_address(coefficients).value=F.from_address(coefficients+4).value=1.
    if stops:
        consume=n.fn(0x16a870,Q,P,I,P,P,B,B)
        consumer_frames=[n.alloc(0x328) for _ in range(3)]
        for frame in consumer_frames:n.fn(0x12a670,P,P)(frame)
        P.from_address(context+0x247b8).value=consumer_frames[0]
        P.from_address(context+0x247c0).value=consumer_frames[1]
        I.from_address(context+0x24124).value=16
        n.fn(0x12ba70,None,P,I,Q,B)(consumer_frames[0],16,0x30,0)
    for off,value in [(8,8),(12,8),(16,128),(20,128)]:I.from_address(coefficients+off).value=value
    rows=[]
    for profile,samples in profiles.items():
        path=work/(profile+'.pcm')
        with path.open('xb') as file:file.write(b'\x35'*64+struct.pack('<640h',*samples)+b'\x35'*64)
        handle=crt._wfopen(str(path.resolve()),'rb')
        if not handle:raise OSError('Cannot open owned PCM fixture')
        P.from_address(stream+8).value=handle
        try:
            categories=['UnvoicedPlosives','VoicedPlosives','UnvoicedAffricates','VoicedAffricates'] if stops else ['Semivowels','VoicedFricatives']
            coefficient_pairs=list(itertools.product([0.,.5,.75,1.,1.5],repeat=2)) if pcm_coefficients else [(1.,1.)]
            for core,stage,reverse,category,members,coefs in itertools.product(
                    [0,64],[0,1],[0,1],categories,[(0,),(1,),(0,1)],coefficient_pairs):
                F.from_address(coefficients).value,F.from_address(coefficients+4).value=coefs
                Q.from_address(part+0x1c0).value=64+core*2
                I.from_address(descriptor+8).value=stage
                raw=category.encode()+b'\0';c.memmove(category_buffer,raw,len(raw))
                I.from_address(record).value=len(members)
                P.from_address(record+0x10).value=n.array(P,[names[i] for i in members])
                selected=groups[stage*2:stage*2+2][::(-1 if reverse else 1)]
                offsets=[]
                for g in selected:
                    energies=[energy(samples,core,float(i),8,16) for i in range(g[0],g[1])]
                    assert min(energies)<1
                    offsets.append(energies.index(min(energies)))
                choices=[[0,1,g[1]-g[0],g[1]-g[0]+5] for g in selected]
                for left,right in itertools.product(*choices):
                    if left+right==0:continue
                    for off in [0x34,0x48,0x60]:I.from_address(ring+off).value=0
                    c.memset(frames,0,256*0x18)
                    for i in range(256):F.from_address(frames+i*0x18+8).value=-999.
                    assert emit(outer,descriptor,*names,left,right,0,0,coefficients,17,23,reverse)==1
                    expected=[];hold_writes={}
                    for side,count in enumerate([left,right]):
                        if side in members and not stops:
                            writes=predict_hold(selected[side],count,side,reverse,offsets[side],coefs[side],selected[0],True)
                            shift=left if side else 0
                            hold_writes.update({shift+i:value for i,value in writes.items()})
                            values=[writes[i] for i in range(count)]
                        else:
                            lengths=[0,0];lengths[side]=count
                            preserve=stops and 0 in members and category in ['UnvoicedPlosives','UnvoicedAffricates']
                            values=positions(selected,lengths,reverse,coefs,preserve)
                            shift=left if side else 0
                            hold_writes.update({shift+i:value for i,value in enumerate(values)})
                        expected.extend(values)
                    pivot=None;outside=[]
                    for slot,value in hold_writes.items():
                        if 0<=slot<len(expected):expected[slot]=value
                        else:
                            actual_outside=F.from_address(frames+(slot%256)*0x18+8).value
                            assert actual_outside==value,(profile,stage,reverse,left,right,coefs,slot,actual_outside,value)
                            outside.append(dict(relative_slot=slot,position=value))
                    if stops and 1 in members:
                        writes,pivot=predict_stop(groups[stage*2:stage*2+2],right,reverse,samples,core,coefs)
                        for i,value in writes.items():
                            slot=left+i
                            if 0<=slot<len(expected):expected[slot]=value
                            else:
                                actual_outside=F.from_address(frames+(slot%256)*0x18+8).value
                                assert actual_outside==value,(profile,stage,reverse,left,right,slot,actual_outside,value)
                                outside.append(dict(relative_slot=slot,position=value))
                    actual=[F.from_address(frames+i*0x18+8).value for i in range(left+right)]
                    key=(profile,core,stage,reverse,category,members,left,right,offsets,coefs)
                    assert max(abs(a-e) for a,e in zip(actual,expected))<1e-5,(key,actual,expected)
                    assert all(B.from_address(frames+i*0x18+0xd).value==0 for i in range(left+right))
                    consumed_blanks=[]
                    if stops:
                        for index,value in enumerate(actual):
                            if value>=-1e30:continue
                            source=frames+index*0x18
                            assert c.c_uint32.from_address(source+8).value==0xff7fffff
                            first,second,result=consumer_frames
                            for offset in [0x1b0,0x1b8]:
                                for j in range(16):F.from_address(P.from_address(first+offset).value+j*4).value=123.
                            F.from_address(context+0x2415c).value=7.
                            untouched=c.string_at(second,0x328)
                            consume(context,index,result,source,0,0)
                            assert F.from_address(context+0x2415c).value==0.
                            assert Q.from_address(first+0x150).value==0x30
                            assert Q.from_address(result+0x150).value==0
                            assert c.c_uint32.from_address(result+0x1d0).value==0xff7fffff
                            assert I.from_address(result+0x1a8).value==0
                            assert all(F.from_address(P.from_address(first+offset).value+j*4).value==0.
                                       for offset in [0x1b0,0x1b8] for j in range(16))
                            assert untouched==c.string_at(second,0x328)
                            consumed_blanks.append(index)
                    rows.append(dict(profile=profile,core=core,stage=stage,reverse=reverse,category=category,
                        members=members,lengths=[left,right],minimum_offsets=offsets,positions=actual,
                        stop_pivot=pivot,outside_writes=outside,consumed_blank_indices=consumed_blanks,coefficients=coefs))
        finally:crt.fclose(handle)
    I.from_address(classes+0x140).value=0
    F.from_address(coefficients).value=F.from_address(coefficients+4).value=1.
    for off in [8,12,16,20]:I.from_address(coefficients+off).value=0
    alignment=n.array(I,[v for g in fixture['layouts'][-1] for v in g])
    P.from_address(part+0x1d8).value=alignment
    P.from_address(part+0x1e0).value=alignment+64
    return rows
