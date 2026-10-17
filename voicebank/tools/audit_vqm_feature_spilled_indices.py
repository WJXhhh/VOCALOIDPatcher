"""Bound stack-literal indices and non-unwind data-pointer entry candidates.

Installed instructions remain in memory. Reports contain counts, RVAs and
untyped leads, never code payloads or a whole-program producer absence claim.
"""
from __future__ import annotations

import argparse
from bisect import bisect_right
from collections import Counter, deque
import hashlib
import importlib
import json
from pathlib import Path
import struct
import sys

from audit_frm2_numeric import SHA256
from audit_vqm_feature_calls_cfg import merge, runtime_groups, stack_location
from audit_vqm_feature_constant_indices import (
    analyze as previous_analyze, extended_transfer, index_value, literal,
)
from audit_vqm_feature_entry_returns import decode_leaf, direct_target, reviewed_switch_data
from audit_vqm_feature_pointer_accesses import ARGUMENTS, parent_register
from audit_vqm_v4_module_leads import PE
from locate_vqm_feature_accesses import executable_sections


BYTE = 'integer_stack_byte'
ESCAPED = ('integer_stack_address_exposed',)


def signed64(value):
    value &= (1 << 64) - 1
    return value - (1 << 64) if value >= 1 << 63 else value


def clear_bytes(aliases):
    for key in list(aliases):
        if isinstance(key, tuple) and key[0] == BYTE:
            del aliases[key]


def merge_states(left, right):
    combined = merge(left, right)
    if right[0].get(ESCAPED) or left is not None and left[0].get(ESCAPED):
        combined[0][ESCAPED] = True
        clear_bytes(combined[0])
    return combined


def transfer(op, state, cs, base):
    outgoing, next_sp, next_bp = extended_transfer(op, state, cs, base)
    before, sp, bp = state
    exposed = before.get(ESCAPED, False)
    destination = op.operands[0] if op.operands else None
    source = op.operands[1] if len(op.operands) == 2 else None
    # Do not treat an exposed stack address as a private literal cell. This is
    # deliberately sticky; no attempt is made to recover it after an escape.
    if destination is not None and destination.type == cs.x86.X86_OP_REG:
        target = op.reg_name(destination.reg)
        if target not in ('rsp', 'rbp') and source is not None:
            if (op.mnemonic == 'lea' and source.type == cs.x86.X86_OP_MEM
                    and op.reg_name(source.mem.base) in ('rsp', 'rbp')):
                exposed = True
            elif (source.type == cs.x86.X86_OP_REG
                    and op.reg_name(source.reg) in ('rsp', 'rbp')):
                exposed = True
    if exposed:
        outgoing[ESCAPED] = True
        clear_bytes(outgoing)

    if (op.mnemonic == 'movabs' and destination is not None
            and destination.type == cs.x86.X86_OP_REG and source is not None
            and source.type == cs.x86.X86_OP_IMM):
        outgoing[('literal', parent_register(op.reg_name(destination.reg)))] = source.imm & ((1 << (8 * destination.size)) - 1)

    if (destination is not None and destination.type == cs.x86.X86_OP_REG
            and destination.size == 8 and source is not None):
        register = op.reg_name(destination.reg)
        if op.mnemonic == 'lea' and source.type == cs.x86.X86_OP_MEM:
            pointer = before.get(op.reg_name(source.mem.base))
            index = index_value(source, op, before)
            if pointer is not None and index is not None:
                outgoing[register] = (signed64(pointer[0] + source.mem.disp + index * source.mem.scale), pointer[1])
        elif op.mnemonic in ('add', 'sub'):
            pointer = before.get(register)
            amount = literal(source, op, before, cs)
            if pointer is not None and amount is not None:
                outgoing[register] = (signed64(pointer[0] + signed64(amount) * (1 if op.mnemonic == 'add' else -1)), pointer[1])

    # Little-endian byte cells allow partial overwrites and differently sized
    # reloads only when every required byte is independently known.
    loaded = None
    if (not exposed and source is not None and source.type == cs.x86.X86_OP_MEM
            and destination.type == cs.x86.X86_OP_REG and destination.size in (4, 8)
            and source.size in (1, 2, 4, 8)
            and op.mnemonic in ('mov', 'movzx', 'movsx', 'movsxd')):
        slot = stack_location(source, op, sp, bp, cs)
        if slot is not None:
            values = [before.get((BYTE, slot + i)) for i in range(source.size)]
            if all(value is not None for value in values):
                loaded = sum(value << (8 * i) for i, value in enumerate(values))
                if op.mnemonic in ('movsx', 'movsxd') and loaded >= 1 << (8 * source.size - 1):
                    loaded -= 1 << (8 * source.size)
                loaded &= (1 << (8 * destination.size)) - 1

    for number, operand in enumerate(op.operands):
        if operand.type != cs.x86.X86_OP_MEM:
            continue
        writes = bool(operand.access & cs.CS_AC_WRITE)
        if op.mnemonic.startswith(('mov', 'vmov')):
            writes = number == 0
        if not writes:
            continue
        slot = stack_location(operand, op, sp, bp, cs)
        if slot is None:
            # A write through an unknown address may alias a tracked cell.
            clear_bytes(outgoing)
            continue
        for at in range(slot, slot + operand.size):
            outgoing.pop((BYTE, at), None)
        if (not exposed and op.mnemonic == 'mov' and number == 0
                and operand.size in (1, 2, 4, 8) and source is not None):
            value = literal(source, op, before, cs)
            if value is not None:
                for i in range(operand.size):
                    outgoing[(BYTE, slot + i)] = (value >> (8 * i)) & 255

    if op.mnemonic == 'push':
        if next_sp is None:
            clear_bytes(outgoing)
        else:
            for at in range(next_sp, next_sp + 8):
                outgoing.pop((BYTE, at), None)

    if loaded is not None:
        outgoing[('literal', parent_register(op.reg_name(destination.reg)))] = loaded
    if op.group(cs.CS_GRP_CALL):
        # Without a callee contract even slots beyond shadow space could be
        # outgoing arguments. Do not carry literal cells across any CALL.
        clear_bytes(outgoing)
    return outgoing, next_sp, next_bp


