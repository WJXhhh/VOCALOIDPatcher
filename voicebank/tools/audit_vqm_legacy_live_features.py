"""Observe live feature blocks through the established compact-DVQM audio API.

The installed attack/release resource is separate from V4X Growl VQMp banks.
All native objects use Create/load/config/audio/Destroy. Commercial PCM and
feature arrays remain in memory; reports contain only aggregate statistics.
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

from audit_frm2_numeric import B, D, F, I, Native, P, Q, SHA256, f32
from verify_region_frame_blend import EMPTY


RESOURCE_SHA256='45ac6bb79bf12fd6f79d6739e06154d25585ec7850bd52531fe28d2943057438'


def pointer_items(owner,count_offset,array_offset,maximum=10000):
    assert owner
    count=I.from_address(owner+count_offset).value
    pointer=P.from_address(owner+array_offset).value
    assert 0<=count<=maximum and (not count or pointer)
    return list((P*count).from_address(pointer)) if count else []


def loaded_regions(control):
    # loadSample builds collection -> generic -> track -> regions using the
    # reviewed 938c0/93b50, 902e0/90a30 and track +140/+148 contracts.
    internal=P.from_address(control).value
    collection=P.from_address(internal+0x28).value
    generics=pointer_items(collection,0x44,0x48,100)
    assert len(generics)==1
    tracks=pointer_items(generics[0],0x48,0x50,100)
    assert len(tracks)==1
    return pointer_items(tracks[0],0x140,0x148,100)


def feature_digest(regions):
    digest=hashlib.sha256()
    count=0
    for region in regions:
        for frame in pointer_items(region,0x18,0x20):
            mask=Q.from_address(frame+0x50).value
            feature=P.from_address(frame+0x128).value
            assert mask&8192 and feature
            digest.update(c.string_at(feature,0x228))
            count+=1
    return dict(frames=count,sha256=digest.hexdigest())


def feature_payloads(regions):
    result=[]
    for region in regions:
        for frame in pointer_items(region,0x18,0x20):
            assert Q.from_address(frame+0x50).value&8192
            pointer=P.from_address(frame+0x128).value
            assert pointer
            result.append(c.string_at(pointer,0x228))
    return result


def feature_changes(before,after):
    assert len(before)==len(after)
    counts=Counter()
    for old,new in zip(before,after):
        counts['frames']+=1
        counts['changed_frames']+=old!=new
        for bit,offset in ((7,0x20),(17,0x21c),(18,0x220)):
            counts['changed_bit'+str(bit)]+=old[offset:offset+4]!=new[offset:offset+4]
        counts['changed_flags']+=old[:4]!=new[:4]
        counts['changed_array_storage']+=old[0x2c:0x20c]!=new[0x2c:0x20c]
        masked_old=bytearray(old)
        masked_new=bytearray(new)
        for offset in (0x20,0x220):
            masked_old[offset:offset+4]=masked_new[offset:offset+4]=bytes(4)
        counts['changed_outside_bit7_and_bit18']+=masked_old!=masked_new
    return dict(counts)


def children(node):
    count=I.from_address(node+0x50).value
    assert 0<=count<=1000
    pointer=P.from_address(node+0x48).value
    assert not count or pointer
    return list((P*count).from_address(pointer)) if count else []


def name(node):
    pointer=P.from_address(node+0x18).value
    assert pointer
    return c.string_at(pointer).decode('ascii')


def catalog(native,root):
    result=[]
    def visit(node,path,side):
        assert 1<=len(path)<=4
        entries={name(x):x for x in children(node)}
        if 'config' not in entries:
            for label,child in entries.items():
                assert label not in ('keywords','phmap')
                visit(child,path+[label],side)
            return
        descriptor=native.alloc(0x108)
        I.from_address(descriptor).value=len(path)
        for k,label in enumerate(path):
            encoded=label.encode('ascii')
            assert len(encoded)<65
            c.memmove(descriptor+4+k*65,encoded,len(encoded))
        assert native.fn(0x23f00,P,P,I,P)(root,side,descriptor)==node
        for label,sample in entries.items():
            if label not in ('config','keywords','phmap'):
                result.append((side,descriptor,label,sample))
    branches=children(root)
    assert len(branches)==2 and [name(x) for x in branches]==['attack','release']
    for side,branch in enumerate(branches):
        for node in children(branch):
            visit(node,[name(node)],side)
    assert len(result)==119
    return result


class FeatureStats:
    def __init__(self):
        self.counts=Counter()
        self.values={bit:Counter() for bit in (7,17,18)}
        self.flags=Counter()
        self.seen=set()

    def region(self,region,check_adapted=False):
        assert region
        count=I.from_address(region+0x18).value
        array=P.from_address(region+0x20).value
        assert 0<=count<=10000 and (not count or array)
        self.counts['region_snapshots']+=1
        for frame in (P*count).from_address(array) if count else []:
            assert frame
            mask=Q.from_address(frame+0x50).value
            feature=P.from_address(frame+0x128).value
            if not mask&8192 or not feature:
                self.counts['snapshots_without_active_features']+=1
                continue
            # Deduplicate identical feature states of a native frame within one
            # configured sample; different samples clear this set explicitly.
            raw=c.string_at(feature,0x228)
            token=(frame,hashlib.sha256(raw).digest())
            if token in self.seen:
                continue
            self.seen.add(token)
            self.counts['distinct_frame_feature_states']+=1
            flags=I.from_address(feature).value
            self.flags[hex(flags)]+=1
            for bit,offset in ((7,0x20),(17,0x21c),(18,0x220)):
                value=F.from_address(feature+offset).value
                assert math.isfinite(value)
                if flags&(1<<bit):
                    self.values[bit][value]+=1
                else:
                    self.counts['inactive_bit'+str(bit)+'_states']+=1
            arrays=[F.from_address(feature+offset+4*k).value
                for offset,count in ((0x2c,40),(0xcc,40),(0x16c,20),(0x1bc,20)) for k in range(count)]
            assert all(math.isfinite(x) for x in arrays)
            self.counts['states_with_nonzero_arrays']+=any(x!=0. for x in arrays)
            if not check_adapted:
                continue
            stored_pitch=F.from_address(frame+0xf0).value
            spectral_count=I.from_address(frame+0xa8).value
            smooth=P.from_address(frame+0xb0).value
            residual=P.from_address(frame+0xb8).value
            if mask&0x30==0x30 and spectral_count>0 and smooth and residual:
                s=F.from_address(smooth).value
                d=F.from_address(residual).value
                assert all(math.isfinite(x) for x in (stored_pitch,s,d))
                self.counts['prepared_source_states']+=1
                self.counts['prepared_positive_stored_pitch_states']+=stored_pitch>0
                self.counts['bit7_matches_residual']+=F.from_address(feature+0x20).value==d
                self.counts['bit18_matches_smooth']+=F.from_address(feature+0x220).value==s
                self.counts['residual_matches_stored_pitch_minus_smooth']+=struct.pack('<f',d)==struct.pack('<f',f32(stored_pitch-s))

    def report(self):
        return dict(counts=dict(self.counts),flags=dict(self.flags),scalars={str(bit):dict(
            observations=sum(values.values()),unique=len(values),nonzero=sum(n for v,n in values.items() if v!=0.),
            minimum=min(values) if values else None,maximum=max(values) if values else None)
            for bit,values in self.values.items()})


def audio(native,control,observe=None):
    call=native.fn(0xeb00,Q,I,F,P,P,P,P,P,P,I,P,P,F,F,F,I,F,F,F,P,D,D,D,B,B,B,B,B,D,D,I,P,P)
    block=I.from_address(control+0xa4).value
    assert block==256
    samples=native.alloc(block*4+32)
    amplitudes=native.array(F,[.1]*500)
    aux=[native.array(F,[0. if k==0 else 1.]) for k in range(4)]
    digest=hashlib.sha256()
    blocks=[]
    for mode in (1,2,2,2,2):
        c.memset(samples,0xa5,block*4+32)
        c.memset(samples+16,0,block*4)
        status=call(0,440.,amplitudes,*aux,samples+16,0,None,None,.5,.5,.5,mode,
            1.,.5,0.,None,0.,0.,0.,0,0,0,0,1,0.,0.,0,None,control)
        assert status==0
        assert c.string_at(samples,16)==bytes([0xa5])*16
        assert c.string_at(samples+16+block*4,16)==bytes([0xa5])*16
        values=list((F*block).from_address(samples+16))
        assert all(math.isfinite(x) for x in values)
        digest.update(c.string_at(samples+16,block*4))
        blocks.append(dict(mode=mode,sha256=hashlib.sha256(c.string_at(samples+16,block*4)).hexdigest(),
            peak=max(abs(x) for x in values),nonzero=sum(x!=0. for x in values)))
        if observe:
            observe(control,mode)
    return dict(sha256=digest.hexdigest(),blocks=blocks)


def load_configure(native,root,row):
    side,descriptor,label,sample=row
    control=native.fn(0xb400,P)()
    assert control
    assert native.fn(0x100a0,Q,P,I,P,c.c_char_p,P)(root,side,descriptor,label.encode('ascii'),control)==0
    internal=P.from_address(control).value
    assert internal and P.from_address(internal).value==sample
    info=native.alloc(0x64)
    c.memset(info,0xa5,0x64)
    assert native.fn(0xb5b0,Q,P,P)(info+16,control)==0
    assert c.string_at(info,16)==bytes([0xa5])*16 and c.string_at(info+0x54,16)==bytes([0xa5])*16
    scale=[min(1.,max(0.,F.from_address(info+16+offset).value)) for offset in (0x34,0x38,0x3c,0x40)]
    count=101
    arrays=[native.array(F,values) for values in ([f32(k*.01) for k in range(count)],
        [0.]*count,[EMPTY]*count,[.6]*count,[.5]*count)]
    assert native.fn(0xb6b0,Q,F,F,F,F,F,F,I,P,P,P,P,P,P)(.5,*scale,1.,count,*arrays,control)==0
    return control


def run(dll,directory,limit):
    resource=directory/'dvqm.dat'
    with resource.open('rb') as stream:
        resource_hash=hashlib.file_digest(stream,'sha256').hexdigest()
    assert resource_hash==RESOURCE_SHA256
    native=Native(dll)
    root=native.fn(0xfea0,P,c.c_char_p)(str(directory.resolve()).encode('utf-8'))
    assert root
    source=FeatureStats()
    produced=FeatureStats()
    loaded=FeatureStats()
    rows=[]
    directory_bytes=str(directory.resolve()).encode('utf-8')
    def observe(control,mode):
        internal=P.from_address(control).value
        context=P.from_address(internal+0x60).value
        assert context
        source.region(P.from_address(context+0x2e88).value,True)
        lanes=I.from_address(context+0x2df0).value
        assert lanes==1
        produced.region(P.from_address(context+0x2ed8+0x10).value)
    try:
        for index,row in enumerate(catalog(native,root)[:limit]):
            source.seen.clear()
            produced.seen.clear()
            loaded.seen.clear()
            digests=[]
            loaded_digests=[]
            feature_paths=[]
            source_identity=[]
            changes=[]
            for run_index,observed in enumerate((True,False,False,True)):
                trial_root=native.fn(0xfea0,P,c.c_char_p)(directory_bytes)
                assert trial_root and trial_root!=root
                side,descriptor,label,_=row
                node=native.fn(0x23f00,P,P,I,P)(trial_root,side,descriptor)
                actual_sample={name(x):x for x in children(node)}[label]
                control=load_configure(native,trial_root,(side,descriptor,label,actual_sample))
                try:
                    if observed:
                        regions=loaded_regions(control)
                        before=feature_digest(regions)
                        before_bytes=feature_payloads(regions)
                        if run_index==0:
                            for region in regions:
                                loaded.region(region)
                        snapshots=[]
                        identities=[]
                        def observer(control,mode):
                            internal=P.from_address(control).value
                            context=P.from_address(internal+0x60).value
                            assert context and I.from_address(context+0x2df0).value==1
                            current=P.from_address(context+0x2e88).value
                            output=P.from_address(context+0x2ed8+0x10).value
                            if run_index==0:
                                observe(control,mode)
                            snapshots.append(dict(mode=mode,source=feature_digest([current]),
                                produced=feature_digest([output])))
                            identities.append(current in regions)
                    else:
                        observer=None
                    digests.append(audio(native,control,observer))
                    if observed:
                        after=feature_digest(loaded_regions(control))
                        changes.append(feature_changes(before_bytes,feature_payloads(loaded_regions(control))))
                        loaded_digests.append(dict(before=before,after=after))
                        feature_paths.append(snapshots)
                        source_identity.append(identities)
                finally:
                    native.fn(0xb480,None,P)(control)
                    assert native.fn(0x10080,Q,P)(trial_root)==0
            rows.append(dict(sample_index=index,runs=digests,
                loaded_feature_changes=changes,loaded_feature_digests=loaded_digests,
                source_is_loaded_region=source_identity,
                observed_feature_paths_equal=feature_paths[0]==feature_paths[1],
                observed_feature_paths=feature_paths,
                unobserved_pair_equal=digests[1]['sha256']==digests[2]['sha256'],
                observed_pair_equal=digests[0]['sha256']==digests[3]['sha256'],
                observed_unobserved_equal=digests[0]['sha256']==digests[1]['sha256']))
    finally:
        assert native.fn(0x10080,Q,P)(root)==0
    changes=Counter()
    for row in rows:
        changes.update(row['loaded_feature_changes'][0])
    summary=dict(first_observed_changes=dict(changes),
        changed_source_samples=sum(row['loaded_feature_changes'][0]['changed_frames']>0 for row in rows),
        loaded_before_equal=sum(row['loaded_feature_digests'][0]['before']==row['loaded_feature_digests'][1]['before'] for row in rows),
        observed_feature_paths_equal=sum(row['observed_feature_paths_equal'] for row in rows),
        source_is_loaded_region_snapshots=sum(sum(sum(x) for x in row['source_is_loaded_region']) for row in rows),
        unobserved_audio_equal=sum(row['unobserved_pair_equal'] for row in rows),
        observed_audio_equal=sum(row['observed_pair_equal'] for row in rows),
        cross_audio_equal=sum(row['observed_unobserved_equal'] for row in rows),
        audio_runs=len(rows)*4,audio_calls=len(rows)*20,
        finite_guarded_audio_samples=len(rows)*20*256,
        audio_runs_with_nonzero_output=sum(any(b['nonzero'] for b in run['blocks']) for row in rows for run in row['runs']))
    assert summary['loaded_before_equal']==len(rows)
    assert summary['observed_feature_paths_equal']==len(rows)
    assert summary['source_is_loaded_region_snapshots']==len(rows)*10
    assert all(row['loaded_feature_changes'][0]==row['loaded_feature_changes'][1] for row in rows)
    assert summary['audio_runs_with_nonzero_output']==len(rows)*4
    assert all(changes[key]==0 for key in ('changed_bit17','changed_flags',
        'changed_array_storage','changed_outside_bit7_and_bit18'))
    for key in ('bit7_matches_residual','bit18_matches_smooth','residual_matches_stored_pitch_minus_smooth'):
        assert source.counts[key]==source.counts['prepared_source_states']
    assert all(not values for values in produced.values.values())
    return dict(dse_sha256=SHA256,resource_sha256=resource_hash,samples=len(rows),cases=rows,
        summary=summary,
        entry_rvas=dict(create='0xb400',destroy='0xb480',resource_load='0xfea0',
            resource_free='0x10080',sample_load='0x100a0',sample_info='0xb5b0',
            configure='0xb6b0',audio='0xeb00',property_resolve='0x23f00'),
        audio_modes=[1,2,2,2,2],
        loaded_features=loaded.report(),source_features=source.report(),produced_features=produced.report(),
        scope='Established 207/208 compact attack/release API contract, '
              'native constructors/load/configuration/prepass and four audio blocks; '
              'read-only feature observations in the same isolated Python process, '
              'two fresh observed and two fresh unobserved controls and resource roots '
              'in interleaved order. Source features before/after audio are compared. '
              'PCM repeatability is measured, not assumed. Feature-state counts '
              'are from the first observed control only and deduplicated within '
              'each sample, not independent recordings. Only active scalar flags '
              'are interpreted; inactive produced feature storage is not a zero estimate. '
              'Separate from V4X Growl VQMp and VQM.vdb auxiliary libraries. '
              'No original maker, host scheduling, full playback or bit17 formula claim.')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('dll','directory','output'):
        parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--limit',type=int,default=119)
    args=parser.parse_args()
    assert 1<=args.limit<=119
    report=run(args.dll,args.directory,args.limit)
    sources=('audit_vqm_legacy_live_features.py','audit_frm2_numeric.py','verify_region_frame_blend.py','probe_frm2.py')
    report['source_sha256']={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources}
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:report[k] for k in ('samples','summary','loaded_features','source_features','produced_features')}))


if __name__=='__main__':
    main()
