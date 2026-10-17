"""Build real DSE indexes from privately owned synthetic directory nodes."""
import argparse
import ctypes as c
import json
from pathlib import Path
from audit_frm2_numeric import Native,P,I,Q,B,SHA256


def run(dll):
    n=Native(dll)
    build=n.fn(0x18d790,None,P,P)
    equal_range=n.fn(0x18ce50,P,P,P,P)
    parents={}
    parent_callback=c.CFUNCTYPE(P,P)(lambda node: parents.get(node,0))
    n.keep.append(parent_callback)
    vtable=n.alloc(0x68)
    P.from_address(vtable+0x60).value=c.cast(parent_callback,P).value

    def string_at(obj):
        size=Q.from_address(obj+0x10).value
        data=P.from_address(obj).value if Q.from_address(obj+0x18).value>15 else obj
        return c.string_at(data,size).decode('ascii')

    reports=[]
    for tokens in [('a','i'),('n','^n#2','a'),('a','^a','a'),
                   ('long_phoneme_name','^second_long_phoneme','last_long_phoneme')]:
        for count in [0,1,3]:
            index,sentinel=n.alloc(16),n.alloc(0xa8)
            P.from_address(index).value=sentinel
            for offset in [0,8,16]:P.from_address(sentinel+offset).value=sentinel
            B.from_address(sentinel+0x18).value=1
            B.from_address(sentinel+0x19).value=1
            previous=0
            for name in ('articulation',)+tokens:
                node=n.alloc(0x180)
                P.from_address(node).value=vtable
                c.memmove(node+0x18,name.encode()+b'\0',len(name)+1)
                parents[node]=previous
                previous=node
            parts=[n.alloc(0x280) for _ in range(count)]
            P.from_address(node+0x148).value=n.array(P,parts)
            I.from_address(node+0x150).value=count
            build(index,node)
            found=[]
            pending=[P.from_address(sentinel+8).value]
            seen=set()
            while pending:
                entry=pending.pop()
                if entry==sentinel:continue
                assert entry and entry not in seen
                seen.add(entry)
                assert len(seen)<=count*(len(tokens)-1)
                pending += [P.from_address(entry).value,P.from_address(entry+16).value]
                found.append((string_at(entry+0x20),parts.index(P.from_address(entry+0x40).value),
                              I.from_address(entry+0x48).value,I.from_address(entry+0x4c).value,
                              string_at(entry+0x50),string_at(entry+0x70)))
            clean=[t[1:] if t.startswith('^') else t for t in tokens]
            expected=[(f'[{clean[i]} {clean[i+1]}]',p,i,len(tokens)-1,clean[i],clean[i+1])
                      for p in range(count) for i in range(len(tokens)-1)]
            assert sorted(found)==sorted(expected),(tokens,count,found,expected)
            assert Q.from_address(index+8).value==len(expected)
            ordered=[]
            def visit(entry):
                if entry==sentinel:return
                visit(P.from_address(entry).value)
                ordered.append(entry)
                visit(P.from_address(entry+16).value)
            visit(P.from_address(sentinel+8).value)
            keys=[string_at(e+0x20) for e in ordered]
            queries=[]
            for key in sorted(set(keys)|{'','[missing missing]','zzzz'}):
                raw=key.encode('ascii')
                query=n.alloc(32)
                data=query if len(raw)<=15 else n.alloc(len(raw)+1)
                if data!=query:P.from_address(query).value=data
                c.memmove(data,raw+b'\0',len(raw)+1)
                Q.from_address(query+16).value=len(raw)
                Q.from_address(query+24).value=max(15,len(raw))
                output=n.alloc(16)
                equal_range(index,output,query)
                lo=next((i for i,k in enumerate(keys) if k>=key),len(keys))
                hi=next((i for i,k in enumerate(keys) if k>key),len(keys))
                endpoints=ordered+[sentinel]
                assert P.from_address(output).value==endpoints[lo]
                assert P.from_address(output+8).value==endpoints[hi]
                queries.append(dict(key=key,matches=hi-lo))
            reports.append(dict(tokens=tokens,parts=count,entries=sorted(found),queries=queries))
    return dict(dll_sha256=SHA256,cases=len(reports),entries=sum(len(r['entries']) for r in reports),
                mismatches=0,rows=reports,
                scope='Native builder and tree insertion; fixture supplies parent access only; no installed voice data')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dll',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True)
    a=p.parse_args();r=run(a.dll)
    a.output.write_text(json.dumps(r,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:r[k] for k in ['cases','entries','mismatches']}))
