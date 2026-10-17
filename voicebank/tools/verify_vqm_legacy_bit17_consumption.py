"""Bounded bit17 perturbations through the established compact-DVQM audio API.

Only independently loaded in-memory objects are changed. Installed resources
are read-only; PCM and feature payloads never leave memory. Nonrepeatable audio
baselines remain inconclusive rather than being treated as an ablation result.
"""
from __future__ import annotations

import argparse
from collections import Counter
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import struct

from audit_frm2_numeric import B,D,F,I,Native,P,Q,SHA256,f32
from audit_vqm_legacy_live_features import (RESOURCE_SHA256,catalog,children,
    feature_payloads,loaded_regions,name,pointer_items)
from verify_region_frame_blend import EMPTY


def change_source(control,variant):
    regions=loaded_regions(control)
    before=feature_payloads(regions)
    stats=Counter()
    for region in regions:
        for frame in pointer_items(region,0x18,0x20):
            feature=P.from_address(frame+0x128).value
            assert feature and I.from_address(feature).value&(1<<17)
            value=F.from_address(feature+0x21c).value
            assert math.isfinite(value)
            stats['frames']+=1
            stats['original_nonzero_bit17']+=value!=0.
            if variant in ('zero','pi','rotate'):
                new=0. if variant=='zero' else f32(math.pi) if variant=='pi' else f32((value+math.pi/2)%(2*math.pi))
                F.from_address(feature+0x21c).value=new
                stats['changed_bit17']+=struct.pack('<f',value)!=struct.pack('<f',new)
            elif variant=='amplitude_plus6':
                count=I.from_address(frame+0x58).value
                pointer=P.from_address(frame+0x68).value
                assert 0<=count<=10000 and (not count or pointer)
                for k in range(count):
                    old=F.from_address(pointer+4*k).value
                    assert math.isfinite(old)
                    F.from_address(pointer+4*k).value=f32(old+6.)
                    stats['changed_harmonic_amplitudes']+=1
            else:
                assert variant=='baseline'
    after=feature_payloads(regions)
    assert len(before)==len(after)
    for old,new in zip(before,after):
        if variant in ('zero','pi','rotate'):
            assert old[:0x21c]==new[:0x21c] and old[0x220:]==new[0x220:]
        else:
            assert old==new
    return dict(stats)


def load_configure(native,root,row,variant):
    side,descriptor,label,sample=row
    control=native.fn(0xb400,P)()
    assert control
    assert native.fn(0x100a0,Q,P,I,P,c.c_char_p,P)(root,side,descriptor,label.encode('ascii'),control)==0
    internal=P.from_address(control).value
    assert internal and P.from_address(internal).value==sample
    changes=change_source(control,variant)
    before=feature_payloads(loaded_regions(control))
    info=native.alloc(0x64)
    c.memset(info,0xa5,0x64)
    assert native.fn(0xb5b0,Q,P,P)(info+16,control)==0
    assert c.string_at(info,16)==b'\xa5'*16 and c.string_at(info+0x54,16)==b'\xa5'*16
    scale=[min(1.,max(0.,F.from_address(info+16+offset).value)) for offset in (0x34,0x38,0x3c,0x40)]
    count=101
    arrays=[native.array(F,values) for values in ([f32(k*.01) for k in range(count)],
        [0.]*count,[EMPTY]*count,[.6]*count,[.5]*count)]
    assert native.fn(0xb6b0,Q,F,F,F,F,F,F,I,P,P,P,P,P,P)(.5,*scale,1.,count,*arrays,control)==0
    assert before==feature_payloads(loaded_regions(control))
    return control,changes


