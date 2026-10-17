"""Check native ART cache construction for the eight V4X short-margin shapes.

The V4X report supplies only geometry. Frame and PCM content is generated in an
owned work directory; every native read stays within its known cache capacity.
"""

import argparse
import ctypes as c
import hashlib
import json
from pathlib import Path
import struct

from audit_frm2_numeric import Native, P, I, Q, F, SHA256
from probe_frm2 import UnvoicedFrame, UNVOICED_MASK, serialize_frame
from verify_region_frame_blend import cents


WINDOW = 2048
HOP = 256
PREFIX = 4096


def run(dll: Path, report: Path, work: Path) -> dict:
    work.mkdir(parents=True, exist_ok=True)
    source = report.read_bytes()
    banks = json.loads(source)['banks']
    native = Native(dll)
    build = native.fn(0x1b9bd0, None, P, c.c_char_p, c.c_char_p, P, P)
    load = native.fn(0x175dd0, None, P, P, I, P, I, I, P)
    init_frame = native.fn(0x12a670, P, P)
    crt = c.CDLL('ucrtbase.dll')
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    rows = []
    native_reads = 0
    cached_reads = 0
    file_reads = 0

    def node(name: bytes, children=()):
        obj = native.alloc(0x280)
        c.memmove(obj + 0x18, name + b'\0', len(name) + 1)
        P.from_address(obj + 0x148).value = native.array(P, children)
        I.from_address(obj + 0x150).value = len(children)
        return obj

    for bank in banks:
        for sample in bank['regions']['short_margin_samples']:
            edge = len(rows)
            frames = sample['frames']
            count = frames * HOP + sample['pcm_margin']
            core = sample['core_delta_bytes'] // 2
            safe = min(frames - 1, (count - WINDOW - 1) // HOP)
            assert 0 <= safe < frames
            samples = [((i * 193 + edge * 251) % 60001) - 30000
                       for i in range(PREFIX + count + PREFIX)]
            blob = bytearray(struct.pack(f'<{len(samples)}h', *samples))
            offsets = []
            for index in range(frames):
                offsets.append(len(blob))
                blob.extend(serialize_frame(UnvoicedFrame(
                    0, float(1000 + index), UNVOICED_MASK, 220. + index)))
            path = work / f'owned_short_margin_cache_{edge}.bin'
            with path.open('xb') as output:
                output.write(blob)
            handle = crt._wfopen(str(path.resolve()), 'rb')
            if not handle:
                raise OSError('Cannot open owned short-margin fixture')
            try:
                for mode in (0, 1, 2):
                    config, context, outer = (native.alloc(size)
                                              for size in (0x100, 0x2b000, 0x4300))
                    P.from_address(outer).value = config
                    P.from_address(outer + 8).value = context
                    P.from_address(context).value = config
                    I.from_address(config + 0x80).value = mode
                    F.from_address(context + 0x1d550).value = HOP
                    c.c_double.from_address(context + 0x1d558).value = .01
                    I.from_address(context + 0x2af18).value = 3
                    analysis = native.alloc(0x100)
                    I.from_address(analysis + 0x8c).value = WINDOW
                    P.from_address(context + 0x1fc8).value = analysis
                    part = native.alloc(0x280)
                    P.from_address(part + 0x1a8).value = native.array(Q, offsets)
                    I.from_address(part + 0x1b0).value = frames
                    Q.from_address(part + 0x1b8).value = PREFIX * 2
                    Q.from_address(part + 0x1c0).value = (PREFIX + core) * 2
                    I.from_address(part + 0x1d0).value = count
                    alignment = native.array(I, [group[field]
                        for group in sample['groups']
                        for field in ('start', 'end', 'start2', 'end2')])
                    P.from_address(part + 0x1d8).value = alignment
                    P.from_address(part + 0x1e0).value = alignment + 32
                    I.from_address(part + 0x200).value = 77
                    unit = node(b'b', [part])
                    I.from_address(unit + 8).value = 0x75545241
                    branch = node(b'articulation', [node(b'a', [unit])])
                    root = node(b'root', [node(b'voice', [branch])])
                    stream = native.alloc(0x80)
                    P.from_address(stream + 8).value = handle
                    I.from_address(stream + 0x2c).value = 0x32534d53
                    P.from_address(root + 0x1a0).value = stream
                    table = native.alloc(0x180)
                    token = native.alloc(2)
                    c.memmove(token, b'b\0', 2)
                    I.from_address(table + 0x144).value = 1
                    P.from_address(table + 0x148).value = native.array(P, [token])
                    build(outer, b'a', b'b', root, table)
                    assert I.from_address(part + 0x200).value == mode
                    cache = P.from_address(part + 0x258).value
                    assert bool(cache) == (mode in (1, 2))
                    if cache:
                        begin = PREFIX + core - WINDOW // 2
                        actual = list((c.c_int16 * count).from_address(cache))
                        assert actual == samples[begin:begin + count]

                    registry = native.alloc(0x20)
                    I.from_address(registry + 8).value = 1
                    P.from_address(registry + 0x10).value = native.array(P, [root])
                    P.from_address(registry + 0x18).value = native.array(P, [native.alloc(8)])
                    P.from_address(config + 0x10).value = registry
                    controls = native.alloc(0x100)
                    P.from_address(context + 0x1d568).value = controls
                    frame_work = native.alloc(0x140)
                    P.from_address(frame_work + 0x20).value = part
                    output_frame = native.alloc(0x328)
                    init_frame(output_frame)
                    destination = native.alloc((WINDOW + 1) * 2 + 32)
                    P.from_address(context + 0x29510).value = destination + 16
                    loaded = I()
                    first_cache_file_difference = None
                    for active in (0, 1):
                        c.c_ubyte.from_address(controls + 0x78).value = active
                        # Active cache reads stop at the last complete window.
                        # The file-backed branch also checks the first later frame.
                        indices = (range(safe + 1) if active and cache
                                   else range(frames) if not active
                                   else (0, safe, safe + 1, frames - 1))
                        for index in indices:
                            c.memset(destination, 0xa5, (WINDOW + 1) * 2 + 32)
                            loaded.value = 0
                            load(context, frame_work, index, output_frame, 0, 1,
                                 c.byref(loaded))
                            assert loaded.value == 1
                            assert c.c_double.from_address(output_frame + 0x148).value == 1000 + index
                            assert abs(F.from_address(output_frame + 0x1d0).value -
                                       cents(220. + index)) < 1e-4
                            start = core + index * HOP - WINDOW // 2
                            begin = PREFIX + start
                            use_cache = bool(active and cache)
                            expected = [samples[i] if use_cache or PREFIX <= i < PREFIX + count
                                        else 0 for i in range(begin, begin + WINDOW + 1)]
                            actual = list((c.c_int16 * (WINDOW + 1)).from_address(destination + 16))
                            assert actual == expected, (bank['audit']['bank'], sample['key'],
                                                        mode, active, index)
                            assert c.string_at(destination, 16) == b'\xa5' * 16
                            assert c.string_at(destination + 16 + (WINDOW + 1) * 2, 16) == b'\xa5' * 16
                            if use_cache and first_cache_file_difference is None:
                                different = sum(not (PREFIX <= i < PREFIX + count)
                                                and samples[i] != 0
                                                for i in range(begin, begin + WINDOW + 1))
                                if different:
                                    first_cache_file_difference = dict(frame=index,
                                                                       samples=different)
                            native_reads += 1
                            if use_cache:
                                cached_reads += 1
                            else:
                                file_reads += 1
                    rows.append(dict(bank=bank['audit']['bank'], key=sample['key'],
                                     mode=mode, frames=frames, pcm_count=count,
                                     core_offset_samples=core, max_complete_cache_frame=safe,
                                     incomplete_cache_frames=frames - safe - 1,
                                     full_cache_present=bool(cache),
                                     first_cache_file_difference=first_cache_file_difference))
            finally:
                assert crt.fclose(handle) == 0
    assert len(rows) == 24
    return dict(dll_sha256=SHA256, input_sha256=hashlib.sha256(source).hexdigest(),
                window=WINDOW, hop=HOP, shapes=8, cases=len(rows),
                native_frame_reads=native_reads, cached_frame_reads=cached_reads,
                file_frame_reads=file_reads, mismatches=0, guard_failures=0,
                rows=rows,
                scope='Native ART cache builder and frame loader with V4X short-margin geometry '
                      'and owned frame/PCM data. Cached reads are restricted to complete windows; '
                      'the result does not observe Editor configuration or commercial PCM.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--work-dir', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll, args.report, args.work_dir)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: result[key] for key in ('shapes', 'cases', 'native_frame_reads',
                                                  'cached_frame_reads', 'file_frame_reads',
                                                  'mismatches', 'guard_failures')}))
