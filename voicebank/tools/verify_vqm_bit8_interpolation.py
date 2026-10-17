"""Verify a bounded native bit8 endpoint/interpolation and lower-bound span.

All Frame/context fixtures are owned. Native instructions are relocated only
in this disposable process; installed code and commercial payloads are intact.
Register inputs are explicit and do not claim natural caller reachability.
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

from audit_frm2_numeric import F,I,Native,P,SHA256,f32

START,END=0x35843,0x35964
INVALID=f32(-3.4028234663852886e38)


def model(a,b,weight,threshold,enabled,has_b,guard,context_value,pitch):
    result=f32(a)
    kind='copy'
    if has_b and weight>0. and enabled:
        difference=f32(b-a)
        if threshold>abs(difference):
            result=f32(f32(difference*weight)+a)
            kind='linear'
        else:
            result=f32(a if weight<.5 else b)
            kind='endpoint_a' if weight<.5 else 'endpoint_b'
    scale=f32(context_value*f32(1.1))
    limit=f32(math.log(float(scale)/440.)*1200./math.log(2.))
    raised_reference=result>guard and limit>result
    raised_pitch=pitch>guard and limit>pitch
    return (limit if raised_reference else result,limit if raised_pitch else pitch,
        kind,raised_reference,raised_pitch)


def adapter(native,decoder_dir):
    sys.path.insert(0,str(decoder_dir))
    cs=importlib.import_module('capstone')
    md=cs.Cs(cs.CS_ARCH_X86,cs.CS_MODE_64);md.detail=True
    raw=c.string_at(native.lib._handle+START,END-START)
    ops=list(md.disasm(raw,START))
    assert sum(x.size for x in ops)==len(raw)
    # Win64 pointer-to-fixture adapter. Preserve all nonvolatile registers used
    # by the span, shadow space and original +0x50/+0x68 local stack slots.
    code=bytearray(bytes.fromhex('55535756415441564881ec08010000'))
    saves=((6,0x80),(7,0x90),(8,0xA0),(11,0xB0),(12,0xC0))
    for reg,offset in saves:
        code+=(b'\x44' if reg>=8 else b'')+b'\x0f\x11'+bytes([0x84|((reg&7)<<3),0x24])+struct.pack('<i',offset)
    code+=bytes.fromhex('488b194c8b6108488b79104c8b7118488b69208b7128')
    code+=bytes.fromhex('f3440f10592cf3440f104130f3440f106134')
    code+=bytes.fromhex('8b4138894424688b413c89442450')
    body=len(code);code+=raw
    for reg,offset in saves:
        code+=(b'\x44' if reg>=8 else b'')+b'\x0f\x10'+bytes([0x84|((reg&7)<<3),0x24])+struct.pack('<i',offset)
    code+=bytes.fromhex('4881c408010000415e415c5e5f5b5dc3')
    payloads={};fixups=[]
    for op in ops:
        offset=body+op.address-START
        if op.mnemonic=='call':
            target=op.operands[0].imm
            assert target in (0x2880,0x2890) and op.size==5
            key=('call',target)
            payloads[key]=bytes.fromhex('ff2500000000')+struct.pack('<Q',native.lib._handle+target)
            fixups.append((offset+1,key))
        elif op.group(cs.CS_GRP_JUMP):assert START<=op.operands[0].imm<=END
        for operand in op.operands:
            if operand.type==cs.x86.X86_OP_MEM and operand.mem.base==cs.x86.X86_REG_RIP:
                target=op.address+op.size+operand.mem.disp
                assert target==0x5BF258
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
    return c.CFUNCTYPE(None,P)(address),dict(start_rva=hex(START),end_exclusive_rva=hex(END),
        original_span_sha256=hashlib.sha256(raw).hexdigest(),instructions=len(ops),decoder_version=cs.__version__)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for key in ('dll','decoder-dir','output'):parser.add_argument('--'+key,type=Path,required=True)
    args=parser.parse_args();native=Native(args.dll)
    invoke,span=adapter(native,args.decoder_dir)
    rows=[]
    pairs=((0.,0.),(0.,259.),(0.,260.),(0.,599.9999389648438),
        (0.,600.),(0.,600.0000610351562),(-1200.,1200.),(1200.,0.))
    for (a,b),weight,threshold,mode,guard,context_value,pitch in itertools.product(
        pairs,(0.,.25,.5,.75,1.),(260.,600.),('both','disabled','missing_b'),
        (INVALID,0.),(110.,880.),(0.,2400.)):
        frames=[native.alloc(0x148) for _ in range(3)]
        features=[native.alloc(0x228) for _ in range(3)]
        for frame,feature in zip(frames,features):P.from_address(frame+0x128).value=feature
        for feature,value in zip(features,(a,b,917.)):F.from_address(feature+0x24).value=value
        F.from_address(frames[2]+0xF0).value=pitch
        context=native.alloc(0x2E48);F.from_address(context+0x2E40).value=context_value
        local=native.alloc(0x200);local_base=local+0x100
        F.from_address(local_base+0x74).value=threshold
        fixture=native.alloc(64)
        for offset,value in zip((0,8,16,24,32),(frames[0],frames[1] if mode!='missing_b' else 0,frames[2],context,local_base)):
            P.from_address(fixture+offset).value=value
        I.from_address(fixture+40).value=mode!='disabled'
        for offset,value in ((44,weight),(48,0.),(52,guard),(56,.5),(60,0.)):
            F.from_address(fixture+offset).value=value
        objects=list(zip(frames,[0x148]*3))+list(zip(features,[0x228]*3))+[(context,0x2E48),(local,0x200),(fixture,64)]
        before=[c.string_at(at,size) for at,size in objects]
        expected,expected_pitch,kind,raised_ref,raised_pitch=model(a,b,weight,threshold,
            mode!='disabled',mode!='missing_b',guard,context_value,pitch)
        invoke(fixture)
        actual=F.from_address(features[2]+0x24).value
        actual_pitch=F.from_address(frames[2]+0xF0).value
        assert struct.pack('<ff',actual,actual_pitch)==struct.pack('<ff',expected,expected_pitch),(
            a,b,weight,threshold,mode,guard,context_value,pitch,expected,actual,expected_pitch,actual_pitch)
        for (at,size),saved in zip(objects,before):
            after=c.string_at(at,size)
            if at==frames[2]:assert saved[:0xF0]==after[:0xF0] and saved[0xF4:]==after[0xF4:]
            elif at==features[2]:assert saved[:0x24]==after[:0x24] and saved[0x28:]==after[0x28:]
            else:assert saved==after
        rows.append(dict(a=a,b=b,weight=weight,threshold=threshold,mode=mode,guard=guard,
            context_scalar=context_value,input_pitch=pitch,actual_reference=actual,actual_pitch=actual_pitch,
            independent_reference=expected,independent_pitch=expected_pitch,predicted_mode=kind,
            predicted_reference_raised=raised_ref,predicted_pitch_raised=raised_pitch,float32_exact=True))
    assert hashlib.sha256(c.string_at(native.lib._handle+START,END-START)).hexdigest()==span['original_span_sha256']
    counts={kind:sum(r['predicted_mode']==kind for r in rows) for kind in ('copy','linear','endpoint_a','endpoint_b')}
    result=dict(dse_sha256=SHA256,source_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        span=span,cases=rows,summary=dict(cases=len(rows),float32_exact=len(rows),predicted_modes=counts,
            predicted_reference_raised=sum(r['predicted_reference_raised'] for r in rows),
            predicted_pitch_raised=sum(r['predicted_pitch_raised'] for r in rows),original_span_unchanged=True),
        limits=['Private native span, not full renderer or caller reachability proof.',
            'Guard register and threshold are explicit inputs; caller provenance is a separate static question.',
            'No native branch conditions are replaced; calls go to original abs and Hz-to-cent helpers.',
            'No installed payloads, native instruction bytes or PCM are saved.',
            'No original feature production or DDI maker assignment claim.'])
    args.output.write_text(json.dumps(result,indent=2,allow_nan=False)+'\n',encoding='utf-8')
    print(json.dumps(result['summary']))


if __name__=='__main__':main()