def audio(native,control,blocks,reset_random=None):
    call=native.fn(0xeb00,Q,I,F,P,P,P,P,P,P,I,P,P,F,F,F,I,F,F,F,P,D,D,D,B,B,B,B,B,D,D,I,P,P)
    size=I.from_address(control+0xa4).value
    assert size==256
    buffer=native.alloc(size*4+32)
    amplitudes=native.array(F,[.1]*500)
    aux=[native.array(F,[0. if k==0 else 1.]) for k in range(4)]
    digest=hashlib.sha256()
    rows=[]
    visited=set()
    nonzero_bit17_visits=0
    if reset_random is not None:
        reset_random()
    for number,mode in enumerate([1]+[2]*blocks):
        c.memset(buffer,0xa5,size*4+32)
        c.memset(buffer+16,0,size*4)
        status=call(0,440.,amplitudes,*aux,buffer+16,0,None,None,.5,.5,.5,mode,
            1.,.5,0.,None,0.,0.,0.,0,0,0,0,1,0.,0.,0,None,control)
        assert status in (0,1)
        assert c.string_at(buffer,16)==b'\xa5'*16
        assert c.string_at(buffer+16+size*4,16)==b'\xa5'*16
        values=list((F*size).from_address(buffer+16))
        assert all(math.isfinite(x) for x in values)
        raw=c.string_at(buffer+16,size*4)
        digest.update(raw)
        row=dict(call=number,mode=mode,status=int(status),sha256=hashlib.sha256(raw).hexdigest(),
            nonzero=sum(x!=0. for x in values),peak=max(abs(x) for x in values))
        if status==0:
            internal=P.from_address(control).value
            context=P.from_address(internal+0x60).value
            assert context and I.from_address(context+0x2df0).value==1
            source=P.from_address(context+0x2e88).value
            frames=pointer_items(source,0x18,0x20)
            index=I.from_address(context+0x2ed8+0x18).value
            row['source_index']=index
            row['source_frames']=len(frames)
            if 0<=index<len(frames):
                feature=P.from_address(frames[index]+0x128).value
                assert feature and I.from_address(feature).value&(1<<17)
                visited.add(index)
                row['selected_nonzero_bit17']=F.from_address(feature+0x21c).value!=0.
                nonzero_bit17_visits+=row['selected_nonzero_bit17']
        rows.append(row)
        if status==1:
            break
    return dict(sha256=digest.hexdigest(),calls=len(rows),blocks=rows,
        selected_source_indices=len(visited),nonzero_bit17_selections=nonzero_bit17_visits)


