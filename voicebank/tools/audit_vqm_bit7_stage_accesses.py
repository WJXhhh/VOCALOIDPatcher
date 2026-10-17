"""Locate bounded bit7 accesses and the saved negative constant in pinned V6.

Pointer-shaped loads are untyped leads. This is local basic-block tracking,
not a whole-program absence proof. Only addresses and statistics are saved.
"""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import importlib
import json
from pathlib import Path
import struct
import sys

from audit_frm2_numeric import SHA256
from audit_vqm_feature_pointer_accesses import VOLATILE, parent_register
from audit_vqm_v4_module_leads import PE


SAVED_VALUE = -0.015800755470991135
REVIEWS = {
    0x31830: 'Aligned instructions: zero or copy Frame-shaped +b8 first scalar into '
             'feature+20; later read two source feature+20 values in pitch adjustment. '
             'Large caller context and actual runtime branch remain unverified.',
    0x636c0: 'Ghidra: generic content copy transfers existing feature fields, covered by 404.',
    0x6c68e: 'Tail runtime fragment of reviewed 6c3c0 categorization helper; fallback 1 and threshold read.',
    0x80324: 'Frame copy fragment: wide existing feature-block copy, covered by 404.',
    0x80b70: 'Ghidra: generic DRS interpolation and feature-block copy.',
    0xbf0ee: 'Ghidra resolves bf090: weighted feature-statistic accumulation into another object.',
    0xd2cc0: 'Aligned caller: initialize bit7 to 0, then write 646a0 regression slope divided by 10; verified in 415.',
    0x12bc76: 'Existing DSE5 feature-copy fragment, covered by 404.',
    0x12c8a4: 'Existing DSE5 feature-copy fragment, covered by 404.',
    0x1307b0: 'DSE5 interpolation and feature-block-copy layout; matches corresponding DRS operations.',
    0x13e7fe: 'Ghidra resolves 13e7a0: DSE5 weighted feature-statistic accumulation.',
}


def inspect(instructions, cs, image_base):
    aliases = {}
    rows = []
    counts = Counter()
    for number, op in enumerate(instructions):
        aliases = {r: a for r, a in aliases.items() if number - a[2] <= 64}
        for index, operand in enumerate(op.operands):
            if operand.type != cs.x86.X86_OP_MEM:
                continue
            register = op.reg_name(operand.mem.base)
            if register not in aliases:
                continue
            if operand.mem.index:
                counts['indexed_accesses_excluded'] += 1
                continue
            offset, root, born = aliases[register]
            location = offset + operand.mem.disp
            address_only = op.mnemonic == 'lea'
            if (0x20 <= location < 0x24 if address_only else
                    location < 0x24 and 0x20 < location + operand.size):
                read, write = bool(operand.access & cs.CS_AC_READ), bool(operand.access & cs.CS_AC_WRITE)
                if op.mnemonic.startswith(('mov', 'vmov')):
                    read, write = index != 0, index == 0
                rows.append(dict(root_load_rva=hex(root), instruction_rva=hex(op.address-image_base),
                    effective_offset=location, operand_bytes=operand.size,
                    operation=op.mnemonic, address_only=address_only,
                    read=read and not address_only, write=write and not address_only))
        if op.group(cs.CS_GRP_JUMP) or op.group(cs.CS_GRP_RET):
            aliases.clear()
            continue
        if op.group(cs.CS_GRP_CALL):
            aliases = {r: a for r, a in aliases.items() if r not in VOLATILE}
            continue
        value = None
        if len(op.operands) == 2 and op.operands[0].type == cs.x86.X86_OP_REG and op.operands[0].size == 8:
            source = op.operands[1]
            destination = op.reg_name(op.operands[0].reg)
            if op.mnemonic == 'mov' and source.type == cs.x86.X86_OP_REG:
                value = aliases.get(op.reg_name(source.reg))
            elif op.mnemonic == 'lea' and source.type == cs.x86.X86_OP_MEM and not source.mem.index:
                previous = aliases.get(op.reg_name(source.mem.base))
                if previous:
                    value = previous[0]+source.mem.disp, previous[1], previous[2]
            elif op.mnemonic in ('add', 'sub') and source.type == cs.x86.X86_OP_IMM and destination in aliases:
                previous = aliases[destination]
                value = previous[0]+source.imm*(1 if op.mnemonic=='add' else -1), previous[1], previous[2]
            elif (op.mnemonic == 'mov' and source.type == cs.x86.X86_OP_MEM and source.size == 8
                    and not source.mem.index and source.mem.disp in (0x128, 0x208)
                    and op.reg_name(source.mem.base) not in ('rsp','rbp','rip','')):
                value = 0, op.address-image_base, number
                counts['pointer_shaped_loads'] += 1
        for register in op.regs_access()[1]:
            aliases.pop(parent_register(op.reg_name(register)), None)
        if value is not None:
            aliases[op.reg_name(op.operands[0].reg)] = value
    return rows, counts


