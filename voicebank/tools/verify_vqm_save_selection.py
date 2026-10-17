"""Check outer Frame selection versus inner feature selection in two writers.

All native file inputs are generated here. One explicitly named scratch file is
created exclusively, reused and deleted. No commercial payload is written.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import mmap
from pathlib import Path
import struct

from audit_frm2_numeric import I, Native, P, Q, SHA256
from audit_v4x_vqm_fields import feature_array, feature_scalars
from probe_frm2 import VqmFrame, parse_frame, vqm_feature_payload_size
from verify_reference_ddi import load_public_parser, pointer
from verify_vqm_feature_dse5_roundtrip import crt_functions


LAYOUT = [(4 + 4 * bit, 4) for bit in range(10)] + [
    (0x2c, 160), (0xcc, 160), (0x16c, 80), (0x1bc, 80)] + [
    (0x20c if bit == 14 else 0x214 + 4 * (bit - 15), 4) for bit in range(14, 20)]
ARRAY_FLAGS = sum(1 << bit for bit in (10, 11, 12, 13))
FULL = 0x1fffff
ROUTES = [dict(name='DRS', ctor=0x7ec30, mask=0x50, feature=0x128,
    feature_writer=0x6c9f0, feature_reader=0x6cef0, writer=0x92f40, reader=0x93000),
    dict(name='DSE5', ctor=0x12a670, mask=0x150, feature=0x208,
    feature_writer=0x1380a0, feature_reader=0x135eb0, writer=0x107060, reader=0x106e20)]


def owned_block(flags):
    data = bytearray(struct.pack('<138f', *(0.125 + k / 256. for k in range(138))))
    struct.pack_into('<I', data, 0, flags)
    return bytes(data)


def disk_block(raw):
    flags = struct.unpack_from('<I', raw)[0]
    result = struct.pack('<I', flags) + b''.join(raw[offset:offset + size]
        for bit, (offset, size) in enumerate(LAYOUT) if flags & (1 << bit))
    assert len(result) == 4 + vqm_feature_payload_size(flags)
    return result


def loaded_block(raw):
    flags = struct.unpack_from('<I', raw)[0]
    result = bytearray(0x228)
    result[:4] = raw[:4]
    for bit, (offset, size) in enumerate(LAYOUT):
        if flags & (1 << bit):
            result[offset:offset + size] = raw[offset:offset + size]
    return bytes(result)


class Scratch:
    def __init__(self, native, path):
        self.native = native
        self.path = path
        self.crt = crt_functions()

    def invoke(self, rva, pointer, mask, reading=False, feature=False):
        handle = self.crt._wfopen(str(self.path), 'rb' if reading else 'wb')
        if not handle:
            raise OSError('Cannot open exclusive owned scratch')
        stream = self.native.alloc(0x80)
        P.from_address(stream + 8).value = handle
        Q.from_address(stream + 0x20).value = mask
        I.from_address(stream + 0x2c).value = 0x32534d53
        try:
            if feature:
                code = self.native.fn(rva, Q, P, P, P)(0, stream, pointer)
            else:
                code = self.native.fn(rva, Q, P, P)(pointer, stream)
            used = Q.from_address(stream).value
        finally:
            if self.crt.fclose(handle) != 0:
                raise OSError('Cannot close owned scratch')
        assert used == self.path.stat().st_size, ('stream length', hex(rva), reading)
        return code, self.path.read_bytes()


def guard_block(native, raw):
    address = native.alloc(0x228 + 64)
    c.memset(address, 0xa5, 32)
    c.memmove(address + 32, raw, 0x228)
    c.memset(address + 32 + 0x228, 0x5a, 32)
    return address, address + 32


def check_guards(address):
    assert c.string_at(address, 32) == b'\xa5' * 32
    assert c.string_at(address + 32 + 0x228, 32) == b'\x5a' * 32


def verify(dll, scratch_path):
    native = Native(dll)
    scratch = Scratch(native, scratch_path)
    selections = [0] + [1 << bit for bit in range(21)]
    selections += [(FULL & ~ARRAY_FLAGS & ~(1 << 20)) |
        sum(1 << (10 + bit) for bit in range(4) if subset & (1 << bit)) |
        (reserved << 20) for subset in range(16) for reserved in (0, 1)]
    selections = sorted(set(selections))
    direct_rows = []
    frame_rows = []
    for route in ROUTES:
        for flags in selections:
            raw = owned_block(flags)
            expected = disk_block(raw)
            base, feature = guard_block(native, raw)
            for stream_mask in (0, 0xffffffffffffffff):
                code, actual = scratch.invoke(route['feature_writer'], feature, stream_mask, feature=True)
                assert actual == expected, ('feature write', route['name'], hex(flags))
                assert c.string_at(feature, 0x228) == raw
                check_guards(base)
                target_base, target = guard_block(native, bytes(0x228))
                read_code, _ = scratch.invoke(route['feature_reader'], target, stream_mask,
                                               reading=True, feature=True)
                assert c.string_at(target, 0x228) == loaded_block(raw)
                check_guards(target_base)
                direct_rows.append(dict(route=route['name'], flags=hex(flags),
                    stream_mask=hex(stream_mask), bytes=len(actual), writer_result=int(code),
                    reader_result=int(read_code), exact_disk=True, exact_loaded_block=True,
                    source_and_guards_preserved=True))
        for subset in range(16):
            flags = (FULL & ~ARRAY_FLAGS) | sum(1 << (10 + bit)
                for bit in range(4) if subset & (1 << bit))
            raw = owned_block(flags)
            base, feature = guard_block(native, raw)
            frame = native.alloc(0x328)
            native.fn(route['ctor'], P, P)(frame)
            # Partial frames test the writer's field-selection contract only;
            # they are not offered as synthesis inputs or playable voicebanks.
            Q.from_address(frame + route['mask']).value = 8192
            P.from_address(frame + route['feature']).value = feature
            for stream_mask in (0, 8192, 0xffffffffffffffff, 0xffffffffffffdfff):
                code, actual = scratch.invoke(route['writer'], frame, stream_mask)
                assert code == 0 and actual[:4] == b'FRM2'
                assert struct.unpack_from('<I', actual, 4)[0] == len(actual)
                stored_mask = struct.unpack_from('<Q', actual, 20)[0]
                enabled = bool(stream_mask & 8192)
                assert stored_mask == (8192 if enabled else 0)
                expected = disk_block(raw) + b'\0' if enabled else b''
                assert actual[28:] == expected, ('Frame write', route['name'], subset, stream_mask)
                assert c.string_at(feature, 0x228) == raw
                check_guards(base)
                target = native.alloc(0x328)
                native.fn(route['ctor'], P, P)(target)
                read_code, _ = scratch.invoke(route['reader'], target, 0xffffffffffffffff, reading=True)
                assert read_code == 0
                loaded = P.from_address(target + route['feature']).value
                assert bool(loaded) == enabled
                assert Q.from_address(target + route['mask']).value & 8192 == stored_mask
                if loaded:
                    assert c.string_at(loaded, 0x228) == loaded_block(raw)
                frame_rows.append(dict(route=route['name'], flags=hex(flags),
                    stream_mask=hex(stream_mask), stored_mask=hex(stored_mask), bytes=len(actual),
                    feature_written=enabled, exact_selected_payload=True,
                    exact_loaded_flags_and_fields=True, source_and_guards_preserved=True))
    return dict(dse_sha256=SHA256, fields=[dict(bit=bit, memory_offset=hex(offset), bytes=size)
        for bit, (offset, size) in enumerate(LAYOUT)],
        omitted_memory_word='0x210', no_payload_flag=20, routes=ROUTES,
        direct_cases=direct_rows, frame_cases=frame_rows,
        conclusion='Outer selection either omits the whole feature block or keeps its '
            'original inner flags and all selected bytes. Inner selection changes flags '
            'and payload length; it cannot serialize full flags with zeroed arrays from '
            'a nonzero source block. Missing fields remain zero only when omitted by '
            'inner flags. Neither tested writer computes bit17.',
        scope='Generated finite owned metadata, two DRS/DSE5 feature writers/readers '
            'and Frame wrappers. All 16 array subsets tested at Frame level; 21 '
            'individual flags plus reserved-flag combinations at feature level. '
            'No original authoring caller or other serialization format is proved.')


def saved_selection(manifest_path, voice_root, parser_root):
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    parser_hash = hashlib.sha256((parser_root / 'utils' / 'ddi_utils.py').read_bytes()).hexdigest()
    assert parser_hash == manifest['parser_sha256']
    parse_ddi = load_public_parser(parser_root)
    paths = list(voice_root.rglob('*.ddi'))
    rows = []
    for bank in manifest['banks']:
        matches = [p for p in paths if p.stem == bank['bank']]
        assert len(matches) == 1
        path = matches[0]
        ddi = path.read_bytes()
        assert hashlib.sha256(ddi).hexdigest() == bank['ddi_sha256']
        model = parse_ddi(ddi)
        model.read()
        frames = 0
        arrays = 0
        nonzero_bit17 = 0
        parts = list((model.vqm_data or {}).values())
        assert len(parts) == 3
        with path.with_suffix('.ddb').open('rb') as source:
            with mmap.mmap(source.fileno(), 0, access=mmap.ACCESS_READ) as ddb:
                for part in parts:
                    for item in part['epr']:
                        start, _ = pointer(item, ddi)
                        assert 0 <= start <= len(ddb) - 8
                        size = struct.unpack_from('<I', ddb, start + 4)[0]
                        assert 28 <= size <= len(ddb) - start
                        frame = parse_frame(ddb[start:start + size])
                        assert isinstance(frame, VqmFrame)
                        assert frame.mask & 8192 and frame.feature_block.flags == FULL
                        assert len(frame.feature_block.payload) == 544
                        for bit in (10, 11, 12, 13):
                            values = feature_array(frame, bit)
                            assert all(x == 0. for x in values)
                            arrays += len(values)
                        nonzero_bit17 += feature_scalars(frame)[17] != 0.
                        frames += 1
        rows.append(dict(bank=bank['bank'], ddi_sha256=bank['ddi_sha256'], parts=len(parts),
            frames=frames, full_internal_flags_frames=frames, physically_present_zero_array_values=arrays,
            nonzero_bit17_frames=nonzero_bit17))
    assert len(rows) == 8 and sum(row['frames'] for row in rows) == 1968
    return dict(parser_sha256=parser_hash, banks=rows,
        scope='Eight hash-pinned DDI/DDB inputs read only. Counts include repeated '
              'samples across banks; they are not independent recordings. All four '
              'arrays physically present, not reconstructed from missing flags.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'scratch', 'output', 'manifest', 'voice-root', 'ddb-tools'):
        parser.add_argument('--' + name, required=True, type=Path)
    args = parser.parse_args()
    saved = saved_selection(args.manifest, args.voice_root, args.ddb_tools)
    scratch = args.scratch.resolve()
    if not scratch.parent.is_dir() or scratch.exists():
        raise ValueError('Explicit scratch parent must exist and file must be absent')
    # The exclusive create proves this process owns the only path it removes.
    with scratch.open('xb'):
        pass
    try:
        report = verify(args.dll, scratch)
    finally:
        scratch.unlink()
    names = ('verify_vqm_save_selection.py', 'audit_frm2_numeric.py',
             'probe_frm2.py', 'verify_vqm_feature_dse5_roundtrip.py',
             'audit_v4x_vqm_fields.py', 'verify_reference_ddi.py')
    report['source_sha256'] = {name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                              for name in names}
    report['owned_scratch_removed'] = not scratch.exists()
    report['saved_selection'] = saved
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(direct_cases=len(report['direct_cases']), frame_cases=len(report['frame_cases']),
        exact=True, owned_scratch_removed=report['owned_scratch_removed'])))


if __name__ == '__main__':
    main()
