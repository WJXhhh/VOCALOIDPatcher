"""Bounded score-field access audit through branch joins and feature aliases.

Reuse the verified must-alias transfer, inspecting fields4/14/15 instead of17.
Only aggregate counts, RVAs and untyped access metadata are retained. This is
not a whole-program absence proof or recovery of an original maker protocol.
"""
from __future__ import annotations

import argparse
from collections import Counter, deque
import hashlib
import importlib
import json
from pathlib import Path
import struct
import sys

from audit_frm2_numeric import SHA256
from audit_vqm_feature_calls_cfg import merge, runtime_groups, transfer
from audit_vqm_feature_pointer_accesses import ARGUMENTS
from audit_vqm_v4_module_leads import PE


FIELDS = {4: (0x14,0x18),14: (0x20c,0x210),15: (0x214,0x218)}


def inspect(ops,cs,base,seeds=None):
    if not ops:
        return [],[],Counter()
    positions = {op.address:i for i,op in enumerate(ops)}
    states = [None]*len(ops)
    states[0] = (dict(seeds or {}),0,None)
    pending = deque([0]);queued = {0};counts = Counter()
    while pending:
        at = pending.popleft();queued.remove(at)
        op = ops[at]
        outgoing = transfer(op,states[at],cs,base)
        counts['transfer_visits'] += 1
        assert counts['transfer_visits'] <= 500000
        successors = []
        if not op.group(cs.CS_GRP_RET):
            if op.group(cs.CS_GRP_JUMP):
                if len(op.operands)==1 and op.operands[0].type==cs.x86.X86_OP_IMM:
                    target = positions.get(op.operands[0].imm)
                    if target is not None:
                        successors.append(target)
                if op.mnemonic!='jmp' and at+1<len(ops) and op.address+op.size==ops[at+1].address:
                    successors.append(at+1)
            elif at+1<len(ops) and op.address+op.size==ops[at+1].address:
                successors.append(at+1)
        for target in successors:
            new = merge(states[target],outgoing)
            if new != states[target]:
                states[target] = new
                if target not in queued:
                    pending.append(target);queued.add(target)
    rows,calls = [],[]
    for op,state in zip(ops,states):
        if state is None:
            counts['unreachable_instructions'] += 1
            continue
        aliases,_,_ = state
        for at,operand in enumerate(op.operands):
            if operand.type!=cs.x86.X86_OP_MEM:
                continue
            previous = aliases.get(op.reg_name(operand.mem.base))
            if previous is None:
                continue
            if operand.mem.index:
                counts['indexed_operands_excluded'] += 1
                continue
            location = previous[0]+operand.mem.disp
            address_only = op.mnemonic=='lea'
            fields = [bit for bit,(lo,hi) in FIELDS.items()
                      if (lo<=location<hi if address_only else location<hi and lo<location+operand.size)]
            if not fields:
                continue
            read,write = bool(operand.access&cs.CS_AC_READ),bool(operand.access&cs.CS_AC_WRITE)
            if op.mnemonic.startswith(('mov','vmov')):
                read,write = at!=0,at==0
            rows.append(dict(root_rva=hex(previous[1]),instruction_rva=hex(op.address-base),
                effective_offset=location,operand_bytes=operand.size,operation=op.mnemonic,
                address_only=address_only,read=read and not address_only,write=write and not address_only,
                fields=fields,exact_scalar=(not address_only and operand.size==4 and
                    any(location==lo for lo,_ in FIELDS.values()))))
        tail = op.mnemonic=='jmp' and (not op.operands or op.operands[0].type!=cs.x86.X86_OP_IMM
                                      or op.operands[0].imm not in positions)
        if op.group(cs.CS_GRP_CALL) or tail:
            target = None
            if len(op.operands)==1 and op.operands[0].type==cs.x86.X86_OP_IMM:
                target = hex(op.operands[0].imm-base)
            for register in ARGUMENTS:
                if register in aliases:
                    offset,root = aliases[register]
                    calls.append(dict(root_rva=hex(root),call_rva=hex(op.address-base),
                        argument_register=register,alias_offset=offset,target_rva=target,
                        transfer_kind='tail_jump' if tail else 'call'))
    counts['reached_instructions'] = sum(state is not None for state in states)
    return rows,calls,counts


