"""Bounded indexed leads and owned native temporal-feature verification.

Uses genuine analyzer construction and finite synthetic features in memory.
This verifies two identified mode-zero profiles, not PCM-to-bit17 production.
Only aggregate checks, identifiers and address bounds are serialized.
"""
from __future__ import annotations

import argparse
from collections import Counter
import ctypes as c
import hashlib
import importlib
import json
from pathlib import Path
import random
import struct
import sys

from audit_frm2_numeric import D, F, I, Native, P, Q, SHA256, f32
from audit_vqm_feature_pointer_accesses import inspect, owned_checks
from audit_vqm_v4_module_leads import PE


PATTERNS = ('zero', 'constant', 'ramp', 'impulse', 'noise', 'cancellation', 'wide')
GUARD = bytes(range(32))


def temporal(old, recent, ahead1, ahead2, cepstral):
    # SSE doubles ahead2 before adding it, rather than two successive adds.
    doubled = f32(ahead2 + ahead2)
    value = f32(f32(old * -2.0) - recent)
    value = f32(value + ahead1)
    value = f32(value + doubled)
    return f32(value * 0.375) if cepstral else value


def sample(pattern, time, channel, rng):
    if pattern == 'zero':
        return 0.0
    if pattern == 'constant':
        return f32(0.125 + channel / 16)
    if pattern == 'ramp':
        return f32(time * 0.17 + channel * 0.09)
    if pattern == 'impulse':
        return f32((channel + 1) * 100.0) if time == 5 else 0.0
    if pattern == 'noise':
        return f32(rng.uniform(-100, 100))
    if pattern == 'cancellation':
        return f32(1e7 + (time % 7 - 3) * (channel % 3 + 1))
    return f32(rng.uniform(-1, 1) * 10 ** rng.randint(-12, 12))


def guarded_feature(native):
    base = native.alloc(0x228 + 64)
    c.memmove(base, GUARD, 32)
    c.memmove(base + 32 + 0x228, GUARD, 32)
    values = [f32(0.25 + k / 1024) for k in range(138)]
    blob = struct.pack('<138f', *values)
    c.memmove(base + 32, blob, len(blob))
    I.from_address(base + 32).value = 0x1fffff
    return base, base + 32


