"""Bounded allocation-return leads for the 0x228-byte VQM feature object.

Installed instructions are decoded in memory only. Evidence contains RVAs,
counts and classifications, never instructions or decompiler output.
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


ALLOCATOR = 0x216454
SIZE = 0x228
REVIEWS = {
    0x2b500: ('DRS_feature_enable', 'Optional whole-block clear'),
    0x7b0f0: ('DRS_band_features', 'Whole-block clear when enabling bit13'),
    0x7b4b0: ('DRS_band_features_extended', 'Whole-block clear when enabling bit13'),
    0x7f1a0: ('DRS_Frame_allocate', 'Whole-block clear for a newly allocated feature'),
    0x87750: ('DRS_Frame_reader', 'Replace, clear and deserialize feature'),
    0xd2710: ('DRS_raw_frame_features', 'Whole-block clear on each invocation'),
    0xd92e0: ('DRS_raw_pcm_features', 'Whole-block clear on each invocation'),
    0xfb4a0: ('DRS_batch_analysis', 'Whole-block clear when enabling bit13'),
    0x12a7b0: ('DSE5_Frame_allocate', 'Whole-block clear for a newly allocated feature'),
    0x1363a0: ('DSE5_Frame_reader', 'Clear and deserialize new or reused feature'),
}


def direct_target(op, cs, image_base):
    if (op.group(cs.CS_GRP_CALL) and len(op.operands) == 1
            and op.operands[0].type == cs.x86.X86_OP_IMM):
        return op.operands[0].imm - image_base
    return None


def inspect(instructions, cs, image_base, allocator=ALLOCATOR):
    if not instructions:
        return [], [], [], [], Counter()
    positions = {op.address: i for i, op in enumerate(instructions)}
    states = [None] * len(instructions)
    states[0] = ({}, 0, None)
    pending = deque([0])
    queued = {0}
    counts = Counter()
    while pending:
        number = pending.popleft()
        queued.remove(number)
        op = instructions[number]
        outgoing = transfer(op, states[number], cs, image_base)
        aliases, sp, bp = outgoing
        if (direct_target(op, cs, image_base) == allocator
                and states[number][0].get(('literal', 'rcx')) == SIZE):
            aliases['rax'] = (0, op.address - image_base)
        counts['transfer_visits'] += 1
        if counts['transfer_visits'] > 500000:
            raise ValueError('Allocation CFG exceeds bounded convergence budget')
        successors = []
        if not op.group(cs.CS_GRP_RET):
            if op.group(cs.CS_GRP_JUMP):
                if len(op.operands) == 1 and op.operands[0].type == cs.x86.X86_OP_IMM:
                    target = positions.get(op.operands[0].imm)
                    if target is not None:
                        successors.append(target)
                if (op.mnemonic != 'jmp' and number + 1 < len(instructions)
                        and op.address + op.size == instructions[number + 1].address):
                    successors.append(number + 1)
            elif (number + 1 < len(instructions)
                  and op.address + op.size == instructions[number + 1].address):
                successors.append(number + 1)
        for successor in successors:
            combined = merge(states[successor], (aliases, sp, bp))
            if combined != states[successor]:
                states[successor] = combined
                if successor not in queued:
                    queued.add(successor)
                    pending.append(successor)
    allocations = []
    for op, state in zip(instructions, states):
        if state is not None and direct_target(op, cs, image_base) == allocator:
            counts['reached_allocator_calls'] += 1
            if state[0].get(('literal', 'rcx')) == SIZE:
                allocations.append(dict(call_rva=hex(op.address - image_base),
                    allocator_rva=hex(allocator), allocation_bytes=SIZE))
    roots = {int(row['call_rva'], 16) for row in allocations}
    attachments, accesses, calls = [], [], []
    for op, state in zip(instructions, states):
        if state is None:
            continue
        aliases, _, _ = state
        rva = op.address - image_base
        if (op.mnemonic == 'mov' and len(op.operands) == 2
                and op.operands[0].type == cs.x86.X86_OP_MEM
                and op.operands[0].size == 8
                and op.operands[0].mem.disp in (0x128, 0x208)
                and not op.operands[0].mem.index
                and op.reg_name(op.operands[0].mem.base) not in ('rsp', 'rbp', 'rip', '')
                and op.operands[1].type == cs.x86.X86_OP_REG):
            alias = aliases.get(op.reg_name(op.operands[1].reg))
            if alias is not None and alias[0] == 0 and alias[1] in roots:
                attachments.append(dict(allocation_call_rva=hex(alias[1]),
                    instruction_rva=hex(rva), frame_pointer_offset=hex(op.operands[0].mem.disp)))
        for index, operand in enumerate(op.operands):
            if operand.type != cs.x86.X86_OP_MEM:
                continue
            alias = aliases.get(op.reg_name(operand.mem.base))
            if alias is None or alias[1] not in roots:
                continue
            if operand.mem.index:
                counts['indexed_allocation_operands_excluded'] += 1
                continue
            offset = alias[0] + operand.mem.disp
            address_only = op.mnemonic == 'lea'
            if (0x21c <= offset < 0x220 if address_only else
                    offset < 0x220 and 0x21c < offset + operand.size):
                read = bool(operand.access & cs.CS_AC_READ)
                write = bool(operand.access & cs.CS_AC_WRITE)
                if op.mnemonic.startswith(('mov', 'vmov')):
                    read, write = index != 0, index == 0
                accesses.append(dict(allocation_call_rva=hex(alias[1]),
                    instruction_rva=hex(rva), effective_offset=offset,
                    operand_bytes=operand.size, address_only=address_only,
                    read=read and not address_only, write=write and not address_only))
        if op.group(cs.CS_GRP_CALL):
            if direct_target(op, cs, image_base) == 0x2b500:
                calls.append(dict(allocation_call_rva=None, call_rva=hex(rva),
                    target_rva='0x2b500', kind='feature_enable_protocol',
                    initialize_argument=aliases.get(('literal', 'rdx'))))
            for register in ARGUMENTS:
                alias = aliases.get(register)
                if alias is not None and alias[1] in roots:
                    target = direct_target(op, cs, image_base)
                    row = dict(allocation_call_rva=hex(alias[1]), call_rva=hex(rva),
                        target_rva=None if target is None else hex(target),
                        argument_register=register, alias_offset=alias[0])
                    if target == 0x217c94 and register == 'rcx':
                        value = aliases.get(('literal', 'rdx'))
                        size = aliases.get(('literal', 'r8'))
                        row.update(kind='memset', zero_value=value, span_bytes=size,
                            whole_feature_clear=alias[0] == 0 and value == 0 and size == SIZE)
                    calls.append(row)
    counts['reached_instructions'] = sum(x is not None for x in states)
    return allocations, attachments, accesses, calls, counts


def owned_checks(decoder, cs):
    # Generated instruction fixtures, independent of installed instruction bytes.
    allocate = 'b928020000e8f6ff0f00'  # At RVA5, call the owned RVA0x100000.
    store = 'f30f11801c020000'
    cases = [
        ('direct_return_store', allocate + store, 1, 0),
        ('attach_before_store', allocate + '48898728010000' + store, 1, 1),
        ('affine_return_store', allocate + '4883c020f30f1180fc010000', 1, 0),
        ('spill_restore', allocate + '488944244031c0488b442440' + store, 1, 0),
        ('spill_partial_kill', allocate + '4889442440c64424410131c0488b442440' + store, 0, 0),
        ('conditional_kill_join', allocate + '750231c0' + store, 0, 0),
        ('callee_kills_volatile_return', allocate + 'e800000000' + store, 0, 0),
        ('nonvolatile_survives_call', allocate + '4889c3e800000000f30f11831c020000', 1, 0),
        ('unreachable_return_use', allocate + 'c3' + store, 0, 0),
        ('partial_register_kill', allocate + '31c0' + store, 0, 0),
        ('non_feature_size', 'b924020000e8f6ff0f00' + store, 0, 0),
    ]
    rows = []
    for name, code, overlaps, attachments in cases:
        raw = bytes.fromhex(code)
        decoded = list(decoder.disasm(raw, 0))
        assert sum(op.size for op in decoded) == len(raw), name
        found, attached, accesses, _, _ = inspect(decoded, cs, 0, 0x100000)
        assert len(found) == (0 if name == 'non_feature_size' else 1), name
        assert len(accesses) == overlaps and len(attached) == attachments, (name, accesses, attached)
        rows.append(dict(case=name, bit17_overlaps=overlaps, attachments=attachments, passed=True))
    raw = bytes.fromhex(allocate + '4889c131d241b828020000')
    raw += b'\xe8' + struct.pack('<i', 0x217c94 - len(raw) - 5)
    _, _, _, calls, _ = inspect(list(decoder.disasm(raw, 0)), cs, 0, 0x100000)
    assert len(calls) == 1 and calls[0]['whole_feature_clear']
    rows.append(dict(case='returned_pointer_whole_block_clear', passed=True))
    raw = bytes.fromhex('ba01000000')
    raw += b'\xe8' + struct.pack('<i', 0x2b500 - len(raw) - 5)
    _, _, _, calls, _ = inspect(list(decoder.disasm(raw, 0)), cs, 0, 0x100000)
    assert len(calls) == 1 and calls[0]['initialize_argument'] == 1
    rows.append(dict(case='enable_initialization_argument', passed=True))
    return rows


def run(dll, decoder_dir):
    data = dll.read_bytes()
    assert hashlib.sha256(data).hexdigest() == SHA256
    pe = PE(data)
    assert pe.machine == 0x8664 and pe.width == 8
    sys.path.insert(0, str(decoder_dir.resolve()))
    cs = importlib.import_module('capstone')
    assert cs.__version__ == '5.0.3'
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    decoder.detail = True
    fixtures = owned_checks(decoder, cs)
    groups, fragments = runtime_groups(data, pe)
    totals = Counter(runtime_groups=len(groups), exception_ranges=fragments)
    candidates, gaps, protocol_calls = [], [], []
    for root, members in sorted(groups.items()):
        instructions = []
        for start, end, _ in members:
            raw = data[pe.offset(start):pe.offset(start) + end - start]
            decoded = list(decoder.disasm(raw, pe.image_base + start))
            covered = sum(op.size for op in decoded)
            if covered != len(raw):
                gaps.append(dict(group_rva=hex(root), fragment_rva=hex(start),
                    undecoded_start_rva=hex(start + covered), undecoded_bytes=len(raw) - covered))
            instructions.extend(decoded)
        totals['decoded_instructions'] += len(instructions)
        allocator_calls = sum(direct_target(op, cs, pe.image_base) == ALLOCATOR for op in instructions)
        enable_calls = any(direct_target(op, cs, pe.image_base) == 0x2b500 for op in instructions)
        if not allocator_calls and not enable_calls:
            continue
        totals['groups_with_direct_allocator_calls'] += bool(allocator_calls)
        totals['direct_allocator_calls'] += allocator_calls
        allocations, attachments, accesses, calls, counts = inspect(instructions, cs, pe.image_base)
        totals.update(counts)
        protocol_calls.extend(dict(group_rva=hex(root), **row) for row in calls
                              if row.get('kind') == 'feature_enable_protocol')
        if root == 0x31830:
            by_rva = {op.address - pe.image_base: op for op in instructions}
            copy_shapes = [
                (0x399e2, 'rax', 'r15', 0x128, 8, False),
                (0x399e9, 'rcx', 'r12', 0x128, 8, False),
                (0x399f1, 'eax', 'rax', 0x18, 4, False),
                (0x399f4, 'eax', 'rcx', 0x18, 4, True),
            ]
            for rva, register, base, offset, size, writing in copy_shapes:
                op = by_rva[rva]
                assert op.mnemonic == 'mov' and len(op.operands) == 2
                memory, scalar = op.operands if writing else reversed(op.operands)
                assert memory.type == cs.x86.X86_OP_MEM and memory.size == size
                assert op.reg_name(memory.mem.base) == base and not memory.mem.index
                assert memory.mem.disp == offset
                assert scalar.type == cs.x86.X86_OP_REG and op.reg_name(scalar.reg) == register
            next(row for row in protocol_calls if row['call_rva'] == '0x399dd').update(
                post_call_copy=dict(read_rva='0x399f1', write_rva='0x399f4',
                    feature_offset='0x18', internal_bit=5, bytes=4,
                    overlaps_bit17=False, basis='Bounded decoded operands at the pinned RVAs'))
        for allocation in allocations:
            call_rva = allocation['call_rva']
            attached = [row for row in attachments if row['allocation_call_rva'] == call_rva]
            following = next((op for op in instructions if op.address ==
                pe.image_base + int(call_rva, 16) + 5), None)
            allocation.update(group_rva=hex(root), attachments=attached,
                next_instruction_attaches=bool(following is not None and any(
                    int(row['instruction_rva'], 16) == following.address - pe.image_base for row in attached)),
                returned_alias_bit17_overlaps=[row for row in accesses if row['allocation_call_rva'] == call_rva],
                returned_alias_calls=[row for row in calls if row['allocation_call_rva'] == call_rva],
                role=REVIEWS.get(root, ('unreviewed', 'unreviewed'))[0],
                initialization=REVIEWS.get(root, ('unreviewed', 'unreviewed'))[1])
            candidates.append(allocation)
        if len(candidates) > 100:
            raise ValueError('Allocation report exceeds candidate bound')
    assert {int(row['group_rva'], 16) for row in candidates} == set(REVIEWS)
    assert len(candidates) == 10
    assert all(row['next_instruction_attaches'] and len(row['attachments']) == 1 for row in candidates)
    assert {int(row['call_rva'], 16) for row in protocol_calls} == {0x3583e, 0x399dd}
    assert {int(row['call_rva'], 16): row['initialize_argument'] for row in protocol_calls} == {
        0x3583e: 1, 0x399dd: 0}
    paths = [Path(__file__), *(Path(__file__).with_name(name) for name in (
        'audit_vqm_feature_calls_cfg.py', 'audit_vqm_feature_pointer_accesses.py',
        'audit_vqm_v4_module_leads.py', 'audit_frm2_numeric.py'))]
    return dict(schema=1, dse_sha256=SHA256, decoder_version=cs.__version__,
        source_sha256={p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in paths},
        counts=dict(totals), owned_checks=fixtures, allocation_sites=candidates, decode_gaps=gaps,
        enable_helper_direct_callers=protocol_calls,
        review_basis='Pinned Ghidra function metadata and bounded per-function queries; large fb4a0 uses bounded local decode instead of unsuccessful decompilation',
        conclusion='All ten recognized 0x228 allocation returns attach to Frame in the next instruction. No unattached producer window exists at these sites.',
        limits=['Only the pinned allocator at direct call sites with must-known RCX=0x228 is seeded',
                'PE unwind groups, direct CFG edges, affine registers and private spills only; indirect edges and indexed memory are excluded',
                'Frame reloads retain their original load root; this audit complements 421 and does not replace it',
                'A lost must-alias, unrecognized allocator, assembly-only range or undecoded tail may hide other paths',
                'The Ghidra data reference to the enable helper does not resolve indirect callers',
                'Allocation-size coincidence alone is untyped; the ten sites were individually checked against feature ownership',
                'No whole-program producer-absence claim or bit17 numerical formula follows from this audit'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ('dll', 'decoder-dir', 'output'):
        parser.add_argument('--' + option, type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.decoder_dir)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(counts=report['counts'], allocation_sites=len(report['allocation_sites']),
        immediate_attachments=sum(row['next_instruction_attaches'] for row in report['allocation_sites']))))


if __name__ == '__main__':
    main()
