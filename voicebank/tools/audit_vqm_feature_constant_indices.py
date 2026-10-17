"""Refine indexed bit17 leads with bounded integer-literal propagation.

This complements the untyped entry/Frame-load audit423. Installed instructions
stay in memory; reports retain RVAs, offsets, classifications and counters.
"""
from __future__ import annotations

import argparse
from collections import Counter, deque
import hashlib
import importlib
import json
from pathlib import Path
import re
import struct
import sys

from audit_frm2_numeric import SHA256
from audit_vqm_feature_calls_cfg import merge, runtime_groups, transfer
from audit_vqm_feature_entry_returns import reviewed_switch_data, analyze as previous_analyze
from audit_vqm_feature_pointer_accesses import ARGUMENTS, parent_register
from audit_vqm_v4_module_leads import PE, V4_SHA256


def literal(operand, op, aliases, cs):
    if operand.type == cs.x86.X86_OP_IMM:
        return operand.imm
    if operand.type != cs.x86.X86_OP_REG:
        return None
    name = op.reg_name(operand.reg)
    value = aliases.get(('literal', parent_register(name)))
    if value is None:
        return None
    if name in ('ah', 'bh', 'ch', 'dh'):
        return (value >> 8) & 255
    return value & ((1 << (operand.size * 8)) - 1)


def index_value(operand, op, aliases):
    if not operand.mem.index:
        return 0
    value = aliases.get(('literal', parent_register(op.reg_name(operand.mem.index))))
    if value is not None and value >= 1 << 63:
        value -= 1 << 64
    return value


def extended_transfer(op, state, cs, image_base):
    outgoing = transfer(op, state, cs, image_base)
    before = state[0]
    result = outgoing[0]
    if len(op.operands) < 2 or op.operands[0].type != cs.x86.X86_OP_REG:
        return outgoing
    destination = op.operands[0]
    if destination.size not in (4, 8):
        return outgoing
    register = parent_register(op.reg_name(destination.reg))
    width = destination.size * 8
    mask = (1 << width) - 1
    value = None
    if op.mnemonic in ('shl', 'sal', 'shr', 'sar'):
        current = before.get(('literal', register))
        count = literal(op.operands[1], op, before, cs)
        if current is not None and count is not None:
            count &= 63 if width == 64 else 31
            current &= mask
            if op.mnemonic == 'sar' and current >= 1 << (width - 1):
                current -= 1 << width
            value = current << count if op.mnemonic in ('shl', 'sal') else current >> count
    elif op.mnemonic in ('and', 'or'):
        current = before.get(('literal', register))
        other = literal(op.operands[1], op, before, cs)
        if op.mnemonic == 'and' and other == 0:
            value = 0
        elif current is not None and other is not None:
            value = current & other if op.mnemonic == 'and' else current | other
    elif op.mnemonic == 'imul':
        left = (literal(op.operands[1], op, before, cs) if len(op.operands) == 3 else
                before.get(('literal', register)))
        right = literal(op.operands[-1], op, before, cs)
        if left is not None and right is not None:
            value = left * right
    elif op.mnemonic in ('movzx', 'movsxd', 'movsx'):
        source = op.operands[1]
        current = literal(source, op, before, cs)
        if current is not None:
            if op.mnemonic != 'movzx' and current >= 1 << (source.size * 8 - 1):
                current -= 1 << (source.size * 8)
            value = current
    if value is not None:
        result[('literal', register)] = value & mask
    source = op.operands[1]
    if (op.mnemonic == 'mov' and destination.size == 8 and source.type == cs.x86.X86_OP_MEM
            and source.size == 8 and source.mem.index
            and op.reg_name(source.mem.base) not in ('rsp', 'rbp', 'rip', '')):
        index = index_value(source, op, before)
        if index is not None and source.mem.disp + index * source.mem.scale in (0x128, 0x208):
            result[op.reg_name(destination.reg)] = (0, op.address - image_base)
    return outgoing


