"""Bounded static candidates for wide stores overlapping VQM feature bit17.

This recognizes selected legacy SSE and REX.W store byte patterns, not complete
x86 decoding. Candidates require instruction-boundary and object-layout review.
The hash-pinned DLL is read only; reports never contain binary/code payloads.
"""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import struct

from audit_frm2_numeric import SHA256
from locate_vqm_feature_accesses import executable_sections


FIELD_OFFSET = 0x21c
REVIEW = {
    0x10fbb: ('0x100a0', 'stack', 'RSP+0x210 saves XMM13'),
    0xc8fef: ('0xc8ee0', 'stack', 'RBP+0x210 stores local constants'),
    0x13febf: ('0x13fd60', 'other_object_copy',
               'Region-object copy; flags+0x185, frames+0x18/+0x20; not feature+0x228'),
    0x364fc: ('0x31830', 'stack', 'RBP+0x218 is an output-pointer local'),
    0x76697: ('0x763b0', 'stack', 'RSP+0x218 saves an allocated ENV pointer'),
    0x9ec4b: ('0x9e350', 'stack', 'RBP+0x218 clears an owned local pointer'),
    0xc8479: (None, 'unresolved_indexed_history',
              'Undefined Ghidra region; legacy SSE store to RCX+RAX*8+0x218. '
              'Nearby byte flag+0xb8 and counter+0x210 bound ten double entries; '
              'no direct E8 call or exact absolute-pointer reference found. '
              'Object ownership and reachability remain unverified'),
    0x109bc8: ('0x109b30', 'other_object_init',
               'DSE5::CDBDataPhUPart constructor, pointer slots'),
    0x10a86b: ('0x10a830', 'other_object_cleanup',
               'CDBDataPhUPart-owned pointer cleanup'),
    0x12b208: ('0x12a7b0', 'other_object_pointer',
               'Frame+0x218 receives a 0x1c8 ENV allocation, invokes its vtable'),
    0x12c666: ('0x12c550', 'other_object_cleanup',
               'Frame-owned ENV pointer deleted via vtable then cleared'),
    0x12d2b7: ('0x12cf40', 'other_object_cleanup',
               'Frame mask+0x150 clearing and owned ENV-pointer reset'),
    0x130480: ('0x1303b0', 'other_object_init',
               'Frame construction clears pointers, feature pointer is +0x208'),
    0x136f6a: ('0x1363a0', 'other_object_pointer',
               'Frame reader allocates ENV then reads it through vtable+0x18'),
    0x1403f0: ('0x140240', 'other_object_cleanup',
               'Region reset with flags+0x350 and objects through +0x1058'),
    0x1b9f6f: ('0x1b9bd0', 'other_object_pointer',
               'Allocated double-array pointer; length at +0x208'),
}


def candidates(code, base=0):
    result = []
    for offset in range(FIELD_OFFSET - 15, FIELD_OFFSET):
        needle = struct.pack('<I', offset)
        position = 0
        while (position := code.find(needle, position)) >= 0:
            for gap in (1, 2):
                modrm = position - gap
                if modrm < 4 or code[modrm] & 0xc0 != 0x80:
                    continue
                if (code[modrm] & 7 == 4) != (gap == 2):
                    continue
                opcode = modrm - 1
                width = 0
                start = opcode
                family = ''
                if code[opcode] in (0x11, 0x29, 0x7f, 0x13, 0x17, 0xd6,
                                    0x7e, 0x2b, 0xe7, 0xc3) and code[opcode - 1] == 0x0f:
                    start = opcode - 1
                    rex = 0
                    if 0x40 <= code[start - 1] <= 0x4f:
                        start -= 1
                        rex = code[start]
                    prefix = 0
                    if code[start - 1] in (0x66, 0xf2, 0xf3):
                        start -= 1
                        prefix = code[start]
                    op = code[opcode]
                    if op == 0x11:
                        width = 4 if prefix == 0xf3 else 8 if prefix == 0xf2 else 16
                    elif op in (0x29, 0x2b):
                        width = 16
                    elif op in (0x7f, 0xe7):
                        width = 16 if prefix in (0x66, 0xf3) else 8
                    elif op in (0x13, 0x17):
                        width = 8
                    elif op == 0xd6 and prefix == 0x66:
                        width = 8
                    elif op == 0x7e and prefix == 0x66 and rex & 8:
                        width = 8
                    elif op == 0xc3 and rex & 8:
                        width = 8
                    family = 'legacy_SSE_or_non_temporal'
                elif code[opcode] in (0x89, 0xc7) and 0x48 <= code[opcode - 1] <= 0x4f:
                    width = 8
                    start = opcode - 1
                    family = 'REX_W_integer'
                if width and offset + width > FIELD_OFFSET:
                    result.append(dict(rva=hex(base + start), offset=hex(offset),
                        width=width, family=family, sib=gap == 2))
            position += 1
    return sorted(result, key=lambda row: int(row['rva'], 16))


def self_check():
    cases = [
        ('0f118b', 0x210, 16), ('f20f118b', 0x218, 8),
        ('f30f118b', 0x218, None), ('0f108b', 0x210, None),
        ('0f298b', 0x210, 16), ('660f7f8b', 0x210, 16),
        ('f30f7f8b', 0x210, 16), ('660fd68b', 0x218, 8),
        ('66480f7e8b', 0x218, 8), ('f30f7e8b', 0x218, None),
        ('48898b', 0x218, 8), ('488b8b', 0x218, None),
        ('488984c1', 0x218, 8), ('48898b', 0x214, None),
        ('0f118b', 0x200, None), ('660fe78b', 0x210, 16),
        ('480fc38b', 0x218, 8), ('f20f118cc1', 0x218, 8),
        ('0f118b', 0x21c, None), ('48898b', 0x220, None),
    ]
    for header, offset, width in cases:
        code = b'\xcc' * 8 + bytes.fromhex(header) + struct.pack('<I', offset) + b'\xcc' * 8
        rows = candidates(code)
        if width is None:
            assert not rows, (header, offset, rows)
        else:
            assert len(rows) == 1 and rows[0]['width'] == width, (header, offset, rows)
            assert int(rows[0]['rva'], 16) == 8
    return len(cases)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    checks = self_check()
    data = args.dll.read_bytes()
    if hashlib.sha256(data).hexdigest() != SHA256:
        raise ValueError('Unsupported DSE hash; review candidates again')
    rows = [row for _, base, code in executable_sections(data)
            for row in candidates(code, base)]
    for row in rows:
        review = REVIEW.get(int(row['rva'], 16))
        row['review'] = (dict(function_rva=review[0], classification=review[1], note=review[2])
                         if review else dict(classification='unreviewed'))
    report = dict(dse_sha256=SHA256,
        scanner_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        owned_instruction_fixture_checks=checks, candidate_count=len(rows), candidates=rows,
        classifications=dict(Counter(row['review']['classification'] for row in rows)),
        scope='Selected disp32 legacy SSE, non-temporal and REX.W stores starting '
              'before +0x21c and overlapping any of its bytes. Byte patterns are '
              'candidates, not a complete decoder or proof of object identity. '
              'Ghidra read-only instruction/object reviews recorded separately. '
              'Does not cover VEX/EVEX, x87, rebased pointers, computed displacements, '
              'memcpy/memset, ordinary loops or external producers. The unresolved '
              'leaf region cannot be dismissed solely because Ghidra has no function.')
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(candidates=len(rows), classifications=report['classifications'],
                          owned_fixture_checks=checks)))


if __name__ == '__main__':
    main()
