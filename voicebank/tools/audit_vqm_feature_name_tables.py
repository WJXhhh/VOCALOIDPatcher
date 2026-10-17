"""Bound the V6 feature-name metadata tables and their direct reference leads.

Names, hashes, RVAs and coverage counts only. No proprietary instruction bytes
or decompiled implementation is serialized. Empty results are not a global
proof of unreachable metadata or absence of an analysis producer.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib
import json
from pathlib import Path
import re
import struct
import sys

from audit_frm2_numeric import SHA256
from audit_vqm_v4_module_leads import PE
from locate_vqm_feature_accesses import executable_sections


RIP = re.compile(r'\[rip(?: ([+-]) (0x[0-9a-f]+))?\]')
MARKER = 'PitchMFPhaseFlatness'


def relative_target(address, size, operands):
    match = RIP.search(operands)
    if not match:
        return None
    displacement = int(match[2], 16) * (-1 if match[1] == '-' else 1) if match[2] else 0
    return address + size + displacement


def decoder_checks(cs):
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    cases = [(bytes.fromhex('488d0510000000'), 0x1000, 0x1017),
        (bytes.fromhex('488b05f0ffffff'), 0x1000, 0xff7),
        (bytes.fromhex('f30f100500000000'), 0x1000, 0x1008),
        (bytes.fromhex('488b00'), 0x1000, None),
        (bytes.fromhex('488d0418'), 0x1000, None),
        (bytes.fromhex('488d0d00000000'), 0x1000, 0x1007)]
    for raw, origin, expected in cases:
        rows = list(decoder.disasm_lite(raw, origin))
        assert len(rows) == 1 and rows[0][1] == len(raw)
        assert relative_target(rows[0][0], rows[0][1], rows[0][3]) == expected
    return len(cases)


def contiguous_table(pe, marker_slot):
    low = high = marker_slot
    for _ in range(256):
        value = struct.unpack_from('<Q', pe.data, pe.offset(low - 8))[0]
        if not pe.text(value - pe.image_base):
            break
        low -= 8
    else:
        raise ValueError('Backward table extent exceeds bound')
    for _ in range(256):
        value = struct.unpack_from('<Q', pe.data, pe.offset(high + 8))[0]
        if not pe.text(value - pe.image_base):
            break
        high += 8
    else:
        raise ValueError('Forward table extent exceeds bound')
    names = [pe.text(struct.unpack_from('<Q', pe.data, pe.offset(at))[0] - pe.image_base)
             for at in range(low, high + 8, 8)]
    assert all(names) and names[(marker_slot - low) // 8] == MARKER
    return low, high + 8, names


def run(dll, decoder_dir):
    data = dll.read_bytes()
    assert hashlib.sha256(data).hexdigest() == SHA256
    pe = PE(data)
    assert pe.width == 8
    sys.path.insert(0, str(decoder_dir.resolve()))
    cs = importlib.import_module('capstone')
    assert cs.__version__ == '5.0.3'
    checks = decoder_checks(cs)
    leads = pe.name_table_leads()
    assert len(leads) == 2
    tables = [contiguous_table(pe, int(row['pointer_reference_rva'], 16)) for row in leads]
    assert tables[0][2] == tables[1][2]
    assert len(tables[0][2]) == 99
    string_rva = int(leads[0]['marker_string_rva'], 16)
    directory, size = struct.unpack_from('<II', data, pe.directories + 24)
    ranges = list(struct.iter_unpack('<III', data[pe.offset(directory):pe.offset(directory) + size]))
    assert len(ranges) <= 10000
    executable = [(base, base + len(raw)) for _, base, raw in executable_sections(data)]
    hits = []
    total_instructions = total_bytes = undecoded_tail = 0
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    for start, end, _ in ranges:
        assert 0 < end - start <= 0x40000
        assert any(a <= start < end <= b for a, b in executable)
        raw = data[pe.offset(start):pe.offset(start) + end - start]
        total_bytes += len(raw)
        used = 0
        for address, length, mnemonic, operands in decoder.disasm_lite(raw, start):
            total_instructions += 1
            used += length
            target = relative_target(address, length, operands)
            if target is None:
                continue
            match = None
            if string_rva <= target < string_rva + len(MARKER) + 1:
                match = 'marker_string'
            for low, high, _ in tables:
                if low <= target < high:
                    match = 'name_table'
                elif low - 64 <= target < high + 64:
                    match = 'neighbor_only'
            if match:
                hits.append(dict(range_start_rva=hex(start), instruction_rva=hex(address),
                                 target_rva=hex(target), operation=mnemonic, relation=match))
        undecoded_tail += len(raw) - used
        assert len(hits) <= 100
    pointers = []
    # Every slot address is tested, not only the table's first or marker slot.
    for low, high, _ in tables:
        for target in range(low, high, 8):
            needle = struct.pack('<Q', pe.image_base + target)
            position = 0
            while (position := data.find(needle, position)) >= 0:
                pointers.append(dict(reference_rva=hex(pe.rva(position)), target_rva=hex(target)))
                position += 1
                assert len(pointers) <= 100
    reviewed = {
        0x6b638: (0x6b4e0, 0x5aac50), 0x6b642: (0x6b4e0, 0x5aac5c),
        0x6b73f: (0x6b4e0, 0x5aac40), 0x6b74b: (0x6b4e0, 0x5aac4c),
        0x6c13c: (0x6c0a0, 0x5aac40), 0x6c146: (0x6c0a0, 0x5aac4c)}
    assert len(hits) == len(reviewed)
    for row in hits:
        address = int(row['instruction_rva'], 16)
        assert (int(row['range_start_rva'], 16), int(row['target_rva'], 16)) == reviewed[address]
        assert row['relation'] == 'neighbor_only' and row['operation'] == 'lea'
        row['review'] = ('Ghidra: numeric threshold range adjoining table. '
            '6b4e0 writes outer Frame bit14 categorization; 6c0a0 is a threshold '
            'membership helper writing its supplied output. Neither lead is a name lookup.')
    return dict(dse_sha256=SHA256, decoder_version=cs.__version__, decoder_checks=checks,
        tables=[dict(start_rva=hex(low), end_rva=hex(high), entries=len(names),
            marker_index=names.index(MARKER), names_sha256=hashlib.sha256(
                '\0'.join(names).encode('ascii')).hexdigest()) for low, high, names in tables],
        names=tables[0][2], marker_string_rva=hex(string_rva),
        coverage=dict(exception_ranges=len(ranges), decoded_instructions=total_instructions,
            runtime_range_bytes=total_bytes, undecoded_tail_bytes=undecoded_tail,
            exact_table_or_marker_rip_references=sum(x['relation'] != 'neighbor_only' for x in hits),
            neighbor_references=len(hits), absolute_slot_pointer_references=len(pointers)),
        instruction_leads=hits, absolute_slot_pointer_leads=pointers,
        feature_bit_name_binding_proved=False,
        scope='Pinned V6 contiguous ASCII-pointer metadata, bounded at the first non-name '
            'word in each direction. These boundaries are not a recovered C++ declaration. '
            'RIP-relative references decoded only from PE runtime ranges; leaf code outside '
            'those ranges, decoded tails, dynamically computed addresses, cross-module '
            'references and indirect lookup are not excluded. Absolute slot pointers searched '
            'in this file only. No conclusion that metadata is unreachable or that a '
            'producer is absent. In particular index89 is not serialized feature bit17.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'decoder-dir', 'output'):
        parser.add_argument('--' + name, required=True, type=Path)
    args = parser.parse_args()
    report = run(args.dll, args.decoder_dir)
    names = ('audit_vqm_feature_name_tables.py', 'audit_vqm_v4_module_leads.py',
             'audit_frm2_numeric.py', 'locate_vqm_feature_accesses.py')
    report['source_sha256'] = {name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                              for name in names}
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report['coverage']))


if __name__ == '__main__':
    main()
