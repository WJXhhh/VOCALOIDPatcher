"""Bounded local affine-pointer audit for the VQM feature bit17 cell.

This scans pointer-shaped loads, not typed objects or complete control flow.
Owned instruction fixtures validate alias invalidation and byte overlap.
Reports contain addresses and classifications, never installed code bytes.
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
from audit_vqm_v4_module_leads import PE


VOLATILE = {'rax', 'rcx', 'rdx', 'r8', 'r9', 'r10', 'r11'}
ARGUMENTS = ('rcx', 'rdx', 'r8', 'r9')
KNOWN = {
    0x84fe6: 'DRS feature interpolation source 1',
    0x84ff5: 'DRS feature interpolation source 2',
    0x85011: 'DRS feature interpolation destination',
    0x1100d8: 'TimbreModel dynamics getter',
    0x1347f6: 'DSE5 feature interpolation source 1',
    0x134805: 'DSE5 feature interpolation source 2',
    0x134820: 'DSE5 feature interpolation destination',
}


def parent_register(name):
    groups = (
        ('rax', 'eax', 'ax', 'al', 'ah'), ('rcx', 'ecx', 'cx', 'cl', 'ch'),
        ('rdx', 'edx', 'dx', 'dl', 'dh'), ('rbx', 'ebx', 'bx', 'bl', 'bh'),
        ('rsi', 'esi', 'si', 'sil'), ('rdi', 'edi', 'di', 'dil'),
        ('rbp', 'ebp', 'bp', 'bpl'), ('rsp', 'esp', 'sp', 'spl'),
    )
    for group in groups:
        if name in group:
            return group[0]
    for number in range(8, 16):
        if name in (f'r{number}', f'r{number}d', f'r{number}w', f'r{number}b'):
            return f'r{number}'
    return name


def inspect(instructions, cs, image_base, indexed_rows=None):
    aliases = {}
    accesses, calls = [], []
    counts = Counter()
    for number, op in enumerate(instructions):
        stale = [key for key, value in aliases.items() if number - value[2] > 64]
        for key in stale:
            del aliases[key]
        counts['expired_aliases'] += len(stale)
        for operand_index, operand in enumerate(op.operands):
            if operand.type != cs.x86.X86_OP_MEM:
                continue
            register = op.reg_name(operand.mem.base)
            if register not in aliases:
                continue
            if operand.mem.index:
                counts['indexed_operands_excluded'] += 1
                if indexed_rows is not None:
                    offset, root, _ = aliases[register]
                    read = bool(operand.access & cs.CS_AC_READ)
                    write = bool(operand.access & cs.CS_AC_WRITE)
                    if op.mnemonic.startswith(('mov', 'vmov')):
                        read, write = operand_index != 0, operand_index == 0
                    indexed_rows.append(dict(root_load_rva=hex(root), instruction_rva=hex(
                        op.address - image_base), alias_offset=offset,
                        displacement=operand.mem.disp, index_register=op.reg_name(operand.mem.index),
                        index_scale=operand.mem.scale, operand_bytes=operand.size,
                        instruction_kind=op.mnemonic, address_only=op.mnemonic == 'lea',
                        read=read and op.mnemonic != 'lea', write=write and op.mnemonic != 'lea'))
                continue
            offset, root, born = aliases[register]
            location = offset + operand.mem.disp
            is_address = op.mnemonic == 'lea'
            read = bool(operand.access & cs.CS_AC_READ) and not is_address
            write = bool(operand.access & cs.CS_AC_WRITE) and not is_address
            # Capstone 5.0.3 incorrectly marks the destination memory of
            # MOVUPS as read-only. Use copy operand direction for MOV families,
            # checked against both owned store and load encodings below.
            if op.mnemonic.startswith(('mov', 'vmov')):
                read, write = operand_index != 0, operand_index == 0
            if (0x21c <= location < 0x220 if is_address else
                    location < 0x220 and 0x21c < location + operand.size):
                accesses.append(dict(root_load_rva=hex(root), instruction_rva=hex(
                    op.address - image_base), alias_offset=offset,
                    effective_offset=location, operand_bytes=operand.size,
                    address_only=is_address,
                    read=read, write=write,
                    instruction_kind=op.mnemonic))
        if op.group(cs.CS_GRP_JUMP) or op.group(cs.CS_GRP_RET):
            counts['branch_or_return_alias_resets'] += bool(aliases)
            aliases.clear()
            continue
        if op.group(cs.CS_GRP_CALL):
            for register in ARGUMENTS:
                if register in aliases and aliases[register][0] != 0:
                    offset, root, _ = aliases[register]
                    calls.append(dict(root_load_rva=hex(root), call_rva=hex(
                        op.address - image_base), argument_register=register,
                        alias_offset=offset))
            aliases = {key: value for key, value in aliases.items() if key not in VOLATILE}
            continue
        value = None
        if (len(op.operands) == 2 and op.operands[0].type == cs.x86.X86_OP_REG
                and op.operands[0].size == 8):
            destination, source = op.operands
            register = op.reg_name(destination.reg)
            if op.mnemonic == 'mov' and source.type == cs.x86.X86_OP_REG:
                value = aliases.get(op.reg_name(source.reg))
            elif (op.mnemonic == 'lea' and source.type == cs.x86.X86_OP_MEM
                  and not source.mem.index and op.reg_name(source.mem.base) in aliases):
                previous = aliases[op.reg_name(source.mem.base)]
                value = (previous[0] + source.mem.disp, previous[1], previous[2])
            elif (op.mnemonic in ('add', 'sub') and source.type == cs.x86.X86_OP_IMM
                  and register in aliases):
                previous = aliases[register]
                value = (previous[0] + source.imm * (1 if op.mnemonic == 'add' else -1),
                         previous[1], previous[2])
            elif (op.mnemonic == 'mov' and source.type == cs.x86.X86_OP_MEM
                  and source.size == 8 and not source.mem.index
                  and source.mem.disp in (0x128, 0x208)
                  and op.reg_name(source.mem.base) not in ('rsp', 'rbp', 'rip', '')):
                value = (0, op.address - image_base, number)
                counts['pointer_shaped_loads'] += 1
        # A write to EAX/AL invalidates a tracked RAX just as a full write does.
        for register in op.regs_access()[1]:
            aliases.pop(parent_register(op.reg_name(register)), None)
        if value is not None:
            aliases[op.reg_name(op.operands[0].reg)] = value
    return accesses, calls, counts


def owned_checks(decoder, cs):
    seed = '488b8128010000'  # Owned fixture: RAX <- [RCX+0x128].
    shift = '488db800020000'  # RDI <- RAX+0x200.
    store = 'f30f11471c'  # float store at RDI+0x1c.
    cases = (
        ('lea_split_offset', seed + shift + store, 1, True),
        ('add_split_offset', seed + '480500020000' + 'f30f11401c', 1, True),
        ('sub_split_offset', seed + shift + '4883ef04' + 'f30f114720', 1, True),
        ('partial_register_kill', seed + '31c0' + 'f30f11801c020000', 0, False),
        ('branch_reset', seed + shift + '7500' + store, 0, False),
        ('volatile_call_kill', seed + 'e800000000' + 'f30f11801c020000', 0, False),
        ('nonvolatile_call_survival', seed + shift + 'e800000000' + store, 1, True),
        ('indexed_operand_excluded', seed + shift + 'f30f11448f1c', 0, False),
        ('wide_overlap', seed + '0f118010020000', 1, True),
        ('wide_overlap_read', seed + '0f108010020000', 1, False),
        ('partial_tail_store', seed + '6689801e020000', 1, True),
        ('upper_boundary_no_overlap', seed + 'f30f118020020000', 0, False),
        ('adjacent_narrow_no_overlap', seed + 'f30f118018020000', 0, False),
        ('lea_is_not_read', seed + '488db818020000', 0, False),
        ('exact_address_only', seed + '488db81c020000', 1, False),
        ('alias_age_boundary', seed + '90' * 63 + 'f30f11801c020000', 1, True),
        ('alias_age_expiry', seed + '90' * 64 + 'f30f11801c020000', 0, False),
    )
    rows = []
    for name, code, count, writes in cases:
        raw = bytes.fromhex(code)
        instructions = list(decoder.disasm(raw, 0x1000))
        assert sum(op.size for op in instructions) == len(raw), name
        accesses, _, _ = inspect(instructions, cs, 0x1000)
        assert len(accesses) == count, (name, accesses)
        assert any(row['write'] for row in accesses) == writes, (name, accesses)
        rows.append(dict(case=name, accesses=count, memory_write=writes, passed=True))
    # Call arguments are recorded before ABI invalidation, without pretending
    # the called function's memory effects are known.
    code = bytes.fromhex(seed + '488d8800020000' + 'e800000000')
    _, calls, _ = inspect(list(decoder.disasm(code, 0x1000)), cs, 0x1000)
    assert len(calls) == 1 and calls[0]['alias_offset'] == 0x200
    rows.append(dict(case='shifted_argument_pass', shifted_calls=1, passed=True))
    return rows


def run(args):
    data = args.dll.read_bytes()
    if hashlib.sha256(data).hexdigest() != SHA256:
        raise ValueError('Review a changed DSE before reusing the address checks')
    pe = PE(data)
    if pe.machine != 0x8664 or pe.width != 8:
        raise ValueError('Only the reviewed Windows x64 module is supported')
    sys.path.insert(0, str(args.decoder_dir.resolve()))
    cs = importlib.import_module('capstone')
    if cs.__version__ != '5.0.3':
        raise ValueError('Review a changed instruction decoder first')
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    decoder.detail = True
    fixtures = owned_checks(decoder, cs)
    directory, size = struct.unpack_from('<II', data, pe.directories + 24)
    if size % 12 or size // 12 > 20000:
        raise ValueError('Exception table outside the bounded record count')
    totals = Counter()
    accesses, calls = [], []
    for at in range(0, size, 12):
        start, end, _ = struct.unpack_from('<III', data, pe.offset(directory) + at)
        if not 0 < end - start <= 0x40000:
            raise ValueError('Unexpected exception-code range')
        raw = data[pe.offset(start):pe.offset(start) + end - start]
        totals['exception_ranges'] += 1
        if not any(struct.pack('<I', offset) in raw for offset in (0x128, 0x208)):
            continue
        instructions = list(decoder.disasm(raw, pe.image_base + start))
        totals['selected_ranges'] += 1
        totals['decoded_instructions'] += len(instructions)
        totals['undecoded_tail_bytes'] += len(raw) - sum(op.size for op in instructions)
        rows, passed, counts = inspect(instructions, cs, pe.image_base)
        totals.update(counts)
        for row in rows + passed:
            row.update(range_start_rva=hex(start), range_end_rva=hex(end))
        accesses += rows
        calls += passed
        if len(accesses) + len(calls) > 500:
            raise ValueError('Candidate count exceeds this bounded audit')
    for row in accesses:
        row['known_role'] = KNOWN.get(int(row['instruction_rva'], 16), 'unclassified')
    return dict(dse_sha256=SHA256, decoder_version=cs.__version__,
                source_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                owned_decoder_checks=fixtures, counts=dict(totals),
                bit17_overlaps=accesses, shifted_argument_calls=calls,
                scope='Local MOV/LEA/ADD/SUB affine aliases, at most 64 instructions '
                      'from pointer-shaped loads at +0x128/+0x208. Reset at branches, '
                      'returns and exception range boundaries; kill volatile aliases '
                      'at calls and canonicalize partial-register writes. Pointer loads '
                      'are untyped leads, not proof of Frame ownership. Excludes indexed '
                      'addressing, stack spills, pointers produced as function results, '
                      'function arguments, other offsets and callee memory effects. '
                      'Exception records can be fragments rather than complete functions. '
                      'Empty shifted results do not prove absence of a producer.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'decoder-dir', 'output'):
        parser.add_argument('--' + name, required=True, type=Path)
    args = parser.parse_args()
    report = run(args)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(checks=len(report['owned_decoder_checks']),
                          counts=report['counts'], overlaps=len(report['bit17_overlaps']),
                          shifted_calls=len(report['shifted_argument_calls']))))


if __name__ == '__main__':
    main()
