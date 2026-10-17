"""Bounded owned-part tests for unknown2/dynamics/tempo transport and selection.

Only known metadata, primary-selector and duration-sort functions are called.
No commercial frames, PCM, Editor, global DLL search or maker reconstruction.
"""
from __future__ import annotations

import argparse
import ctypes as c
import itertools
import json
from pathlib import Path
import struct
import subprocess
import sys

import analyze_recording_units as analysis
from audit_frm2_numeric import Native, P, I, F, Q, B, SHA256, f32
from verify_region_budget_search import candidates, predict
from verify_vqm_feature_dse5_roundtrip import crt_functions
from analyze_reference_layers import walk_art
from verify_reference_ddi import load_public_parser


TOOLS=Path(__file__).resolve().parent
OFFSETS={'unknown2':0x168,'dynamics':0x16c,'tempo':0x170}
DISK={'unknown2':18,'dynamics':22,'tempo':26}


def raw32(value):return struct.pack('<f',value)


def reference_statistics(manifest_path,voice_root,parser_root):
    manifest=json.loads(manifest_path.read_text(encoding='utf-8'))
    assert analysis.file_sha256(parser_root/'utils/ddi_utils.py')==manifest['parser_sha256']
    model_type=load_public_parser(parser_root);banks=[]
    from collections import Counter
    total=Counter();distributions={name:Counter() for name in OFFSETS}
    for bank in manifest['banks']:
        paths=list(voice_root.rglob(bank['bank']+'.ddi'));assert len(paths)==1
        path=paths[0];assert analysis.file_sha256(path)==bank['ddi_sha256']
        model=model_type(path.read_bytes());model.read()
        groups=[('STA',list(unit['stap'].values())) for unit in model.sta_data.values()]
        groups.extend(('ART'+str(len(key)),parts) for root in model.art_data.values() for key,parts in walk_art(root))
        groups.append(('VQM',list((model.vqm_data or {}).values())))
        counts=Counter();values={name:Counter() for name in OFFSETS}
        for kind,parts in groups:
            for part in parts:
                counts[kind]+=1
                for name in OFFSETS:
                    value=float(part[name]);bits=raw32(value).hex()
                    values[name][bits]+=1;distributions[name][bits]+=1
        total.update(counts)
        banks.append(dict(bank=bank['bank'],ddi_sha256=bank['ddi_sha256'],counts=dict(counts),
            bit_distributions={name:dict(v) for name,v in values.items()}))
    assert len(banks)==8 and total==dict(STA=688,ART2=20615,ART3=3266,VQM=24)
    for name,value in (('unknown2',0.),('dynamics',.6),('tempo',90.)):
        assert distributions[name]=={raw32(value).hex():24593}
    return dict(parser_sha256=manifest['parser_sha256'],manifest_sha256=analysis.file_sha256(manifest_path),
        total_parts=24593,counts=dict(total),banks=banks,
        bit_distributions={name:dict(v) for name,v in distributions.items()},
        scope='Eight hash-matched installed DDI metadata only; no DDB, PCM, rendering or recording data read.')


