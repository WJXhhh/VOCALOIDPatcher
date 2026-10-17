"""Verify the bounded legacy-render bit8 reference-pitch consumer.

The private CPU adapter runs only 0x35b28..0x35bf3, with original native math
calls and owned Frame/context data. No installed instructions are modified.
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

START,END=0x35B28,0x35BF3
INVALID=f32(-3.4028234663852886e38)


def hz(value,encoded=True):
    if not encoded:return f32(value)
    if value==INVALID:return 0.
    return f32(float(f32(math.pow(2.,f32(value/1200.))))*440.)


def model(f0,encoded,reference,context_value,multiplier,enabled):
    result=f32(multiplier)
    if not enabled:return result,False
    scale=f32(context_value*f32(1.1))
    physical=hz(f0,encoded)
    if f0>INVALID and scale>f32(physical*result):
        result=f32(scale/physical)
    used=False
    if reference>INVALID and float(f32(f32(physical*result)/scale))<.99999:
        result=f32(scale/hz(reference))
        used=True
    return result,used


def adapter(native,decoder_dir):
    sys.path.insert(0,str(decoder_dir))
    cs=importlib.import_module('capstone')
    md=cs.Cs(cs.CS_ARCH_X86,cs.CS_MODE_64);md.detail=True
    raw=c.string_at(native.lib._handle+START,END-START)
    ops=list(md.disasm(raw,START))
    assert sum(x.size for x in ops)==len(raw)
    # Own ABI wrapper: frame/context/local-base/input-multiplier. Reserve the
    # original span's +0x58 stack constant apart from nonvolatile XMM saves.
    code=bytearray(bytes.fromhex('55575641564881ecb80000004889ce4989d64c89c5'))
    for reg,offset in ((6,0x70),(7,0x80),(9,0x90),(12,0xA0)):
        code+=(b'\x44' if reg>=8 else b'')+b'\x0f\x11'+bytes([0x84|((reg&7)<<3),0x24])+struct.pack('<i',offset)
    code+=bytes.fromhex('440f28e3f30f115d9cc7442458ffff7fff')
    body=len(code);code+=raw
    code+=bytes.fromhex('f30f10459c')
    for reg,offset in ((6,0x70),(7,0x80),(9,0x90),(12,0xA0)):
        code+=(b'\x44' if reg>=8 else b'')+b'\x0f\x10'+bytes([0x84|((reg&7)<<3),0x24])+struct.pack('<i',offset)
    code+=bytes.fromhex('4881c4b8000000415e5e5f5dc3')
    payloads={};fixups=[]
    for op in ops:
        offset=body+op.address-START
        if op.mnemonic=='call':
            target=op.operands[0].imm
            assert target in (0x4400,0x2890,0x28E0)
            assert op.size==5
            key=('call',target)
            payloads[key]=bytes.fromhex('ff2500000000')+struct.pack('<Q',native.lib._handle+target)
            fixups.append((offset+1,key))
        elif op.group(cs.CS_GRP_JUMP):
            target=op.operands[0].imm
            if target==0x35C1C:
                # This is the original disabled-mode exit from the selected
                # block. Return its unchanged multiplier to the test caller.
                assert op.imm_size==4
                fixups.append((offset+op.imm_offset,('exit',0)))
            else: assert START<=target<=END
        for operand in op.operands:
            if operand.type==cs.x86.X86_OP_MEM and operand.mem.base==cs.x86.X86_REG_RIP:
                target=op.address+op.size+operand.mem.disp
                assert target in (0x5BF258,0x5BF640)
                key=('constant',target)
                payloads[key]=c.string_at(native.lib._handle+target,8)
                fixups.append((offset+op.disp_offset,key))
    positions={('exit',0):body+len(raw)}
    for key,value in payloads.items():positions[key]=len(code);code+=value
    for offset,key in fixups:struct.pack_into('<i',code,offset,positions[key]-(offset+4))
    k=c.WinDLL('kernel32',use_last_error=True)
    k.VirtualAlloc.argtypes,k.VirtualAlloc.restype=[P,c.c_size_t,c.c_uint,c.c_uint],P
    k.VirtualProtect.argtypes,k.VirtualProtect.restype=[P,c.c_size_t,c.c_uint,P],c.c_int
    address=k.VirtualAlloc(None,len(code),0x3000,4)
    if not address:raise c.WinError(c.get_last_error())
    c.memmove(address,bytes(code),len(code));old=c.c_uint()
    if not k.VirtualProtect(address,len(code),0x20,c.byref(old)):raise c.WinError(c.get_last_error())
    return c.CFUNCTYPE(F,P,P,P,F)(address),dict(start_rva=hex(START),end_exclusive_rva=hex(END),
        original_span_sha256=hashlib.sha256(raw).hexdigest(),instructions=len(ops),decoder_version=cs.__version__)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True)
    parser.add_argument('--decoder-dir',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args();native=Native(args.dll)
    invoke,span=adapter(native,args.decoder_dir)
    rows=[]
    for (pitch,encoded),reference,context_value,multiplier,enabled in itertools.product(
        ((INVALID,True),(220.,False),(-1200.,True),(880.,False),(1200.,True)),
        (INVALID,-1200.,0.,1200.),(110.,440.),(.25,1.,4.),(False,True)):
        frame=native.alloc(0x148);feature=native.alloc(0x228);context=native.alloc(0x2E48)
        local=native.alloc(0x200);local_base=local+0x100
        P.from_address(frame+0x128).value=feature
        Q.from_address(frame+0x50).value=8192|((1<<31) if encoded else 0)
        F.from_address(frame+0xF0).value=pitch
        I.from_address(feature).value=1<<8
        F.from_address(feature+0x24).value=reference
        F.from_address(context+0x2E40).value=context_value
        I.from_address(local_base-0x7C).value=int(enabled)
        F.from_address(local_base-0x64).value=multiplier
        sources=[c.string_at(at,size) for at,size in ((frame,0x148),(feature,0x228),(context,0x2E48))]
        before_local=c.string_at(local,0x200)
        expected,used=model(pitch,encoded,reference,context_value,multiplier,enabled)
        actual=invoke(frame,context,local_base,multiplier)
        assert struct.pack('<f',actual)==struct.pack('<f',expected),(pitch,reference,actual,expected)
        assert sources==[c.string_at(at,size) for at,size in ((frame,0x148),(feature,0x228),(context,0x2E48))]
        after_local=c.string_at(local,0x200);off=0x100-0x64
        assert before_local[:off]==after_local[:off] and before_local[off+4:]==after_local[off+4:]
        rows.append(dict(stored_f0=pitch,encoded_cent=encoded,reference_cent=reference,
            context_scalar=context_value,input_multiplier=multiplier,enabled=enabled,
            independently_expected=expected,actual=actual,predicted_reference_used=used,
            float32_exact=True,source_and_other_local_bytes_unchanged=True))
    assert hashlib.sha256(c.string_at(native.lib._handle+START,END-START)).hexdigest()==span['original_span_sha256']
    result=dict(dse_sha256=SHA256,source_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        parent_function_rva='0x31830',caller_rva='0x31290',span=span,cases=rows,
        reference_feature_offset='0x24',frame_feature_pointer_offset='0x128',
        summary=dict(cases=len(rows),float32_exact=len(rows),
            predicted_reference_used=sum(r['predicted_reference_used'] for r in rows),original_span_unchanged=True),
        downstream_static_rvas=['0x369a6','0x37544','0x37e42','0x39b95','0x39ba2'],
        limits=['Original inline calculation is relocated in private process RAM; no installed code changes.',
            'Tests the identified reference multiplier block, not a full call of 0x31830 or final audio.',
            'The original disabled-mode branch to 0x35c1c exits through the private adapter return.',
            'Frame F0/feature pointer and context scalar are owned explicit inputs.',
            'Reference-use labels are independent predicates, not program-counter traces.',
            'No inference of bit8 original maker production, bit17 production or DDI pitch assignment.'])
    args.output.write_text(json.dumps(result,indent=2,allow_nan=False)+'\n',encoding='utf-8')
    print(json.dumps(result['summary']))


if __name__=='__main__':main()