def analyze(instructions, cs, image_base, switches=None, unresolved=None):
    if not instructions:
        return [], Counter()
    positions = {op.address: i for i, op in enumerate(instructions)}
    states = [None] * len(instructions)
    states[0] = ({register: (0, -i - 1) for i, register in enumerate(ARGUMENTS)}, 0, None)
    pending, queued = deque([0]), {0}
    counts = Counter()
    while pending:
        number = pending.popleft()
        queued.remove(number)
        op = instructions[number]
        outgoing = extended_transfer(op, states[number], cs, image_base)
        counts['transfer_visits'] += 1
        if counts['transfer_visits'] > 500000:
            raise ValueError('Indexed CFG exceeds bounded convergence budget')
        successors = []
        if not op.group(cs.CS_GRP_RET):
            if op.group(cs.CS_GRP_JUMP):
                if len(op.operands) == 1 and op.operands[0].type == cs.x86.X86_OP_IMM:
                    target = positions.get(op.operands[0].imm)
                    if target is not None:
                        successors.append(target)
                elif switches and op.address - image_base in switches:
                    successors += [positions[image_base + target] for target in switches[op.address - image_base]
                                   if image_base + target in positions]
                falls = op.mnemonic != 'jmp'
            else:
                falls = True
            if (falls and number + 1 < len(instructions)
                    and op.address + op.size == instructions[number + 1].address):
                successors.append(number + 1)
        for successor in successors:
            combined = merge(states[successor], outgoing)
            if combined != states[successor]:
                states[successor] = combined
                if successor not in queued:
                    queued.add(successor)
                    pending.append(successor)
    rows = []
    for op, state in zip(instructions, states):
        if state is None:
            continue
        aliases = state[0]
        for number, operand in enumerate(op.operands):
            if operand.type != cs.x86.X86_OP_MEM:
                continue
            pointer = aliases.get(op.reg_name(operand.mem.base))
            if pointer is None:
                continue
            index = 0
            if operand.mem.index:
                counts['indexed_alias_operands'] += 1
                index = index_value(operand, op, aliases)
                if index is None:
                    counts['unknown_index_operands'] += 1
                    write = bool(operand.access & cs.CS_AC_WRITE)
                    if op.mnemonic.startswith(('mov', 'vmov')):
                        write = number == 0
                    if write and unresolved is not None:
                        root = pointer[1]
                        unresolved.append(dict(instruction_rva=hex(op.address - image_base),
                            root_load_rva=hex(root) if root >= 0 else None,
                            entry_argument=ARGUMENTS[-root - 1] if root < 0 else None,
                            alias_offset=pointer[0], displacement=operand.mem.disp,
                            index_scale=operand.mem.scale, operand_bytes=operand.size))
                    continue
                counts['constant_index_operands'] += 1
            location = pointer[0] + operand.mem.disp + index * operand.mem.scale
            address_only = op.mnemonic == 'lea'
            overlaps = (0x21c <= location < 0x220 if address_only else
                        location < 0x220 and 0x21c < location + operand.size)
            if not overlaps:
                continue
            read, write = bool(operand.access & cs.CS_AC_READ), bool(operand.access & cs.CS_AC_WRITE)
            if op.mnemonic.startswith(('mov', 'vmov')):
                read, write = number != 0, number == 0
            root = pointer[1]
            rows.append(dict(instruction_rva=hex(op.address - image_base),
                root_load_rva=hex(root) if root >= 0 else None,
                entry_argument=ARGUMENTS[-root - 1] if root < 0 else None,
                effective_offset=location, operand_bytes=operand.size,
                indexed=bool(operand.mem.index), index_value=index,
                index_scale=operand.mem.scale, displacement=operand.mem.disp,
                address_only=address_only, read=read and not address_only,
                write=write and not address_only))
    counts['reached_instructions'] = sum(state is not None for state in states)
    return rows, counts