def transport(dll,root):
    n=Native(dll);crt=crt_functions();rows=[];defaults=[]
    ctor=n.fn(0x109b30,P,P,Q);read=n.fn(0x10b1a0,Q,P,P);write=n.fn(0x10b5d0,Q,P,P)
    path=root/'owned-part.bin';path.touch(exist_ok=False)
    def part():
        p=n.alloc(0x280)
        c.memset(p+0x160,0xa5,0x18)
        assert ctor(p,0)==p
        assert c.string_at(p+0x168,12)==raw32(0.)+raw32(.6)+raw32(90.)
        defaults.append(c.string_at(p+0x168,12).hex())
        return p
    def stream(handle,legacy):
        s=n.alloc(0x80);P.from_address(s+8).value=handle
        I.from_address(s+0x1c).value=1 # metadata-only, do not clear frame caches
        I.from_address(s+0x34).value=legacy
        return s
    patterns=(0,0x80000000,0x3e000000,0xc18a0000,0x3f19999a,0x42f70000,
        0xff7fffff,0x7f800000,0xff800000,0x7fc12345)
    for field,offset in OFFSETS.items():
        for bits in patterns:
            p=part();F.from_address(p+0x168).value=.125;F.from_address(p+0x16c).value=.75;F.from_address(p+0x170).value=123.
            c.memmove(p+offset,struct.pack('<I',bits),4)
            before=c.string_at(p,0x280)
            output=[]
            for flag in (0,1):
                handle=crt._wfopen(str(path.resolve()),'wb');assert handle
                s=stream(handle,flag)
                try:assert write(p,s)==0
                finally:assert crt.fclose(handle)==0
                raw=path.read_bytes();assert len(raw)==30 and Q.from_address(s).value==30
                assert raw[DISK[field]:DISK[field]+4]==struct.pack('<I',bits)
                assert c.string_at(p,0x280)==before
                output.append(raw)
            assert output[0]==output[1] # writer does not implement the legacy read layout
            raw=output[0]
            for legacy in (0,1):
                data=raw if not legacy else raw[:10]+raw[10:14]+raw[22:26]+raw[14:18]
                path.write_bytes(data);target=part()
                for off in OFFSETS.values():c.memset(target+off,0xa5,4)
                handle=crt._wfopen(str(path.resolve()),'rb');assert handle
                s=stream(handle,legacy)
                try:assert read(target,s)==0
                finally:assert crt.fclose(handle)==0
                assert Q.from_address(s).value==len(data)
                expected=raw[18:30] if not legacy else bytes(4)+raw[22:26]+bytes(4)
                assert c.string_at(target+0x168,12)==expected
                rows.append(dict(field=field,bits=f'{bits:08x}',legacy=bool(legacy),consumed=len(data),
                    loaded_bits={name:c.string_at(target+off,4).hex() for name,off in OFFSETS.items()},
                    source_unchanged=True,writer_legacy_flag_ignored=True))
    return dict(default_initializations=len(defaults),defaults_exact=True,cases=rows,
        offsets={name:hex(off) for name,off in OFFSETS.items()},file_offsets=DISK,
        scope='Bit transport only for nonfinite patterns; only finite values enter selectors.')


