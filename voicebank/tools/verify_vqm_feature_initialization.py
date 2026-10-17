"""Owned feature initialization followed by native Frame save/read checks.

Every payload is generated here. Never reads an uninitialized native feature;
only initialized or explicitly filled bytes are compared. Scratch files stay in
the repository sibling research directory and are removed after the checks.
"""
from __future__ import annotations

import argparse
from collections import Counter
import ctypes as c
import hashlib
import json
from pathlib import Path
import struct

from audit_frm2_numeric import I, Native, P, Q, SHA256
from audit_vqm_feature_allocations import run as audit_allocations
from frm2_work_directory import temporary_directory
from verify_vqm_save_selection import (ROUTES, Scratch, check_guards, disk_block,
                                       guard_block, loaded_block, owned_block)


SIZE = 0x228
FULL = 0x1fffff
ENABLE = 1 << 13


def owned_payload(bit17):
    raw = bytearray(owned_block(FULL))
    struct.pack_into('<f', raw, 0x21c, bit17)
    return bytes(raw)


def frame_block(native, route, mask):
    base = native.alloc(0x328 + 64)
    c.memset(base, 0xa5, 32)
    c.memset(base + 32 + 0x328, 0x5a, 32)
    frame = base + 32
    native.fn(route['ctor'], P, P)(frame)
    Q.from_address(frame + route['mask']).value = mask
    assert not P.from_address(frame + route['feature']).value
    return base, frame


def check_frame_guards(base):
    assert c.string_at(base, 32) == b'\xa5' * 32
    assert c.string_at(base + 32 + 0x328, 32) == b'\x5a' * 32


def release_feature(native, route, frame):
    # Only this helper's native allocator/reader returns enter this cleanup path.
    feature = P.from_address(frame + route['feature']).value
    if feature:
        P.from_address(frame + route['feature']).value = None
        native.fn(0x2161b4, None, P)(feature)


def roundtrip(native, scratch, route, frame, raw):
    feature = P.from_address(frame + route['feature']).value
    before = c.string_at(feature, SIZE)
    assert before == raw
    code, actual = scratch.invoke(route['writer'], frame, 0xffffffffffffffff)
    assert code == 0 and actual[:4] == b'FRM2'
    assert struct.unpack_from('<I', actual, 4)[0] == len(actual)
    assert struct.unpack_from('<Q', actual, 20)[0] == ENABLE
    assert actual[28:] == disk_block(raw) + b'\0'
    assert c.string_at(feature, SIZE) == before
    target_base, target = frame_block(native, route, 0)
    try:
        code, _ = scratch.invoke(route['reader'], target, 0xffffffffffffffff, reading=True)
        assert code == 0
        loaded = P.from_address(target + route['feature']).value
        assert loaded and Q.from_address(target + route['mask']).value == ENABLE
        expected = loaded_block(raw)
        assert c.string_at(loaded, SIZE) == expected
        assert c.string_at(loaded + 0x21c, 4) == expected[0x21c:0x220]
        check_frame_guards(target_base)
    finally:
        release_feature(native, route, target)
    return dict(saved_bytes=len(actual), exact_selected_disk=True,
        exact_loaded_block=True, source_unchanged_by_serialization=True,
        bit17_selected=bool(struct.unpack_from('<I', raw)[0] & (1 << 17)))


def generic_call(native, route, frame, requested, initialize):
    descriptor = native.alloc(0x60)
    Q.from_address(descriptor).value = requested
    rva = 0x7f1a0 if route['name'] == 'DRS' else 0x12a7b0
    code = native.fn(rva, I, P, P, I)(frame, descriptor, initialize)
    assert code == 0
    return rva