def native_checks(native):
    constants = dict(history_factor=F.from_address(native.lib._handle + 0x5bfeb0).value,
                     cepstral_scale=D.from_address(native.lib._handle + 0x5bf498).value)
    assert constants == dict(history_factor=-2.0, cepstral_scale=0.375)
    results = []
    for alternative in (0, 1):
        bands, ceps = (40, 20) if not alternative else (24, 13)
        cfg = native.alloc(0x35f0)
        native.fn(0x90de0, P, P)(cfg)
        I.from_address(cfg + 0x54c).value = 1
        # This profile selector is supplied before genuine construction.
        I.from_address(cfg + 0x5b4).value = alternative
        context = native.alloc(0xce0)
        native.fn(0xcdde0, P, P, I, P)(context, 44100, cfg)
        analyzer = P.from_address(context + 0xca8).value
        assert analyzer and I.from_address(analyzer + 0x3b0).value == 0
        assert P.from_address(analyzer + 0x3b8).value == cfg
        assert I.from_address(cfg + 0x5a0).value == 0
        count = I.from_address(analyzer + 0x6d0).value
        assert count == 8
        histories = {}
        for name, offset, width, used in (('band', 0x6c0, 40, bands),
                                           ('cep', 0x6b8, 20, ceps)):
            vector = P.from_address(analyzer + offset).value
            pointers = [P.from_address(vector + 8 * k).value for k in range(count)]
            assert vector and all(pointers) and len(set(pointers)) == count
            histories[name] = (vector, pointers, width, used)
        frames, blocks, links = [], [], []
        for _ in range(3):
            frame = native.alloc(0x230)
            native.frame_ctor(frame)
            base, feature = guarded_feature(native)
            P.from_address(frame + 0x128).value = feature
            Q.from_address(frame + 0x50).value = 8192
            link = native.alloc(0x30)
            P.from_address(link + 0x28).value = frame
            frames.append(frame)
            blocks.append((base, feature))
            links.append(link)
        queue = native.array(P, links)
        invoke = native.fn(0xe4240, Q, P, P)
        analyzer_before = c.string_at(analyzer, 0x6d8)
        config_before = c.string_at(cfg, 0x35f0)
        frames_before = [c.string_at(frame, 0x230) for frame in frames]
        for pattern in PATTERNS:
            rng = random.Random(408 + alternative)
            for vector, pointers, width, used in histories.values():
                for k, pointer in enumerate(pointers):
                    values = [sample(pattern, k - count, j, rng) for j in range(width)]
                    c.memmove(pointer, struct.pack('<' + 'f' * width, *values), width * 4)
                c.memmove(vector, struct.pack('<8Q', *pointers), 64)
            exact_values, outside_bytes, nonzero = 0, 0, 0
            for step in range(32):
                for ahead, (_, feature) in enumerate(blocks):
                    for start, length in ((0x2c, bands), (0x16c, ceps)):
                        data = [sample(pattern, step + ahead, j, rng) for j in range(length)]
                        c.memmove(feature + start, struct.pack('<' + 'f' * length, *data), length * 4)
                before = [c.string_at(base, 0x228 + 64) for base, _ in blocks]
                expected = bytearray(before[0])
                history_expected = {}
                for name, (vector, _, width, used) in histories.items():
                    pointers = [P.from_address(vector + 8 * k).value for k in range(count)]
                    snapshots = {pointer: c.string_at(pointer, width * 4) for pointer in pointers}
                    source = 0x16c if name == 'cep' else 0x2c
                    destination = 0x1bc if name == 'cep' else 0xcc
                    for j in range(used):
                        old = F.from_address(pointers[-2] + 4 * j).value
                        recent = F.from_address(pointers[-1] + 4 * j).value
                        next1 = F.from_address(blocks[1][1] + source + 4 * j).value
                        next2 = F.from_address(blocks[2][1] + source + 4 * j).value
                        value = temporal(old, recent, next1, next2, name == 'cep')
                        struct.pack_into('<f', expected, 32 + destination + j * 4, value)
                        exact_values += 1
                        nonzero += value != 0
                    # Rotation reuses the oldest allocation; it does not move
                    # contents between the other seven allocations.
                    snapshots[pointers[0]] = (
                        c.string_at(blocks[0][1] + source, used * 4)
                        + snapshots[pointers[0]][used * 4:])
                    history_expected[name] = (pointers[1:] + pointers[:1], snapshots)
                assert invoke(analyzer, queue) == 0
                after = [c.string_at(base, 0x228 + 64) for base, _ in blocks]
                assert after[0] == bytes(expected), (alternative, pattern, step, 'target')
                assert after[1:] == before[1:], (alternative, pattern, step, 'neighbors')
                for name, (pointers, snapshots) in history_expected.items():
                    vector, _, width, _ = histories[name]
                    assert [P.from_address(vector + 8 * k).value for k in range(count)] == pointers
                    assert all(c.string_at(pointer, width * 4) == blob
                               for pointer, blob in snapshots.items())
                assert c.string_at(analyzer, 0x6d8) == analyzer_before
                assert c.string_at(cfg, 0x35f0) == config_before
                assert [c.string_at(frame, 0x230) for frame in frames] == frames_before
                outside_bytes += 0x228 - 4 * (bands + ceps) + 64
            results.append(dict(alternative=alternative, mode=0, pattern=pattern,
                calls=32, history_slots=count, band_values=bands, cepstral_values=ceps,
                exact_float32_values=exact_values, nonzero_output_values=nonzero,
                target_outside_bytes_preserved=outside_bytes, neighbors_preserved=True,
                history_rotation_and_copy_exact=True, inactive_history_tail_preserved=True,
                analyzer_config_and_frames_preserved=True, guards_preserved=True,
                bit17_preserved=True))
    return dict(constants=constants, cases=results,
                calls=sum(row['calls'] for row in results),
                exact_float32_values=sum(row['exact_float32_values'] for row in results))