def make_work(n,groups,unknown_pattern=0.,four=False):
    work=n.alloc(0x1d00);I.from_address(work+12).value=len(groups);pointers=[]
    for item,rows in enumerate(groups):
        parts=[]
        for index,row in enumerate(rows):
            p=n.alloc(0x280)
            F.from_address(p+0x160).value=row['pitch'];F.from_address(p+0x168).value=unknown_pattern*(index+1)
            F.from_address(p+0x16c).value=row['dynamic'];F.from_address(p+0x170).value=row['tempo']
            length=row['length'];front=length-41 if four else length
            inner=row.get('inner',front)
            assert 2<=inner<=front
            I.from_address(p+0x1b0).value=length
            split=inner//2
            values=[0,front//2,0,split,front//2,front,front//2,front//2+inner-split]
            if four:values += [front,front+1,front,front+1,front+1,length,front+1,length]
            previous_end=0
            for k in range(0,len(values),4):
                start,end,lo,hi=values[k:k+4]
                assert start==previous_end and 0<=start<=lo<hi<=end<=length
                previous_end=end
            assert previous_end==length
            align=n.array(I,values);P.from_address(p+0x1d8).value=align;P.from_address(p+0x1e0).value=align+len(values)*4
            parts.append(p)
        unit=n.alloc(0x180);P.from_address(unit+0x148).value=n.array(P,parts);I.from_address(unit+0x150).value=len(parts)
        P.from_address(work+0x410+item*8).value=unit;pointers.append(parts)
    return work,pointers


def select(n,work,pointers,budget,target,dynamic):
    before=[c.string_at(p+0x160,20) for group in pointers for p in group]
    total=n.fn(0x1b8f40,Q,P,P,I,F,F,B)(0,work,budget,target,dynamic,0)
    assert before==[c.string_at(p+0x160,20) for group in pointers for p in group]
    return [group.index(P.from_address(work+0x810+i*0x68).value) for i,group in enumerate(pointers)],int(total)


def narrowing(rows,target,dynamic):
    # Reuse the already native-validated pitch/dynamics narrowing, replacing
    # only its equal-tempo length order with the independently tested comparator.
    retained=candidates(rows,target,dynamic)
    retained.sort(key=lambda r:r['pitch']) # deterministic pre-tempo order is dynamics order below
    retained.sort(key=lambda r:r['dynamic'])
    return duration_order(retained)


def duration_order(rows):
    def less(a,b):
        left=a.get('inner',a['length']);right=b.get('inner',b['length'])
        ratio=f32(f32(min(left,right))/f32(max(left,right)))
        return left<right if ratio>=f32(.8) or a['tempo']==b['tempo'] else a['tempo']<b['tempo']
    output=[]
    for row in rows:
        # The native small-range sort has a first-element shortcut. The
        # length/tempo predicate need not be transitive, so this is observable
        # and cannot be replaced with a generic key sort or insertion variant.
        if output and less(row,output[0]):
            output.insert(0,row)
            continue
        i=len(output)
        while i and less(row,output[i-1]):i-=1
        output.insert(i,row)
    return output


def dynamic_contract(dll):
    n=Native(dll);evidence=[]
    pitches=(-400.,-100.,-50.,0.,50.,100.,400.)
    values=(-.5,0.,.5,1.,1.5)
    orders=[values[i:]+values[:i] for i in range(5)]
    for order in orders:
        rows=[dict(id=i,pitch=p,dynamic=f32(d),tempo=90.,length=20)
            for i,(p,d) in enumerate(zip(pitches,(-9.,*order,9.)))]
        for unknown in (0.,-17.25,99.5):
            work,ptrs=make_work(n,[rows],unknown)
            for target in (-2.,-.5,-.25,0.,.25,.5,.75,1.,1.25,1.5,3.):
                expected=min(range(1,6),key=lambda i:(abs(f32(rows[i]['dynamic']-f32(target))),rows[i]['dynamic']))
                actual,total=select(n,work,ptrs,1000,0.,target)
                assert actual==[expected] and total==20,(order,target,unknown,actual,expected,total)
                evidence.append(dict(dynamics_order=list(order),target=target,unknown_pattern=unknown,selected=expected,total=total))
    threshold=f32(.15);bits=struct.unpack('<I',raw32(threshold))[0]
    neighbours=[struct.unpack('<f',struct.pack('<I',bits+d))[0] for d in (-1,0,1)]
    boundaries=[]
    for side in ('lower','upper'):
        for value in neighbours:
            for unknown in (0.,-17.25,99.5):
                # Both dynamics guards must survive pitch narrowing. Otherwise
                # the selector's neighbour defaults can hide the inequality.
                dynamics=[-9.,-1.,-value if side=='lower' else -.5,0.,value if side=='upper' else .5,1.,9.]
                neighbour=2 if side=='lower' else 4
                rows=[dict(id=i,pitch=p,dynamic=f32(d),tempo=90.,length=80 if i==neighbour else 20)
                    for i,(p,d) in enumerate(zip((-400.,-100.,-40.,0.,40.,100.,400.),dynamics))]
                expected_rows=candidates(rows,0.,0.)
                expected,total=predict([expected_rows],1000)
                included=value<=threshold if side=='lower' else value<threshold
                assert expected==[neighbour if included else 3]
                work,ptrs=make_work(n,[rows],unknown);actual,actual_total=select(n,work,ptrs,1000,0.,0.)
                assert (actual,actual_total)==(expected,total)
                boundaries.append(dict(side=side,difference=value,threshold=threshold,unknown_pattern=unknown,
                    candidates=[r['id'] for r in expected_rows],selected=actual,total=actual_total))
    return dict(nearest_cases=evidence,boundary_cases=boundaries,source_scalar_fields_preserved=True,
        scope='Finite synthetic coordinates; outside [0,1] tests do not establish an approved production domain.')


def tempo_contract(dll):
    n=Native(dll);rows_out=[]
    native_sort=n.fn(0x190f60,P,P,P)
    for left,right,tempos,four in itertools.product((79,80,81),(100,),((60.,120.),(120.,60.),(90.,90.)),(False,True)):
        rows=[dict(id=i,pitch=0.,dynamic=.6,tempo=tempos[i],length=L+(41 if four else 0),inner=L) for i,L in enumerate((left,right))]
        for unknown in (0.,-17.25,99.5):
            work,parts=make_work(n,[rows],unknown,four);ptr=n.array(P,parts[0])
            native_sort(ptr,ptr+16)
            actual=[parts[0].index(P.from_address(ptr+i*8).value) for i in range(2)]
            expected=[r['id'] for r in duration_order(rows)];assert actual==expected
            rows_out.append(dict(inner_lengths=[left,right],tempos=list(tempos),four_regions=four,
                unknown_pattern=unknown,order=actual))
    triangle=[dict(id=i,pitch=0.,dynamic=.6,tempo=t,length=L) for i,(L,t) in enumerate(((79,90.),(80,120.),(100,60.)))]
    pair_checks=[];permutation_checks=[]
    for pair,expected in (((0,1),[0,1]),((1,2),[1,2]),((0,2),[2,0])):
        group=[triangle[i] for i in pair];_,parts=make_work(n,[group]);ptr=n.array(P,parts[0]);native_sort(ptr,ptr+16)
        actual=[group[parts[0].index(P.from_address(ptr+i*8).value)]['id'] for i in range(2)]
        assert actual==expected
        pair_checks.append(dict(input=list(pair),order=actual))
    for order in itertools.permutations(range(3)):
        group=[triangle[i] for i in order];_,parts=make_work(n,[group]);ptr=n.array(P,parts[0]);native_sort(ptr,ptr+24)
        actual=[group[parts[0].index(P.from_address(ptr+i*8).value)]['id'] for i in range(3)]
        assert actual==[r['id'] for r in duration_order(group)]
        permutation_checks.append(dict(input=list(order),order=actual))
    joined=[]
    for lengths,tempos,four,unknown in itertools.product(((20,40,100),(79,80,100),(80,81,100)),
            itertools.permutations((60.,90.,120.)),(False,True),(0.,-17.25,99.5)):
        group=[dict(id=0,pitch=-400.,dynamic=-9.,tempo=90.,length=20)]
        dynamics=(-1.,-.0625,0.,.0625,1.)
        for i,(pitch,dynamic) in enumerate(zip((-100.,-50.,0.,50.,100.),dynamics),1):
            j=i-2
            group.append(dict(id=i,pitch=pitch,dynamic=f32(dynamic),tempo=tempos[j] if 0<=j<3 else 90.,
                length=lengths[j] if 0<=j<3 else 20))
        group.append(dict(id=6,pitch=400.,dynamic=9.,tempo=90.,length=20))
        if four:
            for row in group:
                row['inner']=row['length'];row['length']+=41
        reduced=narrowing(group,0.,0.);work,parts=make_work(n,[group],unknown,four)
        for budget in (0,15,20,39,40,79,80,99,100,1000):
            expected,total=predict([reduced],budget);actual,actual_total=select(n,work,parts,budget,0.,0.)
            assert (actual,actual_total)==(expected,total),(lengths,tempos,four,budget,actual,expected,actual_total,total)
            joined.append(dict(lengths=list(lengths),tempos=list(tempos),four_regions=four,unknown_pattern=unknown,
                sorted_candidates=[r['id'] for r in reduced],budget=budget,selected=actual,total=actual_total))
    return dict(boundary_sort_cases=rows_out,nontransitive_pairs=pair_checks,three_candidate_orders=permutation_checks,
        joint_selection_cases=joined,source_scalar_fields_preserved=True,
        scope='Two/three candidate comparator path, two/four regions, finite tempos, one work item; not a global BPM time-scaling formula.')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True);parser.add_argument('--work-root',type=Path,required=True)
    parser.add_argument('--worker',choices=('transport','dynamics','tempo'))
    parser.add_argument('--reference-manifest',type=Path)
    parser.add_argument('--voice-root',type=Path)
    parser.add_argument('--parser-root',type=Path)
    args=parser.parse_args();root=args.work_root.resolve()
    if args.worker:
        result={'transport':transport,'dynamics':dynamic_contract,'tempo':tempo_contract}[args.worker]
        data=result(args.dll,root) if args.worker=='transport' else result(args.dll)
        analysis.write_json_atomic(root/'result.json',data);return
    root.mkdir();prior={p.name:analysis.file_sha256(p) for p in (TOOLS.parent.parent/'voicebank_doc/evidence').glob('*.json')}
    analysis.write_json_atomic(root/'prior_evidence_sha256.json',prior)
    results={}
    for phase in ('transport','dynamics','tempo'):
        case=root/phase;case.mkdir()
        command=[sys.executable,'-B',str(Path(__file__).resolve()),'--worker',phase,'--dll',str(args.dll.resolve()),'--work-root',str(case)]
        with (case/'worker.log').open('xb') as log:
            done=subprocess.run(command,stdout=log,stderr=subprocess.STDOUT,timeout=180)
        if done.returncode:raise RuntimeError(f'{phase} failed; see owned worker.log')
        results[phase]=json.loads((case/'result.json').read_text())
        analysis.write_json_atomic(root/'results.json',dict(dse_sha256=SHA256,results=results))
        print(phase+' passed',flush=True)
    if any((args.reference_manifest,args.voice_root,args.parser_root)):
        if not all((args.reference_manifest,args.voice_root,args.parser_root)):raise ValueError('all reference inputs are required together')
        result=reference_statistics(args.reference_manifest,args.voice_root,args.parser_root)
        analysis.write_json_atomic(root/'reference_statistics.json',result)
        print('reference statistics passed',flush=True)


if __name__=='__main__':main()
