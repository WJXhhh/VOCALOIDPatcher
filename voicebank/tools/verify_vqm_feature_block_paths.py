"""Review bounded feature-block candidates and owned native write boundaries.

The immediate scan is not an instruction decoder. Each pinned-image candidate
has a separate Ghidra instruction/object review. Native fixtures contain only
owned spectra and metadata; no commercial payload or decompiled code is saved.
"""
from __future__ import annotations

import argparse
from collections import Counter
import ctypes as c
import hashlib
import json
from pathlib import Path
import random
import struct

from audit_frm2_numeric import F, I, Native, P, Q, SHA256, f32
from locate_vqm_feature_accesses import executable_sections


GROUPS = (
    (0x2b500, 0x128, 0x2b52d, 0x2b54b),
    (0x7b0f0, 0x128, 0x7b137, 0x7b151),
    (0x7b4b0, 0x128, 0x7b51d, 0x7b537),
    (0x7f1a0, 0x128, 0x7fad1, 0x7fae4),
    (0x87750, 0x128, 0x88060, 0x88073),
    (0xd2710, 0x128, 0xd28e4, 0xd28f7),
    (0xd92e0, 0x128, 0xd9346, 0xd9360),
    (0xfb4a0, 0x128, 0xfef2f, 0xfef49),
    (0x12a7b0, 0x208, 0x12b177, 0x12b18a),
    (0x1363a0, 0x208, 0x136e31, 0x136e44),
)
REVIEW = {}
for function, pointer, allocation, clear in GROUPS:
    for address, operation, target in (
            (allocation, 'allocation', 0x216454),
            (clear, 'zero_fill', 0x217c94)):
        REVIEW[address] = dict(function_rva=hex(function),
            frame_feature_pointer_offset=hex(pointer), operation=operation,
            called_rva=hex(target))
for function, pointer, address in (
        (0x7eeb0, 0x128, 0x7efd4), (0x88f40, 0x128, 0x891ae),
        (0x12c550, 0x208, 0x12c625), (0x12cf40, 0x208, 0x12d232)):
    REVIEW[address] = dict(function_rva=hex(function),
        frame_feature_pointer_offset=hex(pointer), operation='sized_release',
        called_rva='0x216490')
REVIEW[0x130490] = dict(function_rva='0x1303b0', operation='overlapping_bytes',
    instruction_rva='0x13048e',
    note='Frame+0x228 pointer reset; the ECX-immediate pattern starts inside its displacement')


def candidates(code, base=0):
    result = []
    for prefix, argument in ((b'\x41\xb8', 'R8D'), (b'\x41\xb9', 'R9D'),
                             (b'\xba', 'EDX'), (b'\xb9', 'ECX')):
        needle = prefix + struct.pack('<I', 0x228)
        start = 0
        while (position := code.find(needle, start)) >= 0:
            result.append(dict(rva=hex(base + position), argument=argument))
            start = position + 1
    return sorted(result, key=lambda row: int(row['rva'], 16))


def fixture_checks():
    checked = 0
    for prefix, argument in ((b'\x41\xb8', 'R8D'), (b'\x41\xb9', 'R9D'),
                             (b'\xba', 'EDX'), (b'\xb9', 'ECX')):
        for immediate in (0x228, 0x227):
            rows = candidates(b'\xcc' * 3 + prefix + struct.pack('<I', immediate))
            expected = [dict(rva='0x3', argument=argument)] if immediate == 0x228 else []
            if prefix == b'\x41\xb9' and immediate == 0x228:
                expected.append(dict(rva='0x4', argument='ECX'))
            assert rows == expected
            checked += 1
    # Deliberately demonstrate why a byte candidate is not an instruction.
    rows = candidates(bytes.fromhex('4889b928020000'))
    assert rows == [dict(rva='0x2', argument='ECX')]
    return checked + 1


