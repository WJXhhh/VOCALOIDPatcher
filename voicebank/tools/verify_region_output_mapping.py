"""Exercise the full native output-frame mapping entry on owned ring buffers."""
import argparse
import ctypes as c
import itertools
import json
from pathlib import Path
from audit_frm2_numeric import Native, P, I, Q, F, B, SHA256, f32


def predict(group, count, side, reverse):
    if not count:
        return []
    start, end, inner_start, inner_end = group
    width, inner = end-start, inner_end-inner_start
    if count > width:
        step = f32(1/f32((count-1)/(width-1))) if width>1 else 0.
        origin = end-1 if reverse else start
        return [max(start,min(end-1,f32(origin+(-1 if reverse else 1)*f32(i*step)))) for i in range(count)]
    if side == 0:
        if count < inner:
            origin = inner_start+count-1 if reverse else inner_end-count
        else:
            origin = start+count-1 if reverse else end-count
    else:
        origin = inner_end-1 if reverse else inner_start
    return [float(origin+(-i if reverse else i)) for i in range(count)]


def run(dll, extended=False, caller_metadata=False, fricatives=False, pcm_work=None, stops=False, nasal_coefficients=False, fricative_coefficients=False, pcm_coefficients=False):
    n=Native(dll)
    inactive_metadata=[I.from_address(n.lib._handle+0x226100+i*4).value for i in range(4)]
    assert inactive_metadata==[-1,-1,-1,-1]
    emit=n.fn(0x1a0450,B,P,P,P,P,I,I,I,I,P,I,I,B)
    ring,context,config,registry,voice,outer,table,classes=(n.alloc(s) for s in
        [0x80,0x2b000,0x100,0x20,0x300,16,0x180,0x160])
    P.from_address(outer).value=ring
    P.from_address(outer+8).value=context
    P.from_address(context).value=config
    P.from_address(config+0x10).value=registry
    I.from_address(registry+8).value=1
    P.from_address(registry+0x10).value=n.array(P,[voice])
    P.from_address(registry+0x18).value=n.array(P,[n.alloc(8)])
    P.from_address(voice+0x170).value=table
    P.from_address(table+0x158).value=classes
    names=[n.alloc(0x40),n.alloc(0x40)]
    for p,name in zip(names,[b'a\0',b'b\0']):c.memmove(p,name,2)
    I.from_address(table+0x144).value=2
    P.from_address(table+0x148).value=n.array(P,names)
    frames=n.alloc(256*0x18)
    items=n.alloc(16*0x140)
    P.from_address(ring).value=frames
    I.from_address(ring+8).value=256
    for off in [0x20,0x38,0x50]:P.from_address(ring+off).value=items
    for off in [0x28,0x2c,0x40,0x58]:I.from_address(ring+off).value=16
    for off in [0x44,0x5c]:I.from_address(ring+off).value=15
    part,parent,descriptor=(n.alloc(s) for s in [0x280,0x180,0x68])
    callback=c.CFUNCTYPE(P,P)(lambda unused:parent)
    n.keep.append(callback)
    vtable=n.alloc(0x68)
    P.from_address(vtable+0x60).value=c.cast(callback,P).value
    P.from_address(part).value=vtable
    P.from_address(descriptor).value=part
    I.from_address(descriptor+12).value=2
    for off in [0x28,0x48]:Q.from_address(descriptor+off).value=15
    coefficients=n.array(F,[1.,1.,0.,0.,0.,0.])
    layouts=[[(0,7,1,4),(7,20,9,18),(20,31,21,26),(31,48,33,46)],
             [(0,5,0,5),(5,12,5,12),(12,15,12,15),(15,20,15,20)],
             [(0,1,0,1),(1,4,2,3),(4,9,6,7),(9,10,9,10)],
             [(3,10,4,7),(10,23,12,21),(23,34,24,29),(34,51,36,49)]]
    rows=[]
    for layout_index,stage,reverse in itertools.product(range(len(layouts)),[0,1],[0,1]):
        groups=layouts[layout_index]
        alignment=n.array(I,[v for g in groups for v in g])
        P.from_address(part+0x1d8).value=alignment
        P.from_address(part+0x1e0).value=alignment+64
        I.from_address(descriptor+8).value=stage
        selected=groups[stage*2:stage*2+2][::(-1 if reverse else 1)]
        counts=[]
        for g in selected:
            width,inner=g[1]-g[0],g[3]-g[2]
            counts.append(sorted(set([0,1,max(1,inner-1),inner,inner+1,width,width+1,width*2])))
        for left,right in itertools.product(*counts):
            if left+right==0:continue
            for off in [0x34,0x48,0x60]:I.from_address(ring+off).value=0
            c.memset(frames,0,256*0x18)
            returned=emit(outer,descriptor,*names,left,right,0,0,coefficients,17,23,reverse)
            actual=[F.from_address(frames+i*0x18+8).value for i in range(left+right)]
            expected=predict(selected[0],left,0,reverse)+predict(selected[1],right,1,reverse)
            assert returned==1 and len(actual)==len(expected)
            assert max(abs(a-e) for a,e in zip(actual,expected))<1e-5, (layout_index,stage,reverse,left,right,actual,expected)
            assert [I.from_address(items+i*4).value for i in range(3)]==[0,left+right,left]
            assert P.from_address(items+0x20).value==part and I.from_address(items+0x28).value==stage
            assert I.from_address(items+0x2c).value==2 and B.from_address(items+0x12c).value==reverse
            assert [I.from_address(items+off).value for off in [0x10,0x14,0x124,0x128]]==[0,1,17,23]
            assert all(P.from_address(frames+i*0x18).value==items for i in range(left+right))
            assert [B.from_address(items+off).value for off in [0xc,0xd]]==[
                int(left<selected[0][3]-selected[0][2]),int(right<selected[1][3]-selected[1][2])]
            rows.append(dict(layout=layout_index,stage=stage,reverse=reverse,lengths=[left,right],positions=actual))
    extended_rows=[]
    nasal_rows=[]
    fricative_rows=[]
    if extended:
        from region_output_model import positions
        record=n.alloc(0x30)
        P.from_address(classes+0x148).value=record
        I.from_address(record).value=1
        P.from_address(record+0x10).value=n.array(P,[names[0]])
        category_buffer=n.alloc(64)
        P.from_address(record+0x18).value=category_buffer
        for layout_index,stage,reverse,category,left_coefficient,right_coefficient in itertools.product(
                [0,2],[0,1],[0,1],[None,'UnvoicedPlosives','UnvoicedAffricates','Whisper'],
                [0.,.5,.75,1.,1.5],[0.,.5,.75,1.,1.5]):
            I.from_address(classes+0x140).value=int(category is not None)
            if category:
                raw=category.encode()+b'\0';c.memmove(category_buffer,raw,len(raw))
            groups=layouts[layout_index]
            alignment=n.array(I,[v for g in groups for v in g])
            P.from_address(part+0x1d8).value=alignment
            P.from_address(part+0x1e0).value=alignment+64
            I.from_address(descriptor+8).value=stage
            selected=groups[stage*2:stage*2+2][::(-1 if reverse else 1)]
            F.from_address(coefficients).value=left_coefficient
            F.from_address(coefficients+4).value=right_coefficient
            choices=[]
            for g,coefficient in zip(selected,[left_coefficient,right_coefficient]):
                inner=g[3]-g[2];width=g[1]-g[0]
                if coefficient<1:inner=width=int(f32(max(1.,f32(inner*coefficient))+.5))
                choices.append(sorted(set([0,1,max(1,inner-1),inner,inner+1,width,width+3])))
            for left,right in itertools.product(*choices):
                if left+right==0:continue
                for off in [0x34,0x48,0x60]:I.from_address(ring+off).value=0
                for i in range(256):F.from_address(frames+i*0x18+8).value=-999.
                assert emit(outer,descriptor,*names,left,right,0,0,coefficients,17,23,reverse)==1
                actual=[F.from_address(frames+i*0x18+8).value for i in range(left+right)]
                expected=positions(selected,[left,right],reverse,[left_coefficient,right_coefficient],category is not None)
                key=(layout_index,stage,reverse,category,left_coefficient,right_coefficient,left,right)
                assert max(abs(a-e) for a,e in zip(actual,expected))<1e-5,(key,actual,expected)
                extended_rows.append(dict(layout=layout_index,stage=stage,reverse=reverse,category=category,
                    coefficients=[left_coefficient,right_coefficient],lengths=[left,right],positions=actual))
        F.from_address(coefficients).value=F.from_address(coefficients+4).value=1.
        # Activate the separately stored integer expansion metadata on both sides.
        from region_output_model import nasal_positions
        I.from_address(record).value=2
        P.from_address(record+0x10).value=n.array(P,names)
        I.from_address(classes+0x140).value=1
        for off,value in [(8,8),(12,8),(16,24),(20,24)]:I.from_address(coefficients+off).value=value
        peer_pairs=list(itertools.product([0,7,21],repeat=2)) if caller_metadata else [(None,None)]
        coefficient_pairs=list(itertools.product([0.,.5,.75,1.,1.5],repeat=2)) if nasal_coefficients else [(1.,1.)]
        for layout_index,stage,reverse,category,peers,coefs in itertools.product([0,2],[0,1],[0,1],['Nasals','Liquids'],peer_pairs,coefficient_pairs):
            F.from_address(coefficients).value,F.from_address(coefficients+4).value=coefs
            raw=category.encode()+b'\0';c.memmove(category_buffer,raw,len(raw))
            groups=layouts[layout_index]
            alignment=n.array(I,[v for g in groups for v in g])
            P.from_address(part+0x1d8).value=alignment
            P.from_address(part+0x1e0).value=alignment+64
            I.from_address(descriptor+8).value=stage
            selected=groups[stage*2:stage*2+2][::(-1 if reverse else 1)]
            choices=[sorted(set([0,1,g[3]-g[2],g[1]-g[0],g[1]-g[0]+3])) for g in selected]
            for left,right in itertools.product(*choices):
                if left+right==0:continue
                if caller_metadata:
                    raw_left,raw_right=groups[2*stage:2*stage+2]
                    metadata=[raw_left[1]-raw_left[0]+7,raw_right[1]-raw_right[0]+7,
                              left+peers[0],right+peers[1]]
                    for off,value in zip([8,12,16,20],metadata):I.from_address(coefficients+off).value=value
                else:
                    metadata=[8,8,24,24]
                active=[metadata[i]>0 and metadata[i+2]>metadata[i] for i in range(2)]
                for off in [0x34,0x48,0x60]:I.from_address(ring+off).value=0
                for i in range(256):F.from_address(frames+i*0x18+8).value=-999.
                assert emit(outer,descriptor,*names,left,right,0,0,coefficients,17,23,reverse)==1
                actual=[F.from_address(frames+i*0x18+8).value for i in range(left+right)]
                expected=[]
                for side,count in enumerate([left,right]):
                    side_counts=[0,0];side_counts[side]=count
                    expected.extend(nasal_positions(selected,side_counts,reverse,coefs) if active[side]
                                    else positions(selected,side_counts,reverse,coefs))
                key=(layout_index,stage,reverse,category,left,right,metadata,coefs)
                assert actual==expected,(key,actual,expected)
                outside=[]
                start,end,a,b=selected[0]
                width=end-start
                if coefs[0]<1:
                    width=int(f32(max(1.,f32((b-a)*coefs[0]))+.5))
                    if reverse:end=a+width
                    else:start=b-width
                if active[0] and 0<left<width:
                    for offset in range(left-width,0):
                        value=F.from_address(frames+(offset%256)*0x18+8).value
                        expected_value=max(a,float(end-1-(offset-left+width))) if reverse else min(b-1,float(start+offset-left+width))
                        assert value==expected_value,(key,offset,value,expected_value)
                        outside.append(dict(offset=offset,position=value))
                nasal_rows.append(dict(layout=layout_index,stage=stage,reverse=reverse,category=category,
                    lengths=[left,right],positions=actual,preceding_frame_writes=outside,
                    metadata=metadata,active=active,peer_allocations=peers,coefficients=coefs))
        F.from_address(coefficients).value=F.from_address(coefficients+4).value=1.
        if fricatives:
            from region_output_model import fricative_positions
            category=b'UnvoicedFricatives\0'
            c.memmove(category_buffer,category,len(category))
            curve_coefficients=list(itertools.product([0.,.5,.75,1.,1.5],repeat=2)) if fricative_coefficients else [(1.,1.)]
            for layout_index,stage,reverse,members,peers,coefs in itertools.product(
                    [0,2],[0,1],[0,1],[(0,),(1,),(0,1)],itertools.product([0,7,21],repeat=2),curve_coefficients):
                F.from_address(coefficients).value,F.from_address(coefficients+4).value=coefs
                I.from_address(record).value=len(members)
                P.from_address(record+0x10).value=n.array(P,[names[i] for i in members])
                groups=layouts[layout_index]
                alignment=n.array(I,[v for g in groups for v in g])
                P.from_address(part+0x1d8).value=alignment
                P.from_address(part+0x1e0).value=alignment+64
                I.from_address(descriptor+8).value=stage
                selected=groups[stage*2:stage*2+2][::(-1 if reverse else 1)]
                choices=[sorted(set([0,1,g[1]-g[0],g[1]-g[0]+3])) for g in selected]
                for left,right in itertools.product(*choices):
                    if left+right==0:continue
                    counts=[left,right]
                    metadata=[groups[2*stage+i][1]-groups[2*stage+i][0]+7 for i in range(2)]+[left+peers[0],right+peers[1]]
                    for off,value in zip([8,12,16,20],metadata):I.from_address(coefficients+off).value=value
                    for off in [0x34,0x48,0x60]:I.from_address(ring+off).value=0
                    c.memset(frames,0,256*0x18)
                    assert emit(outer,descriptor,*names,left,right,0,0,coefficients,17,23,reverse)==1
                    expected=[];expected_flags=[];active=[]
                    for side,count in enumerate(counts):
                        width=selected[side][1]-selected[side][0]
                        if coefs[side]<1:width=int(f32(max(1.,f32((selected[side][3]-selected[side][2])*coefs[side]))+.5))
                        special=(count>0 and side in members and metadata[side+2]>metadata[side]
                                 and (side==0 or count>width))
                        active.append(special)
                        if special:
                            values,flags=fricative_positions(selected[side],count,side,reverse,metadata[side],metadata[side+2],coefs[side],selected[0])
                        else:
                            side_counts=[0,0];side_counts[side]=count
                            values=positions(selected,side_counts,reverse,coefs);flags=[0]*count
                        expected.extend(values);expected_flags.extend(flags)
                    actual=[F.from_address(frames+i*0x18+8).value for i in range(left+right)]
                    flags=[B.from_address(frames+i*0x18+0xd).value for i in range(left+right)]
                    key=(layout_index,stage,reverse,members,peers,left,right,coefs)
                    assert max(abs(a-e) for a,e in zip(actual,expected))<1e-5,(key,actual,expected)
                    assert flags==expected_flags,(key,flags,expected_flags,actual)
                    fricative_rows.append(dict(layout=layout_index,stage=stage,reverse=reverse,members=members,
                        peers=peers,lengths=counts,metadata=metadata,active=active,positions=actual,flags=flags,coefficients=coefs))
            F.from_address(coefficients).value=F.from_address(coefficients+4).value=1.
        I.from_address(classes+0x140).value=0
        for off in [8,12,16,20]:I.from_address(coefficients+off).value=0
        alignment=n.array(I,[v for g in layouts[-1] for v in g])
        P.from_address(part+0x1d8).value=alignment
        P.from_address(part+0x1e0).value=alignment+64
    pcm_rows=[]
    if pcm_work is not None:
        from verify_region_pcm_mapping import run_fixture
        pcm_rows=run_fixture(locals(),pcm_work,stops,pcm_coefficients)
    # Append without exhausting descriptor capacity; cross the 256-frame ring boundary.
    for off in [0x34,0x48,0x60]:I.from_address(ring+off).value=0
    sequences=[]
    cursor=0
    groups=layouts[-1]
    for index in range(12):
        stage=index%2
        reverse=(index//2)%2
        I.from_address(descriptor+8).value=stage
        selected=groups[stage*2:stage*2+2][::(-1 if reverse else 1)]
        left,right=19+index%3,23+index%4
        assert emit(outer,descriptor,*names,left,right,0,0,coefficients,17,23,reverse)==1
        item=items+(index%16)*0x140
        assert [I.from_address(item+o).value for o in [0,4,8]]==[cursor,cursor+left+right,left], (index,cursor,[I.from_address(item+o).value for o in [0,4,8]], [I.from_address(ring+o).value for o in [0x34,0x48,0x60]])
        expected=predict(selected[0],left,0,reverse)+predict(selected[1],right,1,reverse)
        actual=[F.from_address(frames+((cursor+i)%256)*0x18+8).value for i in range(left+right)]
        assert max(abs(a-e) for a,e in zip(actual,expected))<1e-5
        assert all(P.from_address(frames+((cursor+i)%256)*0x18).value==item for i in range(left+right))
        sequences.append(dict(start=cursor,end=cursor+left+right,slot=index%16,stage=stage,reverse=reverse))
        cursor+=left+right
    guards=[]
    for pointer,stage,limit,left,right in [(0,0,2,3,4),(part,-1,2,3,4),(part,2,2,3,4),
                                           (part,0,2,-1,4),(part,0,2,3,-1),(part,0,2,0,0)]:
        P.from_address(descriptor).value=pointer
        I.from_address(descriptor+8).value=stage
        I.from_address(descriptor+12).value=limit
        before=c.string_at(ring,0x80),c.string_at(items,16*0x140),c.string_at(frames,256*0x18)
        assert emit(outer,descriptor,*names,left,right,0,0,coefficients,17,23,0)==0
        assert before==(c.string_at(ring,0x80),c.string_at(items,16*0x140),c.string_at(frames,256*0x18))
        guards.append(dict(null_part=not pointer,stage=stage,limit=limit,lengths=[left,right]))
    blank_rows=[]
    if stops:
        load=n.fn(0x1757a0,None,P,P,I,P,I,I,I,B,I)
        for count,index in itertools.product([8,16,64],[-1,-2,-2147483648]):
            frame=n.alloc(0x328)
            n.fn(0x12a670,P,P)(frame)
            n.fn(0x12ba70,None,P,I,Q,B)(frame,count,0x30,0)
            for offset in [0x1b0,0x1b8]:
                array=P.from_address(frame+offset).value
                for i in range(count):F.from_address(array+i*4).value=123.
            I.from_address(context+0x24124).value=count
            load(context,0,index,frame,0,0,0,0,0)
            assert I.from_address(frame+0x1a8).value==count
            assert Q.from_address(frame+0x150).value==0x30
            assert c.c_uint32.from_address(frame+0x1d0).value==0xff7fffff
            assert all(F.from_address(P.from_address(frame+offset).value+i*4).value==0.
                       for offset in [0x1b0,0x1b8] for i in range(count))
            blank_rows.append(dict(spectral_count=count,input_index=index,mask='0x30',f0_bits='0xff7fffff',arrays_zero=True))
    return dict(dll_sha256=SHA256,cases=len(rows),sequence_cases=len(sequences),guard_cases=len(guards),
        mismatches=0,rows=rows,sequences=sequences,guards=guards,
        extended_cases=len(extended_rows),extended_rows=extended_rows,
        nasal_cases=len(nasal_rows),nasal_rows=nasal_rows,
        fricative_cases=len(fricative_rows),fricative_rows=fricative_rows,
        pcm_cases=len(pcm_rows),pcm_rows=pcm_rows,
        blank_consumer_cases=len(blank_rows),blank_consumer_rows=blank_rows,
        metadata_origin='Adjacent outer-length sums and allocated-length sums; peer outer=7' if caller_metadata else 'Fixed synthetic expansion metadata',
        direct_path_inactive_metadata=dict(rva='0x226100',values=inactive_metadata),
        scope='Full 1a0450 entry, real descriptor allocation and output frame ring; default '
              'mapping plus optional coefficient/PHDC matrix when extended; '
              'optional owned-PCM boundary/search integration; '
              'owned parent callback supplies parent identity; no host reachability claim')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dll',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True)
    p.add_argument('--extended',action='store_true')
    p.add_argument('--caller-metadata',action='store_true')
    p.add_argument('--fricatives',action='store_true')
    p.add_argument('--pcm-work',type=Path)
    p.add_argument('--stops',action='store_true')
    p.add_argument('--nasal-coefficients',action='store_true')
    p.add_argument('--fricative-coefficients',action='store_true')
    p.add_argument('--pcm-coefficients',action='store_true')
    a=p.parse_args()
    if a.stops and a.pcm_work is None:p.error('--stops requires --pcm-work')
    if a.pcm_coefficients and a.pcm_work is None:p.error('--pcm-coefficients requires --pcm-work')
    r=run(a.dll,a.extended or a.caller_metadata or a.fricatives or a.nasal_coefficients or a.fricative_coefficients,a.caller_metadata,a.fricatives or a.fricative_coefficients,a.pcm_work,a.stops,a.nasal_coefficients,a.fricative_coefficients,a.pcm_coefficients)
    a.output.write_text(json.dumps(r,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:r[k] for k in ['cases','extended_cases','mismatches']}))
