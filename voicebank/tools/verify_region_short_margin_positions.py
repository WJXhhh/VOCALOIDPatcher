"""Enumerate native default ART position mapping for V4X short-margin layouts.

Only alignment and frame-count metadata come from the banks. The fixture builds
owned work buffers and never loads commercial frames or PCM.
"""

import argparse
import ctypes as c
import hashlib
import itertools
import json
import math
from pathlib import Path

from audit_frm2_numeric import Native, P, I, Q, F, B, SHA256
from verify_region_output_mapping import predict


WINDOW = 2048
HOP = 256


def run(dll: Path, report: Path) -> dict:
    source = report.read_bytes()
    banks = json.loads(source)['banks']
    n = Native(dll)
    emit = n.fn(0x1a0450, B, P, P, P, P, I, I, I, I, P, I, I, B)
    ring, context, config, registry, voice, outer, table, classes = (
        n.alloc(size) for size in [0x80, 0x2b000, 0x100, 0x20, 0x300, 16, 0x180, 0x160])
    P.from_address(outer).value = ring
    P.from_address(outer + 8).value = context
    P.from_address(context).value = config
    P.from_address(config + 0x10).value = registry
    I.from_address(registry + 8).value = 1
    P.from_address(registry + 0x10).value = n.array(P, [voice])
    P.from_address(registry + 0x18).value = n.array(P, [n.alloc(8)])
    P.from_address(voice + 0x170).value = table
    P.from_address(table + 0x158).value = classes
    frames_buffer, items = n.alloc(256 * 0x18), n.alloc(16 * 0x140)
    P.from_address(ring).value = frames_buffer
    I.from_address(ring + 8).value = 256
    for offset in [0x20, 0x38, 0x50]:
        P.from_address(ring + offset).value = items
    for offset in [0x28, 0x2c, 0x40, 0x58]:
        I.from_address(ring + offset).value = 16
    for offset in [0x44, 0x5c]:
        I.from_address(ring + offset).value = 15
    part, parent, descriptor = (n.alloc(size) for size in [0x280, 0x180, 0x68])
    callback = c.CFUNCTYPE(P, P)(lambda unused: parent)
    n.keep.append(callback)
    vtable = n.alloc(0x68)
    P.from_address(vtable + 0x60).value = c.cast(callback, P).value
    P.from_address(part).value = vtable
    P.from_address(descriptor).value = part
    I.from_address(descriptor + 12).value = 1
    for offset in [0x28, 0x48]:
        Q.from_address(descriptor + offset).value = 15
    coefficients = n.array(F, [1., 1., 0., 0., 0., 0.])
    rows = []
    total_cases = total_positions = 0

    for bank in banks:
        for sample in bank['regions']['short_margin_samples']:
            groups = [tuple(group[k] for k in ('start', 'end', 'start2', 'end2'))
                      for group in sample['groups']]
            assert len(groups) == 2
            names = []
            for name in sample['key']:
                raw = name.encode('ascii') + b'\0'
                token = n.alloc(max(0x40, len(raw)))
                c.memmove(token, raw, len(raw))
                names.append(token)
            I.from_address(table + 0x144).value = 2
            P.from_address(table + 0x148).value = n.array(P, names)
            alignment = n.array(I, [value for group in groups for value in group])
            P.from_address(part + 0x1d8).value = alignment
            P.from_address(part + 0x1e0).value = alignment + 32
            I.from_address(part + 0x1b0).value = sample['frames']
            count = sample['frames'] * HOP + sample['pcm_margin']
            max_safe_frame = min(sample['frames'] - 1, (count - WINDOW - 1) // HOP)
            directions = []

            for reverse in (0, 1):
                selected = groups[::-1] if reverse else groups
                widths = [group[1] - group[0] for group in selected]
                assert sum(width * 2 for width in widths) < 256
                cases = unsafe_cases = positions = 0
                highest_frame = -1
                witness = None
                both_positive_witness = None
                inner_bounded_witness = None
                inner_widths = [group[3] - group[2] for group in selected]
                for left, right in itertools.product(range(2 * widths[0] + 1),
                                                     range(2 * widths[1] + 1)):
                    if left + right == 0:
                        continue
                    for offset in (0x34, 0x48, 0x60):
                        I.from_address(ring + offset).value = 0
                    assert emit(outer, descriptor, names[0], names[1], left, right,
                                0, 0, coefficients, 17, 23, reverse) == 1
                    actual = [F.from_address(frames_buffer + index * 0x18 + 8).value
                              for index in range(left + right)]
                    expected = predict(selected[0], left, 0, reverse) + predict(
                        selected[1], right, 1, reverse)
                    assert len(actual) == len(expected)
                    assert all(abs(a - b) < 1e-5 for a, b in zip(actual, expected)), (
                        bank['audit']['bank'], sample['key'], reverse, left, right)
                    requested = [min(sample['frames'] - 1, math.ceil(position))
                                 for position in actual if position >= 0]
                    if requested:
                        highest_frame = max(highest_frame, max(requested))
                    unsafe = [frame for frame in requested if frame > max_safe_frame]
                    if unsafe:
                        unsafe_cases += 1
                        candidate = dict(lengths=[left, right],
                                         first_unsafe_frame=min(unsafe),
                                         last_source_position=max(actual))
                        def better(current):
                            return current is None or (left + right, [left, right]) < (
                                sum(current['lengths']), current['lengths'])

                        if better(witness):
                            witness = candidate
                        if left > 0 and right > 0 and better(both_positive_witness):
                            both_positive_witness = candidate
                        if (0 < left <= inner_widths[0] and
                                0 < right <= inner_widths[1] and
                                better(inner_bounded_witness)):
                            inner_bounded_witness = candidate
                    cases += 1
                    positions += len(actual)
                directions.append(dict(reverse=reverse, cases=cases, positions=positions,
                                       candidate_unsafe_cases=unsafe_cases,
                                       maximum_requested_frame=highest_frame,
                                       shortest_candidate=witness,
                                       shortest_both_positive=both_positive_witness,
                                       shortest_inner_bounded=inner_bounded_witness))
                total_cases += cases
                total_positions += positions

            rows.append(dict(bank=bank['audit']['bank'], key=sample['key'],
                             frames=sample['frames'], groups=sample['groups'],
                             cached_window_max_in_bounds_frame=max_safe_frame,
                             directions=directions))
    assert len(rows) == 8
    return dict(dll_sha256=SHA256, input_sha256=hashlib.sha256(source).hexdigest(),
                window=WINDOW, hop=HOP, sample_shapes=len(rows),
                native_mapping_cases=total_cases, compared_positions=total_positions,
                mismatches=0, rows=rows,
                scope='Owned direct ART emitter, unit coefficients, no category extension; '
                      'ceil(position) is a potential second frame before loader clamping, '
                      'not an observed host PCM request or commercial-cache overread')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll, args.report)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: result[key] for key in (
        'sample_shapes', 'native_mapping_cases', 'compared_positions', 'mismatches')}))
