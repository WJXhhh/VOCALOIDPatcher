"""Observe split ART work-item writes for every inventoried PHDC class.

Uses owned metadata and a tiny generated PCM file in a disposable process.
Observations describe synthetic inputs, not editor reachability or sound quality.
"""
import argparse
import ctypes as c
import hashlib
import itertools
import json
import struct
from collections import Counter
from pathlib import Path

from audit_frm2_numeric import Native, P, I, Q, F, B, SHA256


def run(dll, catalog, work):
    inventory_bytes = catalog.read_bytes()
    inventory = json.loads(inventory_bytes)
    categories = [None] + inventory['class_names'] + ['Whisper']
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
    names = []
    for token in ['a', 'b', 'donor']:
        name = n.alloc(0x40)
        raw = token.encode() + b'\0'
        c.memmove(name, raw, len(raw))
        names.append(name)
    I.from_address(table + 0x144).value = len(names)
    P.from_address(table + 0x148).value = n.array(P, names)
    frames, items = n.alloc(256 * 0x18), n.alloc(16 * 0x140)
    P.from_address(ring).value = frames
    I.from_address(ring + 8).value = 256
    for offset in [0x20, 0x38, 0x50]:
        P.from_address(ring + offset).value = items
    for offset in [0x28, 0x2c, 0x40, 0x58]:
        I.from_address(ring + offset).value = 16
    for offset in [0x44, 0x5c]:
        I.from_address(ring + offset).value = 15
    parent = n.alloc(0x180)
    callback = c.CFUNCTYPE(P, P)(lambda unused: parent)
    n.keep.append(callback)
    vtable = n.alloc(0x68)
    P.from_address(vtable + 0x60).value = c.cast(callback, P).value
    layouts = [[(0, 7, 1, 4), (7, 20, 9, 18), (20, 31, 21, 26), (31, 48, 33, 46)],
               [(0, 8, 1, 5), (8, 22, 10, 20), (22, 33, 23, 28), (33, 51, 35, 49)]]
    descriptors, parts = [], []
    for groups in layouts:
        part, descriptor = n.alloc(0x280), n.alloc(0x68)
        P.from_address(part).value = vtable
        alignment = n.array(I, [value for group in groups for value in group])
        P.from_address(part + 0x1d8).value = alignment
        P.from_address(part + 0x1e0).value = alignment + 64
        I.from_address(part + 0x1b0).value = groups[-1][1]
        Q.from_address(part + 0x1b8).value = 64
        Q.from_address(part + 0x1c0).value = 64
        I.from_address(part + 0x1d0).value = 640
        P.from_address(descriptor).value = part
        I.from_address(descriptor + 12).value = 2
        for offset in [0x28, 0x48]:
            Q.from_address(descriptor + offset).value = 15
        descriptors.append(descriptor)
        parts.append(part)
    analysis, destination = n.alloc(0x100), n.alloc(2 * 17)
    I.from_address(analysis + 0x8c).value = 16
    P.from_address(context + 0x1fc8).value = analysis
    P.from_address(context + 0x24118).value = destination
    F.from_address(context + 0x1d550).value = 8
    stream = n.alloc(0x80)
    P.from_address(voice + 0x1a0).value = stream
    work.mkdir(parents=True, exist_ok=False)
    pcm = work / 'owned.pcm'
    samples = [200 if 80 <= i < 110 or 250 <= i < 290 else 12000 for i in range(640)]
    pcm.write_bytes(b'\x35' * 64 + struct.pack('<640h', *samples) + b'\x35' * 64)
    crt = c.CDLL('ucrtbase.dll')
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    handle = crt._wfopen(str(pcm.resolve()), 'rb')
    if not handle:
        raise OSError('Cannot open owned PCM fixture')
    P.from_address(stream + 8).value = handle
    record, category_buffer = n.alloc(0x30), n.alloc(64)
    P.from_address(record + 0x18).value = category_buffer
    P.from_address(classes + 0x148).value = record
    coefficients = n.alloc(24)
    summaries, rows = [], []
    try:
        for category in categories:
            summary = Counter()
            examples = []
            membership_cases = [(0,)] if category is None else [(0,), (1,), (0, 1)]
            for members, stage, reverse, coefs, expansion, counts in itertools.product(
                    membership_cases, [0, 1], [0, 1], [(1., 1.), (.5, .75)],
                    [False, True], [(1, 1), (7, 13), (40, 40)]):
                I.from_address(classes + 0x140).value = int(category is not None)
                if category is not None:
                    raw = category.encode() + b'\0'
                    c.memset(category_buffer, 0, 64)
                    c.memmove(category_buffer, raw, len(raw))
                    I.from_address(record).value = len(members)
                    P.from_address(record + 0x10).value = n.array(P, [names[i] for i in members])
                for descriptor in descriptors:
                    I.from_address(descriptor + 8).value = stage
                F.from_address(coefficients).value, F.from_address(coefficients + 4).value = coefs
                for offset in [8, 12, 16, 20]:
                    I.from_address(coefficients + offset).value = (8 if offset < 16 else 128) if expansion else -1
                for offset in [0x34, 0x48, 0x60]:
                    I.from_address(ring + offset).value = 0
                c.memset(frames, 0, 256 * 0x18)
                for slot in range(256):
                    F.from_address(frames + slot * 0x18 + 8).value = -999.
                calls, start = [], 0
                for sequence, (descriptor, left, right) in enumerate([
                        (descriptors[0], counts[0], 0), (descriptors[1], 0, counts[1])]):
                    before = [c.string_at(frames + slot * 0x18, 0x18) for slot in range(256)]
                    assert emit(outer, descriptor, names[0], names[1], left, right,
                                0, 0, coefficients, 17, 23, reverse)
                    item = items + sequence * 0x140
                    assert P.from_address(item + 0x20).value == parts[sequence]
                    assert [I.from_address(item + offset).value for offset in [0x10, 0x14]] == [0, 1]
                    assert [I.from_address(item + offset).value for offset in [0, 4, 8]] == [start, start + left + right, left]
                    changed = {slot for slot in range(256)
                               if c.string_at(frames + slot * 0x18, 0x18) != before[slot]}
                    allowed = set(range(start, start + left + right))
                    outside = sorted(changed - allowed)
                    previous = sorted(changed & set(range(start)))
                    calls.append(dict(outside_slots=outside, prior_slots=previous))
                    start += left + right
                assert I.from_address(ring + 0x34).value == 2
                summary['cases'] += 1
                summary['outside_cases'] += int(any(call['outside_slots'] for call in calls))
                summary['right_rewrites_left_cases'] += int(bool(calls[1]['prior_slots']))
                summary['outside_slots'] += sum(len(call['outside_slots']) for call in calls)
                entry = dict(category=category or 'Default', members=list(members), stage=stage,
                             reverse=reverse, coefficients=coefs, expansion=expansion,
                             counts=counts, calls=calls)
                rows.append(entry)
                if (calls[1]['prior_slots'] or calls[0]['outside_slots']) and len(examples) < 3:
                    examples.append(entry)
            summaries.append(dict(category=category or 'Default', **summary, examples=examples))
    finally:
        crt.fclose(handle)
    return dict(schema_version=1, dll_sha256=SHA256,
                tool_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                inventory_sha256=hashlib.sha256(inventory_bytes).hexdigest(),
                category_count=len(categories), cases=len(rows), native_calls=2 * len(rows),
                structural_assertion_failures=0,
                summaries=summaries, rows=rows,
                scope='Two-stage owned metadata; original left-only emission followed '
                      'by different donor right-only emission, with original physical '
                      'IDs. Enumerates PHDC labels plus Whisper and Default, both '
                      'directions, coefficients and fixed enabled/disabled expansion. '
                      'Outside-slot writes are observations within owned ring storage; '
                      'they do not establish host corruption or audible discontinuity. '
                      'No installed voice PCM, complete event commit or vocoder output.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--catalog', type=Path, required=True)
    parser.add_argument('--work', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.catalog, args.work)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: report[key] for key in ['category_count', 'cases', 'native_calls']}))
    for row in report['summaries']:
        print(json.dumps({key: value for key, value in row.items() if key != 'examples'}))