def checks(decoder,cs):
    seed = '488b8128010000'
    store = 'f30f118014020000'
    cases = [
        ('confidence_store',seed+'f30f114014',[4]),
        ('ratio_store',seed+'f30f11800c020000',[14]),
        ('strength_store',seed+store,[15]),
        ('strength_load',seed+'f30f108014020000',[15]),
        ('cross_field_wide',seed+'0f11800c020000',[14,15]),
        ('adjacent_reserved',seed+'f30f118010020000',[]),
        ('split_pointer',seed+'488d7804f30f114710',[4]),
        ('same_branch_join',seed+'750190'+store,[15]),
        ('kill_branch_join',seed+'750231c0'+store,[]),
        ('spill_call_restore',seed+'4889442440e800000000488b442440'+store,[15]),
        ('shadow_call_invalidated',seed+'4889442410e800000000488b442410'+store,[]),
        ('partial_register_invalidated',seed+'31c0'+store,[]),
        ('confidence_address_only',seed+'488d4814',[4]),
        ('indexed_excluded',seed+'f30f11488c',[]),
    ]
    rows = []
    for name,code,fields in cases:
        raw = bytes.fromhex(code);ops = list(decoder.disasm(raw,0x1000))
        assert sum(op.size for op in ops)==len(raw)
        found,_,_ = inspect(ops,cs,0x1000)
        assert sorted({bit for row in found for bit in row['fields']})==fields,(name,found)
        if name=='confidence_address_only':
            assert found[0]['address_only'] and not found[0]['read'] and not found[0]['write']
        if name=='strength_load':
            assert found[0]['read'] and not found[0]['write']
        rows.append(dict(case=name,passed=True))
    return rows


