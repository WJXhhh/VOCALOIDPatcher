"""Bounded caller-constant audit and native config12/context assignment test.

Only two identified functions are decoded. Private native span execution uses
owned configuration curves/context; no installed instructions are modified.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import importlib
import json
import math
from pathlib import Path
import struct
import sys

from audit_frm2_numeric import F,I,Native,P,SHA256,f32

START,END=0x2D93A,0x2D98F


def decode(native,cs,start,end):
    raw=c.string_at(native.lib._handle+start,end-start)
    md=cs.Cs(cs.CS_ARCH_X86,cs.CS_MODE_64);md.detail=True
    ops=list(md.disasm(raw,start))
    assert sum(x.size for x in ops)==len(raw)
    return raw,ops


def provenance(native,cs):
    raw,ops=decode(native,cs,0x31830,0x3A66B)
    by={x.address:x for x in ops};pred={x.address:[] for x in ops}
    for op in ops:
        successors=[]
        if not op.group(cs.CS_GRP_RET):
            if op.group(cs.CS_GRP_JUMP):
                assert op.operands[0].type==cs.x86.X86_OP_IMM,'Unresolved indirect jump'
                successors.append(op.operands[0].imm)
                if op.mnemonic!='jmp':successors.append(op.address+op.size)
            else:successors.append(op.address+op.size)
        for target in successors:
            if target in pred:pred[target].append(op.address)
    def last(target,predicate):
        todo=list(pred[target]);seen=set();found=[]
        while todo:
            at=todo.pop()
            if at in seen:continue
            seen.add(at);op=by[at]
            if predicate(op):found.append(op)
            else:todo+=pred[at]
        assert found
        return sorted(found,key=lambda x:x.address)
    def register(reg):
        return lambda op:any(o.type==cs.x86.X86_OP_REG and op.reg_name(o.reg)==reg and o.access&cs.CS_AC_WRITE for o in op.operands)
    def memory(base,disp):
        return lambda op:any(o.type==cs.x86.X86_OP_MEM and o.mem.base==base and o.mem.disp==disp and o.access&cs.CS_AC_WRITE for o in op.operands)
    def loaded_constant(op):
        assert op.mnemonic=='movss' and op.operands[1].type==cs.x86.X86_OP_MEM
        operand=op.operands[1]
        assert operand.mem.base==cs.x86.X86_REG_RIP
        rva=op.address+op.size+operand.mem.disp
        return rva,F.from_address(native.lib._handle+rva).value
    threshold=last(0x3589B,memory(cs.x86.X86_REG_RBP,0x74))
    assert [x.address for x in threshold]==[0x33201]
    assert loaded_constant(by[0x331F9])==(0x5BFD24,600.)
    findings={}
    for target,reg,slot,value in ((0x35860,'xmm8',0x50,0.),(0x358E6,'xmm12',0x58,f32(-3.4028234663852886e38))):
        definitions=last(target,register(reg))
        for op in definitions:
            if op.mnemonic=='xorps':
                assert value==0. and op.operands[0].reg==op.operands[1].reg
            elif op.operands[1].mem.base==cs.x86.X86_REG_RSP:
                assert op.mnemonic=='movss' and op.operands[1].mem.disp==slot
            else:assert loaded_constant(op)[1]==value
        writes=[op for op in ops if memory(cs.x86.X86_REG_RSP,slot)(op)]
        assert len(writes)==1
        write=writes[0]
        regname=write.reg_name(write.operands[1].reg)
        origins=last(write.address,register(regname))
        assert len(origins)==1
        origin=origins[0]
        if value==0.:assert origin.mnemonic=='xorps'
        else:assert loaded_constant(origin)[1]==value
        findings[reg]=dict(read_rva=hex(target),value=value,stack_slot=hex(slot),
            stack_write_rva=hex(write.address),origin_rva=hex(origin.address),
            reaching_definition_rvas=[hex(x.address) for x in definitions])
    midpoint=[op for op in ops if memory(cs.x86.X86_REG_RSP,0x68)(op)]
    assert len(midpoint)==1
    origin=last(midpoint[0].address,register('xmm15'))
    assert len(origin)==1 and loaded_constant(origin[0])[1]==.5
    return dict(function_rva='0x31830',bytes=len(raw),instructions=len(ops),
        function_sha256=hashlib.sha256(raw).hexdigest(),threshold_cent=600.,
        threshold_write_rva='0x33201',threshold_load_rva='0x331f9',register_inputs=findings,
        midpoint=.5,midpoint_write_rva=hex(midpoint[0].address),midpoint_origin_rva=hex(origin[0].address),
        limits=['Intraprocedural direct control flow; calls assumed to obey Win64 nonvolatile register ABI.',
            'Direct stack-slot definitions only; not a whole-program escaped-alias proof.',
            'No indirect jumps occurred in the bounded decoded function.'])


def adapter(native,cs):
    raw,ops=decode(native,cs,START,END)
    # Context pointer RCX; address of config pointer RDX. Native constants:
    # XMM7=1, XMM12=.5. Preserve Win64 nonvolatile state and aligned calls.
    code=bytearray(bytes.fromhex('5641564883ec684889ce4989d6'))
    code+=bytes.fromhex('0f117c2440440f11642450')
    code+=bytes.fromhex('b80000803f660f6ef8b80000003f66440f6ee0')
    body=len(code);code+=raw
    code+=bytes.fromhex('0f107c2440440f106424504883c468415e5ec3')
    payloads={};fixups=[]
    for op in ops:
        offset=body+op.address-START
        if op.mnemonic=='call':
            target=op.operands[0].imm
            assert target in (0x2B050,0x217E08) and op.size==5
            payloads[target]=bytes.fromhex('ff2500000000')+struct.pack('<Q',native.lib._handle+target)
            fixups.append((offset+1,target))
        elif op.group(cs.CS_GRP_JUMP):assert START<=op.operands[0].imm<=END
        assert all(o.type!=cs.x86.X86_OP_MEM or o.mem.base!=cs.x86.X86_REG_RIP for o in op.operands)
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
    return c.CFUNCTYPE(None,P,P)(address),dict(start_rva=hex(START),end_exclusive_rva=hex(END),
        original_span_sha256=hashlib.sha256(raw).hexdigest(),instructions=len(ops))


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for key in ('dll','decoder-dir','output'):parser.add_argument('--'+key,type=Path,required=True)
    args=parser.parse_args();native=Native(args.dll)
    sys.path.insert(0,str(args.decoder_dir));cs=importlib.import_module('capstone')
    static=provenance(native,cs)
    invoke,span=adapter(native,cs);rows=[]
    for sample_rate in (44100.,48000.):
        for value in (-1000.,0.,.5,1.,55.,110.,440.,880.):
            config=native.alloc(0x99C0);c.c_ubyte.from_address(config).value=1
            curve=config+0x5D0+12*0xA0
            native.env_ctor(curve);native.cfg(curve,3,1,1,-2000.,2000.,0.,1,0,0)
            native.add(curve,0.,value)
            config_pointer=native.array(P,[config])
            context=native.alloc(0x2E48);F.from_address(context+0x2DD4).value=sample_rate
            before=c.string_at(context,0x2E48)
            assert native.fn(0x2B050,F,P,I,F)(config,12,0.)==value
            expected=max(1.,value)
            expected_period=math.floor(f32(f32(sample_rate/expected)+.5))
            invoke(context,config_pointer)
            actual=F.from_address(context+0x2E40).value
            period=I.from_address(context+0x2CC8).value
            assert actual==expected and period==expected_period
            after=c.string_at(context,0x2E48)
            a,b=bytearray(before),bytearray(after)
            for offset in (0x2E40,0x2CC8):a[offset:offset+4]=b[offset:offset+4]=bytes(4)
            assert a==b
            rows.append(dict(config12=value,sample_rate=sample_rate,context_scalar=actual,
                period_samples=period,independent_scalar=expected,independent_period=expected_period,exact=True))
    assert hashlib.sha256(c.string_at(native.lib._handle+START,END-START)).hexdigest()==span['original_span_sha256']
    result=dict(dse_sha256=SHA256,source_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        decoder_version=cs.__version__,static=static,constructor_span=span,cases=rows,
        summary=dict(cases=len(rows),exact=len(rows),threshold_cent=600.,guard_is_invalid_sentinel=True,
            context_source_config_id=12,original_span_unchanged=True),
        limits=['Constructor arithmetic span only; not the full constructor or final audio.',
            'Curve12 is evaluated at t=0; fixtures use one constant point and configuration type1.',
            'Frequency role follows sample_rate/period and downstream Hz calculations; original UI label remains unknown.',
            'No feature maker formula or DDI part pitch assignment recovered.'])
    args.output.write_text(json.dumps(result,indent=2,allow_nan=False)+'\n',encoding='utf-8')
    print(json.dumps(result['summary']))


if __name__=='__main__':main()
