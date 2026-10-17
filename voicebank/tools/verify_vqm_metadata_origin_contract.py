"""Bounded native part transport and Frame/Timbre pitch normalization checks.

All serialized fixtures are owned. This does not identify a commercial maker
or treat a constructor/serializer as a pitch production algorithm.
"""
from __future__ import annotations

import argparse
import ctypes as c
from dataclasses import replace
import hashlib
import json
import math
from pathlib import Path
import struct

from audit_frm2_numeric import F,I,Native,P,Q,SHA256,f32
from probe_frm2 import parse_frame,serialize_frame
from verify_vqm_feature_dse5_roundtrip import owned_frame,read_frame,crt_functions


def raw32(value): return struct.pack('<f',value)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True)
    parser.add_argument('--scratch',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    args.scratch.mkdir(parents=True,exist_ok=True)
    part_path=args.scratch/'owned_vqm_part_metadata_469.bin'
    frame_path=args.scratch/'owned_vqm_pitch_encoding_469.frm2'
    if part_path.exists() or frame_path.exists(): raise ValueError('Owned fixture path already exists')
    native=Native(args.dll);crt=crt_functions()
    construct=native.fn(0x109B30,P,P,Q)
    read=native.fn(0x10B1A0,Q,P,P)
    write=native.fn(0x10B5D0,Q,P,P)
    getters=native.fn(0x110020,Q,P,c.c_char_p,I,P)
    append=native.fn(0x10FD10,I,P,P,c.c_char_p)
    root=native.alloc(0x168);native.fn(0x10FB30,P,P)(root)
    rows=[];loads=[]

    def part():
        at=native.alloc(0x280)
        assert construct(at,0)==at
        assert c.string_at(at+0x160,8)==bytes(8)
        assert raw32(F.from_address(at+0x16C).value)==raw32(.6)
        assert F.from_address(at+0x170).value==90.
        return at

    def stream(handle,alternate=0):
        at=native.alloc(0x80)
        P.from_address(at+8).value=handle
        I.from_address(at+0x1C).value=1
        I.from_address(at+0x34).value=alternate
        return at

    patterns=(0,0x80000000,0x3F000000,0x43DC0000,0x44960000,
              0xC4960000,0xFF7FFFFF,0x7F800000,0x7FC12345)
    try:
        for k,bits in enumerate(patterns):
            p1=struct.pack('<I',bits);p2=struct.pack('<I',patterns[-1-k])
            source=part()
            c.memmove(source+0x160,p1+p2,8)
            F.from_address(source+0x168).value=.125
            F.from_address(source+0x16C).value=.75
            F.from_address(source+0x170).value=123.
            source_before=c.string_at(source,0x280)
            handle=crt._wfopen(str(part_path.resolve()),'wb')
            if not handle: raise OSError('Cannot write owned metadata')
            out=stream(handle)
            try: assert write(source,out)==0
            finally: assert crt.fclose(handle)==0
            raw=part_path.read_bytes()
            assert len(raw)==30 and raw[10:18]==p1+p2
            assert c.string_at(source,0x280)==source_before
            part_path.unlink()
            for alternate in (0,1):
                data=raw if not alternate else raw[:10]+p1+raw32(.75)+p2
                with part_path.open('xb') as target: target.write(data)
                target=part()
                handle=crt._wfopen(str(part_path.resolve()),'rb')
                if not handle: raise OSError('Cannot read owned metadata')
                inp=stream(handle,alternate)
                try: assert read(target,inp)==0
                finally: assert crt.fclose(handle)==0
                assert Q.from_address(inp).value==len(data)
                assert c.string_at(target+0x160,8)==p1+p2
                assert F.from_address(target+0x16C).value==.75
                assert F.from_address(target+0x168).value==(.125 if not alternate else 0.)
                assert F.from_address(target+0x170).value==(123. if not alternate else 0.)
                rows.append(dict(pitch1_bits=p1.hex(),pitch2_bits=p2.hex(),alternate=bool(alternate),
                    consumed=len(data),pitch_fields_raw_exact=True,source_unchanged=True))
                part_path.unlink()
        base=parse_frame(owned_frame())
        for index,hz in enumerate((55.,110.,220.,330.,440.,520.6,880.)):
            physical=f32(hz)
            expected=f32(math.log(physical/440.)*1200./math.log(2.))
            frame=replace(base,f0_hz=physical)
            raw=serialize_frame(frame)
            # Keep the conservative serializer unchanged: the encoded fixture
            # changes only the owned mask and F0 bytes defined by the format.
            f0_offset=32+12*len(frame.component_frequencies_hz)+(4 if frame.mask&0x30 else 0)+8*len(frame.spectrum_amplitudes)
            assert struct.unpack_from('<f',raw,f0_offset)[0]==physical
            for encoded in (False,True):
                data=bytearray(raw)
                if encoded:
                    struct.pack_into('<Q',data,20,frame.mask|(1<<31))
                    struct.pack_into('<f',data,f0_offset,expected)
                with frame_path.open('xb') as target: target.write(data)
                loaded,_=read_frame(native,crt,frame_path)
                assert Q.from_address(loaded+0x150).value&(1<<31)
                observed=F.from_address(loaded+0x1D0).value
                assert raw32(observed)==raw32(expected),(physical,encoded,observed,expected)
                leaf=native.alloc(0x170)
                assert native.fn(0x10EC80,P,P,P)(leaf,loaded)==leaf
                assert append(root,leaf,b'pitch')==0
                result=F(999.)
                assert getters(root,b'pitch',index*2+int(encoded),c.byref(result))==0
                assert bytes(result)==raw32(expected)
                # Both bit13 properties remain raw values from the same loaded
                # feature block; their names do not imply production formulas.
                properties={}
                for name,offset in ((b'dynamics',0x21C),(b'opening',0x218)):
                    assert append(root,leaf,name)==0
                    assert getters(root,name,index*2+int(encoded),c.byref(result))==0
                    at=P.from_address(loaded+0x208).value+offset
                    assert bytes(result)==c.string_at(at,4)
                    properties[name.decode()]=result.value
                loads.append(dict(physical_hz=physical,input_encoded_cent=encoded,
                    native_cent=observed,timbre_pitch_cent_exact=True,properties_raw_exact=properties))
                frame_path.unlink()
    finally:
        part_path.unlink(missing_ok=True);frame_path.unlink(missing_ok=True)
    result=dict(dse_sha256=SHA256,source_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        constructor_rva='0x109b30',part_reader_rva='0x10b1a0',part_writer_rva='0x10b5d0',
        frame_reader_rva='0x106e20',timbre_getter_rva='0x110020',
        reviewed_native_edges=[
            dict(caller_rva='0x959b0',call_rva='0x95a11',callee_rva='0xc84f0',
                 role='DRS region finalization; collection statistic recalculation'),
            dict(caller_rva='0x8d6e0',call_rvas=['0x8e44c','0x8e467'],callee_rva='0xc84f0',
                 role='Adjacent region boundary movement; recalculates both collections'),
            dict(caller_rvas=['0x112220','0x113cc0','0x1148e0'],callee_rva='0x10b1a0',
                 role='Derived part readers; common pitch fields are input bytes'),
            dict(caller_rva='0x10ffb0',callee_rva='0x110020',
                 role='Reads first/last property values; not a producer')],
        part_type=dict(vtable_rva='0x225b28',rtti_name='.?AVCDBDataPhUPart@DSE5@@'),
        part_cases=rows,frame_cases=loads,
        summary=dict(part_transport_cases=len(rows),frame_normalization_cases=len(loads),
            raw_pitch_fields_exact=True,paired_frame_encodings_same_cent=True),
        limits=['Part constructor defaults and transport do not estimate original pitch1/pitch2.',
                'Timbre pitch reads normalized Frame cents; this is distinct from Growl part pitch1 Hz.',
                'The getter does not itself normalize manually assembled unnormalized Frames.',
                'dynamics/opening are raw property-axis names, not proof of VQMp maker units.',
                'No commercial maker metadata assignment or nonzero bit17 production is established.'])
    args.output.write_text(json.dumps(result,indent=2,allow_nan=False)+'\n',encoding='utf-8')
    print(json.dumps(result['summary']))


if __name__=='__main__':main()
