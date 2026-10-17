"""Verify the Region double-list layout using owned metadata and native I/O.

The undefined leaf at RVA c8450 is examined only through static observations;
it is never invoked. All persisted native data is self-created and removed.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
from pathlib import Path
import struct

from audit_frm2_numeric import B, D, I, Native, P, Q, SHA256
from verify_vqm_feature_arrays_roundtrip import file_functions


def run(dll: Path, scratch: Path) -> dict:
    if not scratch.parent.is_dir() or scratch.exists():
        raise ValueError('Use a new scratch file in an existing research directory')
    native = Native(dll)
    crt = file_functions()
    rows = []
    try:
        for enabled, count in ((False, 0), (True, 0), (True, 1),
                               (True, 5), (True, 10)):
            region = native.alloc(0x838)
            if native.fn(0xbc3c0, P, P)(region) != region:
                raise AssertionError('Region constructor returned another object')
            ctor_counter = I.from_address(region + 0x210).value
            ctor_flags = B.from_address(region + 0xb8).value
            if ctor_counter != 0 or ctor_flags != 1:
                raise AssertionError('Region constructor layout changed')
            values = tuple((i - 3) * 0.125 for i in range(count))
            B.from_address(region + 0xb8).value = 0x10 if enabled else 0
            B.from_address(region + 0x280).value = 0
            D.from_address(region + 0x28).value = 0.75
            I.from_address(region + 0x210).value = count
            for i, value in enumerate(values):
                D.from_address(region + 0x218 + 8 * i).value = value
            expected = struct.pack('<dBB', 0.75, 7, 0x10 if enabled else 0)
            if enabled:
                expected += struct.pack('<i', count)
                expected += struct.pack(f'<{count}d', *values)
            expected += bytes(1)
            handle = crt._wfopen(str(scratch.resolve()), 'wb')
            if not handle:
                raise OSError('Could not open owned Region metadata')
            stream = native.alloc(0x80)
            P.from_address(stream + 8).value = handle
            try:
                code = native.fn(0xbca10, Q, P, P)(region + 0x40, stream)
                written = Q.from_address(stream).value
            finally:
                if crt.fclose(handle):
                    raise OSError('Could not close owned Region metadata')
            actual = scratch.read_bytes()
            if code or written != len(expected) or actual != expected:
                raise AssertionError('Native Region content differs from independent layout')
            loaded = native.alloc(0x838)
            native.fn(0xbc3c0, P, P)(loaded)
            handle = crt._wfopen(str(scratch.resolve()), 'rb')
            if not handle:
                raise OSError('Could not reopen owned Region metadata')
            read_stream = native.alloc(0x80)
            P.from_address(read_stream + 8).value = handle
            try:
                code = native.fn(0xbcef0, Q, P, P)(loaded + 0x40, read_stream)
                consumed = Q.from_address(read_stream).value
            finally:
                if crt.fclose(handle):
                    raise OSError('Could not close owned Region metadata input')
            loaded_values = c.string_at(loaded + 0x218, count * 8)
            if (code or consumed != written or
                    I.from_address(loaded + 0x210).value != count or
                    B.from_address(loaded + 0xb8).value != (0x10 if enabled else 0) or
                    loaded_values != struct.pack(f'<{count}d', *values) or
                    D.from_address(loaded + 0x28).value != 0.75):
                raise AssertionError('Native Region double-list round-trip differs')
            rows.append(dict(enabled=enabled, count=count, bytes=written,
                             independent_disk_exact=True, native_roundtrip_exact=True,
                             constructor_counter=ctor_counter, constructor_flags=ctor_flags))
            scratch.unlink()
    finally:
        scratch.unlink(missing_ok=True)
    return dict(
        dse_sha256=SHA256,
        source_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        native_constructor_rva='0xbc3c0', native_content_writer_rva='0xbca10',
        native_content_reader_rva='0xbcef0', content_this_adjustment='0x40',
        region_allocation_size='0x838',
        layout=dict(flag_offset='0xb8', flag_mask='0x10', count_offset='0x210',
                    list_offset='0x218', element_type='double', maximum_count=10,
                    end_exclusive='0x268'),
        leaf_candidate=dict(entry_rva='0xc8450', store_rva='0xc8479',
            status='Matches the Region optional double-list layout',
            invoked=False, semantic_name_unknown=True, indirect_reachability_unknown=True),
        static_basis=[
            '95780 allocates 0x838, invokes bc3c0 and assigns the result to analysis+cd8.',
            'bc3c0 sets CSMSRegion vtables, resets +210 and initializes flags at +b8.',
            'bca10/bcef0 use this=Region+40; flag+78, count+1d0 and doubles+1d8 map back to +b8/+210/+218.',
            'bcef0 clamps the loaded list count to ten; c8450 appends only while count is below ten.',
        ],
        cases=rows,
        scope='Five owned Region content cases establish the constructor and optional '
              'double-list I/O schema. The candidate leaf matches this schema, rather '
              'than a dedicated per-frame feature-bit17 calculation. This does not '
              'recover the list semantic name, prove leaf callers, exclude rebased '
              'or computed feature stores, or complete VQM/Growl production semantics.')


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--scratch', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.scratch)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(cases=len(report['cases']), exact=all(
        row['independent_disk_exact'] and row['native_roundtrip_exact']
        for row in report['cases']))))


if __name__ == '__main__':
    main()