def run(dll,decoder_dir):
    data = dll.read_bytes();assert hashlib.sha256(data).hexdigest()==SHA256
    pe = PE(data);assert pe.width==8 and pe.machine==0x8664
    sys.path.insert(0,str(decoder_dir.resolve()))
    cs = importlib.import_module('capstone');assert cs.__version__=='5.0.3'
    decoder = cs.Cs(cs.CS_ARCH_X86,cs.CS_MODE_64);decoder.detail = True
    owned = checks(decoder,cs)
    groups,records = runtime_groups(data,pe)
    counts = Counter(exception_ranges=records,runtime_groups=len(groups))
    rows,calls,gaps = [],[],{}
    def decode(root):
        result = []
        for start,end,_ in groups[root]:
            raw = data[pe.offset(start):pe.offset(start)+end-start]
            ops = list(decoder.disasm(raw,pe.image_base+start));covered = sum(op.size for op in ops)
            if covered!=len(raw):
                gaps[start] = dict(group_rva=hex(root),fragment_rva=hex(start),
                    undecoded_start_rva=hex(start+covered),undecoded_bytes=len(raw)-covered)
            result.extend(ops)
        return result
    for root,members in sorted(groups.items()):
        if not any(struct.pack('<I',offset) in data[pe.offset(a):pe.offset(a)+end-a]
                   for a,end,_ in members for offset in (0x128,0x208)):
            continue
        ops = decode(root);found,passed,stats = inspect(ops,cs,pe.image_base)
        counts.update(stats);counts['selected_groups'] += 1
        counts['selected_fragments'] += len(members);counts['decoded_instructions'] += len(ops)
        for row in found+passed:
            row['group_rva'] = hex(root)
        rows += found;calls += passed
        assert len(rows)+len(calls)<=3000
    requests = sorted({(int(row['target_rva'],16),row['argument_register'],row['alias_offset'])
        for row in calls if row['target_rva'] and int(row['target_rva'],16) in groups})
    visited = set();audits = []
    for depth in (1,2):
        additional = set()
        for target,register,offset in requests:
            if (target,register,offset) in visited:
                continue
            visited.add((target,register,offset));assert len(visited)<=200
            ops = decode(target)
            found,passed,stats = inspect(ops,cs,pe.image_base,{register:(offset,target)})
            for row in found+passed:
                row['group_rva'] = hex(target)
            audits.append(dict(target_rva=hex(target),argument_register=register,alias_offset=offset,
                depth=depth,decoded_instructions=len(ops),accesses=found,calls=passed,counts=dict(stats)))
            additional.update((int(row['target_rva'],16),row['argument_register'],row['alias_offset'])
                for row in passed if row['target_rva'] and int(row['target_rva'],16) in groups)
        requests = sorted(additional)
    sources = ('audit_vqm_score_field_writes_cfg.py','audit_vqm_feature_calls_cfg.py',
        'audit_vqm_feature_pointer_accesses.py','audit_vqm_v4_module_leads.py','audit_frm2_numeric.py')
    # Verify actual aligned direct calls, rather than deriving parentage from
    # a nearby byte pattern or a similarly named function.
    links = ((0xd2cc0,0xd580b,0xe60a0),(0xd2cc0,0xd5a80,0xe60a0),
        (0xe60a0,0xe61c5,0xe6a70),(0xe60a0,0xe67aa,0xe6a70),
        (0xd2cc0,0xd6ed9,0xf6220),(0xf6220,0xf6327,0xf7eb0),
        (0xcdde0,0xce6bc,0xe5ce0))
    for root,site,target in links:
        op = next(op for op in decode(root) if op.address-pe.image_base==site)
        assert op.group(cs.CS_GRP_CALL) and len(op.operands)==1
        assert op.operands[0].type==cs.x86.X86_OP_IMM and op.operands[0].imm-pe.image_base==target
    binding = next(op for op in decode(0xcdde0) if op.address-pe.image_base==0xce6c4)
    assert binding.mnemonic=='mov' and binding.operands[0].type==cs.x86.X86_OP_MEM
    assert binding.operands[0].mem.disp==0xc88 and binding.operands[0].size==8
    assert binding.operands[1].type==cs.x86.X86_OP_REG and binding.reg_name(binding.operands[1].reg)=='rax'
    roles = {0x80b70:'DRS interpolation; existing endpoint transfer, not a new score producer',
        0x1307b0:'DSE5 interpolation; existing endpoint transfer, not a new score producer',
        0xd2cc0:'Batch initialization, external fallback, raw strength and adjacent-array flux stages',
        0xd92e0:'Raw feature initialization and crossing-distribution strength; verified391',
        0xfb4a0:'Mode4 score contrast/moments and alternative branches; default verified395',
        0xe6a70:'Additional candidate-analysis confidence writes; linked from e60a0; numerical rule not verified here',
        0xf7eb0:'Additional confidence calculation/clamping, linked from f6220; numerical rule not verified here'}
    for row in rows:
        if row['write']:
            row['stage_review'] = roles.get(int(row['group_rva'],16),'unreviewed')
    return dict(schema=1,dse_sha256=SHA256,fields={str(bit):list(interval) for bit,interval in FIELDS.items()},
        source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        decoder_version=cs.__version__,owned_checks=owned,counts=dict(counts),accesses=rows,
        feature_calls=calls,direct_callee_audits=audits,decode_gaps=list(gaps.values()),
        aligned_direct_call_links=[dict(caller_rva=hex(root),site_rva=hex(site),target_rva=hex(target))
                                  for root,site,target in links],
        constructor_binding=dict(constructor_rva='0xcdde0',factory_rva='0xe5ce0',
            factory_call_rva='0xce6bc',return_store_rva='0xce6c4',context_offset='0xc88'),
        scalar_write_groups=sorted({row['group_rva'] for row in rows if row['write'] and row['exact_scalar']}),
        limits=['Pointer-shaped roots at Frame+128/+208 are untyped until independently verified',
            'Direct branch joins and private spills; indirect branches, indexed accesses and pointer returns not covered',
            'Two direct callee levels, four register arguments; not all ABI or whole-program paths',
            'Writes include initialization, copied endpoints and intermediate stages; not original maker identification',
            'No commercial arrays, PCM, proprietary disassembly or code bytes retained'],
        installed_files_unchanged=True,no_windows_temp=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True)
    parser.add_argument('--decoder-dir',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args = parser.parse_args();report = run(args.dll,args.decoder_dir)
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(counts=report['counts'],accesses=len(report['accesses']),
        scalar_write_groups=report['scalar_write_groups'],callee_audits=len(report['direct_callee_audits']))))


if __name__=='__main__':
    main()
