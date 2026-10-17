"""Read-only older API leads and feature-label boundaries against pinned V6.

Only hashes, names, RVAs, counts and access classifications are persisted.
Older modules are never loaded or selected in Ghidra. Linear decoding is a lead
inventory, not an interprocedural proof that a producer does not exist.
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
from audit_vqm_feature_calls_cfg import runtime_groups
from audit_vqm_v4_module_leads import PE, V4_SHA256
from locate_vqm_feature_accesses import executable_sections


MODULE_HASHES = {
    'va_DSE4.dll': V4_SHA256,
    'va_DBM4.dll': 'e1ed2ddb16310f0f848055d95e292a619297dd0eeb4a44010e355023ebc3d567',
    'OpenVS4.dll': '3a3faf18c9ad554fc96884894682fabd881eea2fff8d447012a2793ea87e2c04',
}


def operand_rows(instructions, cs, image_base):
    rows = []
    for op in instructions:
        if not op.id:
            continue
        for index, operand in enumerate(op.operands):
            if operand.type != cs.x86.X86_OP_MEM or operand.mem.disp != 0x21c:
                continue
            address_only = op.mnemonic == 'lea'
            read = bool(operand.access & cs.CS_AC_READ)
            write = bool(operand.access & cs.CS_AC_WRITE)
            if op.mnemonic.startswith(('mov', 'vmov')):
                read, write = index != 0, index == 0
            rows.append(dict(instruction_rva=hex(op.address - image_base),
                operation=op.mnemonic, operand_bytes=operand.size,
                base_register=op.reg_name(operand.mem.base), indexed=bool(operand.mem.index),
                address_only=address_only, read=read and not address_only,
                write=write and not address_only))
    return rows


def owned_checks(decoder, cs):
    cases = [
        ('owned_scalar_read', 'f30f10801c020000', 1, True, False),
        ('owned_scalar_write', 'f30f11801c020000', 1, False, True),
        ('stack_byte_write', 'c684241c02000001', 1, False, True),
        ('address_only', '8d801c020000', 1, False, False),
        ('indexed_scalar_write', 'f30f1184881c020000', 1, False, True),
        ('literal_not_memory', 'b81c020000', 0, False, False),
        ('adjacent_scalar_not_target', 'f30f108018020000', 0, False, False),
    ]
    result = []
    for name, text, count, read, write in cases:
        raw = bytes.fromhex(text)
        ops = list(decoder.disasm(raw, 0))
        assert sum(op.size for op in ops) == len(raw), name
        rows = operand_rows(ops, cs, 0)
        assert len(rows) == count, (name, rows)
        if count:
            assert rows[0]['read'] == read and rows[0]['write'] == write, name
        result.append(dict(case=name, passed=True))
    return result


def name_tables(pe):
    result = []
    for lead in pe.name_table_leads():
        marker = int(lead['pointer_reference_rva'], 16)
        start = marker
        for _ in range(128):
            pointer = struct.unpack_from(pe.pointer_format, pe.data, pe.offset(start - pe.width))[0]
            if pe.text(pointer - pe.image_base) is None:
                break
            start -= pe.width
        else:
            raise ValueError('Feature name table exceeds left search bound')
        names = []
        for index in range(256):
            pointer = struct.unpack_from(pe.pointer_format, pe.data, pe.offset(start + index * pe.width))[0]
            name = pe.text(pointer - pe.image_base)
            if name is None:
                break
            names.append(name)
        else:
            raise ValueError('Feature name table exceeds right search bound')
        assert 99 <= len(names) <= 180 and names[89] == 'PitchMFPhaseFlatness'
        assert (marker - start) // pe.width == 89
        result.append(dict(table_start_rva=hex(start), contiguous_name_pointer_words=len(names),
            pointer_bytes=pe.width, phase_label_index=89,
            matched_prefix_names=99,
            prefix_labels_sha256=hashlib.sha256('\n'.join(names[:99]).encode('ascii')).hexdigest(),
            first_label=names[0], prefix_last_label=names[98],
            following_labels=names[99:]))
    assert len(result) == 2
    return result


def imports(pe):
    start, size = struct.unpack_from('<II', pe.data, pe.directories + 8)
    assert 0 < size <= 0x10000
    result = []
    for at in range(pe.offset(start), pe.offset(start) + size, 20):
        lookup, stamp, forwarder, name, iat = struct.unpack_from('<5I', pe.data, at)
        if not any((lookup, stamp, forwarder, name, iat)):
            break
        count = 0
        for index in range(2048):
            pointer = struct.unpack_from(pe.pointer_format, pe.data,
                pe.offset(lookup or iat) + index * pe.width)[0]
            if not pointer:
                break
            count += 1
        else:
            raise ValueError('Module import count exceeds bound')
        result.append(dict(module=pe.text(name), symbols=count))
    return result


def validate_older_roles(ops, cs, pe):
    by_rva = {op.address - pe.image_base: op for op in ops if op.id}
    def memory(rva, operand, base, displacement, size):
        op = by_rva[rva]
        target = op.operands[operand]
        assert target.type == cs.x86.X86_OP_MEM and target.size == size
        assert op.reg_name(target.mem.base) == base and target.mem.disp == displacement
        assert not target.mem.index
    # Same-name getter and comparator follow the older leaf/Frame layout.
    label = by_rva[0x7f576]
    assert label.mnemonic == 'mov' and label.operands[1].type == cs.x86.X86_OP_IMM
    assert pe.text(label.operands[1].imm - pe.image_base) == 'dynamics'
    memory(0x7f5a9, 1, 'edi', 0x150, 4)
    memory(0x7f5b0, 1, 'eax', 0x1b0, 4)
    memory(0x7f5b7, 1, 'eax', 0x21c, 4)
    memory(0x7f5c1, 0, 'eax', 0, 4)
    for rva, displacement in ((0x7f9d6, 0x150), (0x7f9dc, 0x1b0),
            (0x7f9e2, 0x21c), (0x7f9f0, 0x150), (0x7f9f6, 0x1b0), (0x7f9fc, 0x21c)):
        memory(rva, 1, 'eax', displacement, 4)
    assert by_rva[0x7fa04].mnemonic == 'ucomiss'
    for rva, base in ((0xa18e3, 'esi'), (0xa18f1, 'edx'), (0xa18ff, 'edi')):
        memory(rva, 1, base, 0x1b0, 4)
    for rva in (0xa18e9, 0xa18f7):
        memory(rva, 1, 'eax', 0x21c, 4)
    memory(0xa1911, 0, 'eax', 0x21c, 4)
    assert [by_rva[rva].mnemonic for rva in (0xa1905, 0xa1909, 0xa190d)] == ['subss', 'mulss', 'addss']
    zero = by_rva[0x9f300]
    memory(0x9f300, 0, 'esi', 0x21c, 4)
    assert zero.operands[1].type == cs.x86.X86_OP_IMM and zero.operands[1].imm == 0
    memory(0xaba02, 0, 'esi', 0x21c, 4)
    assert by_rva[0xab9c2].mnemonic == 'xor' and by_rva[0xab9c2].op_str == 'eax, eax'
    a = next(i for i, op in enumerate(ops) if op.address - pe.image_base == 0xab9c2)
    b = next(i for i, op in enumerate(ops) if op.address - pe.image_base == 0xaba02)
    assert all(not op.group(cs.CS_GRP_CALL) and 'eax' not in {
        op.reg_name(register) for register in op.regs_access()[1]} for op in ops[a + 1:b] if op.id)
    return dict(same_name_getter=dict(read_rva='0x7f5b7', name_load_rva='0x7f576',
            leaf_frame_offset='0x150', frame_feature_offset='0x1b0', feature_value_offset='0x21c',
            result_copy_rva='0x7f5c1', operation='raw_four_byte_copy'),
        comparator=dict(read_rvas=['0x7f9e2', '0x7f9fc'], compare_rva='0x7fa04'),
        interpolation=dict(read_rvas=['0xa18e9', '0xa18f7'], write_rva='0xa1911',
            arithmetic_rvas=['0xa1905', '0xa1909', '0xa190d'], operation='float32_subtract_multiply_add'),
        large_object_zero_writes=[dict(write_rva='0x9f300', value=0,
                later_same_base_store_rva='0x9f31e', later_offset='0x25c'),
            dict(write_rva='0xaba02', zero_definition_rva='0xab9c2',
                earlier_same_base_read_rva='0xab835', earlier_offset='0x328')],
        scope='Pinned local operand chains, not Ghidra V4 function metadata or runtime verification')


def run(dll, api_dir, decoder_dir):
    sys.path.insert(0, str(decoder_dir.resolve()))
    cs = importlib.import_module('capstone')
    assert cs.__version__ == '5.0.3'
    old_decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_32)
    old_decoder.detail = True
    fixtures = owned_checks(old_decoder, cs)
    old_decoder.skipdata = True
    modules = []
    older_tables = None
    for name, digest in MODULE_HASHES.items():
        data = (api_dir / name).read_bytes()
        assert hashlib.sha256(data).hexdigest() == digest, name
        pe = PE(data)
        assert pe.machine == 0x14c and pe.width == 4
        ops = []
        for _, start, code in executable_sections(data):
            assert len(code) <= 0x400000
            ops.extend(old_decoder.disasm(code, pe.image_base + start))
        assert len(ops) <= 1000000
        rows = operand_rows(ops, cs, pe.image_base)
        row = dict(module=name, sha256=digest, file_bytes=len(data),
            image_base=hex(pe.image_base), machine='x86', exports=pe.exports(), imports=imports(pe),
            decoded_operations=len(ops), undecoded_bytes=sum(op.size for op in ops if not op.id),
            literal_21c_memory_operands=rows,
            literal_counts={key:data.count(key.encode('ascii')) for key in
                ('FRM2', 'SMS2', 'VQM', 'PitchMFPhaseFlatness', 'CSMSFrame')})
        if name == 'va_DSE4.dll':
            older_tables = name_tables(pe)
            name_refs = []
            for op in ops:
                if not op.id:
                    continue
                for operand in op.operands:
                    target = (operand.imm - pe.image_base if operand.type == cs.x86.X86_OP_IMM else
                        operand.mem.disp - pe.image_base if operand.type == cs.x86.X86_OP_MEM
                        and not operand.mem.base else None)
                    if target is None:
                        continue
                    for table in older_tables:
                        start = int(table['table_start_rva'], 16)
                        end = start + table['contiguous_name_pointer_words'] * pe.width
                        if start - 0x100 <= target < end + 0x100:
                            name_refs.append(dict(instruction_rva=hex(op.address - pe.image_base),
                                target_rva=hex(target), inside_name_pointer_run=start <= target < end,
                                inside_following_labels=start + 99 * pe.width <= target < end))
            assert not any(x['inside_name_pointer_run'] for x in name_refs)
            row.update(feature_name_tables=older_tables, nearby_name_pointer_references=name_refs,
                reviewed_roles=validate_older_roles(ops, cs, pe))
            assert len(rows) == 20
        else:
            assert not rows
        modules.append(row)
    data = dll.read_bytes()
    assert hashlib.sha256(data).hexdigest() == SHA256
    pe = PE(data)
    assert pe.machine == 0x8664 and pe.width == 8
    tables = name_tables(pe)
    assert {x['prefix_labels_sha256'] for x in tables + older_tables} == {tables[0]['prefix_labels_sha256']}
    assert [x['contiguous_name_pointer_words'] for x in older_tables] == [143, 124]
    assert all(x['contiguous_name_pointer_words'] == 99 for x in tables)
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    decoder.detail = True
    refs, counts = [], Counter()
    groups, fragments = runtime_groups(data, pe)
    for root, members in groups.items():
        for start, end, _ in members:
            ops = list(decoder.disasm(data[pe.offset(start):pe.offset(start) + end - start], pe.image_base + start))
            counts['decoded_operations'] += len(ops)
            counts['undecoded_bytes'] += end - start - sum(op.size for op in ops)
            for op in ops:
                for operand in op.operands:
                    if operand.type != cs.x86.X86_OP_MEM or op.reg_name(operand.mem.base) != 'rip':
                        continue
                    target = op.address + op.size + operand.mem.disp - pe.image_base
                    for table in tables:
                        base = int(table['table_start_rva'], 16)
                        if base - 0x100 <= target < base + 99 * 8 + 0x100:
                            refs.append(dict(group_rva=hex(root), instruction_rva=hex(op.address - pe.image_base),
                                target_rva=hex(target), inside_name_table=base <= target < base + 99 * 8))
    assert len(refs) == 8 and not any(row['inside_name_table'] for row in refs)
    paths = [Path(__file__), *(Path(__file__).with_name(name) for name in (
        'audit_frm2_numeric.py', 'audit_vqm_feature_calls_cfg.py', 'audit_vqm_feature_pointer_accesses.py',
        'audit_vqm_v4_module_leads.py', 'locate_vqm_feature_accesses.py'))]
    return dict(schema=1, dse_sha256=SHA256, decoder_version=cs.__version__,
        source_sha256={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in paths},
        owned_instruction_checks=fixtures, older_modules=modules,
        v6_feature_name_tables=tables, v6_nearby_name_table_references=refs,
        v6_counts=dict(counts, runtime_groups=len(groups), exception_ranges=fragments),
        conclusions=['Older same-name dynamics getter uses raw feature+21c; its comparator and reviewed interpolation contain no new numerical production formula',
            'All four bounded name-pointer runs share a99-label prefix; older runs continue into44/25 additional labels. PitchMFPhaseFlatness is prefix label89, with no verified serialized-bit mapping',
            'Nearby V6 direct RIP references use threshold words outside the name arrays',
            'The two additional installed API modules have no literal displacement21c memory operand in this bounded linear inventory'],
        limits=['Literal-offset linear decoding and local chains only; it is not typed whole-program analysis or absence proof',
                'Bulk copies, rebased pointers, indirect/indexed paths and malformed/undecoded areas remain outside this inventory',
                'Older modules were read only, not loaded, selected in Ghidra or tested at runtime',
                'Nearby code references, absent imports/literals or identical names do not establish feature semantics or producer absence',
                'A contiguous run of valid string pointers does not prove a single declared array or that every named feature is implemented',
                'No original authoring entry, physical unit, nonzero bit17 assignment or VQM/Growl completion is proved'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'api-dir', 'decoder-dir', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.api_dir, args.decoder_dir)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(modules=len(report['older_modules']),
        older_offset_operands=sum(len(x['literal_21c_memory_operands']) for x in report['older_modules']),
        matched_name_tables=4, nearby_v6_references=len(report['v6_nearby_name_table_references']))))


if __name__ == '__main__':
    main()
