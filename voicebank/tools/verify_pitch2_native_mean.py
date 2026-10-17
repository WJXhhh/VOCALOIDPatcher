"""Check the installed DSE track mean-pitch helper with generated frame records."""

import argparse
import ctypes as C
import hashlib
import json
import math
from pathlib import Path


EXPECTED_SHA256 = '7a4965bd377dbc2b08710e807ae1345b67d9fff6579ca0c01a769f744366f5b5'
RVA = 0xC86A0
SENTINEL = -3.4028234663852886e38


def f32(value):
    return C.c_float(value).value


def observed_mean(function, values, start, end):
    records = [(C.c_byte * 0x100)() for _ in values]
    addresses = (C.c_void_p * len(values))()
    for index, (value, encoded_cent) in enumerate(values):
        C.c_float.from_buffer(records[index], 0xF0).value = value
        C.c_uint32.from_buffer(records[index], 0x50).value = 0x80000000 if encoded_cent else 0
        addresses[index] = C.addressof(records[index])
    aggregate = (C.c_byte * 0x280)()
    result = (C.c_float * 6)()
    C.c_void_p.from_buffer(aggregate, 0x20).value = C.addressof(addresses)
    C.c_void_p.from_buffer(aggregate, 0x268).value = C.addressof(result)
    function(C.addressof(aggregate), 0., start, end, 0x5D)
    return result[0]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    digest = hashlib.sha256(args.dll.read_bytes()).hexdigest()
    assert digest == EXPECTED_SHA256, 'DSE version differs'
    library = C.WinDLL(str(args.dll))
    function = C.CFUNCTYPE(None, C.c_void_p, C.c_double, C.c_int, C.c_int, C.c_int)(
        library._handle + RVA)
    cases = [
        ('hz_positive_only', [(220., False), (330., False), (880., False), (0., False)],
         0, 3, [220., 330., 880.]),
        ('bounded_region', [(220., False), (330., False), (880., False), (0., False)],
         1, 2, [330., 880.]),
        ('encoded_cent', [(-1200., True), (-500., True), (700., True), (SENTINEL, True)],
         0, 3, [-1200., -500., 700.]),
        ('mixed_format', [(330., False), (-400., True), (0., False), (880., False)],
         0, 3, [1200. * math.log2(330. / 440.), -400., 1200.]),
        ('no_positive_pitch', [(0., False), (-1., False), (SENTINEL, True)],
         0, 2, []),
    ]
    rows = []
    for name, values, start, end, expected_values in cases:
        actual = observed_mean(function, values, start, end)
        if name in ('hz_positive_only', 'bounded_region'):
            expected_values = [1200. * math.log2(value / 440.) for value in expected_values]
        expected = sum(expected_values) / len(expected_values) if expected_values else 0.
        error = abs(actual - expected)
        assert error < 1e-4, (name, actual, expected, error)
        rows.append(dict(name=name, start=start, end=end, actual_cent=actual,
                         expected_cent=expected, error_cent=error))
    result = dict(dse_sha256=digest, helper_rva=hex(RVA), cases=rows,
                  scope='DSE track-statistic mean over a selected frame interval. '
                        'The caller that chooses PHDC voiced ART intervals and writes '
                        'DDI pitch2 has not been established by this test.')
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(cases=len(rows), max_error_cent=max(row['error_cent'] for row in rows))))


if __name__ == '__main__':
    main()
