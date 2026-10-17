"""Verify text voiced groups -> native record byte -> binary and duration consumers."""
import argparse
import ctypes as c
import itertools
import json
import hashlib
import struct
from collections import Counter
from pathlib import Path

from audit_frm2_numeric import Native, P, I, Q, F, B, SHA256
from verify_region_duration_allocation import predict, predict_cost


def run(dll, work):
    work.mkdir(parents=True, exist_ok=True)
    n = Native(dll)
    text_load = n.fn(0x125530, Q, P, c.c_char_p)
    init = n.fn(0x124b80, P, P)
    write = n.fn(0x126160, Q, P, P)
    read = n.fn(0x1259f0, Q, P, P)
    expand = n.fn(0x1b2260, None, P, P, F, I, F)
    allocate = n.fn(0x19d650, None, P, P, I, P, I, I, I, B, B)
    crt = c.CDLL('ucrtbase.dll')
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I

    def records(table):
        count = I.from_address(table + 0x144).value
        assert 0 <= count <= 4, count
        addresses = P.from_address(table + 0x148).value
        result = []
        for j in range(count):
            record = P.from_address(addresses + 8*j).value
            result.append(dict(name=c.string_at(record).decode('ascii'),
                               byte38=B.from_address(record + 0x38).value))
        return result

    rows = []
    allocations = []
    for index, (left, right, duplicate) in enumerate(itertools.product([False, True], [False, True], [False, True])):
        groups = [('Left', left, ['a']), ('Right', right, ['b'])]
        if duplicate:
            groups.append(('Duplicate', not left, ['a']))
        source = ''.join('phonetic_group, name="' + name + '", voiced=' + str(voiced).lower() + '\n' +
                         ''.join('\t' + token + '\n' for token in tokens)
                         for name, voiced, tokens in groups) + 'end\n'
        path = work / f'groups_{index}.txt'
        with path.open('x', encoding='ascii', newline='\n') as file:
            file.write(source)
        table = n.alloc(0x180)
        init(table)
        assert text_load(table, str(path.resolve()).encode('ascii')) == 0
        actual = records(table)
        expected = [dict(name='a', byte38=int(not left)), dict(name='b', byte38=int(not right))]
        assert actual == expected, (groups, actual)
        binary = work / f'groups_{index}.bin'
        with binary.open('xb'):
            pass
        handle = crt._wfopen(str(binary.resolve()), 'wb')
        assert handle
        stream = n.alloc(0x80)
        P.from_address(stream + 8).value = handle
        try:
            assert write(table, stream) == 0
        finally:
            crt.fclose(handle)
        loaded = n.alloc(0x180)
        init(loaded)
        handle = crt._wfopen(str(binary.resolve()), 'rb')
        assert handle
        P.from_address(stream + 8).value = handle
        Q.from_address(stream).value = 0
        try:
            assert read(loaded, stream) == 0
        finally:
            crt.fclose(handle)
        assert records(loaded) == expected
        blob = binary.read_bytes()
        assert len(blob) >= 70
        assert [blob[8 + j*31 + 30] for j in range(2)] == [int(not left), int(not right)]
        rows.append(dict(groups=groups, records=actual, roundtrip=True, binary_size=len(blob)))
        flags = [int(not left), int(not right)]
        for target in range(2, 37):
            state, part, output = n.alloc(0x1d00), n.alloc(0x280), n.alloc(8)
            for offset, name in [(0x10, b'a\0'), (0x210, b'b\0')]:
                c.memmove(state + offset, name, len(name))
            P.from_address(state + 0x1c38).value = loaded
            I.from_address(state + 4).value = -1
            I.from_address(state + 12).value = 1
            B.from_address(state + 0x1b15).value = 1
            P.from_address(state + 0x810).value = part
            I.from_address(state + 0x81c).value = 1
            align = n.array(I, [0, 7, 2, 5, 7, 18, 9, 16])
            P.from_address(part + 0x1d8).value = align
            P.from_address(part + 0x1e0).value = align + 32
            expand(0, state, 1., -1, .5)
            allocate(0, output, target, state, 0, 2, 0, 0, 0)
            result = [I.from_address(state + 0x1510 + j*4).value for j in range(2)]
            expected_lengths = predict([3, 7], [7, 11], flags, target)
            assert result == expected_lengths
            cost = F.from_address(output + 4).value
            assert abs(cost - predict_cost([3, 7], [7, 11], flags, target, expected_lengths)) < 3e-6
            allocations.append(dict(group_case=index, target=target, result=result, cost=cost))
    return dict(dse_sha256=SHA256, group_cases=len(rows), allocation_cases=len(allocations),
                groups=rows, allocations=allocations,
                scope='Owned text groups, actual native table production and binary roundtrip, '
                      'then native duration allocation; byte38=not voiced; duplicate names use first group; '
                      'not a claim that commercial labels equal measured frame voicing')


