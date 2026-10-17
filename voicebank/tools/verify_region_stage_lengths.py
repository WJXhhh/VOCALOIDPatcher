"""Native stage-to-region length expansion, with optional shaping disabled."""
import argparse
import json
from pathlib import Path
from audit_frm2_numeric import Native,P,I,F,Q,SHA256


def run(dll):
    n=Native(dll)
    expand=n.fn(0x1b2260,None,P,P,F,I,F)
    rows=[]
    for widths in [(5,1,1,40),(5,8,8,40),(1,1,1,1)]:
        for inset in [0,1]:
            groups=[]
            at=0
            for width in widths:
                start=at
                at+=width
                inner_start=min(start+inset,at-1)
                inner_end=max(inner_start+1,at-inset)
                groups.append((start,at,inner_start,inner_end))
            for stage in [0,1]:
                for reverse in [0,1]:
                    work,part,table,classes=(n.alloc(s) for s in [0x1d00,0x280,0x180,0x160])
                    P.from_address(table+0x158).value=classes
                    P.from_address(work+0x1c38).value=table
                    I.from_address(work+4).value=-1
                    I.from_address(work+12).value=1
                    P.from_address(work+0x810).value=part
                    I.from_address(work+0x818).value=stage
                    __import__('ctypes').c_ubyte.from_address(work+0x1c18).value=reverse
                    a=n.array(I,[x for g in groups for x in g])
                    P.from_address(part+0x1d8).value=a
                    P.from_address(part+0x1e0).value=a+64
                    expand(0,work,1.,-1,.5)
                    indices=[stage*2,stage*2+1]
                    if reverse:indices.reverse()
                    expected_inner=[max(1,min(groups[i][3]-groups[i][2],groups[i][1]-groups[i][0])) for i in indices]
                    expected_outer=[max(1,groups[i][1]-groups[i][0]) for i in indices]
                    inner=[I.from_address(work+0x1810+4*i).value for i in range(2)]
                    outer=[I.from_address(work+0x1910+4*i).value for i in range(2)]
                    margin=[I.from_address(work+0x1a10+4*i).value for i in range(2)]
                    assert inner==expected_inner and outer==expected_outer
                    assert margin==[o-i for o,i in zip(outer,inner)]
                    rows.append(dict(groups=groups,stage=stage,reverse=reverse,inner=inner,outer=outer,margin=margin))
    return dict(dll_sha256=SHA256,cases=len(rows),mismatches=0,rows=rows,
                scope='Full length expansion with shaping disabled; stage supplied explicitly, not upstream stage production')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dll',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True)
    a=p.parse_args();r=run(a.dll)
    a.output.write_text(json.dumps(r,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(cases=r['cases'],mismatches=r['mismatches'])))
