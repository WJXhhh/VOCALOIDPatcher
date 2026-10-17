"""Check four native whole-feature copies and two historical stack candidates.

Generated feature blocks exercise active and inactive field bytes. The native
functions receive genuine constructed Frames in an isolated process. No bank
payloads or proprietary instruction bytes are persisted.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import importlib
import json
from pathlib import Path
import struct
import sys

from audit_frm2_numeric import I, Native, P, Q, SHA256
from audit_vqm_v4_module_leads import PE


ROUTES = (
    ('DRS_copy_content', 0x7ec30, 0x128, 0x50, 0x636c0, 3),
    ('DRS_copy_frame', 0x7ec30, 0x128, 0x50, 0x80250, 4),
    ('DSE5_copy_frame', 0x12a670, 0x208, 0x150, 0x12bbc0, 4),
    ('DSE5_copy_content', 0x12a670, 0x208, 0x150, 0x12c800, 3),
)
ARRAYS = ((0x2c, 40), (0xcc, 40), (0x16c, 20), (0x1bc, 20))


def import_slots(pe: PE) -> dict:
    rva = struct.unpack_from('<I', pe.data, pe.directories + 8)[0]
    slots = {}
    for index in range(128):
        descriptor = struct.unpack_from('<5I', pe.data, pe.offset(rva) + index * 20)
        if not any(descriptor):
            return slots
        original, _, _, module, first = descriptor
        if not original:
            continue
        for item in range(4096):
            value = struct.unpack_from('<Q', pe.data, pe.offset(original) + item * 8)[0]
            if not value:
                break
            if not value & (1 << 63):
                slots[first + item * 8] = dict(module=pe.text(module), name=pe.text(value + 2))
        else:
            raise ValueError('Import table exceeds bounded count')
    raise ValueError('Import descriptors exceed bounded count')


def stack_review(pe: PE, decoder_dir: Path) -> dict:
    sys.path.insert(0, str(decoder_dir.resolve()))
    cs = importlib.import_module('capstone')
    if cs.__version__ != '5.0.3':
        raise ValueError('Review a changed decoder first')
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    decoder.detail = True
    directory, size = struct.unpack_from('<II', pe.data, pe.directories + 24)
    ranges = [struct.unpack_from('<III', pe.data, pe.offset(directory) + i)
              for i in range(0, size, 12)]
    start, end, _ = next(row for row in ranges if row[0] <= 0x8b10e < row[1])
    if (start, end) != (0x8a6a0, 0x8b285):
        raise AssertionError('Exception-directory range differs')
    instructions = list(decoder.disasm(pe.data[pe.offset(start):pe.offset(start) + end - start],
                                      pe.image_base + start))
    by_rva = {instruction.address - pe.image_base: instruction for instruction in instructions}
    # Both historical hits are RBP-based locals. RBP originates from entry RSP;
    # it is not this or a feature pointer in the encompassing routine.
    a, b = by_rva[0x8a6a0], by_rva[0x8a6af]
    if (a.mnemonic != 'mov' or a.op_str != 'rax, rsp' or b.mnemonic != 'lea'
            or b.operands[0].reg != cs.x86.X86_REG_RBP
            or b.operands[1].mem.base != cs.x86.X86_REG_RAX
            or b.operands[1].mem.disp != -0x4b8):
        raise AssertionError('Stack frame derivation differs')
    if any(x.operands and x.operands[0].type == cs.x86.X86_OP_REG
           and x.operands[0].reg == cs.x86.X86_REG_RBP
           and x.mnemonic not in ('cmp', 'test', 'push')
           for x in instructions[1:] if 0x8a6af < x.address - pe.image_base <= 0x8b18c):
        raise AssertionError('RBP has another assignment; re-review provenance')
    rows = []
    for candidate, expected in ((0x8b10e, 'lea'), (0x8b16d, 'mov')):
        instruction = next(x for x in instructions
            if x.address <= pe.image_base + candidate < x.address + x.size)
        memory = [operand for operand in instruction.operands
                  if operand.type == cs.x86.X86_OP_MEM]
        if (instruction.mnemonic != expected or len(memory) != 1
                or memory[0].mem.base != cs.x86.X86_REG_RBP
                or memory[0].mem.disp != 0x21c):
            raise AssertionError('Stack candidate instruction differs')
        rows.append(dict(candidate_rva=hex(candidate), instruction_rva=hex(
            instruction.address - pe.image_base), classification='stack_record_header',
            entry_rsp_relative_offset='-0x29c', operation=expected))
    call = by_rva[0x8b112]
    memory = call.operands[0].mem
    slot = call.address + call.size + memory.disp - pe.image_base
    imported = import_slots(pe)[slot]
    if (call.mnemonic != 'call' or memory.base != cs.x86.X86_REG_RIP
            or imported['name'] != 'fread'):
        raise AssertionError('Stack-local read is not fread')
    return dict(decoder_version=cs.__version__, exception_range_start_rva=hex(start),
        exception_range_end_rva=hex(end),
        stack_basis=dict(entry_rsp_capture_rva='0x8a6a0', rbp_derivation_rva='0x8a6af',
                         rbp_entry_rsp_offset='-0x4b8'), candidates=rows,
        read_call_rva='0x8b112', import_slot_rva=hex(slot), imported=imported,
        scope='Complete decoding of this bounded PE runtime range with explicit RBP provenance; '
              'not a whole-library decompilation or execution of its file-cache routine.')


def block(style: str, bit17: float) -> bytes:
    values = tuple(0.125 + i / 256 for i in range(0x228 // 4))
    payload = bytearray(struct.pack(f'<{len(values)}f', *values))
    flags = 0x1fffff
    if style == 'zero_arrays':
        for offset, count in ARRAYS:
            payload[offset:offset + 4 * count] = bytes(4 * count)
    elif style == 'only_bit17_active':
        flags = 0x20000
    elif style != 'dense':
        raise ValueError('Unknown owned block style')
    struct.pack_into('<I', payload, 0, flags)
    struct.pack_into('<f', payload, 0x21c, bit17)
    return bytes(payload)


def run(dll: Path, decoder_dir: Path) -> dict:
    native = Native(dll)
    static = stack_review(PE(dll.read_bytes()), decoder_dir)
    rows = []
    for name, ctor, pointer_offset, mask_offset, copy, nargs in ROUTES:
        call = native.fn(copy, Q, P, P, Q, *([I] if nargs == 4 else []))
        for source_has_feature, request_feature in ((True, True), (True, False),
                                                    (False, True), (False, False)):
            for style in ('dense', 'zero_arrays', 'only_bit17_active'):
                for value in (-3., -0.25, 0., 3.):
                    source, destination = native.alloc(0x400), native.alloc(0x400)
                    native.fn(ctor, P, P)(source)
                    native.fn(ctor, P, P)(destination)
                    payload = block(style, value)
                    guarded = native.alloc(0x248)
                    c.memset(guarded, 0xa5, 0x248)
                    feature = guarded + 16
                    c.memmove(feature, payload, len(payload))
                    P.from_address(source + pointer_offset).value = feature
                    Q.from_address(source + mask_offset).value = 0x2000 if source_has_feature else 0
                    before = c.string_at(guarded, 0x248)
                    status = call(source, destination, 0x2000 if request_feature else 0,
                                  *([0] if nargs == 4 else []))
                    out = P.from_address(destination + pointer_offset).value
                    copied = source_has_feature and request_feature
                    if status or bool(out) != copied:
                        raise AssertionError('Native copy gate or allocation differs')
                    if copied and c.string_at(out, 0x228) != payload:
                        raise AssertionError('Whole feature copy changed generated bytes')
                    if c.string_at(guarded, 0x248) != before:
                        raise AssertionError('Copy changed source or its guards')
                    if Q.from_address(destination + mask_offset).value != (0x2000 if copied else 0):
                        raise AssertionError('Copy destination mask differs')
                    rows.append(dict(route=name, source_bit13=source_has_feature,
                        requested_bit13=request_feature, style=style, input_bit17=value,
                        whole_block_copied=copied, exact_or_not_allocated=True,
                        source_and_guards_preserved=True))
    return dict(dse_sha256=SHA256,
        source_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        stack_candidate_review=static,
        native_routes=[dict(name=name, constructor_rva=hex(ctor), copy_rva=hex(copy),
            feature_pointer_offset=hex(pointer), frame_mask_offset=hex(mask),
            argument_count=nargs, fourth_argument=0 if nargs == 4 else None)
            for name, ctor, pointer, mask, copy, nargs in ROUTES],
        feature_block_bytes=0x228, scalar_bit17_offset='0x21c',
        matrix=rows, cases=len(rows), copied_cases=sum(row['whole_block_copied'] for row in rows),
        scope='Four generic frame copy entries with fresh native-constructed destinations. '
              'All 552 bytes are preserved when source and requested outer bit13 are set, '
              'including nonzero arrays, bit17 and inactive field canaries. Otherwise the '
              'fresh destination has no feature allocation. This tests transport and gates, '
              'not PCM production, original authoring software, every copy configuration, '
              'existing-destination lifecycle, circular interpolation or full synthesis.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--decoder-dir', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.decoder_dir)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(cases=report['cases'], copied=report['copied_cases'],
        stack_candidates=len(report['stack_candidate_review']['candidates']), all_passed=True)))


if __name__ == '__main__':
    main()