def owned_checks(decoder, cs):
    seed = '488b8128010000'
    cases = (
        ('direct_store', seed+'f30f114020', 1),
        ('direct_load', seed+'f30f104020', 1),
        ('dse5_pointer_load', '488b8108020000f30f114020', 1),
        ('address_only', seed+'488d4820', 1),
        ('split_address', seed+'488d7810f30f114710', 1),
        ('wide_overlap', seed+'0f114018', 1),
        ('last_byte', seed+'c6402301', 1),
        ('before_field', seed+'f30f11401c', 0),
        ('after_field', seed+'f30f114024', 0),
        ('partial_register', seed+'31c0f30f114020', 0),
        ('call_invalidates', seed+'e800000000f30f114020', 0),
        ('branch_invalidates', seed+'7500f30f114020', 0),
        ('indexed_excluded', seed+'f30f11448820', 0),
        ('other_pointer_offset', '488b8118010000f30f114020', 0),
    )
    for name, hex_bytes, expected in cases:
        rows, _ = inspect(list(decoder.disasm(bytes.fromhex(hex_bytes),0x1000)),cs,0)
        assert len(rows)==expected, name
        if name in ('direct_store','wide_overlap','last_byte'):
            assert rows[0]['write'] and not rows[0]['read'], name
        if name=='direct_load':
            assert rows[0]['read'] and not rows[0]['write'], name
        if name=='address_only':
            assert rows[0]['address_only'] and not rows[0]['read'] and not rows[0]['write'], name
    return len(cases)


def run(dll, decoder_dir):
    data=dll.read_bytes()
    assert hashlib.sha256(data).hexdigest()==SHA256
    pe=PE(data)
    sys.path.insert(0,str(decoder_dir.resolve()))
    cs=importlib.import_module('capstone')
    assert cs.__version__=='5.0.3'
    decoder=cs.Cs(cs.CS_ARCH_X86,cs.CS_MODE_64)
    decoder.detail=True
    fixtures=owned_checks(decoder,cs)
    directory,size=struct.unpack_from('<II',data,pe.directories+24)
    assert size%12==0 and size//12<=10000
    totals=Counter()
    rows=[]
    for start,end,_ in struct.iter_unpack('<III',data[pe.offset(directory):pe.offset(directory)+size]):
        assert 0<end-start<=0x40000
        totals['runtime_ranges']+=1
        raw=data[pe.offset(start):pe.offset(start)+end-start]
        if not any(struct.pack('<I',offset) in raw for offset in (0x128,0x208)):
            continue
        ops=list(decoder.disasm(raw,pe.image_base+start))
        totals['selected_ranges']+=1
        totals['decoded_instructions']+=len(ops)
        totals['undecoded_tail_bytes']+=len(raw)-sum(op.size for op in ops)
        leads,counts=inspect(ops,cs,pe.image_base)
        totals.update(counts)
        for row in leads:
            row.update(range_start_rva=hex(start),range_end_rva=hex(end))
            assert start in REVIEWS, ('Unreviewed range',hex(start))
            row['review']=REVIEWS[start]
        rows+=leads
        assert len(rows)<=200
    constant=[]
    for width,needle in ((4,struct.pack('<f',SAVED_VALUE)),(8,struct.pack('<d',SAVED_VALUE))):
        position=0
        while (position:=data.find(needle,position))>=0:
            constant.append(dict(width=width,rva=hex(pe.rva(position))))
            position+=1
            assert len(constant)<=100
    return dict(dse_sha256=SHA256,decoder_version=cs.__version__,owned_checks=fixtures,
        counts=dict(totals),bit7_access_leads=rows,saved_constant_exact_storage_leads=constant,
        scope='Local 64-instruction affine aliases from untyped +128/+208 loads; '
              'branch/return resets, ABI call and partial-register invalidation. '
              'Indexed addresses, function inputs/results, spills, cross-block/callee '
              'effects and code outside selected runtime ranges excluded. Exact float '
              'or double constant bytes searched in this file only; empty results do '
              'not exclude calculated or transformed constants or other producers.')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('dll','decoder-dir','output'):
        parser.add_argument('--'+name,type=Path,required=True)
    args=parser.parse_args()
    report=run(args.dll,args.decoder_dir)
    names=('audit_vqm_bit7_stage_accesses.py','audit_frm2_numeric.py',
           'audit_vqm_feature_pointer_accesses.py','audit_vqm_v4_module_leads.py')
    report['source_sha256']={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                             for name in names}
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(counts=report['counts'],leads=len(report['bit7_access_leads']),
        constant_leads=len(report['saved_constant_exact_storage_leads']))))


if __name__=='__main__':
    main()
