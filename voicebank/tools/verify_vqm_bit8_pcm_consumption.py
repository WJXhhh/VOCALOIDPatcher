"""Bounded bit8 source perturbation through the existing compact audio API.

Uses fresh native objects and the genuine CRT seed reset. No installed data or
native instructions are changed; no PCM or commercial feature bytes are saved.
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

from audit_frm2_numeric import F,I,Native,P,Q,SHA256
from audit_vqm_legacy_live_features import (RESOURCE_SHA256,catalog,children,
    feature_payloads,loaded_regions,name,pointer_items)
import verify_vqm_legacy_bit17_consumption as legacy
from verify_vqm_legacy_seeded_consumption import random_reset


def change_source(control,variant):
    regions=loaded_regions(control)
    before=feature_payloads(regions)
    stats=Counter()
    refs={'invalid':-3.4028234663852886e38,'minus1200':-1200.,'zero':0.,'plus1200':1200.}
    for region in regions:
        for frame in pointer_items(region,0x18,0x20):
            feature=P.from_address(frame+0x128).value
            assert feature and I.from_address(feature).value&(1<<8)
            stats['frames']+=1
            if variant in refs:
                old=c.string_at(feature+0x24,4)
                F.from_address(feature+0x24).value=refs[variant]
                stats['bit8_changed']+=old!=c.string_at(feature+0x24,4)
            elif variant=='amplitude_plus6':
                count=I.from_address(frame+0x58).value
                pointer=P.from_address(frame+0x68).value
                assert 0<=count<=10000 and (not count or pointer)
                for k in range(count):
                    value=F.from_address(pointer+4*k).value
                    assert math.isfinite(value)
                    F.from_address(pointer+4*k).value=value+6.
                    stats['amplitudes_changed']+=1
            else:assert variant=='baseline'
    after=feature_payloads(regions)
    assert len(before)==len(after)
    for a,b in zip(before,after):
        if variant in refs:assert a[:0x24]==b[:0x24] and a[0x28:]==b[0x28:]
        else:assert a==b
    return dict(stats)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for key in ('dll','directory','output'):parser.add_argument('--'+key,type=Path,required=True)
    args=parser.parse_args()
    resource=args.directory/'dvqm.dat'
    assert hashlib.sha256(resource.read_bytes()).hexdigest()==RESOURCE_SHA256
    native=Native(args.dll)
    reset,identity=random_reset(args.dll,419)
    directory=str(args.directory.resolve()).encode('utf-8')
    root=native.fn(0xfea0,P,c.c_char_p)(directory)
    assert root
    reports=[]
    original=legacy.change_source
    legacy.change_source=change_source
    try:
        rows=catalog(native,root)
        assert len(rows)==119
        for index in (0,1):
            side,descriptor,label,_=rows[index]
            trials=[]
            for variant in ('baseline','invalid','minus1200','zero','plus1200','amplitude_plus6','baseline'):
                trial_root=native.fn(0xfea0,P,c.c_char_p)(directory)
                assert trial_root and trial_root!=root
                node=native.fn(0x23f00,P,P,I,P)(trial_root,side,descriptor)
                sample={name(x):x for x in children(node)}[label]
                control,changes=legacy.load_configure(native,trial_root,(side,descriptor,label,sample),variant)
                try:
                    before=feature_payloads(loaded_regions(control))
                    result=legacy.audio(native,control,64,reset)
                    after=feature_payloads(loaded_regions(control))
                    assert len(before)==len(after)
                    assert all(a[0x24:0x28]==b[0x24:0x28] for a,b in zip(before,after))
                    details=result.pop('blocks')
                    result['nonzero_samples']=sum(b['nonzero'] for b in details)
                    result['peak']=max(b['peak'] for b in details)
                    result['status_counts']=dict(Counter(str(b['status']) for b in details))
                    result['bit8_preserved_through_configuration_and_audio']=True
                    trials.append(dict(variant=variant,changes=changes,audio=result))
                finally:
                    native.fn(0xb480,None,P)(control)
                    assert native.fn(0x10080,Q,P)(trial_root)==0
            baseline=trials[0]['audio']['sha256']
            assert baseline==trials[-1]['audio']['sha256']
            reports.append(dict(sample_index=index,baseline_repeatable=True,trials=trials,
                pcm_changed={t['variant']:t['audio']['sha256']!=baseline for t in trials[1:-1]}))
            print(json.dumps({k:reports[-1][k] for k in ('sample_index','baseline_repeatable','pcm_changed')}),flush=True)
    finally:
        legacy.change_source=original
        assert native.fn(0x10080,Q,P)(root)==0
    assert hashlib.sha256(resource.read_bytes()).hexdigest()==RESOURCE_SHA256
    sources=(Path(__file__).name,'verify_vqm_legacy_bit17_consumption.py',
        'verify_vqm_legacy_seeded_consumption.py','audit_vqm_legacy_live_features.py',
        'audit_frm2_numeric.py','verify_region_frame_blend.py','probe_frm2.py')
    result=dict(dse_sha256=SHA256,resource_sha256=RESOURCE_SHA256,random_identity=identity,
        source_sha256={n:hashlib.sha256(Path(__file__).with_name(n).read_bytes()).hexdigest() for n in sources},
        cases=reports,summary=dict(samples=len(reports),audio_runs=sum(len(r['trials']) for r in reports),
            baseline_repeatable=len(reports),audio_calls=sum(t['audio']['calls'] for r in reports for t in r['trials']),
            bit8_changed_pcm=sum(any(v for k,v in r['pcm_changed'].items() if k!='amplitude_plus6') for r in reports),
            active_amplitude_controls=sum(r['pcm_changed']['amplitude_plus6'] for r in reports),
            installed_resource_unchanged=True),
        limits=['Only catalog samples 0 and 1 of compact attack/release resource; not V4X Growl.',
            'Uses unmodified established configuration and full native audio entry.',
            'No program-counter observation proves execution of the isolated reference block.',
            'No difference bounds only these inputs; does not prove absence of other consumers.',
            'No claim of original bit8 production or full Editor behavior.'])
    args.output.write_text(json.dumps(result,indent=2,allow_nan=False)+'\n',encoding='utf-8')
    print(json.dumps(result['summary']))


if __name__=='__main__':main()