def inspect(ops, cs, base, switches=None):
    if not ops:
        return [], [], Counter()
    positions = {op.address: i for i, op in enumerate(ops)}
    states = [None] * len(ops)
    states[0] = ({register: (0, -i - 1) for i, register in enumerate(ARGUMENTS)}, 0, None)
    pending, queued, counts = deque([0]), {0}, Counter()
    while pending:
        i = pending.popleft()
        queued.remove(i)
        op = ops[i]
        outgoing = transfer(op, states[i], cs, base)
        counts['transfer_visits'] += 1
        if counts['transfer_visits'] > 500000:
            raise ValueError('Spilled-index CFG convergence budget exceeded')
        successors = []
        if not op.group(cs.CS_GRP_RET):
            if op.group(cs.CS_GRP_JUMP):
                if len(op.operands) == 1 and op.operands[0].type == cs.x86.X86_OP_IMM:
                    target = positions.get(op.operands[0].imm)
                    if target is not None:
                        successors.append(target)
                elif switches and op.address - base in switches:
                    successors += [positions[base + target] for target in switches[op.address - base]
                                   if base + target in positions]
                falls = op.mnemonic != 'jmp'
            else:
                falls = True
            if falls and i + 1 < len(ops) and op.address + op.size == ops[i + 1].address:
                successors.append(i + 1)
        for successor in successors:
            combined = merge_states(states[successor], outgoing)
            if combined != states[successor]:
                states[successor] = combined
                if successor not in queued:
                    queued.add(successor)
                    pending.append(successor)
    rows, unresolved = [], []
    for op, state in zip(ops, states):
        if state is None:
            continue
        aliases = state[0]
        counts['reached_instructions'] += 1
        if any(isinstance(key, tuple) and key[0] == BYTE for key in aliases):
            counts['instructions_with_known_stack_bytes'] += 1
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
                    writes = bool(operand.access & cs.CS_AC_WRITE)
                    if op.mnemonic.startswith(('mov', 'vmov')):
                        writes = number == 0
                    if writes:
                        unresolved.append(dict(instruction_rva=hex(op.address - base),
                            root_load_rva=hex(pointer[1]) if pointer[1] >= 0 else None,
                            entry_argument=ARGUMENTS[-pointer[1] - 1] if pointer[1] < 0 else None,
                            alias_offset=pointer[0], displacement=operand.mem.disp,
                            index_scale=operand.mem.scale, operand_bytes=operand.size))
                    continue
                counts['constant_index_operands'] += 1
            location = signed64(pointer[0] + operand.mem.disp + index * operand.mem.scale)
            address_only = op.mnemonic == 'lea'
            overlaps = (0x21c <= location < 0x220 if address_only else
                        location < 0x220 and 0x21c < location + operand.size)
            if not overlaps:
                continue
            read, write = bool(operand.access & cs.CS_AC_READ), bool(operand.access & cs.CS_AC_WRITE)
            if op.mnemonic.startswith(('mov', 'vmov')):
                read, write = number != 0, number == 0
            rows.append(dict(instruction_rva=hex(op.address - base),
                root_load_rva=hex(pointer[1]) if pointer[1] >= 0 else None,
                entry_argument=ARGUMENTS[-pointer[1] - 1] if pointer[1] < 0 else None,
                effective_offset=location, operand_bytes=operand.size,
                address_only=address_only, read=read and not address_only,
                write=write and not address_only))
    return rows, unresolved, counts


