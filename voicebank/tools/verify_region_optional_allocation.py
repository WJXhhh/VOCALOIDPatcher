"""Native optional-gap and capped-allocation branches with owned work objects."""
import argparse
import ctypes as c
import itertools
import json
from pathlib import Path
from audit_frm2_numeric import Native, P, I, F, B, SHA256, f32
from verify_region_duration_allocation import predict, predict_cost


def model(inner, outer, flags, target, threshold, gap_enabled, capped, selected):
    names = [b'a']*len(inner)  # All fixture names are non-Sil; classification is supplied separately.
    if capped and target >= sum(inner):
        values = predict(inner, outer, flags, min(target, sum(outer)), names)
        return values, 0, .2*(target-sum(values))/target, 'capped'
    if target > sum(outer) and not capped:
        values = [i if chosen else o for i, o, chosen in zip(inner, outer, selected)]
        gap = target-sum(values)
        if gap_enabled and gap >= threshold:
            return values, gap, .2*gap/target, 'gap'
        if gap_enabled and all(flags) and target-sum(inner) >= threshold:
            return [777]*len(inner), 0, 10000., 'rejected'
    values = predict(inner, outer, flags, target, names)
    return values, 0, predict_cost(inner, outer, flags, target, values, names), 'ordinary'


def run(dll):
    n = Native(dll)
    expand = n.fn(0x1b2260, None, P, P, F, I, F)
    allocate = n.fn(0x19d650, None, P, P, I, P, I, I, I, B, B)
    rows = []
    inner, outer = [3, 7, 5, 9], [7, 11, 9, 13]
    for flags, bounds, marker, enabled, join, gap_enabled, capped in itertools.product(
            [(0,0,0,0), (1,1,1,1), (0,1,1,0)], [(0,4),(1,3),(0,1),(3,4)],
            [-1,0], [0,1], [0,1], [0,1], [0,1]):
        start, end = bounds
        base_inner, base_outer = inner[start:end], outer[start:end]
        targets = sorted(set([max(1,sum(base_inner)-1),sum(base_inner),
                              (sum(base_inner)+sum(base_outer))//2,
                              sum(base_outer),sum(base_outer)+1,sum(base_outer)+8]))
        chosen = ([0] if join else [1,2]) if marker >= 0 and enabled else []
        selected = [i in chosen for i in range(start,end)]
        for target in targets:
            excess = target-sum(i if sel else o for i,o,sel in zip(base_inner,base_outer,selected))
            for threshold in sorted(set([0,max(0,excess),max(0,excess+1),target-sum(base_inner)+1])):
                work, table, classes = (n.alloc(s) for s in [0x1d00,0x180,0x160])
                P.from_address(table+0x158).value = classes
                P.from_address(work+0x1c38).value = table
                I.from_address(work+4).value = marker
                B.from_address(work+8).value = enabled
                B.from_address(work+0x1b15).value = join
                I.from_address(work+12).value = 2
                records = []
                for i, name in enumerate([b'a',b'b',b'c',b'd']):
                    record = n.alloc(0x40)
                    c.memmove(record,name+b'\0',2)
                    B.from_address(record+0x38).value = flags[i]
                    records.append(record)
                    offset = (0x10 if i%2==0 else 0x210)+(i//2)*16
                    c.memmove(work+offset,name+b'\0',2)
                    I.from_address(work+0x1510+i*4).value = 777
                I.from_address(table+0x144).value = 4
                P.from_address(table+0x148).value = n.array(P,records)
                for slot in range(2):
                    part = n.alloc(0x280)
                    left, right = outer[slot*2:slot*2+2]
                    a, b = inner[slot*2:slot*2+2]
                    alignment = n.array(I,[0,left,2,2+a,left,left+right,left+2,left+2+b])
                    P.from_address(part+0x1d8).value = alignment
                    P.from_address(part+0x1e0).value = alignment+32
                    P.from_address(work+0x810+slot*0x68).value = part
                expand(0,work,1.,-1,.5)
                output = n.alloc(8)
                allocate(0,output,target,work,start,end,threshold,gap_enabled,capped)
                expected, gap, cost, branch = model(base_inner,base_outer,flags[start:end],
                    target,threshold,gap_enabled,capped,selected)
                expected_all = [777]*start+expected+[777]*(4-end)
                actual = [I.from_address(work+0x1510+i*4).value for i in range(4)]
                actual_gap, actual_cost = I.from_address(output).value,F.from_address(output+4).value
                key = (flags,bounds,marker,enabled,join,gap_enabled,capped,target,threshold,branch)
                assert actual==expected_all and actual_gap==gap, (key,actual,expected_all,actual_gap,gap)
                assert abs(actual_cost-cost)<3e-6, (key,actual_cost,cost)
                rows.append(dict(flags=flags,bounds=bounds,marker=marker,enabled=enabled,join=join,
                    gap_enabled=gap_enabled,capped=capped,target=target,threshold=threshold,
                    branch=branch,allocated=actual,gap=actual_gap,cost=actual_cost,expected_cost=cost))
    return dict(dll_sha256=SHA256,cases=len(rows),mismatches=0,
        branches={name:sum(r['branch']==name for r in rows) for name in ['ordinary','gap','capped','rejected']},
        scope='Full expansion and allocation on two owned work items, full/subranges; '
              'no host reachability or final output-frame mapping claim',rows=rows)


if __name__ == '__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dll',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True)
    a=p.parse_args();r=run(a.dll)
    a.output.write_text(json.dumps(r,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:r[k] for k in ['cases','mismatches','branches']}))
