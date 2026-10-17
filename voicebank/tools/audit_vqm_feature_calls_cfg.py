"""Bounded feature-pointer leads across direct branches and private stack spills.

This is a conservative local must-alias analysis, not typed object recovery or
proof that the module lacks a producer. Reports retain addresses, not code bytes.
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
from audit_vqm_feature_pointer_accesses import ARGUMENTS, KNOWN, VOLATILE, parent_register
from audit_vqm_v4_module_leads import PE


REVIEWED_TARGETS = {
    0x217c94: ('memset', 'bulk_initialization_lead'),
    0x217c76: ('memcpy', 'bulk_copy_lead'),
    0x2161b4: ('free', 'lifetime_cleanup_lead'),
    0x216490: ('free', 'lifetime_cleanup_lead'),
    0x2880: ('float_absolute_value', 'integer_register_not_consumed'),
    0x2320: ('logf', 'integer_register_not_consumed'),
    0x496d0: ('sqrtf', 'integer_register_not_consumed'),
    0x6c9f0: ('DRS_feature_writer', 'serialization'),
    0x6cef0: ('DRS_feature_reader', 'serialization'),
    0x135eb0: ('DSE5_feature_reader', 'serialization'),
    0x1380a0: ('DSE5_feature_writer', 'serialization'),
}


def import_slots(data, pe):
    start, size = struct.unpack_from('<II', data, pe.directories+8)
    assert 0 < size <= 0x10000
    result = {}
    for at in range(pe.offset(start), pe.offset(start)+size, 20):
        lookup, stamp, forwarder, name, iat = struct.unpack_from('<IIIII', data, at)
        if not any((lookup, stamp, forwarder, name, iat)):
            break
        for number in range(2048):
            entry = struct.unpack_from('<Q', data, pe.offset(lookup or iat)+8*number)[0]
            if not entry:
                break
            if entry < 1 << 63:
                result[iat+8*number] = dict(module=pe.text(name), symbol=pe.text(entry+2))
        else:
            raise ValueError('Import count exceeds bound')
    assert result[0x224998]['symbol'] == 'fread'
    assert result[0x2249a8]['symbol'] == 'fwrite'
    return result


def classify(row, imports):
    if row['target_rva']:
        role = REVIEWED_TARGETS.get(int(row['target_rva'], 16))
        if role:
            row['review'] = dict(callee=role[0], classification=role[1],
                                 basis='Successful Ghidra function query in the pinned V6 program')
            if role[0] == 'memset':
                literal = row['literal_integer_arguments']
                if literal.get('rdx') == 0 and 'r8' in literal:
                    row['review']['zero_interval'] = [row['alias_offset'],
                                                      row['alias_offset']+literal['r8']]
                    a, b = row['review']['zero_interval']
                    row['review']['overlaps_bit17'] = a < 0x220 and 0x21c < b
            return
    elif row['indirect_slot_rva']:
        imported = imports.get(int(row['indirect_slot_rva'], 16))
        if imported:
            row['review'] = dict(callee=imported['symbol'], classification='file_io',
                                 basis='Pinned PE import table', import_module=imported['module'])
            return
    row['review'] = dict(classification='unreviewed')


def merge(left, right):
    if left is None:
        return right
    a, sp, bp = left
    b, sp2, bp2 = right
    return ({k: v for k, v in a.items() if b.get(k) == v},
            sp if sp == sp2 else None, bp if bp == bp2 else None)


def stack_location(operand, op, sp, bp, cs):
    if operand.type != cs.x86.X86_OP_MEM or operand.mem.index:
        return None
    base = op.reg_name(operand.mem.base)
    origin = sp if base == 'rsp' else bp if base == 'rbp' else None
    return None if origin is None else origin + operand.mem.disp


def transfer(op, state, cs, image_base):
    aliases, sp, bp = state
    result = dict(aliases)
    value = None
    literal = None
    destination = None
    source = None
    if len(op.operands) == 2 and op.operands[0].type == cs.x86.X86_OP_REG:
        destination = op.reg_name(op.operands[0].reg)
        source = op.operands[1]
        if op.operands[0].size == 8:
            if op.mnemonic == 'mov' and source.type == cs.x86.X86_OP_REG:
                value = aliases.get(op.reg_name(source.reg))
            elif op.mnemonic == 'mov' and source.type == cs.x86.X86_OP_MEM and source.size == 8:
                slot = stack_location(source, op, sp, bp, cs)
                if slot is not None:
                    value = aliases.get(('stack', slot))
                elif (not source.mem.index and source.mem.disp in (0x128, 0x208)
                      and op.reg_name(source.mem.base) not in ('rsp', 'rip', '')):
                    value = (0, op.address - image_base)
            elif op.mnemonic == 'lea' and source.type == cs.x86.X86_OP_MEM:
                previous = aliases.get(op.reg_name(source.mem.base))
                index_value = (aliases.get(('literal', parent_register(op.reg_name(source.mem.index))))
                               if source.mem.index else 0)
                if previous is not None and index_value is not None:
                    value = (previous[0] + source.mem.disp + index_value*source.mem.scale, previous[1])
            elif op.mnemonic in ('add', 'sub'):
                previous = aliases.get(destination)
                amount = (source.imm if source.type == cs.x86.X86_OP_IMM else
                          aliases.get(('literal', parent_register(op.reg_name(source.reg))))
                          if source.type == cs.x86.X86_OP_REG else None)
                if previous is not None and amount is not None:
                    value = (previous[0] + amount * (1 if op.mnemonic == 'add' else -1), previous[1])
        if op.operands[0].size in (4, 8):
            canonical = parent_register(destination)
            if op.mnemonic == 'mov':
                literal = (source.imm if source.type == cs.x86.X86_OP_IMM else
                           aliases.get(('literal', parent_register(op.reg_name(source.reg))))
                           if source.type == cs.x86.X86_OP_REG else None)
            elif (op.mnemonic == 'xor' and source.type == cs.x86.X86_OP_REG
                  and parent_register(op.reg_name(source.reg)) == canonical):
                literal = 0
            elif op.mnemonic in ('add', 'sub'):
                previous_literal = aliases.get(('literal', canonical))
                amount = (source.imm if source.type == cs.x86.X86_OP_IMM else
                          aliases.get(('literal', parent_register(op.reg_name(source.reg))))
                          if source.type == cs.x86.X86_OP_REG else None)
                if previous_literal is not None and amount is not None:
                    literal = previous_literal + amount*(1 if op.mnemonic=='add' else -1)
            if literal is not None:
                literal &= (1 << (8*op.operands[0].size))-1
    # Memory writes invalidate overlapping tracked eight-byte spill cells.
    saved = None
    for index, operand in enumerate(op.operands):
        if operand.type != cs.x86.X86_OP_MEM:
            continue
        writes = bool(operand.access & cs.CS_AC_WRITE)
        if op.mnemonic.startswith(('mov', 'vmov')):
            writes = index == 0
        slot = stack_location(operand, op, sp, bp, cs)
        if writes and slot is not None:
            for key in list(result):
                if isinstance(key, tuple) and key[0]=='stack' and key[1] < slot + operand.size and slot < key[1] + 8:
                    del result[key]
            if (op.mnemonic == 'mov' and index == 0 and operand.size == 8
                    and len(op.operands) == 2 and op.operands[1].type == cs.x86.X86_OP_REG):
                candidate = aliases.get(op.reg_name(op.operands[1].reg))
                if candidate is not None:
                    saved = (('stack', slot), candidate)
    writes = {parent_register(op.reg_name(reg)) for reg in op.regs_access()[1]}
    for reg in writes:
        result.pop(reg, None)
        result.pop(('literal', reg), None)
    if destination is not None and value is not None:
        result[destination] = value
    if saved is not None:
        result[saved[0]] = saved[1]
    if literal is not None:
        result[('literal', parent_register(destination))] = literal
    # Symbolic stack origins are distinct from feature-pointer aliases.
    next_sp, next_bp = sp, bp
    if 'rsp' in writes:
        next_sp = None
        if op.mnemonic == 'push' and sp is not None:
            next_sp = sp - 8
        elif op.mnemonic == 'pop' and sp is not None:
            next_sp = sp + 8
        elif destination == 'rsp' and source is not None:
            if op.mnemonic in ('add', 'sub') and source.type == cs.x86.X86_OP_IMM and sp is not None:
                next_sp = sp + source.imm * (1 if op.mnemonic == 'add' else -1)
            elif op.mnemonic == 'mov' and source.type == cs.x86.X86_OP_REG:
                next_sp = bp if op.reg_name(source.reg) == 'rbp' else sp if op.reg_name(source.reg) == 'rsp' else None
            elif op.mnemonic == 'lea':
                next_sp = stack_location(source, op, sp, bp, cs)
    if 'rbp' in writes:
        next_bp = None
        if destination == 'rbp' and source is not None:
            if op.mnemonic == 'mov' and source.type == cs.x86.X86_OP_REG:
                next_bp = sp if op.reg_name(source.reg) == 'rsp' else bp if op.reg_name(source.reg) == 'rbp' else None
            elif op.mnemonic == 'lea':
                next_bp = stack_location(source, op, sp, bp, cs)
    if op.group(cs.CS_GRP_CALL):
        for reg in VOLATILE:
            result.pop(reg, None)
            result.pop(('literal', reg), None)
        if sp is not None:
            for key in list(result):
                if isinstance(key, tuple) and key[0]=='stack' and key[1] < sp + 32 and sp < key[1] + 8:
                    del result[key]
        # CALL's pushed return address is restored on normal return.
        next_sp = sp
    return result, next_sp, next_bp


def inspect(instructions, cs, image_base, seeds=None, indexed=None):
    if not instructions:
        return [], [], Counter()
    positions = {op.address: index for index, op in enumerate(instructions)}
    states = [None] * len(instructions)
    states[0] = (dict(seeds or {}), 0, None)
    pending = deque([0])
    queued = {0}
    counts = Counter()
    while pending:
        number = pending.popleft()
        queued.remove(number)
        op = instructions[number]
        outgoing = transfer(op, states[number], cs, image_base)
        counts['transfer_visits'] += 1
        if counts['transfer_visits'] > 500000:
            raise ValueError('CFG convergence exceeds bounded visit budget')
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
            new = merge(states[successor], outgoing)
            if new != states[successor]:
                states[successor] = new
                if successor not in queued:
                    queued.add(successor)
                    pending.append(successor)
    accesses, calls = [], []
    for op, state in zip(instructions, states):
        if state is None:
            counts['unreachable_instructions'] += 1
            continue
        aliases, _, _ = state
        for index, operand in enumerate(op.operands):
            if operand.type != cs.x86.X86_OP_MEM:
                continue
            previous = aliases.get(op.reg_name(operand.mem.base))
            if previous is None:
                continue
            if operand.mem.index:
                counts['indexed_operands_excluded'] += 1
                if indexed is not None:
                    read = bool(operand.access & cs.CS_AC_READ)
                    write = bool(operand.access & cs.CS_AC_WRITE)
                    if op.mnemonic.startswith(('mov', 'vmov')):
                        read, write = index != 0, index == 0
                    indexed.append(dict(root_rva=hex(previous[1]),
                        instruction_rva=hex(op.address-image_base), alias_offset=previous[0],
                        displacement=operand.mem.disp, index_register=op.reg_name(operand.mem.index),
                        index_scale=operand.mem.scale, operand_bytes=operand.size, operation=op.mnemonic,
                        address_only=op.mnemonic=='lea', read=read and op.mnemonic!='lea',
                        write=write and op.mnemonic!='lea'))
                continue
            location = previous[0] + operand.mem.disp
            address_only = op.mnemonic == 'lea'
            overlap = (0x21c <= location < 0x220 if address_only else
                       location < 0x220 and 0x21c < location + operand.size)
            if not overlap:
                continue
            read = bool(operand.access & cs.CS_AC_READ)
            write = bool(operand.access & cs.CS_AC_WRITE)
            if op.mnemonic.startswith(('mov', 'vmov')):
                read, write = index != 0, index == 0
            accesses.append(dict(root_rva=hex(previous[1]), instruction_rva=hex(op.address-image_base),
                effective_offset=location, operand_bytes=operand.size, operation=op.mnemonic,
                address_only=address_only, read=read and not address_only, write=write and not address_only))
        tail = (op.mnemonic == 'jmp' and (not op.operands or
                op.operands[0].type != cs.x86.X86_OP_IMM or
                op.operands[0].imm not in positions))
        if op.group(cs.CS_GRP_CALL) or tail:
            target, slot = None, None
            if len(op.operands) == 1:
                operand = op.operands[0]
                if operand.type == cs.x86.X86_OP_IMM:
                    target = hex(operand.imm-image_base)
                elif operand.type == cs.x86.X86_OP_MEM and op.reg_name(operand.mem.base) == 'rip':
                    slot = hex(op.address+op.size+operand.mem.disp-image_base)
            for reg in ARGUMENTS:
                if reg in aliases:
                    offset, root = aliases[reg]
                    calls.append(dict(root_rva=hex(root), call_rva=hex(op.address-image_base),
                        argument_register=reg, alias_offset=offset, target_rva=target,
                        indirect_slot_rva=slot, transfer_kind='tail_jump' if tail else 'call',
                        literal_integer_arguments={r: aliases[('literal',r)] for r in ARGUMENTS
                                                   if ('literal',r) in aliases}))
    counts['reached_instructions'] = sum(x is not None for x in states)
    return accesses, calls, counts


def owned_checks(decoder, cs):
    seed = '488b8128010000'
    store = 'f30f11801c020000'
    cases = [
        ('direct', seed+store, 1),
        ('through_jump', seed+'eb0090'+store, 1),
        ('conditional_same_alias', seed+'750190'+store, 1),
        ('conditional_kill_join', seed+'750231c0'+store, 0),
        ('spill_restore', seed+'4889442440'+'31c0'+'488b442440'+store, 1),
        ('spill_partial_overwrite', seed+'4889442440'+'c644244101'+'31c0'+'488b442440'+store, 0),
        ('spill_call', seed+'4889442440'+'e800000000'+'488b442440'+store, 1),
        ('shadow_spill_call', seed+'4889442410'+'e800000000'+'488b442410'+store, 0),
        ('rebase_stack', seed+'4889442440'+'4883ec20'+'31c0'+'488b442460'+store, 1),
        ('partial_register_kill', seed+'31c0'+store, 0),
        ('unreachable', seed+'c3'+store, 0),
        ('join_different_offset', seed+'75044883c004'+store, 0),
        ('rbp_not_stack_origin', '488b8528010000'+store, 1),
        ('rbp_known_stack_slot', '4889e5488b8528010000'+store, 0),
        ('constant_register_rebase', seed+'41bf000200004c01f8'+'f30f11401c', 1),
        ('constant_index_lea', seed+'b987000000488d0488'+'f30f1100', 1),
    ]
    rows = []
    for name, code, expected in cases:
        raw = bytes.fromhex(code)
        decoded = list(decoder.disasm(raw, 0x1000))
        assert sum(op.size for op in decoded) == len(raw), name
        accesses, _, _ = inspect(decoded, cs, 0x1000)
        assert len(accesses) == expected, (name, accesses)
        rows.append(dict(case=name, overlaps=expected, passed=True))
    raw = bytes.fromhex(seed+'4889c1e800000000')
    _, calls, _ = inspect(list(decoder.disasm(raw, 0x1000)), cs, 0x1000)
    assert len(calls) == 1 and calls[0]['alias_offset'] == 0
    rows.append(dict(case='zero_offset_argument', passed=True))
    first = list(decoder.disasm(bytes.fromhex(seed), 0x1000))
    second = list(decoder.disasm(bytes.fromhex(store), 0x2000))
    accesses, _, _ = inspect(first + second, cs, 0x1000)
    assert not accesses
    rows.append(dict(case='no_fallthrough_across_fragment_gap', passed=True))
    return rows


def runtime_groups(data, pe):
    directory, size = struct.unpack_from('<II', data, pe.directories + 24)
    assert size % 12 == 0 and size // 12 <= 20000
    records = list(struct.iter_unpack('<III', data[pe.offset(directory):pe.offset(directory)+size]))
    table = {start: (start, end, unwind) for start, end, unwind in records}
    assert len(table) == len(records)
    roots = {}
    for original in records:
        current = original
        visited = set()
        while True:
            start, end, unwind = current
            assert 0 < end-start <= 0x40000
            assert unwind not in visited and len(visited) < 16
            visited.add(unwind)
            at = pe.offset(unwind)
            version, _, codes, _ = struct.unpack_from('<BBBB', data, at)
            assert version & 7 in (1, 2)
            if not (version >> 3) & 4:
                break
            chained = struct.unpack_from('<III', data, at+4+((codes+1)&~1)*2)
            assert table.get(chained[0]) == chained
            current = chained
        roots[original[0]] = current[0]
    groups = {}
    for record in records:
        groups.setdefault(roots[record[0]], []).append(record)
    for root, members in groups.items():
        members.sort()
        assert members[0][0] == root
        assert sum(end-start for start,end,_ in members) <= 0x40000
        assert all(a[1] <= b[0] for a,b in zip(members,members[1:]))
    return groups, len(records)


def run(dll, decoder_dir):
    data = dll.read_bytes()
    assert hashlib.sha256(data).hexdigest() == SHA256
    pe = PE(data)
    assert pe.width == 8 and pe.machine == 0x8664
    sys.path.insert(0, str(decoder_dir.resolve()))
    cs = importlib.import_module('capstone')
    assert cs.__version__ == '5.0.3'
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    decoder.detail = True
    fixtures = owned_checks(decoder, cs)
    groups, record_count = runtime_groups(data, pe)
    totals = Counter()
    totals['exception_ranges'] = record_count
    totals['runtime_groups'] = len(groups)
    totals['chained_fragments'] = record_count-len(groups)
    accesses, calls = [], []
    indexed = []
    decode_gaps = {}
    def decode_group(root):
        result = []
        for start, end, _ in groups[root]:
            raw = data[pe.offset(start):pe.offset(start)+end-start]
            decoded = list(decoder.disasm(raw, pe.image_base+start))
            covered = sum(op.size for op in decoded)
            if covered != len(raw):
                decode_gaps[start] = dict(fragment_rva=hex(start), group_rva=hex(root),
                    undecoded_start_rva=hex(start+covered), undecoded_bytes=len(raw)-covered)
            result.extend(decoded)
        return result
    for start, members in sorted(groups.items()):
        if not any(struct.pack('<I', offset) in data[pe.offset(a):pe.offset(a)+e-a]
                   for a,e,_ in members for offset in (0x128, 0x208)):
            continue
        decoded = decode_group(start)
        totals['selected_groups'] += 1
        totals['selected_fragments'] += len(members)
        totals['decoded_instructions'] += len(decoded)
        group_indexed = []
        found, passed, counts = inspect(decoded, cs, pe.image_base, indexed=group_indexed)
        totals.update(counts)
        for row in found+passed+group_indexed:
            row['range_start_rva'] = hex(start)
        accesses += found
        calls += passed
        indexed += group_indexed
        if len(accesses)+len(calls) > 3000:
            raise ValueError('Candidate report bound exceeded')
    # Two direct callee levels, preserving each argument/offset independently.
    requests = sorted({(int(row['target_rva'],16), row['argument_register'], row['alias_offset'])
                       for row in calls if row['target_rva'] and int(row['target_rva'],16) in groups})
    assert len(requests) <= 200
    callee_rows = []
    visited = set()
    for depth in (1, 2):
        additional = set()
        for target, register, offset in requests:
            if (target, register, offset) in visited:
                continue
            visited.add((target, register, offset))
            decoded = decode_group(target)
            found, passed, counts = inspect(decoded, cs, pe.image_base, {register: (offset, target)})
            callee_rows.append(dict(target_rva=hex(target), argument_register=register, alias_offset=offset,
                depth=depth, decoded_instructions=len(decoded), bit17_overlaps=found,
                feature_argument_calls=passed, counts=dict(counts)))
            additional.update((int(row['target_rva'],16), row['argument_register'], row['alias_offset'])
                for row in passed if row['target_rva'] and int(row['target_rva'],16) in groups)
        assert len(visited)+len(additional) <= 200
        requests = sorted(additional)
    imports = import_slots(data, pe)
    for row in accesses:
        row['known_role'] = KNOWN.get(int(row['instruction_rva'],16), 'unreviewed')
    indexed_reviews = {
        0x7b4b0: 'Frequency-band helper; existing native boundary verification in 408',
        0xd2cc0: 'At d6bc3: r15 begins 16c; outer loop advances 4 for 20 iterations; writes 16c..1b8',
        0xe3640: 'Band/cepstral classification helper; native stage verification in 409-410',
        0xe4240: 'Temporal array helper; native indexed-history verification in 408-409',
    }
    for row in indexed:
        if row['write']:
            row['review'] = indexed_reviews.get(int(row['range_start_rva'],16), 'unreviewed')
    for row in calls:
        classify(row, imports)
    for audit in callee_rows:
        for row in audit['feature_argument_calls']:
            classify(row, imports)
    paths = [Path(__file__), Path(__file__).with_name('audit_vqm_feature_pointer_accesses.py'),
             Path(__file__).with_name('audit_vqm_v4_module_leads.py'),
             Path(__file__).with_name('audit_frm2_numeric.py')]
    return dict(schema=1, dse_sha256=SHA256, decoder_version=cs.__version__,
        source_sha256={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in paths},
        owned_checks=fixtures, counts=dict(totals), bit17_overlaps=accesses,
        indexed_feature_pointer_leads=indexed,
        decode_gaps=list(decode_gaps.values()),
        feature_argument_calls=calls, direct_callee_audits=callee_rows,
        caller_classifications=dict(Counter(row['review']['classification'] for row in calls)),
        indexed_write_groups=dict(Counter(row['range_start_rva'] for row in indexed if row['write'])),
        callee_classifications=dict(Counter(row['review']['classification'] for audit in callee_rows
                                           for row in audit['feature_argument_calls'])),
        scope='PE chained-unwind grouping; must-alias intersections at direct CFG joins, affine aliases, constant integer rebasing, private stack spills; '
              'all four integer argument-register leads including zero offsets; two exact-entry direct callee levels.',
        limits=['Pointer-shaped loads are untyped leads, not proof of Frame ownership',
                'Only PE chained-unwind fragments are grouped; unrelated ranges and indirect branches are not joined',
                'Undecoded tails are explicitly recorded, not treated as audited code',
                'Indexed accesses, escaping stack spill addresses and unknown callees are not modeled',
                'A live integer register is not necessarily consumed by the called function',
                'RBP without a resolved stack origin remains an untyped pointer-shaped-load candidate',
                'No returned-pointer tracking, indirect-callee recovery or producer absence proof'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ('dll', 'decoder-dir', 'output'):
        parser.add_argument('--'+option, type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.decoder_dir)
    args.output.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(counts=report['counts'], overlaps=len(report['bit17_overlaps']),
                         calls=len(report['feature_argument_calls']),
                         callee_audits=len(report['direct_callee_audits']))))


if __name__ == '__main__':
    main()