def row_key(row):
    return tuple(row[key] for key in ('instruction_rva', 'root_load_rva', 'entry_argument',
        'effective_offset', 'operand_bytes', 'address_only', 'read', 'write'))


def checks(decoder, cs):
    store = 'f30f11849000020000c3'
    cases = [
        ('dword_reload', '488b8128010000c7442440070000008b542440' + store, 1),
        ('word_reload', '488b812801000066c744244007000fb7542440' + store, 1),
        ('byte_reload', '488b8128010000c6442440070fb6542440' + store, 1),
        ('partial_known_overwrite', '488b8128010000c744244000000000c6442440078b542440' + store, 1),
        ('unknown_partial_overwrite', '488b8128010000c744244007000000884c24418b542440' + store, 0),
        ('wide_reload_missing_bytes', '488b8128010000c744244007000000488b542440' + store, 0),
        ('stack_rebased', '488b81280100004883ec20c7442440070000004883c4208b542420' + store, 1),
        ('frame_pointer', '488b81280100004889e5c74540070000008b5540' + store, 1),
        ('escaped_stack_address', '488b8128010000c744244007000000488d7424408b542440' + store, 0),
        ('exposed_on_one_branch', '488b81280100007505488d742440c7442440070000008b542440' + store, 0),
        ('implicit_push_overwrites', '488b8128010000c74424f807000000538b5424004883c408' + store, 0),
        ('unknown_store_invalidates', '488b8128010000c744244007000000c706000000008b542440' + store, 0),
        ('call_invalidates_all_literals', '488b81280100004889c7c744244007000000e8000000004889f88b542440' + store, 0),
        ('same_branch_join', '488b8128010000c7442440070000007501908b542440' + store, 1),
        ('different_branch_join', '488b8128010000c7442440070000007508c7442440080000008b542440' + store, 0),
        ('signed_reload_negative', '488b8128010000c7442440ffffffff4863542440f30f11849020020000c3', 1),
        ('zero_extend_not_negative', '488b8128010000c7442440ffffffff8b542440f30f11849020020000c3', 0),
        ('negative_lea_rebase', '488b8128010000c7442440ffffffff4863542440488d849020020000f30f1100c3', 2),
        ('negative_add_rebase', '488b8128010000488d8020020000c7442440ffffffff486354244048c1e2024801d0f30f1100c3', 1),
        ('negative_sub_rebase', '488b8128010000488d8018020000c7442440ffffffff486354244048c1e2024829d0f30f1100c3', 1),
        ('zero_extend_add_is_positive', '488b8128010000488d8020020000c7442440ffffffff8b54244048c1e2024801d0f30f1100c3', 0),
        ('lea_modular_address', '488b812801000048ba8700000000000040488d0490f30f1100c3', 2),
        ('indexed_modular_address', '488b812801000048ba8700000000000040f30f110490c3', 1),
        ('high_byte_store', '488b8128010000ba00070000887424400fb6542440' + store, 1),
        ('indexed_frame_pointer_load', 'c7442440020000008b542440488b84d118010000f30f11801c020000c3', 1),
    ]
    results = []
    for name, text, expected in cases:
        raw = bytes.fromhex(text)
        ops = list(decoder.disasm(raw, 0x1000))
        assert sum(op.size for op in ops) == len(raw), name
        rows, _, _ = inspect(ops, cs, 0x1000)
        assert len(rows) == expected, (name, rows)
        assert sum(row['write'] for row in rows) == (1 if expected else 0), name
        results.append(dict(case=name, passed=True, overlaps=expected))
    return results


def verify_prior(report):
    for name, digest in report['source_sha256'].items():
        assert hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() == digest, name