def owned_checks(decoder, cs):
    cases = [
        ('constant_index', 'b887000000f30f11848100000000c3', 1),
        ('unknown_index', 'f30f11848100000000c3', 0),
        ('constant_shift', 'b887000000c1e002f30f11840100000000c3', 1),
        ('constant_multiply', 'b82d0000006bc003f30f11848100000000c3', 1),
        ('partial_register_kills', 'b88700000031c0f30f11848100000000c3', 0),
        ('call_kills_index', 'b887000000e800000000f30f11848100000000c3', 0),
        ('conditional_same', 'b887000000750190f30f11848100000000c3', 1),
        ('conditional_different', 'b887000000750383c001f30f11848100000000c3', 0),
        ('zero_extend_32_is_not_negative', 'b8fffffffff30f11848120020000c3', 0),
        ('sign_extend_negative_index', 'b8ffffffff4863c0f30f11848120020000c3', 1),
        ('arithmetic_shift_negative', '48c7c0feffffff48d1f8f30f11848120020000c3', 1),
        ('resolved_indexed_frame_load', '31c0488b840128010000f30f11801c020000c3', 1),
        ('nonvolatile_index', '4889cfbb87000000e800000000f30f118c9f00000000c3', 1),
        ('wide_overlap', 'b8840000000f11848100000000c3', 1),
        ('adjacent_only', 'b886000000f30f11848100000000c3', 0),
    ]
    rows = []
    for name, text, expected in cases:
        raw = bytes.fromhex(text)
        ops = list(decoder.disasm(raw, 0x1000))
        assert sum(op.size for op in ops) == len(raw), name
        found, _ = analyze(ops, cs, 0x1000)
        assert len(found) == expected, (name, found)
        rows.append(dict(case=name, overlaps=expected, passed=True))
    return rows


def voiceaf_metadata(dll, older_dll):
    rows, names = [], []
    for path, expected in ((dll, SHA256), (older_dll, V4_SHA256)):
        data = path.read_bytes()
        assert hashlib.sha256(data).hexdigest() == expected
        pe = PE(data)
        labels = {}
        for match in re.finditer(rb'(?:frVoiceAF|rgnVoiceAF|timbreDescriptor)[A-Za-z0-9]+\x00', data):
            label = match.group()[:-1].decode('ascii')
            assert label not in labels
            labels[label] = pe.rva(match.start())
        assert len(labels) == 44
        names.append(set(labels))
        rows.append(dict(module=path.name, sha256=expected, pointer_bytes=pe.width,
            labels_count=len(labels), categories={prefix:sum(label.startswith(prefix) for label in labels)
                for prefix in ('frVoiceAF', 'rgnVoiceAF', 'timbreDescriptor')},
            labels_sha256=hashlib.sha256('\n'.join(sorted(labels)).encode('ascii')).hexdigest()))
        if pe.width == 8:
            assert struct.unpack_from('<Q', data, pe.offset(0x5aaf78))[0] == 0
            pointer = struct.unpack_from('<Q', data, pe.offset(0x5aaf80))[0]
            assert pe.text(pointer - pe.image_base) == 'frVoiceAFPitch'
    assert names[0] == names[1]
    return dict(modules=rows, identical_literal_sets=True,
        v6_core_name_prefix_separator_rva='0x5aaf78',
        v6_separate_frame_voiceaf_pointer_rva='0x5aaf80',
        conclusion='All44 following VoiceAF/timbre labels are also present in V6; the shorter contiguous99-word run does not establish an older-only feature set',
        limits=['String identity and pointer placement are metadata, not implementation or serialized-field mapping',
                'No V4-only producer, function binding or bit17 calculation follows from the labels'])


