"""Bounded offline instruction/object review of pinned V4 feature candidates.

Uses an existing external Capstone installation, never imports the proprietary
DLL into a process or Ghidra project, and writes metadata rather than code bytes.
Alignment padding is an inferred decoding anchor, not a function definition.
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

from audit_vqm_v4_module_leads import PE, V4_SHA256
from locate_vqm_feature_accesses import executable_sections


REVIEW = {
    0x66d4: ('non_field_encoding', 'Relative JMP displacement'),
    0x1acec: ('non_field_encoding', 'Relative JE displacement'),
    0x71653: ('stack', 'Exception/unwind state byte'),
    0x717c2: ('stack', 'Exception/unwind state byte'),
    0x71909: ('stack', 'Exception/unwind state byte'),
    0x719b3: ('stack', 'Exception/unwind state byte'),
    0x75335: ('stack', 'Stack pointer-sized local'),
    0x7f5b9: ('attribute_via_frame_layout', 'Getter selects dynamics through child+0x150 then object+0x1b0, matching the Frame feature pointer layout'),
    0x7f9e6: ('attribute_via_frame_layout', 'First comparison operand through the same chain; leaf ownership still needs runtime confirmation'),
    0x7fa00: ('attribute_via_frame_layout', 'Second comparison operand through the same chain; leaf ownership still needs runtime confirmation'),
    0x8d4e5: ('non_field_encoding', 'Relative JE displacement'),
    0x8fafe: ('stack', 'Address of stack local'),
    0x95b97: ('non_field_encoding', 'Byte displacement and integer immediate overlap'),
    0x97a0b: ('stack', 'Indexed stack byte buffer'),
    0x97a3d: ('stack', 'Indexed stack byte buffer'),
    0x97a7c: ('stack', 'Indexed stack byte buffer'),
    0x97a88: ('stack', 'Address of stack byte buffer'),
    0x9f302: ('frame_pointer_reset', 'CSMSFrame initializer resets Frame+0x21c; feature pointer is Frame+0x1b0'),
    0xa18ed: ('feature_interpolation', 'First source feature+0x21c via Frame+0x1b0'),
    0xa18fb: ('feature_interpolation', 'Second source feature+0x21c via Frame+0x1b0'),
    0xa1915: ('feature_interpolation', 'Destination feature+0x21c via Frame+0x1b0'),
    0xa2fdd: ('feature_file_read', 'Feature flag 0x20000; four-byte fread'),
    0xa5023: ('feature_file_write', 'Feature flag 0x20000; four-byte fwrite'),
    0xaba04: ('region_initialization', 'CSMSRegion constructor calls initialization of Region+0x21c'),
}


def padding_anchor(image, rva):
    offset = image.offset(rva)
    anchor = image.data.rfind(b'\xcc\xcc', max(0, offset - 65536), offset)
    if anchor < 0:
        raise ValueError('No bounded alignment anchor')
    while image.data[anchor] == 0xcc:
        anchor += 1
    return image.rva(anchor)


def decode(image, decoder, start, end):
    return list(decoder.disasm(image.data[image.offset(start):image.offset(end)],
                              image.image_base + start))


def imports(image):
    rva = struct.unpack_from('<I', image.data, image.directories + 8)[0]
    result = {}
    for index in range(128):
        original, stamp, chain, name, first = struct.unpack_from('<IIIII', image.data,
            image.offset(rva) + index * 20)
        if not any((original, stamp, chain, name, first)):
            return result
        if not original:
            continue
        for item in range(4096):
            value = struct.unpack_from('<I', image.data, image.offset(original) + 4 * item)[0]
            if not value:
                break
            if not value & 0x80000000:
                result[first + 4 * item] = dict(module=image.text(name), name=image.text(value + 2))
        else:
            raise ValueError('Unbounded import table')
    raise ValueError('Unbounded import descriptors')


def rtti_name(image, vtable_rva):
    locator = struct.unpack_from('<I', image.data, image.offset(vtable_rva - 4))[0]
    descriptor = struct.unpack_from('<I', image.data,
        image.offset(locator - image.image_base) + 12)[0]
    return image.text(descriptor - image.image_base + 8)


def owned_checks(decoder, cs):
    # Explicit whole streams test the mandatory MOVSS prefix and false byte hits.
    cases = (
        ('f30f10811c020000', 'movss', True),
        ('f30f11811c020000', 'movss', True),
        ('8b8c241c020000', 'mov', True),
        ('c6843c1c02000000', 'mov', True),
        ('e91c020000', 'jmp', False),
        ('0f841c020000', 'je', False),
        ('c644241c02000000', 'mov', False),
    )
    for text, mnemonic, actual_field in cases:
        payload = bytes.fromhex(text)
        instructions = list(decoder.disasm(payload, 0x1000))
        if text == 'c644241c02000000':
            # This hit spans the byte displacement/immediate and following bytes.
            assert instructions[0].size == 5
        else:
            assert len(instructions) == 1 and instructions[0].size == len(payload)
        instruction = instructions[0]
        field = any(op.type == cs.x86.X86_OP_MEM and op.mem.disp == 0x21c
                    for op in instruction.operands)
        assert instruction.mnemonic == mnemonic and field == actual_field
        assert struct.pack('<I', 0x21c) in payload
    return len(cases)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--decoder-dir', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    sys.path.insert(0, str(args.decoder_dir.resolve()))
    cs = importlib.import_module('capstone')
    if cs.__version__ != '5.0.3':
        raise ValueError('Decoder version changed; recheck stream fixtures')
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_32)
    decoder.detail = True
    data = args.dll.read_bytes()
    if hashlib.sha256(data).hexdigest() != V4_SHA256:
        raise ValueError('V4 module hash changed; review candidates again')
    image = PE(data)
    assert image.machine == 0x14c and image.image_base == 0x10000000
    candidates = []
    for _, base, code in executable_sections(data):
        position = 0
        while (position := code.find(struct.pack('<I', 0x21c), position)) >= 0:
            candidates.append(base + position)
            position += 1
    assert set(candidates) == set(REVIEW)
    rows = []
    for candidate in sorted(candidates):
        anchor = padding_anchor(image, candidate)
        instructions = decode(image, decoder, anchor, candidate + 16)
        instruction = next((ins for ins in instructions
            if ins.address <= image.image_base + candidate < ins.address + ins.size), None)
        if not instruction:
            raise ValueError('Candidate not reached from bounded decoding anchor')
        classification, note = REVIEW[candidate]
        memory = [op for op in instruction.operands
                  if op.type == cs.x86.X86_OP_MEM and op.mem.disp == 0x21c]
        assert bool(memory) == (classification != 'non_field_encoding')
        if memory:
            assert instruction.disp_size == 4
            assert instruction.address - image.image_base + instruction.disp_offset == candidate
        if classification == 'stack':
            assert memory[0].mem.base == cs.x86.X86_REG_ESP
        rows.append(dict(candidate_rva=hex(candidate), instruction_rva=hex(instruction.address - image.image_base),
            inferred_alignment_anchor_rva=hex(anchor), mnemonic=instruction.mnemonic,
            memory_base=instruction.reg_name(memory[0].mem.base) if memory else None,
            memory_width=memory[0].size if memory else None,
            classification=classification, note=note))
    lookup = imports(image)
    assert lookup[0xd11fc]['name'] == 'fread' and lookup[0xd1200]['name'] == 'fwrite'
    assert rtti_name(image, 0x3a3c2c) == '.?AVCSMSFrame@@'
    assert rtti_name(image, 0x3a426c) == '.?AVCSMSRegion@@'
    # The three source/destination pointer loads directly precede interpolation.
    for load_rva, register in ((0xa18e3, 'esi'), (0xa18f1, 'edx'), (0xa18ff, 'edi')):
        ins = decode(image, decoder, load_rva, load_rva + 6)[0]
        assert ins.mnemonic == 'mov' and ins.operands[1].type == cs.x86.X86_OP_MEM
        assert ins.reg_name(ins.operands[1].mem.base) == register
        assert ins.operands[1].mem.disp == 0x1b0
    operations = decode(image, decoder, 0xa1905, 0xa1919)
    assert [ins.mnemonic for ins in operations] == ['subss', 'mulss', 'addss', 'movss']
    getter = decode(image, decoder, 0x7f576, 0x7f57b)[0]
    assert getter.mnemonic == 'mov'
    assert image.text(getter.operands[1].imm - image.image_base) == 'dynamics'
    report = dict(dll_sha256=V4_SHA256,
        tool_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        decoder_version=cs.__version__, owned_instruction_stream_checks=owned_checks(decoder, cs),
        candidate_count=len(rows), candidates=rows,
        classification_counts=dict(Counter(row['classification'] for row in rows)),
        io_imports={hex(key): lookup[key] for key in (0xd11fc, 0xd1200)},
        object_identity=[dict(vtable_rva='0x3a3c2c', rtti_name='.?AVCSMSFrame@@',
            constructor_rva='0x99210', initialization_rva='0x9f0e0',
            frm2_identifier='0x324d5246'),
            dict(vtable_rva='0x3a426c', rtti_name='.?AVCSMSRegion@@',
                 constructor_rva='0xa9b50', initialization_rva='0xab830')],
        feature_contract=dict(frame_pointer_offset='0x1b0', feature_object_size='0x228',
            feature_bit17_offset='0x21c', feature_bit17_mask='0x20000',
            reader_rva='0xa2ce0', reader_call_rva='0xa3b27',
            writer_rva='0xa4c80', writer_call_rva='0xa5974',
            interpolation_anchor_rva='0x9f610',
            interpolation_arithmetic='first + (second - first) * weight'),
        scope='Pinned x86 image, all 24 exact-offset byte candidates, bounded streams from inferred '
              'alignment anchors, selected import/RTTI/caller chains. Alignment is not a Ghidra '
              'function definition. No native V4 execution, all-path producer proof, arbitrary '
              'rebasing/indexing or other modules. Ghidra still has the separate V6 target.')
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(candidate_count=len(rows), classifications=report['classification_counts'],
        owned_checks=report['owned_instruction_stream_checks'],
        frame_feature_pointer_offset='0x1b0', feature_bit17_offset='0x21c')))


if __name__ == '__main__':
    main()