def owned_native_checks(native):
    rows = []
    rng = random.Random(399)
    sentinels = [f32(0.25 + k / 1024) for k in range(0x228 // 4)]
    sentinel_bytes = struct.pack('<138f', *sentinels)
    for producer in (0x7b0f0, 0x7b4b0):
        for size in (32, 128, 512, 2048):
            for shape in ('zero', 'flat', 'random', 'single'):
                power = [0. if shape == 'zero' else 1234. if shape == 'flat'
                         else f32(10 ** rng.uniform(-8, 9)) if shape == 'random'
                         else (1e7 if k == size // 3 else 0.) for k in range(size)]
                boundaries = [f32(22050 * k / 41) for k in range(42)]
                for initialized in (False, True):
                    frame = native.alloc(0x230)
                    feature = native.alloc(0x228)
                    c.memmove(feature, sentinel_bytes, len(sentinel_bytes))
                    P.from_address(frame + 0x128).value = feature
                    Q.from_address(frame + 0x50).value = 8192 if initialized else 0
                    native.fn(producer, Q, P, P, I, F, P)(frame,
                        native.array(F, power), size, 22050., native.array(F, boundaries))
                    after = c.string_at(feature, 0x228)
                    changed = [k for k, (a, b) in enumerate(zip(sentinel_bytes, after)) if a != b]
                    if initialized:
                        assert all(0x2c <= k < 0xcc for k in changed)
                        assert after[0x21c:0x220] == sentinel_bytes[0x21c:0x220]
                    else:
                        assert all(value == 0 for k, value in enumerate(after)
                                   if not 0x2c <= k < 0xcc)
                    assert Q.from_address(frame + 0x50).value & 8192
                    rows.append(dict(producer_rva=hex(producer), bins=size,
                        shape=shape, feature_already_initialized=initialized,
                        changed_byte_count=len(changed),
                        bit17=F.from_address(feature + 0x21c).value,
                        outside_band_contract=True))
    resets = []
    for initialized in (False, True):
        for reset in (0, 1):
            frame = native.alloc(0x230)
            feature = native.alloc(0x228)
            c.memmove(feature, sentinel_bytes, len(sentinel_bytes))
            P.from_address(frame + 0x128).value = feature
            Q.from_address(frame + 0x50).value = 8192 if initialized else 0
            native.fn(0x2b500, Q, P, I)(frame, reset)
            after = c.string_at(feature, 0x228)
            assert after == (bytes(0x228) if reset else sentinel_bytes)
            resets.append(dict(feature_already_initialized=initialized, reset=reset,
                bit17=F.from_address(feature + 0x21c).value, whole_block_contract=True))
    geometry = []
    divisor = F.from_address(native.lib._handle + 0x5bfd68).value
    assert divisor == 1500.
    for rate in (22050, 44100, 48000, 96000):
        config = native.alloc(0x35f0)
        native.fn(0x90de0, P, P)(config)
        context = native.alloc(0xce0)
        native.fn(0xcdde0, P, P, I, P)(context, rate, config)
        count = I.from_address(context + 0xa5c).value
        expected = int(f32(f32(rate // 2) / divisor))
        assert count == expected
        end = 0xcc + 4 * count
        assert end <= 0x21c
        geometry.append(dict(sample_rate=rate, native_count=count,
            predicted_count=expected, indexed_write_start='0xcc',
            indexed_write_end_exclusive=hex(end), reaches_bit17=False))
    return dict(band_canary_cases=rows, helper_reset_cases=resets,
                native_selected_band_count_cases=geometry, band_count_divisor=divisor)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    data = args.dll.read_bytes()
    if hashlib.sha256(data).hexdigest() != SHA256:
        raise ValueError('Unsupported DSE hash; review instruction/object candidates again')
    checks = fixture_checks()
    rows = [row for _, base, code in executable_sections(data) for row in candidates(code, base)]
    assert {int(row['rva'], 16) for row in rows} == set(REVIEW)
    for row in rows:
        row['review'] = REVIEW[int(row['rva'], 16)]
    report = dict(dse_sha256=SHA256,
        scanner_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        fixture_checks=checks, candidate_count=len(rows), candidates=rows,
        operation_counts=dict(Counter(row['review']['operation'] for row in rows)),
        review_basis='Ghidra GUI metadata plus bounded actual disassembly and object-layout queries',
        indirect_ranges=[
            dict(function_rva='0xd2cc0', call_rva='0xd6b27', start='0x2c',
                 end_exclusive='0xcc', count=40, operation='ENV interval means'),
            dict(function_rva='0xd2cc0', call_rva='0xd6b52', start='0x16c',
                 end_exclusive='0x20c', operation='zero_fill'),
            dict(function_rva='0xe3640', start='0x16c', end_exclusive='0x1bc',
                 count=20, operation='40-input cosine projection'),
            dict(function_rva='0xe3640', start='0x16c', end_exclusive='0x1a0',
                 count=13, operation='24-input cosine projection'),
            dict(function_rva='0xe3640', start='0x2c', end_exclusive='0x40',
                 count=5, operation='frequency-band means')],
        native=owned_native_checks(Native(args.dll)),
        scope='Selected exact immediate patterns, reviewed initialization sites and named indexed ranges. '
              'Does not exhaust arbitrary pointer rebasing, variable-length block operations, other modules '
              'or commercial production configurations. A cleared bit17 is not its production formula.')
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(candidate_count=len(rows), operations=report['operation_counts'],
        band_canary_cases=len(report['native']['band_canary_cases']),
        helper_reset_cases=len(report['native']['helper_reset_cases']),
        band_count_cases=report['native']['native_selected_band_count_cases'])))


if __name__ == '__main__':
    main()
