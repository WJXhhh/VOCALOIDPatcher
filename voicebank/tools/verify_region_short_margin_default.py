"""Verify default, file-backed DSE PCM windows for the eight V4X short-margin shapes.

The report supplies only frame counts, margins and core offsets. Audio is generated
locally; no commercial PCM is read or written.
"""

import argparse
import ctypes as c
import hashlib
import json
from pathlib import Path
import struct

from audit_frm2_numeric import Native, P, I, Q, SHA256


WINDOW = 2048
HOP = 256
PREFIX = 4096


def run(dll: Path, report: Path, work: Path) -> dict:
    work.mkdir(parents=True, exist_ok=True)
    source = report.read_bytes()
    banks = json.loads(source)['banks']
    native = Native(dll)
    read = native.fn(0x175c90, None, P, P, P, I, I, P)
    crt = c.CDLL('ucrtbase.dll')
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    rows = []

    for bank in banks:
        for sample in bank['regions']['short_margin_samples']:
            frames = sample['frames']
            count = frames * HOP + sample['pcm_margin']
            core = sample['core_delta_bytes'] // 2
            # Nonzero sentinels on both sides make accidental cross-SND reads visible.
            samples = [((i * 193) % 60001) - 30000
                       for i in range(PREFIX + count + PREFIX)]
            path = work / f'owned_short_margin_{len(rows)}.bin'
            with path.open('xb') as output:
                output.write(struct.pack(f'<{len(samples)}h', *samples))
            handle = crt._wfopen(str(path.resolve()), 'rb')
            if not handle:
                raise OSError('Cannot open owned synthetic PCM')
            part, stream = native.alloc(0x280), native.alloc(0x80)
            Q.from_address(part + 0x1b8).value = PREFIX * 2
            Q.from_address(part + 0x1c0).value = (PREFIX + core) * 2
            I.from_address(part + 0x1d0).value = count
            P.from_address(stream + 8).value = handle
            front_max = tail_max = last_tail = 0
            try:
                for frame in range(frames):
                    start = frame * HOP - WINDOW // 2
                    output = native.alloc((WINDOW + 1) * 2 + 32)
                    c.memset(output, 0xa5, (WINDOW + 1) * 2 + 32)
                    read(0, part, stream, start, WINDOW + 1, output + 16)
                    begin = PREFIX + core + start
                    expected = [samples[i] if PREFIX <= i < PREFIX + count else 0
                                for i in range(begin, begin + WINDOW + 1)]
                    actual = list((c.c_int16 * (WINDOW + 1)).from_address(output + 16))
                    assert actual == expected, (bank['audit']['bank'], sample['key'], frame)
                    assert c.string_at(output, 16) == b'\xa5' * 16
                    assert c.string_at(output + 16 + (WINDOW + 1) * 2, 16) == b'\xa5' * 16
                    front_max = max(front_max, max(0, PREFIX - begin))
                    tail = max(0, begin + WINDOW + 1 - (PREFIX + count))
                    tail_max = max(tail_max, tail)
                    if frame == frames - 1:
                        last_tail = tail
            finally:
                crt.fclose(handle)
            rows.append(dict(bank=bank['audit']['bank'], key=sample['key'],
                             frames=frames, pcm_count=count, core_offset_samples=core,
                             max_front_zero_samples=front_max,
                             max_tail_zero_samples=tail_max,
                             final_frame_tail_zero_samples=last_tail,
                             matched_windows=frames))

    assert len(rows) == 8
    return dict(dll_sha256=SHA256, input_sha256=hashlib.sha256(source).hexdigest(),
                window=WINDOW, hop=HOP, sample_shapes=len(rows),
                frame_windows=sum(row['matched_windows'] for row in rows),
                mismatches=0, guard_failures=0, rows=rows,
                scope='Direct normal file-backed DSE read windows with owned PCM and '
                      'V4X metadata shapes; does not observe Editor cache mode or frame requests')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--work-dir', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll, args.report, args.work_dir)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: result[key] for key in ('sample_shapes', 'frame_windows',
                                                  'mismatches', 'guard_failures')}))