def helper_references(pe, groups):
    directory, size = struct.unpack_from('<II', pe.data, pe.directories + 24)
    records = {start: (start, end, unwind) for members in groups.values() for start, end, unwind in members}
    result = []
    for target, refs in ((0x104b30, (0xab1810, 0x5d35bc, 0x5d35cc)),
                         (0x1050c0, (0xab18a0, 0x5d3670, 0x5d3680))):
        for reference in refs:
            triple = struct.unpack_from('<III', pe.data, pe.offset(reference))
            assert triple == records[target]
            pdata = directory <= reference < directory + size
            if pdata:
                assert (reference - directory) % 12 == 0
                role = 'PE exception-directory RUNTIME_FUNCTION record'
            else:
                role = 'Chained UNWIND_INFO RUNTIME_FUNCTION record'
                matches = []
                for start, end, unwind in groups[target]:
                    at = pe.offset(unwind)
                    version, _, codes, _ = struct.unpack_from('<BBBB', pe.data, at)
                    if version >> 3 & 4:
                        chained_rva = unwind + 4 + ((codes + 1) & ~1) * 2
                        if chained_rva == reference:
                            matches.append(hex(start))
                assert len(matches) == 1, (hex(reference), matches)
            result.append(dict(target_rva=hex(target), reference_rva=hex(reference),
                classification=role, executable_dispatch_proved=False))
    return result


def helper_code_references(pe, cs):
    targets = {0x104b30, 0x1050c0}
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    decoder.detail = True
    decoder.skipdata = True
    counts = Counter()
    refs = []
    for _, start, raw in executable_sections(pe.data):
        for op in decoder.disasm(raw, pe.image_base + start):
            if not op.id:
                counts['linear_skipped_bytes'] += op.size
                continue
            counts['linear_decoded_instructions'] += 1
            for operand in op.operands:
                target = None
                if operand.type == cs.x86.X86_OP_IMM:
                    target = operand.imm - pe.image_base
                elif (operand.type == cs.x86.X86_OP_MEM
                      and op.reg_name(operand.mem.base) == 'rip'):
                    target = op.address + op.size + operand.mem.disp - pe.image_base
                if target in targets:
                    refs.append(dict(instruction_rva=hex(op.address - pe.image_base),
                                     target_rva=hex(target), operation=op.mnemonic))
    pointer_refs = []
    for target in sorted(targets):
        needle = struct.pack('<Q', pe.image_base + target)
        position = 0
        while (position := pe.data.find(needle, position)) >= 0:
            pointer_refs.append(dict(reference_rva=hex(pe.rva(position)), target_rva=hex(target)))
            position += 1
    exports = [row for row in pe.exports() if int(row['rva'], 16) in targets]
    return dict(counts=dict(counts), direct_code_address_leads=refs,
        absolute_pointer_leads=pointer_refs, export_leads=exports,
        limit='Linear code decoding may include data and skip instructions; computed addresses and cross-module callers are not excluded')


