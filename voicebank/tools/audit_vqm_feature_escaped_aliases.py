"""Bound heap stores and Windows x64 stack arguments of feature-shaped aliases.

Candidate aliases are deliberately untyped. Reports retain addresses/offsets,
not instruction text or bytes. No producer-absence conclusion is supported.
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
from audit_vqm_feature_calls_cfg import inspect, merge, runtime_groups, stack_location, transfer
from audit_vqm_v4_module_leads import PE


def analyze(ops, cs, base, stack_argument=None, argument_alias_offset=0):
    if not ops:
        return [], [], [], Counter()
    positions = {op.address:i for i, op in enumerate(ops)}
    states = [None]*len(ops)
    # Fifth and later caller slots: callee entry RSP has the return address.
    initial = {('stack', stack_argument+8):(argument_alias_offset, -1)} if stack_argument is not None else {}
    states[0] = (initial, 0, None)
    pending, queued, counts = deque([0]), {0}, Counter()
    while pending:
        index = pending.popleft()
        queued.remove(index)
        op = ops[index]
        outgoing = transfer(op, states[index], cs, base)
        counts['transfer_visits'] += 1
        assert counts['transfer_visits'] <= 500000
        successors = []
        if not op.group(cs.CS_GRP_RET):
            if op.group(cs.CS_GRP_JUMP):
                if op.operands[0].type == cs.x86.X86_OP_IMM and op.operands[0].imm in positions:
                    successors.append(positions[op.operands[0].imm])
                falls = op.mnemonic != 'jmp'
            else:
                falls = True
            if falls and index+1 < len(ops) and op.address+op.size == ops[index+1].address:
                successors.append(index+1)
        for successor in successors:
            new = merge(states[successor], outgoing)
            if new != states[successor]:
                states[successor] = new
                if successor not in queued:
                    queued.add(successor)
                    pending.append(successor)
    stores, calls, accesses = [], [], []
    for op, state in zip(ops, states):
        if state is None:
            continue
        aliases, sp, bp = state
        if stack_argument is not None:
            for number, operand in enumerate(op.operands):
                if operand.type != cs.x86.X86_OP_MEM:
                    continue
                slot = stack_location(operand, op, sp, bp, cs)
                if slot == stack_argument+8 and (number != 0 or not op.mnemonic.startswith('mov')):
                    counts['incoming_slot_read_operands'] += 1
                previous = aliases.get(op.reg_name(operand.mem.base))
                if previous is not None and previous[1] == -1:
                    counts['incoming_pointer_memory_operands'] += 1
            if op.group(cs.CS_GRP_CALL):
                for reg in ('rcx', 'rdx', 'r8', 'r9'):
                    value = aliases.get(reg)
                    if value is not None and value[1] == -1:
                        counts['incoming_pointer_register_argument_candidates'] += 1
        if (op.mnemonic == 'mov' and len(op.operands) == 2
                and op.operands[0].type == cs.x86.X86_OP_MEM and op.operands[0].size == 8
                and op.operands[1].type == cs.x86.X86_OP_REG):
            source = aliases.get(op.reg_name(op.operands[1].reg))
            target = op.operands[0]
            if source is not None and stack_location(target, op, sp, bp, cs) is None:
                stores.append(dict(instruction_rva=hex(op.address-base),
                    root_load_rva=hex(source[1]) if source[1] >= 0 else None,
                    feature_alias_offset=source[0], owner_base_register=op.reg_name(target.mem.base),
                    owner_index_register=op.reg_name(target.mem.index) if target.mem.index else None,
                    owner_index_scale=target.mem.scale, member_displacement=target.mem.disp,
                    scope='Untyped escaped pointer store; owner/class identity must be separately checked'))
        if op.group(cs.CS_GRP_CALL):
            target = op.operands[0]
            direct = target.imm-base if target.type == cs.x86.X86_OP_IMM else None
            if sp is not None:
                # Bounded ordinary outgoing argument area, excluding shadow space.
                for slot in range(32, 128, 8):
                    value = aliases.get(('stack', sp+slot))
                    if value is not None:
                        calls.append(dict(call_rva=hex(op.address-base),
                            target_rva=hex(direct) if direct is not None else None,
                            argument_index=slot//8+1, caller_rsp_offset=slot,
                            callee_entry_rsp_offset=slot+8, alias_offset=value[0],
                            root_load_rva=hex(value[1]) if value[1] >= 0 else None,
                            scope='Slot matches outgoing argument position, but may still be a private spill'))
        if stack_argument is not None:
            for number, operand in enumerate(op.operands):
                if operand.type != cs.x86.X86_OP_MEM:
                    continue
                previous = aliases.get(op.reg_name(operand.mem.base))
                if previous is None or previous[1] != -1:
                    continue
                if operand.mem.index:
                    counts['indexed_argument_operands_excluded'] += 1
                    continue
                location = previous[0]+operand.mem.disp
                address_only = op.mnemonic == 'lea'
                overlaps = 0x21c <= location < 0x220 if address_only else location < 0x220 and 0x21c < location+operand.size
                if overlaps:
                    writes = number == 0 if op.mnemonic.startswith('mov') else bool(operand.access & cs.CS_AC_WRITE)
                    accesses.append(dict(instruction_rva=hex(op.address-base), effective_offset=location,
                        bytes=operand.size, address_only=address_only, writes=writes and not address_only))
    counts['decoded_instructions'] = len(ops)
    counts['reachable_instructions'] = sum(state is not None for state in states)
    return stores, calls, accesses, counts


def owned_checks(decoder, cs):
    seed = '488b8128010000'
    cases = (
        ('heap_store', seed+'48898380010000c3', 1, 0),
        ('stack_fifth', seed+'4889442420e800000000c3', 0, 1),
        ('shadow_space_excluded', seed+'4889442410e800000000c3', 0, 0),
        ('partial_register_kill', seed+'31c048898380010000c3', 0, 0),
        ('stack_rebased', seed+'4883ec204889442430e800000000c3', 0, 1),
        ('short_store_excluded', seed+'898380010000c3', 0, 0),
    )
    result = []
    for name, raw, heap_count, stack_count in cases:
        ops = list(decoder.disasm(bytes.fromhex(raw), 0x1000))
        stores, calls, _, _ = analyze(ops, cs, 0x1000)
        assert len(stores) == heap_count and len(calls) == stack_count, (name, stores, calls)
        result.append(dict(case=name, passed=True))
    for raw, offset in (('488b442428f30f11801c020000c3', 32),
                        ('4883ec20488b442460f30f11801c020000c3', 56)):
        ops = list(decoder.disasm(bytes.fromhex(raw), 0x1000))
        _, _, accesses, _ = analyze(ops, cs, 0x1000, offset)
        assert len(accesses) == 1 and accesses[0]['writes']
        result.append(dict(case='stack_argument_write_'+str(offset), passed=True))
    ops = list(decoder.disasm(bytes.fromhex('488b442428f30f118004000000c3'), 0x1000))
    _, _, accesses, _ = analyze(ops, cs, 0x1000, 32, 0x218)
    assert len(accesses) == 1 and accesses[0]['effective_offset'] == 0x21c
    result.append(dict(case='nonzero_argument_alias_offset', passed=True))
    return result


def run(dll, decoder_dir):
    data = dll.read_bytes()
    assert hashlib.sha256(data).hexdigest() == SHA256
    pe = PE(data)
    sys.path.insert(0, str(decoder_dir.resolve()))
    cs = importlib.import_module('capstone')
    assert cs.__version__ == '5.0.3'
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    decoder.detail = True
    checks = owned_checks(decoder, cs)
    groups, records = runtime_groups(data, pe)
    cache, gaps = {}, []
    def decode(root):
        if root not in cache:
            ops = []
            for start, end, _ in groups[root]:
                part = list(decoder.disasm(data[pe.offset(start):pe.offset(start)+end-start], pe.image_base+start))
                consumed = sum(op.size for op in part)
                if consumed != end-start:
                    gaps.append(dict(group_rva=hex(root), fragment_rva=hex(start), undecoded_bytes=end-start-consumed))
                ops.extend(part)
            cache[root] = ops
        return cache[root]
    totals = Counter(exception_ranges=records, runtime_groups=len(groups))
    stores, calls = [], []
    for root, fragments in sorted(groups.items()):
        if not any(struct.pack('<I', offset) in data[pe.offset(start):pe.offset(start)+end-start]
                   for start, end, _ in fragments for offset in (0x128, 0x208)):
            continue
        owned, passed, _, counts = analyze(decode(root), cs, pe.image_base)
        stores += [dict(group_rva=hex(root), **row) for row in owned]
        calls += [dict(group_rva=hex(root), **row) for row in passed]
        totals.update(counts)
        totals['selected_groups'] += 1
    assert len(stores)+len(calls) < 3000
    requests = {(int(row['target_rva'], 16), row['caller_rsp_offset'], row['alias_offset']) for row in calls
                if row['target_rva'] and int(row['target_rva'], 16) in groups}
    assert len(requests) <= 300
    callees = []
    for target, slot, alias_offset in sorted(requests):
        _, onward, accesses, counts = analyze(decode(target), cs, pe.image_base, slot, alias_offset)
        _, register_calls, _ = inspect(decode(target), cs, pe.image_base,
            {('stack', slot+8):(alias_offset, -1)})
        register_calls = [row for row in register_calls if row['root_rva'] == '-0x1']
        for row in register_calls:
            if row['target_rva'] == '0x217c76':
                row['review'] = dict(classification='bulk_copy_lead', callee='memcpy',
                    basis='Same hash target already identified in evidence421; no new Ghidra query')
        callees.append(dict(target_rva=hex(target), caller_rsp_offset=slot,
            callee_entry_rsp_offset=slot+8, argument_alias_offset=alias_offset,
            bit17_cell_shaped_accesses=accesses,
            onward_stack_slots=onward, onward_register_calls=register_calls, counts=dict(counts)))
    # Actual fifth-slot consumption is established independently of cell overlap.
    assert len(callees) == 1 and callees[0]['target_rva'] == '0x553c0'
    assert callees[0]['counts']['incoming_slot_read_operands'] == 1
    assert callees[0]['onward_register_calls'] == [dict(root_rva='-0x1', call_rva='0x553fc',
        argument_register='rdx', alias_offset=0, target_rva='0x217c76', indirect_slot_rva=None,
        transfer_kind='call', literal_integer_arguments={}, review=dict(classification='bulk_copy_lead',
            callee='memcpy', basis='Same hash target already identified in evidence421; no new Ghidra query'))]
    sources = ('audit_vqm_feature_escaped_aliases.py', 'audit_vqm_feature_calls_cfg.py',
               'audit_vqm_feature_pointer_accesses.py', 'audit_vqm_v4_module_leads.py', 'audit_frm2_numeric.py')
    return dict(schema=1, dse_sha256=SHA256, decoder_version=cs.__version__, owned_checks=checks,
        counts=dict(totals), escaped_heap_stores=stores, outgoing_stack_slot_candidates=calls,
        direct_stack_callee_audits=callees, decode_gaps=gaps,
        source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        scope='Selected185 PE runtime groups with feature-shaped loads, direct CFG and existing must-alias rules. '
              'The sole fifth-slot candidate is consumed as the source argument of the existing memcpy target. '
              'Heap owner/class identity, indirect callees, tail calls, escaping addresses of spill cells and indexed accesses '
              'are not resolved here. Slots are bounded to fifth through sixteenth arguments; stack slots '
              'assume Windows x64 entry return-address layout. Register forwarding uses existing421 rules. '
              'No absence or producer inference from zero matches. No installed mutation or code/payload output.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'decoder-dir', 'output'):
        parser.add_argument('--'+name, type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll, args.decoder_dir)
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(counts=result['counts'], heap_stores=len(result['escaped_heap_stores']),
        stack_slots=len(result['outgoing_stack_slot_candidates']), callees=len(result['direct_stack_callee_audits']))))


if __name__ == '__main__':
    main()