def audit_references(dll, root, baseline):
    known = {bank['audit']['bank']: bank['ddi_sha256'] for bank in json.loads(baseline.read_text(encoding='utf-8'))['banks']}
    n = Native(dll)
    init = n.fn(0x124b80, P, P)
    read = n.fn(0x1259f0, Q, P, P)
    membership = n.fn(0x127c80, Q, P, I, c.c_char_p)
    crt = c.CDLL('ucrtbase.dll')
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt._fseeki64.argtypes, crt._fseeki64.restype = [P, c.c_int64, I], I
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    rows = []
    for path in sorted(root.rglob('*.ddi')):
        if path.stem not in known:
            continue
        raw = path.read_bytes()
        digest = hashlib.sha256(raw).hexdigest()
        assert digest == known[path.stem]
        offset = raw.find(b'PHDC')
        assert offset >= 0
        size, flags, count = struct.unpack_from('<III', raw, offset + 4)
        assert flags == 4 and 0 < count < 10000 and offset + size <= len(raw)
        assert 16 + count * 31 <= size
        table, stream = n.alloc(0x180), n.alloc(0x80)
        init(table)
        handle = crt._wfopen(str(path.resolve()), 'rb')
        assert handle
        try:
            assert crt._fseeki64(handle, offset + 8, 0) == 0
            P.from_address(stream + 8).value = handle
            # CChunk::Read seeks to chunk_start + size using this absolute cursor.
            # Seeking FILE alone would make nested PHG reads seek to the wrong offset.
            Q.from_address(stream).value = offset + 8
            assert read(table, stream) == 0
            assert Q.from_address(stream).value == offset + size
        finally:
            crt.fclose(handle)
        assert I.from_address(table + 0x144).value == count
        array = P.from_address(table + 0x148).value
        class_table = P.from_address(table + 0x158).value
        records = []
        totals = Counter()
        for j in range(count):
            begin = offset + 16 + 31*j
            name = raw[begin:begin+18].split(b'\0', 1)[0].decode('ascii')
            flag = raw[begin+30]
            addr = P.from_address(array + 8*j).value
            assert c.string_at(addr).decode('ascii') == name
            assert B.from_address(addr+0x38).value == flag and flag in [0, 1]
            classes = [label for label in ['Vowels', 'Nasals', 'Liquids', 'Semivowels', 'VoicedFricatives',
                       'UnvoicedFricatives', 'VoicedPlosives', 'UnvoicedPlosives', 'VoicedAffricates',
                       'UnvoicedAffricates', 'Silence']
                       if membership(class_table, j, label.encode('ascii'))]
            totals['voiced' if flag == 0 else 'unvoiced'] += 1
            records.append(dict(name=name, byte38=flag, voiced=flag == 0, classes=classes))
        rows.append(dict(bank=path.stem, ddi_sha256=digest, records=count, counts=dict(totals), phonemes=records))
    assert {row['bank'] for row in rows} == set(known)
    return dict(banks=rows, bank_count=len(rows), record_count=sum(row['records'] for row in rows),
                scope='Hash-pinned installed DDI only, native PHDC load vs independent 31-byte record walk; no DDB/PCM read')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--work-dir', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--reference-root', type=Path)
    parser.add_argument('--baseline', type=Path)
    args = parser.parse_args()
    result = run(args.dll, args.work_dir)
    if args.reference_root:
        if args.baseline is None:
            parser.error('--reference-root requires --baseline')
        result['references'] = audit_references(args.dll, args.reference_root, args.baseline)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: result[key] for key in ['group_cases', 'allocation_cases']}))