def run(dll, decoder_dir):
    data = dll.read_bytes()
    assert hashlib.sha256(data).hexdigest() == SHA256
    pe = PE(data)
    sys.path.insert(0, str(decoder_dir.resolve()))
    cs = importlib.import_module('capstone')
    assert cs.__version__ == '5.0.3' and pe.width == 8 and pe.machine == 0x8664
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    decoder.detail = True
    fixtures = owned_checks(decoder, cs)
    groups, fragments = runtime_groups(data, pe)
    totals = Counter(runtime_groups=len(groups), exception_ranges=fragments)
    rows, gaps, unresolved = [], [], []
    for root, members in sorted(groups.items()):
        ops = []
        for start, end, _ in members:
            raw = data[pe.offset(start):pe.offset(start) + end - start]
            part = list(decoder.disasm(raw, pe.image_base + start))
            used = sum(op.size for op in part)
            if used != len(raw):
                gaps.append(dict(group_rva=hex(root), fragment_rva=hex(start),
                    undecoded_start_rva=hex(start + used), undecoded_bytes=len(raw) - used))
            ops.extend(part)
        totals['decoded_instructions'] += len(ops)
        switches = None
        if root == 0x1b2ab0:
            switches, _ = reviewed_switch_data(data, pe, ops, cs)
            ops = [op for op in ops if op.address - pe.image_base < 0x1b3a84]
        unknown = []
        found, counts = analyze(ops, cs, pe.image_base, switches, unknown)
        totals.update(counts)
        unresolved.extend(dict(group_rva=hex(root), **row) for row in unknown)
        previous, _, _, _ = previous_analyze(ops, cs, pe.image_base, switch_edges=switches)
        def key(row, newer):
            return (row['instruction_rva'], row.get('root_load_rva' if newer else 'root_rva'),
                row['entry_argument'], row['effective_offset'], row['operand_bytes'],
                row['address_only'], row['read'], row['write'])
        old_keys = {key(row, False) for row in previous}
        new_keys = {key(row, True) for row in found}
        assert old_keys <= new_keys, ('Previously recognized overlap lost', hex(root))
        totals['previous_overlaps_preserved'] += len(previous)
        totals['all_refined_overlaps'] += len(found)
        rows.extend(dict(group_rva=hex(root), **row) for row in found if key(row, True) not in old_keys)
        if len(rows) > 1000:
            raise ValueError('Indexed candidate bound exceeded')
    paths = [Path(__file__), *(Path(__file__).with_name(name) for name in (
        'audit_frm2_numeric.py', 'audit_vqm_feature_calls_cfg.py',
        'audit_vqm_feature_entry_returns.py', 'audit_vqm_feature_pointer_accesses.py',
        'audit_vqm_v4_module_leads.py'))]
    return dict(schema=1, dse_sha256=SHA256, decoder_version=cs.__version__,
        source_sha256={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in paths},
        owned_instruction_checks=fixtures, counts=dict(totals), bit17_constant_index_leads=rows,
        decode_gaps=gaps, unresolved_indexed_writes=unresolved,
        unresolved_frame_load_write_groups=dict(Counter(row['group_rva'] for row in unresolved
            if row['root_load_rva'] is not None)),
        reviewed_output_parameter_groups=[
            dict(group_rva='0x104b30', role='Strided four-byte array copy',
                output_argument='r8', count_argument=5,
                basis='Successful pinned Ghidra function/decompile query',
                limit='Generic copy helper; its callers and target ownership are not established'),
            dict(group_rva='0x1050c0', role='Strided float32 array copy multiplied by one supplied scalar',
                output_argument='r9', count_argument=6,
                basis='Successful pinned Ghidra function/decompile query',
                limit='Generic arithmetic helper; a supplied scalar does not identify a bit17 formula'),
            dict(group_rva='0x15da20', role='PCM16 to float conversion, edge taper and reflected padding',
                output_argument='rdx', sample_count_argument=3,
                caller_rvas=['0x15e5a0', '0x15e810'],
                caller_source_bytes_per_sample=2, caller_output_float_count_extra=0x10000,
                basis='Successful pinned Ghidra function/decompile/xref queries including both loaders',
                limit='The inspected loaders prepare PCM and load/copy Frames separately; no bit17 calculation is identified')],
        scope='All pinned PE unwind groups; untyped entry arguments and pointer-shaped Frame loads with must-known integer indices',
        limits=['A candidate is an untyped address lead, not a proved feature or producer',
                'Unknown indices, loop induction ranges, indirect edges/callees and escaping spill addresses remain unresolved',
                'Only the four previously reviewed switch tables are followed',
                'The original private-spill analysis tracks pointer aliases but not spilled integer literals',
                'No original maker assignment, physical unit or whole-program producer absence proof follows'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'decoder-dir', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--older-dll', type=Path)
    args = parser.parse_args()
    report = run(args.dll, args.decoder_dir)
    if args.older_dll is not None:
        report['voiceaf_metadata'] = voiceaf_metadata(args.dll, args.older_dll)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(counts=report['counts'], leads=report['bit17_constant_index_leads'])))


if __name__ == '__main__':
    main()
