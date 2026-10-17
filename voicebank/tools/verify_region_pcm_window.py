"""Verify real DSE bounded PCM reads using a synthetic file, never commercial PCM."""
import argparse
import ctypes as c
import json
from pathlib import Path
import struct

from audit_frm2_numeric import Native, P, I, Q, SHA256


def run(dll, work):
    work.mkdir(parents=True, exist_ok=True)
    path = work / 'owned_window_pcm.bin'
    pcm = [i * 193 - 10000 for i in range(97)]
    with path.open('xb') as stream:
        stream.write(b'X' * 64 + struct.pack('<97h', *pcm) + b'Y' * 64)
    native = Native(dll)
    crt = c.CDLL('ucrtbase.dll')
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    handle = crt._wfopen(str(path.resolve()), 'rb')
    if not handle:
        raise OSError('Cannot open synthetic PCM')
    read = native.fn(0x175c90, None, P, P, P, I, I, P)
    part, stream = native.alloc(0x280), native.alloc(0x80)
    Q.from_address(part + 0x1b8).value = 64
    I.from_address(part + 0x1d0).value = len(pcm)
    P.from_address(stream + 8).value = handle
    cases = 0
    try:
        for core in [0, 1, 13, 48, 96]:
            Q.from_address(part + 0x1c0).value = 64 + core * 2
            for start in [-160, -98, -49, -1, 0, 1, 48, 96, 97, 160]:
                for count in [0, 1, 2, 17, 97, 205]:
                    size = count * 2
                    output = native.alloc(size + 32)
                    c.memset(output, 0xA5, size + 32)
                    read(0, part, stream, start, count, output + 16)
                    expected = [pcm[i] if 0 <= i < len(pcm) else 0
                                for i in range(core + start, core + start + count)]
                    actual = list(struct.unpack('<' + str(count) + 'h', c.string_at(output + 16, size)))
                    assert actual == expected, (core, start, count)
                    assert c.string_at(output, 16) == b'\xA5' * 16
                    assert c.string_at(output + 16 + size, 16) == b'\xA5' * 16
                    cases += 1
    finally:
        crt.fclose(handle)
    return dict(dll_sha256=SHA256, cases=cases, mismatches=0, guard_failures=0,
                source_samples=len(pcm), core_offsets_samples=[0, 1, 13, 48, 96],
                scope='Normal file-backed PCM read with valid SND extent; no cache or truncated-file claim')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--work-dir', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll, args.work_dir)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result))
