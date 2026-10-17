"""Native PHDC-dependent junction shaping on two owned work items."""
import argparse
import ctypes as c
import itertools
import json
from pathlib import Path
from audit_frm2_numeric import Native,P,I,Q,B,F,SHA256,f32


def run(dll):
    n=Native(dll)
    shape=n.fn(0x1b2260,None,P,P,F,I,F)
    rows=[]
    for category,width,matched,position,control in itertools.product(
            [None,'Semivowels','Liquids','Nasals','VoicedFricatives'],[2,8,20],[False,True],
            [0,1,2],[0.,.25,.5,.75,1.]):
        work,table,classes=(n.alloc(s) for s in [0x1d00,0x180,0x160])
        P.from_address(table+0x158).value=classes
        P.from_address(work+0x1c38).value=table
        I.from_address(work+4).value=-1
        I.from_address(work+12).value=2
        def text(value):
            raw=value.encode()+b'\0';ptr=n.alloc(len(raw));c.memmove(ptr,raw,len(raw));return ptr
        if category:
            record=n.alloc(0x30)
            I.from_address(classes+0x140).value=1
            P.from_address(classes+0x148).value=record
            I.from_address(record).value=1
            P.from_address(record+0x10).value=n.array(P,[text('b')])
            P.from_address(record+0x18).value=text(category)
        for offset,name in [(0x10,'a'),(0x210,'b'),(0x20,'b' if matched else 'x'),(0x220,'c')]:
            c.memmove(work+offset,name.encode()+b'\0',2)
        widths=[5,width,width,7]
        inner=[max(1,v-2) for v in widths]
        for slot in range(2):
            part=n.alloc(0x280)
            left,right=widths[slot*2:slot*2+2]
            a=n.array(I,[0,left,0,inner[slot*2],left,left+right,left,left+inner[slot*2+1]])
            P.from_address(part+0x1d8).value=a
            P.from_address(part+0x1e0).value=a+32
            P.from_address(work+0x810+slot*0x68).value=part
        shape(0,work,1.,position,control)
        expected_inner=list(inner);expected_outer=list(widths)
        if control<.5 and position==1 and not matched:
            affected=[];factor=1.
        elif control<.5 and position==1:
            total=2*width
            cap={'Semivowels':25.,'Liquids':18.,'Nasals':30.,'VoicedFricatives':2.*total,None:55.}[category]
            cap=max(cap,2.*total)
            factor=f32(f32(cap/total)**f32(1-2*control))
            affected=[1,2]
        else:
            factor=f32((55. if control<.5 else 2.)**f32(1-2*control))
            affected=list(range(position,3))
        for i in affected:
            expected_inner[i]=int(f32(max(1.,f32(inner[i]*factor))+.5))
            expected_outer[i]=int(f32(max(1.,f32(widths[i]*factor))+.5))
        actual_inner=[I.from_address(work+0x1810+i*4).value for i in range(4)]
        actual_outer=[I.from_address(work+0x1910+i*4).value for i in range(4)]
        actual_margin=[I.from_address(work+0x1a10+i*4).value for i in range(4)]
        assert actual_inner==expected_inner and actual_outer==expected_outer,(category,width,matched,position,control,actual_inner,expected_inner,actual_outer,expected_outer)
        assert actual_margin==[o-i for o,i in zip(actual_outer,actual_inner)]
        rows.append(dict(category=category,width=width,matched=matched,position=position,control=control,
                         inner=actual_inner,outer=actual_outer,factor=factor))
    return dict(dll_sha256=SHA256,cases=len(rows),mismatches=0,rows=rows,
                scope='Full junction-shaping function, private PHDC classes, two work items; '
                      'initial onset coefficient fixed to one; no final frame time allocation claim')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dll',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True)
    a=p.parse_args();r=run(a.dll)
    a.output.write_text(json.dumps(r,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:r[k] for k in ['cases','mismatches']}))
