"""Bounded bit7 residual-pitch consumer and paired full native audio tests.

Owned configuration fixtures explicitly select the reviewed internal +c8 mode.
Installed files/instructions are unchanged. Commercial payloads stay in RAM.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import importlib
import itertools
import json
import math
from pathlib import Path
import struct
import sys

from audit_frm2_numeric import F,I,Native,P,Q,SHA256,f32
from audit_vqm_legacy_live_features import (RESOURCE_SHA256,catalog,children,
    feature_payloads,loaded_regions,name,pointer_items)
import verify_vqm_legacy_bit17_consumption as legacy
from verify_vqm_legacy_seeded_consumption import random_reset

START,END=0x355B2,0x35613


def adapter(native,decoder_dir):
    sys.path.insert(0,str(decoder_dir));cs=importlib.import_module('capstone')
    md=cs.Cs(cs.CS_ARCH_X86,cs.CS_MODE_64);md.detail=True
    raw=c.string_at(native.lib._handle+START,END-START)
    ops=list(md.disasm(raw,START));assert sum(x.size for x in ops)==len(raw)
    code=bytearray(bytes.fromhex('415541574156574881eca8000000'))
    saves=((6,0x40),(7,0x50),(8,0x60),(9,0x70),(10,0x80),(13,0x90))
    for reg,offset in saves:
        code+=(b'\x44' if reg>=8 else b'')+b'\x0f\x11'+bytes([0x84|((reg&7)<<3),0x24])+struct.pack('<i',offset)
    code+=bytes.fromhex('4c8b294c8b79084c8b7110488b7918')
    code+=bytes.fromhex('f3440f105120f30f107924f30f107128f3440f10412cf3440f106930')
    body=len(code);code+=raw;code+=bytes.fromhex('410f28c1')
    for reg,offset in saves:
        code+=(b'\x44' if reg>=8 else b'')+b'\x0f\x10'+bytes([0x84|((reg&7)<<3),0x24])+struct.pack('<i',offset)
    code+=bytes.fromhex('4881c4a80000005f415e415f415dc3')
    payloads={};fixups=[]
    for op in ops:
        offset=body+op.address-START
        if op.mnemonic=='call':
            target=op.operands[0].imm
            assert target==0x217DE4 and op.size==5
            key=('call',target);payloads[key]=bytes.fromhex('ff2500000000')+struct.pack('<Q',native.lib._handle+target)
            fixups.append((offset+1,key))
        elif op.group(cs.CS_GRP_JUMP):assert START<=op.operands[0].imm<=END
        for o in op.operands:
            if o.type==cs.x86.X86_OP_MEM and o.mem.base==cs.x86.X86_REG_RIP:
                target=op.address+op.size+o.mem.disp
                assert target==0x5BFD54 and F.from_address(native.lib._handle+target).value==1200.
                key=('constant',target);payloads[key]=c.string_at(native.lib._handle+target,4)
                fixups.append((offset+op.disp_offset,key))
    positions={}
    for key,value in payloads.items():positions[key]=len(code);code+=value
    for offset,key in fixups:struct.pack_into('<i',code,offset,positions[key]-(offset+4))
    kernel=c.WinDLL('kernel32',use_last_error=True)
    kernel.VirtualAlloc.argtypes,kernel.VirtualAlloc.restype=[P,c.c_size_t,c.c_uint,c.c_uint],P
    kernel.VirtualProtect.argtypes,kernel.VirtualProtect.restype=[P,c.c_size_t,c.c_uint,P],c.c_int
    address=kernel.VirtualAlloc(None,len(code),0x3000,4)
    if not address:raise c.WinError(c.get_last_error())
    c.memmove(address,bytes(code),len(code));old=c.c_uint()
    if not kernel.VirtualProtect(address,len(code),0x20,c.byref(old)):raise c.WinError(c.get_last_error())
    return c.CFUNCTYPE(F,P)(address),dict(start_rva=hex(START),end_exclusive_rva=hex(END),
        original_span_sha256=hashlib.sha256(raw).hexdigest(),instructions=len(ops),decoder_version=cs.__version__)


def numeric(native,decoder_dir):
    invoke,span=adapter(native,decoder_dir);rows=[]
    for (a,b),weight,strength,(base,target) in itertools.product(
        ((0.,0.),(-120.,120.),(120.,-120.),(0.,600.),(-600.,0.),(1200.,-1200.)),
        (0.,.25,.5,.75,1.),(0.,.5,1.),((0.,0.),(1200.,0.),(0.,1200.))):
        frames=[native.alloc(0x148) for _ in range(3)]
        features=[native.alloc(0x228) for _ in range(2)]
        for frame,feature,value in zip(frames,features,(a,b)):
            P.from_address(frame+0x128).value=feature;F.from_address(feature+0x20).value=value
        F.from_address(frames[2]+0xF0).value=target
        context=native.alloc(0x2050);F.from_address(context+0x2048).value=strength
        fixture=native.alloc(56)
        for off,value in zip((0,8,16,24),(frames[0],frames[1],context,frames[2])):P.from_address(fixture+off).value=value
        for off,value in ((32,weight),(36,base),(40,2.),(44,0.),(48,1.)):F.from_address(fixture+off).value=value
        objects=list(zip(frames,[0x148]*3))+list(zip(features,[0x228]*2))+[(context,0x2050),(fixture,56)]
        before=[c.string_at(at,size) for at,size in objects]
        blend=f32(f32(weight*b)+f32(f32(1.-weight)*a)) if weight>0. else a
        exponent=f32(f32(f32(f32(blend*strength)+base)-target)/1200.)
        expected=f32(math.pow(2.,exponent))
        actual=invoke(fixture)
        assert math.isfinite(actual) and abs(actual-expected)<=2e-7*max(1.,abs(expected))
        assert before==[c.string_at(at,size) for at,size in objects]
        rows.append(dict(a=a,b=b,weight=weight,strength=strength,base_cent=base,target_cent=target,
            expected=expected,actual=actual,float32_exact=struct.pack('<f',actual)==struct.pack('<f',expected),
            absolute_error=abs(actual-expected)))
    assert hashlib.sha256(c.string_at(native.lib._handle+START,END-START)).hexdigest()==span['original_span_sha256']
    return dict(span=span,cases=rows,summary=dict(cases=len(rows),float32_exact=sum(r['float32_exact'] for r in rows),
        max_absolute_error=max(r['absolute_error'] for r in rows),all_sources_preserved=True))


def change_source(control,variant):
    mode,label=variant.split(':')
    internal=P.from_address(control).value
    I.from_address(internal+0xC8).value=int(mode)
    before=feature_payloads(loaded_regions(control));frames=0;changed=0
    for region in loaded_regions(control):
        for frame in pointer_items(region,0x18,0x20):
            feature=P.from_address(frame+0x128).value;assert feature and I.from_address(feature).value&(1<<7)
            frames+=1
            if label in ('zero','plus120'):
                value=F.from_address(feature+0x20).value;assert math.isfinite(value)
                new=0. if label=='zero' else f32(value+120.)
                F.from_address(feature+0x20).value=new;changed+=value!=new
            elif label=='amplitude_plus6':
                count=I.from_address(frame+0x58).value;pointer=P.from_address(frame+0x68).value
                assert 0<=count<=10000 and (not count or pointer)
                for k in range(count):
                    value=F.from_address(pointer+4*k).value;assert math.isfinite(value)
                    F.from_address(pointer+4*k).value=f32(value+6.)
            else:assert label=='baseline'
    after=feature_payloads(loaded_regions(control));assert len(before)==len(after)
    for a,b in zip(before,after):
        if label in ('zero','plus120'):assert a[:0x20]==b[:0x20] and a[0x24:]==b[0x24:]
        else:assert a==b
    return dict(frames=frames,changed_bit7=changed,owned_internal_c8_mode=int(mode))


def pcm(native,dll,directory):
    resource=directory/'dvqm.dat';assert hashlib.sha256(resource.read_bytes()).hexdigest()==RESOURCE_SHA256
    reset,identity=random_reset(dll,419);directory_bytes=str(directory.resolve()).encode('utf-8')
    root=native.fn(0xFEA0,P,c.c_char_p)(directory_bytes);assert root
    original=legacy.change_source;legacy.change_source=change_source;reports=[]
    try:
        rows=catalog(native,root);assert len(rows)==119
        for index,mode in itertools.product((0,1),(0,1)):
            side,descriptor,label,_=rows[index];trials=[]
            for variant in ('baseline','zero','plus120','amplitude_plus6','baseline'):
                trial_root=native.fn(0xFEA0,P,c.c_char_p)(directory_bytes);assert trial_root and trial_root!=root
                node=native.fn(0x23F00,P,P,I,P)(trial_root,side,descriptor)
                sample={name(x):x for x in children(node)}[label]
                control,changes=legacy.load_configure(native,trial_root,(side,descriptor,label,sample),str(mode)+':'+variant)
                try:
                    before=feature_payloads(loaded_regions(control));result=legacy.audio(native,control,64,reset)
                    after=feature_payloads(loaded_regions(control));assert len(before)==len(after)
                    internal=P.from_address(control).value;ctx=P.from_address(internal+0x60).value
                    observed=I.from_address(ctx+0x2044).value;assert observed==mode
                    details=result.pop('blocks')
                    result['nonzero_samples']=sum(b['nonzero'] for b in details)
                    result['peak']=max(b['peak'] for b in details)
                    result['source_bit7_changed_during_audio']=sum(a[0x20:0x24]!=b[0x20:0x24] for a,b in zip(before,after))
                    result['consumer_mode']=observed
                    result['adaptation_radius']=I.from_address(ctx+0xAC94).value
                    result['residual_strength']=F.from_address(ctx+0x2048).value
                    trials.append(dict(variant=variant,changes=changes,audio=result))
                finally:
                    native.fn(0xB480,None,P)(control);assert native.fn(0x10080,Q,P)(trial_root)==0
            baseline=trials[0]['audio']['sha256'];assert trials[-1]['audio']['sha256']==baseline
            report=dict(sample_index=index,owned_mode=mode,baseline_repeatable=True,trials=trials,
                pcm_changed={t['variant']:t['audio']['sha256']!=baseline for t in trials[1:-1]})
            reports.append(report);print(json.dumps({k:report[k] for k in ('sample_index','owned_mode','pcm_changed')}),flush=True)
    finally:
        legacy.change_source=original;assert native.fn(0x10080,Q,P)(root)==0
    assert hashlib.sha256(resource.read_bytes()).hexdigest()==RESOURCE_SHA256
    return dict(resource_sha256=RESOURCE_SHA256,random_identity=identity,cases=reports,
        summary=dict(configurations=len(reports),baseline_repeatable=len(reports),
            audio_runs=sum(len(r['trials']) for r in reports),
            audio_calls=sum(t['audio']['calls'] for r in reports for t in r['trials']),
            active_amplitude_controls=sum(r['pcm_changed']['amplitude_plus6'] for r in reports),
            bit7_pcm_changed_by_mode={str(mode):sum(any(r['pcm_changed'][v] for v in ('zero','plus120'))
                for r in reports if r['owned_mode']==mode) for mode in (0,1)},installed_resource_unchanged=True))


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for key in ('dll','decoder-dir','directory','output'):parser.add_argument('--'+key,type=Path,required=True)
    args=parser.parse_args();native=Native(args.dll)
    kernels=numeric(native,args.decoder_dir);audio=pcm(native,args.dll,args.directory)
    sources=(Path(__file__).name,'verify_vqm_legacy_bit17_consumption.py','verify_vqm_legacy_seeded_consumption.py',
        'audit_vqm_legacy_live_features.py','audit_frm2_numeric.py','verify_region_frame_blend.py','probe_frm2.py')
    result=dict(dse_sha256=SHA256,source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        numeric=kernels,pcm=audio,limits=['Numeric span begins after caller guards; native caller reachability is separate.',
            'Numeric model uses Python pow and allows 2e-7 relative/absolute scaled error; exact count is reported separately.',
            'PCM uses owned internal+c8 modes 0/1 before configuration. Mode1 is an explicit fixture, not observed default.',
            'Changing internal+c8 also changes configuration F0-curve source; field perturbations compare within each same mode.',
            'No PC trace proves each PCM change came only from the identified instructions.',
            'Only compact attack/release catalog samples0/1, not V4X Growl or original feature maker.'])
    args.output.write_text(json.dumps(result,indent=2,allow_nan=False)+'\n',encoding='utf-8')
    print(json.dumps(dict(numeric=kernels['summary'],pcm=audio['summary'])))


if __name__=='__main__':main()
