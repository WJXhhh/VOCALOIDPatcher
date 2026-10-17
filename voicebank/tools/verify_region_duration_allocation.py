"""Native duration allocation across inner/outer limits and phoneme byte-0x38 classes."""
import argparse
import ctypes as c
import itertools
import json
import math
from pathlib import Path
from audit_frm2_numeric import Native, P, I, F, B, SHA256, f32


def predict(inner, outer, flags, target, names=(b'a',b'b')):
    def rounded(x):
        return int(f32(x + .5))
    def correct(values):
        difference = target-sum(values)
        last = -1
        while difference:
            sign = 1 if difference > 0 else -1
            order = list(range(last+1, len(values))) + list(range(last+1))
            eligible = [i for i in order if values[i]+sign >= 0]
            chosen = next((i for i in eligible if flags[i] == 0), eligible[0])
            values[chosen] += sign
            difference -= sign
            last = chosen
        return values
    if target < sum(inner):
        special = sum(v for v, flag in zip(inner, flags) if flag == 0)
        other = sum(inner)-special
        ratio = f32(target / f32(f32(special*f32(.8)) + other))
        special_ratio = f32(ratio*f32(.8))
        if ratio >= 1 and special:
            special_ratio = f32((target-other)/special)
            ratio = 1.
        return correct([rounded(f32(v*(special_ratio if flag == 0 else ratio)))
                        for v, flag in zip(inner, flags)])
    if target <= sum(outer):
        values = list(inner)
        remainder = target-sum(values)
        while remainder:
            active = [i for i in range(len(values)) if values[i] < outer[i]]
            step = max(1, remainder//len(active))
            for i in active:
                extra = min(step, outer[i]-values[i], remainder)
                values[i] += extra
                remainder -= extra
        return values
    special = sum(v for v, flag in zip(outer, flags) if flag == 0)
    scalable = [flag==0 for flag in flags] if special else [name!=b'Sil' for name in names]
    total = sum(v for v, enabled in zip(outer,scalable) if enabled)
    ratio = f32((target-sum(outer)+total)/total) if total else f32(target/sum(outer))
    return correct([rounded(f32(v*ratio)) if enabled else v for v,enabled in zip(outer,scalable)])


def run(dll):
    n = Native(dll)
    expand = n.fn(0x1b2260, None, P, P, F, I, F)
    allocate = n.fn(0x19d650, None, P, P, I, P, I, I, I, B, B)
    rows = []
    for inner, margin, flags, names in itertools.product([(1,1), (3,7), (5,7)], [0,2], itertools.product([0,1], repeat=2),
            [(b'a',b'b'),(b'Sil',b'b'),(b'a',b'Sil'),(b'Sil',b'Sil')]):
        outer = [v+2*margin for v in inner]
        for target in range(2, sum(outer)*2+1):
            work, part, table, classes = (n.alloc(s) for s in [0x1d00,0x280,0x180,0x160])
            P.from_address(table+0x158).value = classes
            records = []
            for slot, (name, flag) in enumerate(zip(names, flags)):
                record = n.alloc(0x40)
                c.memmove(record,name+b'\0',len(name)+1)
                B.from_address(record+0x38).value = flag
                c.memmove(work+(0x10 if slot==0 else 0x210),name+b'\0',len(name)+1)
                records.append(record)
            I.from_address(table+0x144).value = 2
            P.from_address(table+0x148).value = n.array(P,records)
            P.from_address(work+0x1c38).value = table
            I.from_address(work+4).value = -1
            I.from_address(work+12).value = 1
            B.from_address(work+0x1b15).value = 1
            P.from_address(work+0x810).value = part
            I.from_address(work+0x81c).value = 1
            align = n.array(I,[0,outer[0],margin,margin+inner[0],
                               outer[0],sum(outer),outer[0]+margin,outer[0]+margin+inner[1]])
            P.from_address(part+0x1d8).value = align
            P.from_address(part+0x1e0).value = align+32
            expand(0,work,1.,-1,.5)
            output = n.alloc(8)
            allocate(0,output,target,work,0,2,0,0,0)
            actual = [I.from_address(work+0x1510+4*i).value for i in range(2)]
            effective_flags = [flags[names.index(name)] for name in names]
            expected = predict(inner,outer,effective_flags,target,names)
            assert actual == expected, (inner,outer,flags,names,target,actual,expected)
            gap, cost = I.from_address(output).value,F.from_address(output+4).value
            assert gap == 0 and sum(actual)==target and min(actual)>=0 and math.isfinite(cost)
            expected_cost = predict_cost(inner, outer, effective_flags, target, expected,names)
            assert abs(cost-expected_cost)<3e-6, (inner,outer,flags,target,cost,expected_cost)
            rows.append(dict(inner=inner,outer=outer,flags=flags,effective_flags=effective_flags,
                             names=[x.decode() for x in names],target=target,
                             allocated=actual,duration_cost=cost,expected_cost=expected_cost))
    return dict(dll_sha256=SHA256,cases=len(rows),mismatches=0,rows=rows,
                scope='Full native length expansion and allocation; ordinary/Sil named phonemes, '
                      'byte-0x38 classes crossed, optional gap/forced-contraction disabled; '
                      'exact length model and independent nonzero duration-cost model')


def predict_cost(inner, outer, flags, target, allocated, names=(b'a',b'b')):
    exponent = f32(2/3)
    if target < sum(inner):
        special = sum(v for v, flag in zip(inner,flags) if flag==0)
        other = sum(inner)-special
        ratio = f32(target/f32(f32(special*f32(.8))+other))
        special_ratio = f32(ratio*f32(.8))
        if ratio>=1 and special:
            special_ratio=f32((target-other)/special)
            ratio=1.
        weight=sum(v for v,flag in zip(allocated,flags) if flag==0)/target
        return .2+.8*(1-weight*special_ratio**exponent-(1-weight)*ratio**exponent)
    if target<=sum(outer):
        return 0.
    special=sum(v for v,flag in zip(outer,flags) if flag==0)
    scalable = special if special else sum(v for v,name in zip(outer,names) if name!=b'Sil')
    ratio=f32((target-sum(outer)+scalable)/scalable) if scalable else f32(target/sum(outer))
    return .2+.8*(1-f32(1/ratio)**exponent)


if __name__ == '__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dll',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True)
    a=p.parse_args();r=run(a.dll)
    a.output.write_text(json.dumps(r,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:r[k] for k in ['cases','mismatches']}))
