"""Read-only V4/V6 module metadata and bounded feature-name table leads.

This inventories local installed modules; it does not load their code, select a
Ghidra program, infer feature identities from names, or serialize code payloads.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
from pathlib import Path
import struct

from audit_frm2_numeric import SHA256
from locate_vqm_feature_accesses import executable_sections


V4_SHA256 = 'b96fec982d0a57b383835590d6ee852eb4ffc607933a33b52728edf35101b0eb'
LITERALS = ('FRM2', 'SMS2', 'VQM', 'DRS', 'PitchMFPhaseFlatness', 'CSMSFrame')


class PE:
    def __init__(self, data):
        self.data = data
        pe = struct.unpack_from('<I', data, 0x3c)[0]
        if data[pe:pe + 4] != b'PE\0\0':
            raise ValueError('Not a PE module')
        self.machine = struct.unpack_from('<H', data, pe + 4)[0]
        optional = pe + 24
        magic = struct.unpack_from('<H', data, optional)[0]
        if magic not in (0x10b, 0x20b):
            raise ValueError('Unsupported PE layout')
        self.width = 8 if magic == 0x20b else 4
        self.pointer_format = '<Q' if self.width == 8 else '<I'
        self.image_base = struct.unpack_from(self.pointer_format, data,
            optional + (24 if self.width == 8 else 28))[0]
        self.directories = optional + (112 if self.width == 8 else 96)
        count = struct.unpack_from('<H', data, pe + 6)[0]
        size = struct.unpack_from('<H', data, pe + 20)[0]
        self.sections = [struct.unpack_from('<IIII', data, optional + size + 40 * i + 8)
                         for i in range(count)]

    def offset(self, rva):
        for _, address, size, start in self.sections:
            if address <= rva < address + size:
                return start + rva - address
        raise ValueError(f'RVA outside file-backed sections: {rva:x}')

    def rva(self, offset):
        for _, address, size, start in self.sections:
            if start <= offset < start + size:
                return address + offset - start
        raise ValueError('Offset outside sections')

    def text(self, rva, maximum=160):
        try:
            start = self.offset(rva)
        except ValueError:
            return None
        end = self.data.find(b'\0', start, start + maximum)
        if end < 0:
            return None
        value = self.data[start:end]
        if value and all(32 <= byte < 127 for byte in value):
            return value.decode('ascii')
        return None

    def exports(self):
        rva, size = struct.unpack_from('<II', self.data, self.directories)
        if not rva:
            return []
        start = self.offset(rva)
        ordinal_base, functions, names, addresses, name_addresses, ordinals = \
            struct.unpack_from('<IIIIII', self.data, start + 16)
        if max(functions, names) > 100000:
            raise ValueError('Export count outside bounded inventory')
        result = []
        for index in range(names):
            name_rva = struct.unpack_from('<I', self.data,
                self.offset(name_addresses) + 4 * index)[0]
            ordinal = struct.unpack_from('<H', self.data, self.offset(ordinals) + 2 * index)[0]
            if ordinal >= functions:
                raise ValueError('Invalid export ordinal')
            target = struct.unpack_from('<I', self.data, self.offset(addresses) + 4 * ordinal)[0]
            result.append(dict(name=self.text(name_rva), rva=hex(target),
                ordinal=ordinal_base + ordinal, forwarded=rva <= target < rva + size))
        return result

    def name_table_leads(self):
        marker = b'PitchMFPhaseFlatness\0'
        start = self.data.find(marker)
        if start < 0:
            return []
        if self.data.find(marker, start + 1) >= 0:
            raise ValueError('Ambiguous marker; review name-table search')
        marker_rva = self.rva(start)
        pointer = struct.pack(self.pointer_format, self.image_base + marker_rva)
        references = []
        position = 0
        while (position := self.data.find(pointer, position)) >= 0:
            neighbors = []
            for relative_slot in range(-5, 7):
                location = position + relative_slot * self.width
                value = struct.unpack_from(self.pointer_format, self.data, location)[0]
                name = self.text(value - self.image_base)
                if not name:
                    raise ValueError('Name-table neighbor is not bounded ASCII metadata')
                neighbors.append(dict(relative_slot=relative_slot,
                    slot_rva=hex(self.rva(location)), name=name))
            references.append(dict(marker_string_rva=hex(marker_rva),
                pointer_reference_rva=hex(self.rva(position)), neighbors=neighbors))
            position += 1
            if len(references) > 16:
                raise ValueError('Too many marker references')
        return references


def version(path):
    library = c.WinDLL('version', use_last_error=True)
    library.GetFileVersionInfoSizeW.argtypes = [c.c_wchar_p, c.POINTER(c.c_ulong)]
    library.GetFileVersionInfoSizeW.restype = c.c_ulong
    library.GetFileVersionInfoW.argtypes = [c.c_wchar_p, c.c_ulong, c.c_ulong, c.c_void_p]
    library.GetFileVersionInfoW.restype = c.c_int
    library.VerQueryValueW.argtypes = [c.c_void_p, c.c_wchar_p,
        c.POINTER(c.c_void_p), c.POINTER(c.c_uint)]
    library.VerQueryValueW.restype = c.c_int
    handle = c.c_ulong()
    size = library.GetFileVersionInfoSizeW(str(path.resolve()), c.byref(handle))
    if not size:
        return None
    data = c.create_string_buffer(size)
    if not library.GetFileVersionInfoW(str(path.resolve()), 0, size, data):
        raise c.WinError(c.get_last_error())
    pointer, length = c.c_void_p(), c.c_uint()
    if not library.VerQueryValueW(data, '\\', c.byref(pointer), c.byref(length)) or length.value < 52:
        raise ValueError('Missing fixed version information')
    fields = (c.c_uint32 * 13).from_address(pointer.value)
    assert fields[0] == 0xfeef04bd
    return '.'.join(str(value) for value in
                   (fields[2] >> 16, fields[2] & 65535, fields[3] >> 16, fields[3] & 65535))


def inspect(path, expected_hash=None, detailed=False):
    data = path.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if expected_hash and digest != expected_hash:
        raise ValueError('Module changed; review pinned leads again')
    image = PE(data)
    row = dict(name=path.name, sha256=digest, size=len(data),
        machine=hex(image.machine), pointer_bytes=image.width,
        image_base=hex(image.image_base), file_version=version(path),
        literal_hits={name: dict(ascii=data.count(name.encode('ascii')),
            utf16le=data.count(name.encode('utf-16le'))) for name in LITERALS})
    if detailed:
        row['exports'] = image.exports()
        row['name_table_leads'] = image.name_table_leads()
        hits = []
        for _, base, code in executable_sections(data):
            position = 0
            while (position := code.find(struct.pack('<I', 0x21c), position)) >= 0:
                hits.append(hex(base + position))
                position += 1
        row['unreviewed_0x21c_byte_candidates'] = hits
    return row


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--v4-api-dir', type=Path, required=True)
    parser.add_argument('--v6-dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    paths = sorted(args.v4_api_dir.glob('*.dll'), key=lambda path: path.name.lower())
    if not paths or len(paths) > 64:
        raise ValueError('API inventory outside bounded scope')
    older = args.v4_api_dir / 'va_DSE4.dll'
    rows = [inspect(path, V4_SHA256 if path == older else None,
                    detailed=path == older) for path in paths]
    v4 = next(row for row in rows if row['name'] == 'va_DSE4.dll')
    v6 = inspect(args.v6_dll, SHA256, detailed=True)
    labels4 = [[item['name'] for item in table['neighbors']] for table in v4['name_table_leads']]
    labels6 = [[item['name'] for item in table['neighbors']] for table in v6['name_table_leads']]
    assert len(labels4) == len(labels6) == 2
    assert labels4[0] == labels4[1] == labels6[0] == labels6[1]
    assert v4['machine'] == '0x14c' and v6['machine'] == '0x8664'
    report = dict(tool_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        v4_api_modules=rows, v6_comparison=v6,
        identical_neighbor_names=labels4[0], name_tables_matched=4,
        scope='Explicit local V4 API folder and pinned V6 DSE. Module identifiers, exports, '
              'bounded name-table neighborhoods and unreviewed byte candidates only. '
              'Names are not mapped to serialized feature bits. No claim of an analysis '
              'producer from literal presence or absence; Ghidra object/function queries remain required.')
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(api_modules=len(rows), v4_version=v4['file_version'],
        v6_version=v6['file_version'], v4_exports=len(v4['exports']),
        v4_bit17_byte_candidates=len(v4['unreviewed_0x21c_byte_candidates']),
        matched_name_tables=4)))


if __name__ == '__main__':
    main()