def rtti_pointer_owners(pe, code, references, decoder):
    def executable(at):
        return any(a <= at < b for a, b in code)
    owners = {}
    tables = 0
    for _, rva, size, offset in pe.sections:
        if executable(rva):
            continue
        for i in range(0, size - 15, 8):
            col = struct.unpack_from('<Q', pe.data, offset + i)[0] - pe.image_base
            try:
                signature, _, _, descriptor, _, self_rva = struct.unpack_from(
                    '<6I', pe.data, pe.offset(col))
                if signature != 1 or self_rva != col:
                    continue
                name = pe.text(descriptor + 16)
                if not name or not name.startswith(('.?AV', '.?AU')):
                    continue
                methods = []
                for slot in range(min(128, (size - i - 8) // 8)):
                    target = struct.unpack_from('<Q', pe.data, offset + i + 8 + 8 * slot)[0] - pe.image_base
                    if not executable(target):
                        break
                    methods.append(target)
                if not methods:
                    continue
                tables += 1
                for target in methods:
                    if target in references:
                        owners.setdefault(target, set()).add(name)
            except (ValueError, struct.error):
                continue
    reviewed = []
    for target, role in ((0x156080, 'Return-only stub'), (0x158e30, 'Return-only stub'),
                         (0x158a50, 'Return-only stub'), (0x153f40, 'Constant byte-one return')):
        assert target in owners
        at = pe.offset(target)
        ops = []
        for op in decoder.disasm(pe.data[at:at + 32], pe.image_base + target):
            ops.append(op)
            if op.mnemonic == 'ret':
                break
        assert ops and ops[-1].mnemonic == 'ret'
        if target != 0x153f40:
            assert len(ops) == 1
        else:
            assert len(ops) == 2 and ops[0].mnemonic == 'mov'
            assert ops[0].reg_name(ops[0].operands[0].reg) == 'al' and ops[0].operands[1].imm == 1
        reviewed.append(dict(entry_rva=hex(target), owner_labels=sorted(owners[target]),
            role=role, decoded_body_instructions=len(ops),
            basis='Pinned RTTI pointer records, local decoding and successful Ghidra queries on 2026-10-13',
            scope='These four methods only; no assertion about other slots or virtual dispatch'))
    return dict(parsed_vtable_candidates=tables, data_pointer_targets_with_owner_labels=len(owners),
        leaf_owner_metadata=[dict(entry_rva=hex(target), owner_labels=sorted(labels))
                             for target, labels in sorted(owners.items())],
        reviewed_vqm_stubs=reviewed,
        limit='RTTI labels and contiguous executable pointer words provide static owner leads, not observed runtime selection or recovered class declarations')


def run(dll, decoder_dir, prior_leaf_path):
    data = dll.read_bytes()
    assert hashlib.sha256(data).hexdigest() == SHA256
    pe = PE(data)
    assert pe.width == 8 and pe.machine == 0x8664
    sys.path.insert(0, str(decoder_dir.resolve()))
    cs = importlib.import_module('capstone')
    assert cs.__version__ == '5.0.3'
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    decoder.detail = True
    owned = checks(decoder, cs)
    prior = json.loads(prior_leaf_path.read_text(encoding='utf-8'))
    assert prior['dse_sha256'] == SHA256 and prior['leaf_bit17_cell_shaped_overlaps'] == []
    verify_prior(prior)
    groups, fragments = runtime_groups(data, pe)
    totals = Counter(runtime_groups=len(groups), exception_ranges=fragments)
    new_rows, unknown, gaps = [], [], []
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
        found, unresolved, counts = inspect(ops, cs, pe.image_base, switches)
        old, _ = previous_analyze(ops, cs, pe.image_base, switches)
        old_keys = {row_key(row) for row in old}
        assert old_keys <= {row_key(row) for row in found}, ('Old access lost', hex(root))
        totals.update(counts)
        totals['previous_overlaps_preserved'] += len(old)
        totals['all_refined_overlaps'] += len(found)
        new_rows += [dict(group_rva=hex(root), **row) for row in found if row_key(row) not in old_keys]
        unknown += [dict(group_rva=hex(root), **row) for row in unresolved]
        assert len(new_rows) <= 1000

    ranges = sorted((a, b) for members in groups.values() for a, b, _ in members)
    starts = [a for a, _ in ranges]
    code = [(a, a + len(raw)) for _, a, raw in executable_sections(data)]
    def in_runtime(at):
        index = bisect_right(starts, at) - 1
        return index >= 0 and at < ranges[index][1]
    def executable(at):
        return any(a <= at < b for a, b in code)
    references = {}
    for _, rva, size, offset in pe.sections:
        if executable(rva):
            continue
        for i in range(0, size - 7, 8):
            target = struct.unpack_from('<Q', data, offset + i)[0] - pe.image_base
            if executable(target) and not in_runtime(target):
                references.setdefault(target, []).append(rva + i)
    old_coverage = {int(row['entry_rva'], 16): row for row in prior['leaf_coverage']}
    roots = set(old_coverage) | set(references)
    assert len(roots) <= 3000
    anchors = sorted(set(starts) | {b for _, b in ranges} | roots | {b for _, b in code})
    pending, visited = deque(sorted(roots)), set()
    leaf_counts = Counter()
    coverage, leaf_rows, leaf_unknown = [], [], []
    unique_instructions, prior_unique_instructions = set(), set()
    while pending:
        root = pending.popleft()
        if root in visited:
            continue
        visited.add(root)
        stop = (int(old_coverage[root]['interval_end_rva'], 16) if root in old_coverage else
                min(root + 2048, anchors[bisect_right(anchors, root)]))
        ops, row = decode_leaf(root, stop, data, pe, decoder, cs)
        if root in old_coverage:
            assert row == old_coverage[root], ('Prior leaf extent changed', hex(root))
            leaf_counts['prior_entries_exactly_redecoded'] += 1
            prior_unique_instructions.update(op.address - pe.image_base for op in ops)
        else:
            leaf_counts['additional_entries'] += 1
        found, unresolved, counts = inspect(ops, cs, pe.image_base)
        leaf_counts.update(counts)
        leaf_counts['decoded_instructions'] += len(ops)
        leaf_counts['additional_decoded_instructions'] += len(ops) if root not in old_coverage else 0
        unique_instructions.update(op.address - pe.image_base for op in ops)
        row['origin'] = ('prior_direct_or_export_entry' if root in old_coverage else
                         'aligned_noncode_pointer_candidate' if root in references else
                         'direct_transfer_from_added_entry')
        row['pointer_reference_rvas'] = [hex(at) for at in references.get(root, ())]
        coverage.append(row)
        leaf_rows += [dict(entry_rva=hex(root), **item) for item in found]
        leaf_unknown += [dict(entry_rva=hex(root), **item) for item in unresolved]
        for op in ops:
            target = direct_target(op, cs, pe.image_base)
            if target is not None and executable(target) and not in_runtime(target) and target not in roots:
                roots.add(target)
                pending.append(target)
                assert len(roots) <= 3000
    leaf_counts['unique_decoded_instruction_addresses'] = len(unique_instructions)
    leaf_counts['prior_unique_decoded_instruction_addresses'] = len(prior_unique_instructions)
    leaf_counts['additional_unique_instruction_addresses'] = len(unique_instructions - prior_unique_instructions)
    leaf_counts['entries'] = len(visited)
    leaf_counts['data_pointer_targets'] = len(references)
    leaf_counts['new_data_pointer_targets'] = len(set(references) - set(old_coverage))
    for field in ('undecodable_rvas', 'outside_direct_edges', 'unresolved_indirect_branch_rvas',
                  'overlapping_instruction_spans'):
        leaf_counts['entries_with_' + field] = sum(bool(row[field]) for row in coverage)
    leaf_counts['entries_with_any_decode_limit'] = sum(any(row[field] for field in (
        'undecodable_rvas', 'outside_direct_edges', 'unresolved_indirect_branch_rvas',
        'overlapping_instruction_spans')) for row in coverage)
    assert leaf_counts['prior_entries_exactly_redecoded'] == len(old_coverage)
    paths = [Path(__file__), *(Path(__file__).with_name(name) for name in (
        'audit_frm2_numeric.py', 'audit_vqm_feature_calls_cfg.py',
        'audit_vqm_feature_constant_indices.py', 'audit_vqm_feature_entry_returns.py',
        'audit_vqm_feature_pointer_accesses.py', 'audit_vqm_v4_module_leads.py',
        'locate_vqm_feature_accesses.py'))]
    return dict(schema=1, dse_sha256=SHA256, decoder_version=cs.__version__,
        source_sha256={p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in paths},
        prior_leaf_report_sha256=hashlib.sha256(prior_leaf_path.read_bytes()).hexdigest(),
        owned_instruction_checks=owned, runtime_counts=dict(totals),
        new_runtime_bit17_leads=new_rows, unresolved_runtime_indexed_writes=unknown,
        runtime_decode_gaps=gaps, leaf_counts=dict(leaf_counts), leaf_coverage=coverage,
        leaf_bit17_leads=leaf_rows, unresolved_leaf_indexed_writes=leaf_unknown,
        output_helper_metadata_references=helper_references(pe, groups),
        output_helper_direct_reference_coverage=helper_code_references(pe, cs),
        rtti_pointer_review=rtti_pointer_owners(pe, code, references, decoder),
        limits=['Entry arguments, data-pointer targets and Frame-shaped loads are untyped candidates',
            'All literal stack cells are discarded at CALL and unknown-address writes; exposed stack addresses disable this extension',
            'Known bytes do not imply loop ranges, symbolic indices, arbitrary stack aliases or callee summaries',
            'Only four reviewed switch tables are followed in runtime groups',
            'Non-unwind entries retain bounded decoding, outside edges, indirect branches and undecodable gaps',
            'Aligned file pointers need not be vtable entries or callable function starts',
            'Additional direct-target discovery does not widen a previously recorded leaf interval',
            'No original producer, physical unit, cross-module completeness or producer absence proof follows'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'decoder-dir', 'prior-leaf-report', 'output'):
        parser.add_argument('--' + name, required=True, type=Path)
    args = parser.parse_args()
    report = run(args.dll, args.decoder_dir, args.prior_leaf_report)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: report[key] for key in ('runtime_counts', 'leaf_counts',
        'new_runtime_bit17_leads', 'leaf_bit17_leads')}))


if __name__ == '__main__':
    main()
