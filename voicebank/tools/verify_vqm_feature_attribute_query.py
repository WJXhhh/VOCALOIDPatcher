"""Test a native attribute getter with owned directory leaves holding Frames.

This establishes the isolated query contract, not commercial directory leaf
construction or the author's interpretation/production of the saved feature.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
from pathlib import Path

from audit_frm2_numeric import F, I, Native, P, Q, SHA256, f32


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    native = Native(args.dll)
    frames, features = [], []
    for _ in range(2):
        frame = native.alloc(0x400)
        assert native.fn(0x12a670, P, P)(frame) == frame
        feature = native.alloc(0x228)
        P.from_address(frame + 0x208).value = feature
        Q.from_address(frame + 0x150).value |= 0x2000
        frames.append(frame)
        features.append(feature)
    # Minimal owned directory data. Its leaves explicitly contain native Frames;
    # this is not a claim about all installed-bank directory node constructors.
    root = native.alloc(0x180)
    nodes = []
    for name in ('dynamics', 'opening', 'pitch'):
        node = native.alloc(0x180)
        raw = name.encode('ascii') + b'\0'
        c.memmove(node + 0x18, raw, len(raw))
        leaves = []
        for frame in frames:
            leaf = native.alloc(0x180)
            P.from_address(leaf + 0x160).value = frame
            leaves.append(leaf)
        P.from_address(node + 0x148).value = native.array(P, leaves)
        I.from_address(node + 0x150).value = len(leaves)
        nodes.append(node)
    P.from_address(root + 0x148).value = native.array(P, nodes)
    I.from_address(root + 0x150).value = len(nodes)
    query = native.fn(0x110020, Q, P, c.c_char_p, I, P)
    endpoints = native.fn(0x10ffb0, None, P, c.c_char_p, P, P)
    rows = []
    values = (-3., -0.25, 0., 0.5, 1.25, 3.)
    for value in values:
        for index, (frame, feature) in enumerate(zip(frames, features)):
            F.from_address(feature + 0x21c).value = f32(value + index / 16)
            F.from_address(feature + 0x218).value = f32(-2.5 + index / 8)
            F.from_address(feature + 0x220).value = f32(17. + index)
            F.from_address(frame + 0x1d0).value = f32(333. + index)
        for index in range(2):
            for name, expected in (
                    ('dynamics', F.from_address(features[index] + 0x21c).value),
                    ('opening', F.from_address(features[index] + 0x218).value),
                    ('pitch', F.from_address(frames[index] + 0x1d0).value)):
                result = F(999.)
                status = query(root, name.encode(), index, c.byref(result))
                assert status == 0 and bytes(result) == bytes(F(expected))
                rows.append(dict(input_bit17=value, leaf_index=index, attribute=name,
                    status=status, result=result.value, float32_bits_match=True))
    ranges = []
    for name, offset in (('dynamics', 0x21c), ('opening', 0x218)):
        first, last = F(999.), F(999.)
        endpoints(root, name.encode(), c.byref(first), c.byref(last))
        assert bytes(first) == bytes(F.from_address(features[0] + offset))
        assert bytes(last) == bytes(F.from_address(features[1] + offset))
        ranges.append(dict(attribute=name, first=first.value, last=last.value,
                           float32_bits_match=True))
    boundary = []
    for name, index, expected_status in (('dynamics', -1, 2), ('dynamics', 2, 2),
                                         ('absent', 0, 1)):
        result = F(999.)
        status = query(root, name.encode(), index, c.byref(result))
        assert status == expected_status and result.value == 999.
        boundary.append(dict(attribute=name, leaf_index=index, status=status,
                             output_preserved=True))
    report = dict(dll_sha256=SHA256,
        tool_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        getter_rva='0x110020', endpoint_wrapper_rva='0x10ffb0',
        native_frame_constructor_rva='0x12a670',
        feature_query_contract=dict(directory_leaf_pointer_offset='0x160',
            frame_feature_pointer_offset='0x208', dynamics_feature_offset='0x21c',
            opening_feature_offset='0x218', pitch_frame_offset='0x1d0'),
        attribute_cases=rows, endpoint_cases=ranges, boundary_cases=boundary,
        scope='Owned minimal directory with leaves explicitly holding genuine constructed DSE5 Frames. '
              'Native isolated query contract and endpoint selection only. Does not establish actual '
              'commercial directory leaf construction, general acoustic units, production formula '
              'or full synthesis effect. It is too strong to classify this access as certainly '
              'unrelated solely from matching numeric offsets.')
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(attribute_cases=len(rows), endpoint_cases=len(ranges),
        boundary_cases=len(boundary), all_passed=True)))


if __name__ == '__main__':
    main()