def run(dll, decoder_dir):
    allocation_audit = audit_allocations(dll, decoder_dir)
    native = Native(dll)
    rows = []
    counts = Counter()
    with temporary_directory('459-feature-init-') as directory:
        path = Path(directory) / 'owned-feature.frm2'
        path.touch(exist_ok=False)
        scratch = Scratch(native, path)
        for bit17 in (-1.75, 0.375, 2.125):
            raw = owned_payload(bit17)
            # Existing pointers are owned Python buffers; these three reviewed
            # entrypoints preserve them and never free them in this protocol.
            for active in (False, True):
                for initialize in (0, 1):
                    route = ROUTES[0]
                    frame_base, frame = frame_block(native, route, ENABLE if active else 0)
                    base, feature = guard_block(native, raw)
                    P.from_address(frame + route['feature']).value = feature
                    assert native.fn(0x2b500, I, P, I)(frame, initialize) == 0
                    expected = bytes(SIZE) if initialize else raw
                    assert P.from_address(frame + route['feature']).value == feature
                    assert Q.from_address(frame + route['mask']).value == ENABLE
                    assert c.string_at(feature, SIZE) == expected
                    check_guards(base)
                    check_frame_guards(frame_base)
                    saved = roundtrip(native, scratch, route, frame, expected)
                    check_guards(base)
                    check_frame_guards(frame_base)
                    rows.append(dict(route='DRS', entry_rva='0x2b500', existing=True,
                        active_before=active, requested=hex(ENABLE), initialize=initialize,
                        owned_bit17=bit17, whole_block_cleared=bool(initialize),
                        whole_block_preserved=not initialize, pointer_preserved=True,
                        guards_preserved=True, **saved))
                    counts['enable_existing_cases'] += 1
            for route in ROUTES:
                for active in (False, True):
                    for requested in (0, ENABLE):
                        for initialize in (0, 1):
                            frame_base, frame = frame_block(native, route, ENABLE if active else 0)
                            base, feature = guard_block(native, raw)
                            P.from_address(frame + route['feature']).value = feature
                            rva = generic_call(native, route, frame, requested, initialize)
                            expected_mask = ENABLE if active or requested else 0
                            assert Q.from_address(frame + route['mask']).value == expected_mask
                            assert P.from_address(frame + route['feature']).value == feature
                            assert c.string_at(feature, SIZE) == raw
                            check_guards(base)
                            check_frame_guards(frame_base)
                            # Enable selection explicitly only after checking
                            # that requested=0 did not alter the allocation mask.
                            Q.from_address(frame + route['mask']).value = ENABLE
                            saved = roundtrip(native, scratch, route, frame, raw)
                            check_guards(base)
                            check_frame_guards(frame_base)
                            rows.append(dict(route=route['name'], entry_rva=hex(rva),
                                existing=True, active_before=active, requested=hex(requested),
                                initialize=initialize, owned_bit17=bit17,
                                mask_after_call=hex(expected_mask), save_mask_set_explicitly=True,
                                whole_block_cleared=False, whole_block_preserved=True,
                                pointer_preserved=True, guards_preserved=True, **saved))
                            counts['generic_existing_cases'] += 1
        # Fresh objects: frame bit13 is absent and its feature pointer is null.
        # The invalid active-bit13/null-pointer clear protocol is never invoked.
        for route in ROUTES:
            for initialize in (0, 1):
                entries = ['generic', 'enable'] if route['name'] == 'DRS' else ['generic']
                for entry in entries:
                    frame_base, frame = frame_block(native, route, 0)
                    try:
                        if entry == 'enable':
                            rva = 0x2b500
                            assert native.fn(rva, I, P, I)(frame, initialize) == 0
                        else:
                            rva = generic_call(native, route, frame, ENABLE, initialize)
                        feature = P.from_address(frame + route['feature']).value
                        assert feature and Q.from_address(frame + route['mask']).value == ENABLE
                        known_zero = entry == 'generic' or bool(initialize)
                        if known_zero:
                            assert c.string_at(feature, SIZE) == bytes(SIZE)
                            expected = bytes(SIZE)
                            counts['fresh_whole_block_zero_cases'] += 1
                        else:
                            # Do not observe allocator contents. Fill every byte
                            # with generated data before any feature read or save.
                            expected = owned_payload(0.375)
                            c.memmove(feature, expected, SIZE)
                            counts['fresh_uninitialized_payload_not_read_cases'] += 1
                        check_frame_guards(frame_base)
                        saved = roundtrip(native, scratch, route, frame, expected)
                        check_frame_guards(frame_base)
                        rows.append(dict(route=route['name'], entry_rva=hex(rva),
                            existing=False, active_before=False, requested=hex(ENABLE),
                            initialize=initialize, allocation_observed=True,
                            allocation_payload_observed=known_zero,
                            filled_before_first_payload_read=not known_zero,
                            whole_block_cleared=known_zero, guards_preserved=True, **saved))
                        counts['fresh_cases'] += 1
                    finally:
                        release_feature(native, route, frame)
    counts['cases'] = len(rows)
    counts['writer_reader_pairs'] = len(rows)
    counts['existing_whole_block_preserved_cases'] = sum(
        row.get('existing', False) and row.get('whole_block_preserved', False) for row in rows)
    counts['selected_nonzero_owned_bit17_roundtrips'] = sum(
        row['bit17_selected'] for row in rows)
    assert counts['cases'] == 66 and counts['existing_whole_block_preserved_cases'] == 54
    assert counts['selected_nonzero_owned_bit17_roundtrips'] == 55
    paths = [Path(__file__), *(Path(__file__).with_name(name) for name in (
        'audit_frm2_numeric.py', 'frm2_work_directory.py', 'verify_vqm_save_selection.py',
        'verify_vqm_feature_dse5_roundtrip.py', 'audit_v4x_vqm_fields.py', 'probe_frm2.py',
        'verify_reference_ddi.py'))]
    sources = dict(allocation_audit['source_sha256'])
    sources.update({p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in paths})
    return dict(schema=1, dse_sha256=SHA256, source_sha256=sources,
        allocation_audit=allocation_audit,
        counts=dict(counts), cases=rows, scratch_removed=True,
        conclusion='The DRS enable helper clears existing feature bytes only for nonzero initialize; both generic allocators retain every existing feature byte even with initialize=1. All selected owned bit17 bytes survive Frame save/read.',
        limits=['Owned finite feature values and feature-only zero-dimension allocation requests',
                'Fresh enable with initialize=0 gives no defined payload; its contents were never read before being fully filled',
                'Whole-block clear also removes inner flags, so bit17 is then omitted rather than computed by serialization',
                'A generic request of zero was checked before explicitly enabling the save mask',
                'No authoring assignment, commercial nonzero bit17 producer or arbitrary invalid allocation protocol is proved'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--decoder-dir', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.decoder_dir)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report['counts']))


if __name__ == '__main__':
    main()
