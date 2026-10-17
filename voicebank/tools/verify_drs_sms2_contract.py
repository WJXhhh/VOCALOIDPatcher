"""Owned-data acceptance for DRS config IDs, full frame storage and selection.

Native calls are isolated in short subprocesses. Artifacts go only under the
explicit new work directory; existing evidence and installed files are read-only.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
import os
from pathlib import Path
import struct
import subprocess
import sys

from audit_frm2_numeric import Native, B, F, D, I, P, Q, SHA256, f32
import analyze_recording_units as analysis
import probe_drs_frame as layout
import probe_sms2
from probe_frm2 import Envelope, serialize_envelope
from probe_frm2_batch_outputs import run as batch, owned_pcm
from verify_vqm_save_selection import Scratch


ALL=(1<<64)-1
MAIN=0x2000e00207
ROOT=Path(__file__).resolve().parent


def frames(collection):
    """The batch result observer obtains its populated track from context."""
    region_count=I.from_address(collection+0x140).value
    assert 0<region_count<100
    regions=P.from_address(collection+0x148).value
    result=[]
    for r in range(region_count):
        region=P.from_address(regions+r*8).value
        count=I.from_address(region+0x18).value
        assert 0<=count<10000
        pointers=P.from_address(region+0x20).value
        result.extend(P.from_address(pointers+i*8).value for i in range(count))
    return result


def metadata(raw):
    frame=layout.parse(raw)
    assert layout.serialize(frame)==raw
    return dict(mask=hex(frame.mask),bytes=len(raw),sha256=hashlib.sha256(raw).hexdigest(),
        fields={f.name:dict(offset=f.offset,bytes=len(f.payload),encoding=f.encoding,
            sha256=hashlib.sha256(f.payload).hexdigest()) for f in frame.fields})


def predict_grid_normalization(raw):
    """The observed uncompressed regular-float ENV reader path, not all ENV flags."""
    parsed=layout.parse(raw);result=bytearray(raw);changes=[]
    for field in parsed.fields:
        if field.encoding!='ENV chunk':continue
        flags=struct.unpack_from('<I',field.payload,8)[0]
        if flags&((1<<23)|(1<<16)):continue # irregular-point path
        if flags&((1<<21)|(1<<22)) or flags&3==1:
            raise AssertionError('Unreviewed packed/constant grid ENV profile')
        lo,hi=struct.unpack_from('<2f',field.payload,16)
        assert len(field.payload[28:])%4==0 and lo<=hi
        count=(len(field.payload)-28)//4;changed=[]
        for i in range(count):
            value=struct.unpack_from('<f',field.payload,28+i*4)[0]
            assert math.isfinite(value)
            clipped=min(hi,max(lo,value))
            if not flags&(1<<17):clipped=float(int(clipped))
            packed=struct.pack('<f',clipped)
            if packed!=field.payload[28+i*4:32+i*4]:changed.append(i)
            result[field.offset+28+i*4:field.offset+32+i*4]=packed
        if changed:changes.append(dict(field=field.name,changed_indices=changed,lo=lo,hi=hi))
    return bytes(result),changes


def config_contract(dll):
    native=Native(dll);cfg=native.alloc(0x35f0);native.fn(0x90de0,P,P)(cfg)
    getter=native.fn(0x2b050,F,P,I,F)
    # getter's direct-ID switch: raw f32, integer-to-f32 and signed byte-to-f32.
    direct={0x4d:(0x18,'f'),0x4e:(0x1c,'f'),0x4f:(0x20,'f'),0x54:(0x14,'u')}
    ids=list(range(0x55,0x88));ids.remove(0x57)
    offsets=list(range(0x508,0x5a0,4))+[0x5a4,0x5a8,0x5ac,0x5b0,0x5a0,0x5b4,0x5b8,0x5bc,0x5c0,0x5c4,0x5c8,0x5cc]
    assert len(ids)==len(offsets)
    floats={0x61,0x62,0x64,0x72,0x73,0x74,0x75,0x77,0x78}
    for id,offset in zip(ids,offsets):direct[id]=(offset,'b' if id in (0x66,0x6a) else 'f' if id in floats else 'i')
    defaults={hex(id):getter(cfg,id,0.) for id in range(0x88)}
    rows=[]
    for id,(offset,kind) in direct.items():
        saved=c.string_at(cfg+offset,4)
        values=(.25+id/128.,-3.5) if kind=='f' else (-3,7) if kind!='u' else (3,7)
        for value in values:
            if kind=='f':F.from_address(cfg+offset).value=value
            elif kind=='b':c.c_byte.from_address(cfg+offset).value=int(value)
            else:(c.c_uint if kind=='u' else I).from_address(cfg+offset).value=int(value)
            actual=getter(cfg,id,.25);assert actual==f32(value),(hex(id),actual,value)
            rows.append(dict(id=hex(id),offset=hex(offset),storage=kind,value=value,result=actual))
        c.memmove(cfg+offset,saved,4)
    # Known dynamic time protocol; owned linear curves, not commercial presets.
    temporal=[]
    absolute_time_ids={bit for bit in range(36) if (0x820000800>>bit)&1}
    assert absolute_time_ids=={11,29,35}
    for id in (0,4,11,13,20,29,33,35,47):
        env=cfg+0x5d0+id*0xa0
        native.cfg(env,3,1,1,-20000.,20000.,0.,1,0,0)
        native.add(env,0.,10.);native.add(env,1.,30.)
        assert B.from_address(env+0x40).value==3
        for mode,duration in ((0,.5),(1,.5),(2,.5),(2,0.),(3,.5)):
            B.from_address(cfg).value=mode;D.from_address(cfg+8).value=duration
            for time in (0.,.125,.25,.75,1.5):
                at=time if id in absolute_time_ids else 0. if mode==1 else time/duration if mode==2 and duration>0 else time
                expected=f32(10.+20.*min(1.,max(0.,at)))
                assert native.evaluate(env,at)==expected
                actual=getter(cfg,id,time);assert actual==expected,(id,mode,time,actual,expected)
                temporal.append(dict(id=hex(id),mode=mode,duration=duration,time=time,evaluated_time=at,value=actual))
    dynamic=[]
    for id in range(77):
        env=cfg+0x5d0+id*0xa0;start=10.+id*.5
        native.cfg(env,3,1,1,-20000.,20000.,0.,1,0,0)
        native.add(env,0.,start);native.add(env,1.,start+20.)
        for mode in (2,3):
            B.from_address(cfg).value=mode;D.from_address(cfg+8).value=.5
            expected=f32(start+(10. if mode==2 and id not in absolute_time_ids else 5.))
            actual=getter(cfg,id,.25);assert actual==expected
            dynamic.append(dict(id=hex(id),envelope_offset=hex(0x5d0+id*0xa0),mode=mode,value=actual))
    # Type2 curves bypass normalization and mode1's forced zero-time query.
    env=cfg+0x5d0+47*0xa0
    native.cfg(env,2,1,1,-20000.,20000.,0.,1,0,0)
    native.add(env,0.,10.);native.add(env,1.,30.)
    bypass=[]
    for mode in (1,2):
        B.from_address(cfg).value=mode;D.from_address(cfg+8).value=.5
        for time in (.125,.25,.75):
            expected=f32(10.+20.*time);actual=getter(cfg,47,time);assert actual==expected
            bypass.append(dict(id='0x2f',mode=mode,time=time,value=actual))
    return dict(dse_sha256=SHA256,direct_ids=len(direct),direct_cases=rows,dynamic_time_cases=temporal,
        all_dynamic_id_cases=dynamic,type2_bypass_cases=bypass,
        defaults=defaults,scope='Physical getter mapping and owned time curves; no official names or commercial presets inferred.')


def owned_raw(bits):
    """Finite sparse fixtures, including uncommon tail storage blocks."""
    mask=sum(1<<b for b in bits)
    if 9 in bits:mask|=1<<31
    data=[]
    def add(bit,raw):
        if bit in bits:data.append(raw)
    if mask&7:
        data.append(struct.pack('<I',2))
        for bit,vals in ((1,(1.,2.)),(0,(-20.,-30.)),(2,(.1,.2))):add(bit,struct.pack('<2f',*vals))
    if mask&0x30:
        data.append(struct.pack('<I',3))
        add(4,struct.pack('<3f',-12.,-13.,-14.));add(5,struct.pack('<3f',.1,.2,.3))
    add(6,struct.pack('<I3h',3,-123,0,456))
    for bit in (9,20,10,11,12):add(bit,struct.pack('<f',-1200. if bit==9 else bit+.25))
    add(13,struct.pack('<I2fB',3,.25,.5,0))
    add(35,bytes((3*k+1)%251 for k in range(52)))
    # ENV disk flags differ from the in-memory configure() word.
    env=serialize_envelope(Envelope(0x01820002,(-100.,10.,0.),(0.,-20.,.5,-15.,1.,-10.)))
    add(37,env)
    for length in (372,76,76):add(27,bytes((7*k+3)%251 for k in range(length)))
    add(14,bytes(range(35)))
    add(32,struct.pack('<I3f',3,.25,.5,.75))
    add(33,struct.pack('<I4f',2,.5,.75,1.,1.25)+bytes(range(96))+struct.pack('<2f',2.5,3.5))
    add(34,struct.pack('<I2f',2,1.25,2.25))
    add(16,struct.pack('<I3f',3,3.5,4.5,5.5))
    add(18,b'\x0a');add(19,struct.pack('<I',2049))
    for primary,secondary in ((21,22),(24,25)):
        add(primary,struct.pack('<I3d',1,500.,-20.,200.))
        add(secondary,struct.pack('<3fI3d',-26.5,1.25,-.001,1,800.,-22.,250.))
    for bit in (7,8,17,23,26):add(bit,env)
    body=struct.pack('<IdQ',1,.25,mask)+b''.join(data)
    return struct.pack('<4sI',b'FRM2',8+len(body))+body


def storage_contract(dll,root):
    native=Native(dll);path=root/'scratch.frm2';path.touch(exist_ok=False)
    scratch=Scratch(native,path)
    bits=[0,1,2,4,5,6,7,8,9,10,11,12,13,14,16,17,18,19,20,21,22,23,24,25,26,27,32,33,34,35,37]
    cases=[]
    # Nonzero sparse fields plus a combined layout prove order, not just size.
    for chosen in [[bit] for bit in bits]+[bits]:
        raw=owned_raw(chosen);frame=layout.parse(raw);assert layout.serialize(frame)==raw
        path.write_bytes(raw)
        target=native.alloc(4096);native.frame_ctor(target)
        # The ordinary ctor allocates set0 tables only. Own set1 tables for
        # these file-layout tests; no claim of naturally configured production.
        provisioned=[]
        for bit,offset,capacity in ((24,0x190,25),(25,0x1c0,1)):
            if bit in chosen:
                P.from_address(target+offset).value=native.array(P,[0]*capacity)
                provisioned.append(dict(bit=bit,offset=hex(offset),capacity=capacity))
        code,_=scratch.invoke(0x93000,target,ALL,reading=True);assert code==0
        code,actual=scratch.invoke(0x92f40,target,ALL);assert code==0
        assert actual==raw,('DRS round-trip',chosen)
        # Independently verify direct scalar, PCM, fixed and array memory slots.
        for bit,offset in ((9,0xf0),(20,0xf4),(10,0xf8),(11,0xfc),(12,0x100)):
            if bit in chosen:
                saved=next(f for f in frame.fields if f.name==f'bit{bit}.scalar')
                assert c.string_at(target+offset,4)==saved.payload
        for bit,offset,length in ((14,0x158,35),(35,0x130,52)):
            if bit in chosen:
                saved=next(f for f in frame.fields if f.name==f'bit{bit}.block')
                assert c.string_at(P.from_address(target+offset).value,length)==saved.payload
        filtered=[]
        for selection in (0,MAIN,ALL & ~(1<<chosen[0])):
            code,selected=scratch.invoke(0x92f40,target,selection);assert code==0
            expected=layout.serialize(frame,selection)
            assert selected==expected,('selection',chosen,hex(selection))
            filtered.append(dict(selection=hex(selection),stored_mask=hex(layout.parse(selected).mask),bytes=len(selected)))
        cases.append(dict(bits=chosen,metadata=metadata(raw),selections=filtered,
            owned_set1_pointer_tables=provisioned))
    grid=[]
    for lo,hi in ((-200.,200.),(-100.,10.),(-1.,1.)):
        values=(-3.4028234663852886e38,lo-1.,lo,-.5,-0.,0.,.5,hi,hi+1.,3.4028234663852886e38)
        env=serialize_envelope(Envelope(0x160003,(lo,hi,0.),values))
        body=struct.pack('<IdQ',1,0.,1<<8)+env
        raw=struct.pack('<4sI',b'FRM2',8+len(body))+body
        predicted,changed=predict_grid_normalization(raw);assert changed
        for route,ctor,reader,writer in (('DRS',0x7ec30,0x93000,0x92f40),('DSE5',0x12a670,0x106e20,0x107060)):
            path.write_bytes(raw);target=native.alloc(4096);native.fn(ctor,P,P)(target)
            code,_=scratch.invoke(reader,target,ALL,reading=True);assert code==0
            code,actual=scratch.invoke(writer,target,ALL);assert code==0 and actual==predicted
            grid.append(dict(route=route,lo=lo,hi=hi,values=list(values),changes=changed,all_values_exact=True))
    return dict(dse_sha256=SHA256,storage_cases=cases,grid_env_normalization=grid,
        scope='Native DRS reader/writer and sparse field selection; bounded DRS/DSE5 grid ENV clipping. No acoustic meaning inferred for unnamed blocks.')


def batch_contract(dll,root,mode,automatic,compact=0):
    observed={}
    def complete(native,context,collection):
        path=root/'scratch.frm2';path.touch(exist_ok=False);scratch=Scratch(native,path)
        code,raw_collection=scratch.invoke(0x92f40,collection,ALL);assert code==0
        sms=root/'raw.sms2';sms.write_bytes(raw_collection)
        summary=probe_sms2.summarize(sms,True)
        rows=[]
        for index,pointer in enumerate(frames(P.from_address(context+0xcd0).value)):
            code,raw=scratch.invoke(0x92f40,pointer,ALL);assert code==0
            parsed=layout.parse(raw);assert layout.serialize(parsed)==raw
            code,selected=scratch.invoke(0x92f40,pointer,MAIN);assert code==0
            assert selected==layout.serialize(parsed,MAIN)
            row=dict(index=index,mask=hex(parsed.mask),bytes=len(raw),selected_main_mask=hex(layout.parse(selected).mask),
                fields=[f.name for f in parsed.fields])
            # DSE5 is a distinct object layout; test its real reader/writer, not
            # a memcpy of the DRS object or an assumption from shared file names.
            target=native.alloc(0x328);native.fn(0x12a670,P,P)(target);path.write_bytes(raw)
            code,_=scratch.invoke(0x106e20,target,ALL,reading=True);assert code==0
            code,dse_raw=scratch.invoke(0x107060,target,ALL);assert code==0
            predicted,changes=predict_grid_normalization(raw)
            assert dse_raw==predicted,('DSE5 storage bridge',mode,automatic,index)
            row.update(dse5_storage_prediction_exact=True,dse5_bytes_unchanged=dse_raw==raw,
                dse5_normalized_fields=changes)
            loaded_target=native.alloc(4096);native.frame_ctor(loaded_target);path.write_bytes(raw)
            code,_=scratch.invoke(0x93000,loaded_target,ALL,reading=True);assert code==0
            code,loaded_raw=scratch.invoke(0x92f40,loaded_target,ALL);assert code==0
            assert loaded_raw==predicted,('DRS load normalization',mode,automatic,index)
            row.update(drs_storage_prediction_exact=True,drs_bytes_unchanged=loaded_raw==raw,
                drs_normalized_fields=changes)
            # Only the established ordinary configuration enters model derivation.
            if mode==4 and automatic==0 and compact==0:
                target=loaded_target
                cents=F.from_address(target+0xf0).value
                f0=440.*2**(cents/1200.) if cents>-1e30 else 0.
                if f0>0 and Q.from_address(target+0x50).value&128:
                    code=native.fn(0x71e00,Q,P,B,F,F)(target,0,22050.,f0);assert code==0
                    _,derived=scratch.invoke(0x92f40,target,ALL);after=layout.parse(derived)
                    _,final_select=scratch.invoke(0x92f40,target,MAIN)
                    assert final_select==layout.serialize(after,MAIN)
                    before_fields={f.name:f for f in layout.parse(loaded_raw).fields};after_fields={f.name:f for f in after.fields}
                    abc=struct.unpack('<3f',after_fields['bit22.ABC'].payload)
                    primary_count=struct.unpack('<I',after_fields['bit21.count'].payload)[0]
                    secondary_count=struct.unpack('<I',after_fields['bit22.count'].payload)[0]
                    assert all(math.isfinite(x) for x in abc) and 0<primary_count<=25 and secondary_count==1
                    assert len(after_fields['bit23.env'].payload)>28
                    row.update(derived_mask=hex(after.mask),derived_selected_mask=hex(layout.parse(final_select).mask),
                        model_bits_present=[b for b in (21,22,23) if after.mask&(1<<b)],
                        derived_abc=list(abc),primary_count=primary_count,secondary_count=secondary_count,
                        residual_env_bytes=len(after_fields['bit23.env'].payload),
                        prior_fields_changed=[name for name,f in before_fields.items()
                            if name in after_fields and f.payload!=after_fields[name].payload],
                        derived_model_payload_sha256=hashlib.sha256(b''.join(f.payload for f in after.fields
                            if f.mask & ((1<<21)|(1<<22)|(1<<23)))).hexdigest())
            rows.append(row)
        assert summary['frame_count']==len(rows)
        observed.update(sms2=summary,frames=rows,collection_sha256=hashlib.sha256(raw_collection).hexdigest())
    summary=batch(dll,automatic,1,'steady',220.,analysis_mode=mode,pitch_seed=220.,config_5a0=compact,
        pcm_override=owned_pcm('steady',220.),result_observer=complete)
    return dict(mode=mode,automatic=automatic,compact=compact,analysis_summary={k:v for k,v in summary.items() if k!='rows'},**observed)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True);parser.add_argument('--work-root',type=Path,required=True)
    parser.add_argument('--worker',choices=('config','storage','batch'))
    parser.add_argument('--mode',type=int,default=4);parser.add_argument('--automatic',type=int,default=0)
    parser.add_argument('--compact',type=int,default=0)
    args=parser.parse_args();root=args.work_root.resolve()
    if args.worker:
        result=(config_contract(args.dll) if args.worker=='config' else storage_contract(args.dll,root)
            if args.worker=='storage' else batch_contract(args.dll,root,args.mode,args.automatic,args.compact))
        analysis.write_json_atomic(root/'result.json',result);return
    root.mkdir()
    prior={p.name:analysis.file_sha256(p) for p in (ROOT.parent.parent/'voicebank_doc/evidence').glob('*.json')}
    analysis.write_json_atomic(root/'prior_evidence_sha256.json',prior)
    cases=[('config',0,0,0),('storage',0,0,0)]+[('batch',m,a,0) for m in range(6) for a in (0,1)]+[('batch',4,a,1) for a in (0,1)]
    results=[]
    for kind,mode,automatic,compact in cases:
        case=root/f'{kind}-{mode}-{automatic}-{compact}';case.mkdir()
        command=[sys.executable,'-B',str(Path(__file__).resolve()),'--worker',kind,'--dll',str(args.dll.resolve()),
            '--work-root',str(case),'--mode',str(mode),'--automatic',str(automatic),'--compact',str(compact)]
        with (case/'worker.log').open('xb') as log:
            finished=subprocess.run(command,stdout=log,stderr=subprocess.STDOUT,timeout=180)
        if finished.returncode:raise RuntimeError(f'Failed {case.name}: see worker.log')
        result=json.loads((case/'result.json').read_text());results.append(dict(case=case.name,result=result))
        analysis.write_json_atomic(root/'results.json',dict(dse_sha256=SHA256,cases=results))
        print(case.name+' passed',flush=True)


if __name__=='__main__':main()