def indexed_leads(dll, decoder_dir):
    data = dll.read_bytes()
    assert hashlib.sha256(data).hexdigest() == SHA256
    pe = PE(data)
    sys.path.insert(0, str(decoder_dir.resolve()))
    cs = importlib.import_module('capstone')
    assert cs.__version__ == '5.0.3'
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    decoder.detail = True
    fixtures = owned_checks(decoder, cs)
    indexed_fixtures = (
        ('f30f1184881c020000', 4, False, True, False),
        ('0f10848810020000', 16, True, False, False),
        ('488d84881c020000', 8, False, False, True),
    )
    for tail, width, read, write, address_only in indexed_fixtures:
        raw = bytes.fromhex('488b8128010000' + tail)
        instructions = list(decoder.disasm(raw, 0x1000))
        assert sum(op.size for op in instructions) == len(raw)
        rows = []
        inspect(instructions, cs, 0x1000, rows)
        assert len(rows) == 1
        row = rows[0]
        assert (row['operand_bytes'], row['read'], row['write'], row['address_only']) == (
            width, read, write, address_only)
        assert row['index_register'] == 'rcx' and row['index_scale'] == 4
    directory, size = struct.unpack_from('<II', data, pe.directories + 24)
    assert size % 12 == 0 and size // 12 < 20000
    indexed = []
    for at in range(0, size, 12):
        start, end, _ = struct.unpack_from('<III', data, pe.offset(directory) + at)
        assert 0 < end - start <= 0x40000
        raw = data[pe.offset(start):pe.offset(start) + end - start]
        if not any(struct.pack('<I', offset) in raw for offset in (0x128, 0x208)):
            continue
        instructions = list(decoder.disasm(raw, pe.image_base + start))
        rows = []
        inspect(instructions, cs, pe.image_base, rows)
        for row in rows:
            row['range_start_rva'] = hex(start)
        indexed.extend(rows)
    assert len(indexed) == 157
    writes = [row for row in indexed if row['write']]
    assert len(writes) == 41
    temporal_writes = [row for row in writes
                       if 0xe4240 <= int(row['instruction_rva'], 16) < 0xe5c50]
    assert len(temporal_writes) == 38
    mode_zero_writes = [row for row in temporal_writes
                        if int(row['instruction_rva'], 16) >= 0xe4462]
    assert len(mode_zero_writes) == 35
    return dict(decoder_version=cs.__version__, owned_decoder_checks=len(fixtures),
        owned_indexed_decoder_checks=len(indexed_fixtures),
        indexed_operands=len(indexed), indexed_write_operands=len(writes),
        temporal_write_operands=len(temporal_writes),
        tested_mode_zero_write_operands=len(mode_zero_writes),
        range_counts=dict(Counter(row['range_start_rva'] for row in indexed)),
        write_leads=writes,
        scope='Same bounded local pointer-shaped alias scope as 407. Index registers '
              'are listed rather than symbolically resolved. Symmetric base/index '
              'forms, function parameters and cross-branch aliases may be missed; '
              '157 and 41 are candidate counts, not a complete module inventory.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'decoder-dir', 'output'):
        parser.add_argument('--' + name, required=True, type=Path)
    args = parser.parse_args()
    leads = indexed_leads(args.dll, args.decoder_dir)
    native = native_checks(Native(args.dll))
    sources = (Path(__file__), Path(__file__).with_name('audit_vqm_feature_pointer_accesses.py'),
               Path(__file__).with_name('audit_frm2_numeric.py'))
    report = dict(dse_sha256=SHA256, source_sha256={path.name: hashlib.sha256(
        path.read_bytes()).hexdigest() for path in sources}, indexed=leads, native=native,
        reviewed_contracts=[
            dict(entry_rva='0xe4240', mode=0, alternative=0,
                 band_source=['0x2c', '0xcc'], band_destination=['0xcc', '0x16c'],
                 cepstral_source=['0x16c', '0x1bc'], cepstral_destination=['0x1bc', '0x20c']),
            dict(entry_rva='0xe4240', mode=0, alternative=1,
                 band_source=['0x2c', '0x8c'], band_destination=['0xcc', '0x12c'],
                 cepstral_source=['0x16c', '0x1a0'], cepstral_destination=['0x1bc', '0x1f0'])],
        call_contract=dict(caller_rva='0xd862d', analyzer_context_offset='0xca8',
                           queue_context_offset='0xc20', link_frame_offset='0x28',
                           frame_feature_offset='0x128', feature_gate='outer bit13'),
        scope='Two genuinely constructed mode-zero profiles with owned preexisting '
              'features and history. Not raw PCM production, commercial maker '
              'configuration, nonzero analyzer modes, all callers, or bit17 formula.')
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(indexed=leads['indexed_operands'], writes=leads['indexed_write_operands'],
                         calls=native['calls'], exact_values=native['exact_float32_values'])))


if __name__ == '__main__':
    main()
