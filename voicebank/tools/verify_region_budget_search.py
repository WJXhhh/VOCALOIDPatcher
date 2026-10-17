"""Full native selection of synthetic work lists, including candidate exhaustion."""
import argparse
import ctypes as c
import json
from pathlib import Path
from audit_frm2_numeric import Native, P, I, F, Q, B, SHA256, f32


def candidates(rows, target, dynamic):
    def narrow(items, field, value, threshold):
        items = sorted(items, key=lambda r: r[field])
        nearest = min(range(len(items)), key=lambda i: (abs(f32(items[i][field]-value)), items[i][field]))
        low, high = nearest-1, nearest+1
        # The native loops retain these immediate-neighbour defaults if no
        # threshold crossing is found; they do not extend to the array edges.
        for i in range(nearest-1, -1, -1):
            if abs(f32(items[nearest][field]-items[i][field])) > threshold:
                low = i
                break
        for i in range(nearest+1, len(items)):
            if abs(f32(items[nearest][field]-items[i][field])) >= threshold:
                high = i
                break
        return items[low+1:high]
    selected = narrow(rows, 'pitch', f32(target), f32(150))
    selected = narrow(selected, 'dynamic', f32(dynamic), f32(.15))
    return sorted(selected, key=lambda r: r['length'])  # Equal tempo, full inner.


def predict(groups, budget):
    indices = [len(g)-1 for g in groups]
    while True:
        chosen = [g[i] for g, i in zip(groups, indices)]
        total = sum(r['length'] for r in chosen)
        available = [i for i in range(len(groups)) if indices[i] > 0]
        if total <= budget or not available:
            return [r['id'] for r in chosen], total
        i = max(available, key=lambda i: (chosen[i]['length'], -i))
        indices[i] -= 1


def run(dll):
    n = Native(dll)
    select = n.fn(0x1b8f40, Q, P, P, I, F, F, B)
    evidence = []
    for count in [1, 2, 3]:
        work = n.alloc(0x1d00)
        I.from_address(work+12).value = count
        groups, pointers = [], []
        for item in range(count):
            rows, parts = [], []
            for index, (pitch, dynamic) in enumerate(zip([-400,-100,-50,0,50,100,400],
                                                        [0,0,.55,.6,.65,1,2])):
                length = 12 + index*6 + item*2
                rows.append(dict(id=index, pitch=f32(pitch), dynamic=f32(dynamic), length=length))
                part = n.alloc(0x280)
                F.from_address(part+0x160).value = pitch
                F.from_address(part+0x16c).value = dynamic
                F.from_address(part+0x170).value = 90
                I.from_address(part+0x1b0).value = length
                align = n.array(I,[0,length//2,0,length//2,length//2,length,length//2,length])
                P.from_address(part+0x1d8).value = align
                P.from_address(part+0x1e0).value = align+32
                parts.append(part)
            unit = n.alloc(0x180)
            P.from_address(unit+0x148).value = n.array(P,parts)
            I.from_address(unit+0x150).value = len(parts)
            P.from_address(work+0x410+item*8).value = unit
            groups.append(rows)
            pointers.append(parts)
        for target in [-150.,-100.,-50.,0.,50.,100.,150.]:
            for dynamic in [.45,.55,.6,.65,.75]:
                reduced = [candidates(g,target,dynamic) for g in groups]
                for budget in [0,20,30,50,70,90,150]:
                    expected, total = predict(reduced,budget)
                    actual_total = select(0,work,budget,target,dynamic,0)
                    actual = [parts.index(P.from_address(work+0x810+i*0x68).value)
                              for i,parts in enumerate(pointers)]
                    assert (actual,actual_total)==(expected,total), (count,target,dynamic,budget,actual,expected,actual_total,total)
                    evidence.append(dict(items=count,target=target,dynamic=dynamic,budget=budget,
                                         candidates=[[r['id'] for r in g] for g in reduced],
                                         selected=actual,total=actual_total))
    return dict(dll_sha256=SHA256,cases=len(evidence),mismatches=0,rows=evidence,
                scope='One to three work items; equal tempo, full inner, seven candidates; no edge trimming')


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dll',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True)
    a=p.parse_args()
    r=run(a.dll)
    a.output.write_text(json.dumps(r,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(cases=r['cases'],mismatches=r['mismatches'])))
