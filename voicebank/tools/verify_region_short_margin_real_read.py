"""Check DSE file-backed frame reads for installed V4X short-margin ART.

Reference DDI and DDB files are opened read-only. The report contains only
identity hashes and aggregate checks, never frame or PCM payloads.
"""

import argparse
from collections import Counter
import ctypes as c
import hashlib
import json
import mmap
from pathlib import Path
import struct

from analyze_reference_layers import walk_art
from analyze_reference_regions import read_frame
from audit_frm2_numeric import Native, P, I, Q, F, D, B, SHA256
from verify_planned_ddi import load_public_parser
from verify_reference_ddi import pointer


WINDOW = 2048
HOP = 256


def sha256_file(path: Path) -> str:
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def run(dll: Path, report: Path, voice_root: Path, ddb_tools: Path,
        frame_limit: int = 0, all_breath: bool = False) -> dict:
    source = report.read_bytes()
    banks = json.loads(source)['banks']
    parser = load_public_parser(ddb_tools)
    native = Native(dll)
    load = native.fn(0x175dd0, None, P, P, I, P, I, I, P)
    init_frame = native.fn(0x12a670, P, P)
    crt = c.CDLL('ucrtbase.dll')
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    rows = []
    total_reads = 0

    for bank in banks:
        shorts = {tuple(sample['key']): sample
                  for sample in bank['regions']['short_margin_samples']}
        if not shorts and not all_breath:
            continue
        name = bank['audit']['bank']
        matches = list(voice_root.rglob(f'{name}.ddi'))
        assert len(matches) == 1, (name, len(matches))
        ddi = matches[0]
        ddb = ddi.with_suffix('.ddb')
        raw = ddi.read_bytes()
        assert hashlib.sha256(raw).hexdigest() == bank['ddi_sha256']
        assert ddb.stat().st_size == bank['audit']['ddb_bytes']
        model = parser(raw)
        model.read()
        parts = {}
        for root in model.art_data.values():
            for key, layers in walk_art(root):
                if key in shorts or (all_breath and len(key) == 2 and
                                     'Sil' in key and any(name.startswith('br') for name in key)):
                    assert len(layers) == 1, (name, key)
                    parts[key] = layers[0]
        if not all_breath:
            assert parts.keys() == shorts.keys(), (name, parts.keys(), shorts.keys())
        ddb_digest = sha256_file(ddb)
        handle = crt._wfopen(str(ddb.resolve()), 'rb')
        if not handle:
            raise OSError(f'Cannot open installed DDB: {name}')
        try:
            with ddb.open('rb') as stream:
                with mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as data:
                    for key, part_data in parts.items():
                        shape = shorts.get(key)
                        frame_offsets = [pointer(value, raw)[0]
                                         for value in part_data['epr']]
                        frames = len(frame_offsets)
                        snd, count = pointer(part_data['snd'], raw, 18)
                        core, _ = pointer(part_data['snd_start'].split('_')[0], raw, 18)
                        if shape:
                            assert count == frames * HOP + shape['pcm_margin']
                            assert snd == int(shape['snd_offset'], 16)
                            assert core - snd == shape['core_delta_bytes']
                        assert snd + count * 2 <= len(data)
                        if shape:
                            assert frames == shape['frames']
                        chosen = list(range(frames)) if not frame_limit else list(range(min(frame_limit, frames)))

                        config, context = native.alloc(0x100), native.alloc(0x2b000)
                        P.from_address(context).value = config
                        F.from_address(context + 0x1d550).value = HOP
                        F.from_address(context + 0x1d564).value = 22050.
                        D.from_address(context + 0x1d558).value = .01
                        analysis = native.alloc(0x100)
                        I.from_address(analysis + 0x8c).value = WINDOW
                        P.from_address(context + 0x1fc8).value = analysis
                        controls = native.alloc(0x100)
                        B.from_address(controls + 0x78).value = 0
                        P.from_address(context + 0x1d568).value = controls
                        part = native.alloc(0x280)
                        P.from_address(part + 0x1a8).value = native.array(Q, frame_offsets)
                        I.from_address(part + 0x1b0).value = frames
                        Q.from_address(part + 0x1b8).value = snd
                        Q.from_address(part + 0x1c0).value = core
                        I.from_address(part + 0x1d0).value = count
                        I.from_address(part + 0x200).value = 0
                        frame_work = native.alloc(0x140)
                        P.from_address(frame_work + 0x20).value = part
                        voice = native.alloc(0x280)
                        stream_obj = native.alloc(0x80)
                        P.from_address(stream_obj + 8).value = handle
                        I.from_address(stream_obj + 0x2c).value = 0x32534d53
                        P.from_address(voice + 0x1a0).value = stream_obj
                        registry = native.alloc(0x20)
                        I.from_address(registry + 8).value = 1
                        P.from_address(registry + 0x10).value = native.array(P, [voice])
                        P.from_address(registry + 0x18).value = native.array(P, [native.alloc(8)])
                        P.from_address(config + 0x10).value = registry
                        output_frame = native.alloc(0x328)
                        init_frame(output_frame)
                        destination = native.alloc((WINDOW + 1) * 2 + 32)
                        P.from_address(context + 0x29510).value = destination + 16
                        loaded = I()
                        mask_counts = Counter()
                        positive_f0 = 0
                        nonzero_pcm_windows = 0
                        max_front_zeros = max_tail_zeros = 0
                        for index in chosen:
                            offset = frame_offsets[index]
                            expected_time, source_f0 = read_frame(data, offset)
                            source_mask = struct.unpack_from('<Q', data, offset + 20)[0]
                            c.memset(destination, 0xa5, (WINDOW + 1) * 2 + 32)
                            loaded.value = 0
                            load(context, frame_work, index, output_frame, 0, 1,
                                 c.byref(loaded))
                            assert loaded.value == 1
                            assert D.from_address(output_frame + 0x148).value == expected_time
                            mask_counts[f'{source_mask:016x}'] += 1
                            positive_f0 += source_f0 > 0
                            start = (core - snd) // 2 + index * HOP - WINDOW // 2
                            first, last = max(0, start), min(count, start + WINDOW + 1)
                            front, tail = max(0, -start), max(0, start + WINDOW + 1 - count)
                            expected = (b'\0' * (front * 2) +
                                        data[snd + first * 2:snd + last * 2] +
                                        b'\0' * (tail * 2))
                            assert len(expected) == (WINDOW + 1) * 2
                            actual = c.string_at(destination + 16, (WINDOW + 1) * 2)
                            assert actual == expected, (name, key, index)
                            assert c.string_at(destination, 16) == b'\xa5' * 16
                            assert c.string_at(destination + 16 + (WINDOW + 1) * 2, 16) == b'\xa5' * 16
                            nonzero_pcm_windows += bool(any(actual))
                            max_front_zeros = max(max_front_zeros, front)
                            max_tail_zeros = max(max_tail_zeros, tail)
                        total_reads += len(chosen)
                        row = dict(bank=name, key=key, frames=frames,
                                   native_reads=len(chosen), pcm_count=count,
                                   source_mask_counts=dict(mask_counts),
                                   source_positive_f0_frames=positive_f0,
                                   nonzero_pcm_windows=nonzero_pcm_windows,
                                   max_front_zero_samples=max_front_zeros,
                                   max_tail_zero_samples=max_tail_zeros)
                        if all_breath:
                            row.update(short_margin_exception=bool(shape),
                                       pcm_margin=count - frames * HOP,
                                       core_delta_bytes=core - snd)
                        rows.append(row)
        finally:
            assert crt.fclose(handle) == 0
        rows_for_bank = [row for row in rows if row['bank'] == name]
        for row in rows_for_bank:
            row['ddi_sha256'] = bank['ddi_sha256']
            row['ddb_sha256'] = ddb_digest

    assert len(rows) == (80 if all_breath else 8)
    if not frame_limit:
        assert total_reads == (3780 if all_breath else 383)
    return dict(dll_sha256=SHA256, input_sha256=hashlib.sha256(source).hexdigest(),
                shapes=len(rows), frame_limit=frame_limit, native_frame_reads=total_reads,
                pcm_mismatches=0, guard_failures=0, rows=rows,
                scope=('All eight installed V4X banks, 80 Silence/breath entry and exit ART; '
                       if all_breath else 'Installed V4X DDI/DDB opened read-only; ') +
                      'native DSE mode-0 frame loader compared against file SND windows. '
                      'No audio or frame payload exported; no Editor playback or host '
                      'configuration observation.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--frame-limit', type=int, default=0)
    parser.add_argument('--all-breath', action='store_true')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    assert args.frame_limit >= 0
    result = run(args.dll, args.report, args.voice_root, args.ddb_tools,
                 args.frame_limit, args.all_breath)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                           encoding='utf-8')
    print(json.dumps({key: result[key] for key in ('shapes', 'native_frame_reads',
                                                  'pcm_mismatches', 'guard_failures')}))