def run(dll,directory,reference,limit,blocks,reset_random=None,all_samples=False):
    resource=directory/'dvqm.dat'
    assert hashlib.sha256(resource.read_bytes()).hexdigest()==RESOURCE_SHA256
    prior=json.loads(reference.read_text(encoding='utf-8'))
    assert prior['dse_sha256']==SHA256 and prior['resource_sha256']==RESOURCE_SHA256
    assert prior['samples']==119
    selected=[r['sample_index'] for r in prior['cases'] if r['unobserved_pair_equal']
        and r['observed_pair_equal'] and r['observed_unobserved_equal']]
    assert len(selected)==20
    if all_samples:
        selected=list(range(119))
    selected=selected[:limit]
    native=Native(dll)
    directory_bytes=str(directory.resolve()).encode('utf-8')
    root=native.fn(0xfea0,P,c.c_char_p)(directory_bytes)
    assert root
    reports=[]
    try:
        all_rows=catalog(native,root)
        for index in selected:
            side,descriptor,label,_=all_rows[index]
            trials=[]
            for variant in ('baseline','zero','pi','rotate','amplitude_plus6','baseline','baseline'):
                trial_root=native.fn(0xfea0,P,c.c_char_p)(directory_bytes)
                assert trial_root and trial_root!=root
                node=native.fn(0x23f00,P,P,I,P)(trial_root,side,descriptor)
                sample={name(x):x for x in children(node)}[label]
                control,changed=load_configure(native,trial_root,(side,descriptor,label,sample),variant)
                try:
                    before=feature_payloads(loaded_regions(control))
                    result=audio(native,control,blocks,reset_random)
                    after=feature_payloads(loaded_regions(control))
                    assert len(before)==len(after)
                    assert all(a[0x21c:0x220]==b[0x21c:0x220] for a,b in zip(before,after))
                    trials.append(dict(variant=variant,source_changes=changed,audio=result))
                finally:
                    native.fn(0xb480,None,P)(control)
                    assert native.fn(0x10080,Q,P)(trial_root)==0
            baseline=trials[0]['audio']
            repeatable=baseline['sha256']==trials[-1]['audio']['sha256']==trials[-2]['audio']['sha256']
            common=min(t['audio']['calls'] for t in trials)
            prefix=0
            for k in range(common):
                if not baseline['blocks'][k]['sha256']==trials[-1]['audio']['blocks'][k]['sha256']==trials[-2]['audio']['blocks'][k]['sha256']:
                    break
                prefix+=1
            assert prefix>0
            prefix_checks={trial['variant']:all(trial['audio']['blocks'][k]['sha256']==baseline['blocks'][k]['sha256']
                for k in range(prefix)) for trial in trials[1:4]}
            positive_changes=sum(trials[4]['audio']['blocks'][k]['sha256']!=baseline['blocks'][k]['sha256'] for k in range(prefix))
            prefix_nonzero_selections=sum(b.get('selected_nonzero_bit17',False) for b in baseline['blocks'][:prefix])
            reports.append(dict(sample_index=index,baseline_repeatable=repeatable,trials=trials,
                repeatable_prefix_calls=prefix,repeatable_prefix_bit17_equal=prefix_checks,
                repeatable_prefix_amplitude_changes=positive_changes,
                repeatable_prefix_nonzero_bit17_selections=prefix_nonzero_selections,
                bit17_unchanged_output={trial['variant']:trial['audio']['sha256']==baseline['sha256']
                    for trial in trials[1:4]} if repeatable else None,
                amplitude_control_changed=trials[4]['audio']['sha256']!=baseline['sha256'] if repeatable else None))
            print(json.dumps({key:reports[-1][key] for key in ('sample_index','baseline_repeatable',
                'repeatable_prefix_calls','repeatable_prefix_bit17_equal','repeatable_prefix_amplitude_changes',
                'repeatable_prefix_nonzero_bit17_selections')}),flush=True)
    finally:
        assert native.fn(0x10080,Q,P)(root)==0
    total_calls=sum(t['audio']['calls'] for r in reports for t in r['trials'])
    nonzero_runs=sum(any(b['nonzero'] for b in t['audio']['blocks']) for r in reports for t in r['trials'])
    report=dict(dse_sha256=SHA256,resource_sha256=RESOURCE_SHA256,
        reference_sha256=hashlib.sha256(reference.read_bytes()).hexdigest(),
        selected_samples=selected,max_audio_blocks=blocks,cases=reports,
        explicit_random_reset=reset_random is not None,
        summary=dict(samples=len(reports),repeatable=sum(r['baseline_repeatable'] for r in reports),
            bit17_all_variants_equal=sum(r['baseline_repeatable'] and all(r['bit17_unchanged_output'].values()) for r in reports),
            active_amplitude_controls=sum(r['amplitude_control_changed'] is True for r in reports),
            repeatable_prefix_calls=sum(r['repeatable_prefix_calls'] for r in reports),
            repeatable_prefix_all_bit17_equal=sum(all(r['repeatable_prefix_bit17_equal'].values()) for r in reports),
            repeatable_prefix_active_amplitude=sum(r['repeatable_prefix_amplitude_changes']>0 for r in reports),
            repeatable_prefix_nonzero_bit17_selections=sum(r['repeatable_prefix_nonzero_bit17_selections'] for r in reports),
            nonzero_selected_source_samples=sum(r['repeatable_prefix_nonzero_bit17_selections']>0 for r in reports),
            nonzero_selected_with_active_control=sum(r['repeatable_prefix_nonzero_bit17_selections']>0
                and r['amplitude_control_changed'] is True for r in reports),
            audio_runs=len(reports)*7,nonzero_audio_runs=nonzero_runs,
            finite_guarded_audio_values=total_calls*256,audio_calls=total_calls),
        scope='Compact attack/release resource, not V4X Growl VQMp. Previously repeatable '
            'short cases selected by evidence 417; three longer fresh baselines are rechecked. '
            'Only their common contiguous repeatable prefix permits exact comparisons; '
            'later differences remain inconclusive. Changes are applied after loadSample '
            'but before sampleInfo/configure and are preserved through audio. This does '
            'not test a possible field use within initial loadSample. '
            'Only independently loaded in-memory bit17 fields or harmonic amplitudes '
            'are varied. Nonrepeatable audio is inconclusive. Source index observations '
            'do not prove every feature was read. No authoring formula or full host claim.')
    # Per-block PCM hashes and cursor records are only needed for comparisons.
    # Save bounded aggregate results, not tens of thousands of repetitive rows.
    for row in reports:
        for trial in row['trials']:
            output=trial['audio']
            details=output.pop('blocks')
            output['status_counts']=dict(Counter(str(b['status']) for b in details))
            output['finite_guarded_values']=len(details)*256
            output['nonzero_samples']=sum(b['nonzero'] for b in details)
            output['peak']=max(b['peak'] for b in details)
            output['bit17_preserved_through_configuration_and_audio']=True
    return report


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for option in ('dll','directory','reference','output'):
        parser.add_argument('--'+option,type=Path,required=True)
    parser.add_argument('--limit',type=int,default=20)
    parser.add_argument('--blocks',type=int,default=64)
    args=parser.parse_args()
    assert 1<=args.limit<=20 and 4<=args.blocks<=1024
    report=run(args.dll,args.directory,args.reference,args.limit,args.blocks)
    sources=('verify_vqm_legacy_bit17_consumption.py','audit_vqm_legacy_live_features.py',
        'audit_frm2_numeric.py','probe_frm2.py','verify_region_frame_blend.py')
    report['source_sha256']={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources}
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__=='__main__':
    main()
