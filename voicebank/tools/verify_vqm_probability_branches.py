"""Check the inline default internal-bit19 kernel on owned feature inputs.

The reviewed probability-only span is relocated in private process memory.
The installed DLL is unchanged. No DLL bytes are persisted in source/evidence.
This isolates the inline kernel; it is not a full estimator or PCM producer test.
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

from audit_frm2_numeric import F, I, Native, P, SHA256, f32
from frm2_pitch_voicing import probability

START, END = 0xFF474, 0xFF737
OFFSETS = dict(feature4=4, feature8=8, feature12=12,
               confidence=0x14, strength=0x214, ratio=0x20C)


def adjacent(value, direction):
    raw = struct.unpack('<I', struct.pack('<f', value))[0]
    return struct.unpack('<f', struct.pack('<I', raw+direction))[0]


def build_kernel(native, decoder_dir):
    sys.path.insert(0, str(decoder_dir))
    cs = importlib.import_module('capstone')
    md = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    md.detail = True
    raw = c.string_at(native.lib._handle+START, END-START)
    ops = list(md.disasm(raw, START))
    assert sum(op.size for op in ops) == len(raw) and ops[-1].address == 0xFF72F
    # Own Win64 call adapter: preserve RDI/R12/R14 and used nonvolatile XMMs.
    entry = bytearray(bytes.fromhex('57415441564881ec900000004989ce4989d4'))
    for reg in range(6, 13):
        entry += (b'\x44' if reg >= 8 else b'') + b'\x0f\x11'
        entry += bytes([0x84 | ((reg & 7)<<3), 0x24]) + struct.pack('<i', 32+(reg-6)*16)
    fixups, own_values = [], []
    for reg, prefix, fmt, value in ((6,b'\xf3','f',1.), (7,b'\xf3','f',1.),
                                  (9,b'\xf2','d',-.5), (10,b'\xf3','f',f32(.2)),
                                  (11,b'\xf2','d',float(f32(.2)))):
        entry += prefix + (b'\x44' if reg>=8 else b'') + b'\x0f\x10'
        entry += bytes([0x05 | ((reg&7)<<3)])
        fixups.append((len(entry), ('own', len(own_values))))
        entry += bytes(4)
        own_values.append(struct.pack('<'+fmt, value).ljust(8,b'\0'))
    body_offset = len(entry)
    code = entry + raw
    for reg in range(6, 13):
        code += (b'\x44' if reg>=8 else b'') + b'\x0f\x10'
        code += bytes([0x84 | ((reg&7)<<3),0x24]) + struct.pack('<i',32+(reg-6)*16)
    code += bytes.fromhex('4881c490000000415e415c5fc3')
    payloads = {('own',k):v for k,v in enumerate(own_values)}
    calls, globals_read = set(), set()
    for op in ops:
        offset = body_offset+op.address-START
        if op.mnemonic == 'call':
            target = op.operands[0].imm
            assert target in (0x217D5A,0x217DCC), hex(target)
            assert op.size == 5
            key = ('call',target)
            payloads[key] = bytes.fromhex('ff2500000000')+struct.pack('<Q',native.lib._handle+target)
            fixups.append((offset+1,key))
            calls.add(target)
        elif op.group(cs.CS_GRP_JUMP):
            assert START <= op.operands[0].imm < END, (hex(op.address),hex(op.operands[0].imm))
        for operand in op.operands:
            if operand.type == cs.x86.X86_OP_MEM and operand.mem.base == cs.x86.X86_REG_RIP:
                target = op.address+op.size+operand.mem.disp
                assert 0x5B0000 <= target < 0x5C1000
                key = ('global',target)
                payloads[key] = c.string_at(native.lib._handle+target,8)
                fixups.append((offset+op.disp_offset,key))
                globals_read.add(target)
    positions = {}
    for key, value in payloads.items():
        positions[key] = len(code)
        code += value
    for offset,key in fixups:
        struct.pack_into('<i',code,offset,positions[key]-(offset+4))
    kernel = c.WinDLL('kernel32',use_last_error=True)
    kernel.VirtualAlloc.argtypes,kernel.VirtualAlloc.restype = [P,c.c_size_t,c.c_uint,c.c_uint],P
    kernel.VirtualProtect.argtypes,kernel.VirtualProtect.restype = [P,c.c_size_t,c.c_uint,P],c.c_int
    address = kernel.VirtualAlloc(None,len(code),0x3000,4)
    if not address: raise c.WinError(c.get_last_error())
    c.memmove(address,bytes(code),len(code))
    old = c.c_uint()
    if not kernel.VirtualProtect(address,len(code),0x20,c.byref(old)):
        raise c.WinError(c.get_last_error())
    assert c.string_at(native.lib._handle+START,END-START) == raw
    return c.CFUNCTYPE(None,P,P)(address), dict(start_rva=hex(START),end_exclusive_rva=hex(END),
        original_span_sha256=hashlib.sha256(raw).hexdigest(),instructions=len(ops),
        external_call_rvas=sorted(map(hex,calls)),constant_rvas=sorted(map(hex,globals_read)),
        decoder_version=cs.__version__)


def cases():
    baseline=dict(feature4=.05,feature8=.1,feature12=.001,confidence=.3,strength=0.,ratio=.8)
    rows=[('baseline',baseline)]
    for name,threshold in [('confidence',.2),('feature4',.1),('feature8',.2),
                           ('feature12',.00002),('ratio',.52),('strength',.015)]:
        for direction in (-1,0,1):
            values=dict(baseline)
            values[name]=adjacent(f32(threshold),direction)
            if name=='strength': values['feature8']=.5
            rows.append((f'{name}_threshold_{direction}',values))
    for noise,strength in itertools.product((.19,.2,.21,.5,1.),
        (0.,adjacent(f32(.015),-1),f32(.015),adjacent(f32(.015),1),.019,
         adjacent(f32(.023),-1),f32(.023),adjacent(f32(.023),1),.05)):
        rows.append(('noise_suppression',dict(baseline,feature8=noise,strength=strength)))
    for flags in itertools.product((False,True),repeat=5):
        rows.append(('penalty_combination',dict(baseline,
            confidence=.05 if flags[0] else .3,feature4=.3 if flags[1] else .05,
            feature8=.45 if flags[2] else .1,feature12=.000005 if flags[3] else .001,
            ratio=.44 if flags[4] else .8)))
    rows.append(('zero_energy_without_noise',dict(baseline,feature12=0.)))
    return rows


def branches(v):
    noise=v['feature8']
    suppression=0.
    if noise>f32(.2) and v['strength']>f32(.015):
        suppression=min(1.,f32(f32(v['strength']-f32(.015))/f32(.008)))
        noise=f32(noise*f32(1.-suppression))
    return dict(confidence=v['confidence']<f32(.2),crossing=v['feature4']>f32(.1),
        noise=noise>f32(.2),low_energy=v['feature12']<f32(.00002),ratio=v['ratio']<.52,
        suppression=suppression,effective_noise=noise)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True)
    parser.add_argument('--decoder-dir',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    native=Native(args.dll)
    invoke,span=build_kernel(native,args.decoder_dir)
    rows=[]
    for name,values in cases():
        inputs={k:f32(v) for k,v in values.items()}
        expected=probability(inputs)
        base=native.alloc(0x228+64);feature=base+32
        c.memset(base,0xA5,0x228+64)
        frame=native.alloc(0x148)
        P.from_address(frame+0x128).value=feature
        estimator=native.alloc(0x50)
        for key,offset in OFFSETS.items(): F.from_address(feature+offset).value=inputs[key]
        before=c.string_at(base,0x228+64)
        invoke(frame,estimator)
        actual=F.from_address(feature+0x224).value
        after=c.string_at(base,0x228+64)
        assert before[:32+0x224]==after[:32+0x224] and before[32+0x228:]==after[32+0x228:]
        assert struct.pack('<f',actual)==struct.pack('<f',expected),(name,inputs,actual,expected)
        rows.append(dict(case=name,inputs=inputs,independently_expected=expected,actual=actual,
                         branch_conditions=branches(inputs),float32_exact=True,other_bytes_unchanged=True))
    assert hashlib.sha256(c.string_at(native.lib._handle+START,END-START)).hexdigest()==span['original_span_sha256']
    summary=dict(cases=len(rows),float32_exact=len(rows),
        predicted_active_branch_counts={k:sum(r['branch_conditions'][k] for r in rows)
            for k in ('confidence','crossing','noise','low_energy','ratio')},original_dll_span_unchanged=True)
    result=dict(dse_sha256=SHA256,span=span,summary=summary,cases=rows,
        script_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        model_sha256=hashlib.sha256(Path(__file__).with_name('frm2_pitch_voicing.py').read_bytes()).hexdigest(),
        limits=['Default estimator fields +0x44/+0x48 are zero.',
                'Private in-memory relocation tests the original inline probability span, not the entire native estimator call.',
                'Branch labels/counts are independent predicates, not sampled program-counter telemetry.',
                'Owned feature inputs test conditional consumption, not natural PCM production of those features.',
                'No history/gating, nonfinite input or original commercial scores are claimed.'])
    args.output.write_text(json.dumps(result,indent=2,allow_nan=False)+'\n',encoding='utf-8')
    print(json.dumps(summary))


if __name__=='__main__': main()
