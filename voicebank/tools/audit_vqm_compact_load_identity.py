"""Compare compact DVQM serialized FRM2 fields to real loaded native objects.

The installed file is read only and its payloads remain in memory. Explicit
placeholder intervals and native chunk source offsets bind every comparison.
Only hashes, address identifiers, counts and aggregate errors are written.
"""
from __future__ import annotations

import argparse
from collections import Counter
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import struct

from audit_frm2_numeric import B, D, F, I, Native, P, Q, SHA256
from audit_vqm_legacy_live_features import RESOURCE_SHA256, catalog, children, loaded_regions, name, pointer_items
from audit_vqm_legacy_phase_relations import circular, metrics, physical_f0, snapshot
from probe_frm2 import Cursor, ProbeError, fail, vqm_feature_payload_size


MASKS = (0x800c2207, 0x800c2237)
LAYOUT = [(4+4*bit, 4) for bit in range(10)] + [
    (0x2c, 160), (0xcc, 160), (0x16c, 80), (0x1bc, 80)] + [
    (0x20c if bit == 14 else 0x214+4*(bit-15), 4) for bit in range(14, 20)]


def parse_compact_frame(raw):
    cursor = Cursor(raw)
    tag, length = cursor.unpack('<4sI', 'compact FRM2 header')
    if tag != b'FRM2' or length != len(raw):
        fail('Invalid compact frame header/length')
    kind, time, mask = cursor.unpack('<IdQ', 'compact FRM2 prefix')
    if mask not in MASKS:
        fail('Unreviewed compact frame mask')
    count = cursor.u32('compact component count')
    if not 0 < count <= 10000:
        fail('Compact component count outside bounded contract')
    components = [cursor.take(count*4, 'compact component '+label)
                  for label in ('frequencies', 'amplitudes', 'phases')]
    spectrum_count = cursor.u32('compact spectrum count') if mask & 0x30 else 0
    if spectrum_count > 100000:
        fail('Compact spectrum count outside bounded contract')
    spectrum = [cursor.take(spectrum_count*4, 'compact spectrum '+label)
                for label in ('amplitudes', 'phases')]
    pitch = cursor.take(4, 'compact stored pitch')
    flags = cursor.u32('compact feature flags')
    if flags & ~0x1fffff:
        fail('Unreviewed compact feature flags')
    feature = bytearray(0x228)
    struct.pack_into('<I', feature, 0, flags)
    feature_bytes = 0
    for bit, (offset, size) in enumerate(LAYOUT):
        if flags & (1 << bit):
            feature[offset:offset+size] = cursor.take(size, 'compact feature field')
            feature_bytes += size
    assert feature_bytes == vqm_feature_payload_size(flags)
    trailer = cursor.u8('compact feature trailer')
    control18 = cursor.take(1, 'compact outer18')
    control19 = cursor.take(4, 'compact outer19')
    if cursor.position != len(raw):
        fail('Unconsumed compact frame bytes')
    return dict(kind=kind, time=time, mask=mask, count=count, components=components,
        spectrum_count=spectrum_count, spectrum=spectrum, pitch=pitch,
        feature=bytes(feature), trailer=trailer, control18=control18, control19=control19)


def owned_frame(mask, flags=0x1fffff):
    components = [struct.pack('<3f', *values) for values in
                  ((0., 220., 440.), (-10., -20., -30.), (1., .25, .75))]
    body = struct.pack('<IdQI', 7, .125, mask, 3)+b''.join(components)
    if mask & 0x30:
        body += struct.pack('<I4f', 2, .5, 1.5, .25, .75)
    body += struct.pack('<fI', -1200., flags)
    for bit, (_, size) in enumerate(LAYOUT):
        if flags & (1 << bit):
            body += struct.pack('<'+str(size//4)+'f', *(.125+k/256. for k in range(size//4)))
    body += struct.pack('<BBI', 1, 10, 4096)
    return struct.pack('<4sI', b'FRM2', len(body)+8)+body


def owned_checks():
    cases = []
    for mask in MASKS:
        for flags in (0, 1 << 17, 0x1fffff):
            raw = owned_frame(mask, flags)
            frame = parse_compact_frame(raw)
            assert frame['count'] == 3 and frame['spectrum_count'] == (2 if mask & 0x30 else 0)
            assert struct.unpack_from('<I', frame['feature'])[0] == flags
            assert frame['feature'][0x210:0x214] == bytes(4)
            cases.append(dict(case=f'owned_mask_{mask:x}_flags_{flags:x}', passed=True))
            for length in range(28, len(raw)):
                broken = bytearray(raw[:length])
                struct.pack_into('<I', broken, 4, length)
                try:
                    parse_compact_frame(bytes(broken))
                except ProbeError:
                    pass
                else:
                    raise AssertionError(('Truncated fixture accepted', mask, flags, length))
    raw = bytearray(owned_frame(MASKS[0]))
    for at, fmt, value, label in ((28, '<I', 0xffffffff, 'huge_component_count'),
                                (20, '<Q', MASKS[0] | 0x10, 'unsupported_partial_spectrum'),
                                (4, '<I', len(raw)+4, 'wrong_length')):
        broken = bytearray(raw)
        struct.pack_into(fmt, broken, at, value)
        try:
            parse_compact_frame(bytes(broken))
        except ProbeError:
            cases.append(dict(case=label, passed=True))
        else:
            raise AssertionError(label)
    return cases


def frames_in_interval(data, start, end):
    result = []
    position = start
    while (position := data.find(b'FRM2', position, end)) >= 0:
        if position+28 > end:
            raise ValueError('Frame prefix crosses its track interval')
        size = struct.unpack_from('<I', data, position+4)[0]
        if size < 32 or position+size > end:
            raise ValueError('Frame crosses its track interval')
        parsed = parse_compact_frame(data[position:position+size])
        result.append((position, size, parsed))
        if len(result) > 10000:
            raise ValueError('Track frame bound exceeded')
        position += size
    return result


def compare_fields(native, pointer, wire, source_offset):
    counts = Counter()
    mismatches = Counter()
    counts['frames'] += 1
    assert I.from_address(pointer+0x10).value == source_offset
    vt = P.from_address(pointer).value
    assert P.from_address(vt+0x18).value-native.lib._handle == 0x93000
    assert P.from_address(vt+0x70).value-native.lib._handle == 0x87750
    assert I.from_address(pointer+0x58).value == wire['count']
    assert I.from_address(pointer+0xa8).value == wire['spectrum_count']
    checks = {
        'kind': (struct.pack('<I', wire['kind']), c.string_at(pointer+0x40, 4)),
        'time': (struct.pack('<d', wire['time']), c.string_at(pointer+0x48, 8)),
        'mask': (struct.pack('<Q', wire['mask']), c.string_at(pointer+0x50, 8)),
        'stored_pitch': (wire['pitch'], c.string_at(pointer+0xf0, 4)),
        'outer18': (wire['control18'], c.string_at(pointer+0x178, 1)),
        'outer19': (wire['control19'], c.string_at(pointer+0x17c, 4)),
    }
    for key, offset, values in zip(('frequencies', 'amplitudes', 'phases'), (0x60, 0x68, 0x70), wire['components']):
        target = P.from_address(pointer+offset).value
        assert target
        checks[key] = (values, c.string_at(target, len(values)))
        counts[key+'_float_values'] += wire['count']
    if wire['spectrum_count']:
        for key, offset, values in zip(('spectrum_amplitudes', 'spectrum_phases'), (0xb0, 0xb8), wire['spectrum']):
            target = P.from_address(pointer+offset).value
            assert target
            checks[key] = (values, c.string_at(target, len(values)))
            counts[key+'_float_values'] += wire['spectrum_count']
    feature = P.from_address(pointer+0x128).value
    assert feature
    checks['feature'] = (wire['feature'], c.string_at(feature, 0x228))
    checks['bit17'] = (wire['feature'][0x21c:0x220], c.string_at(feature+0x21c, 4))
    counts['feature_storage_bytes'] += 0x228
    for key, (expected, actual) in checks.items():
        counts[key+'_comparisons'] += 1
        counts[key+'_matching'] += expected == actual
        if expected != actual:
            mismatches[key] += 1
    return counts, mismatches


def run(dll, directory, limit):
    resource = directory/'dvqm.dat'
    data = resource.read_bytes()
    assert hashlib.sha256(data).hexdigest() == RESOURCE_SHA256
    fixtures = owned_checks()
    native = Native(dll)
    root = native.fn(0xfea0, P, c.c_char_p)(str(directory.resolve()).encode('utf-8'))
    assert root
    totals, mismatches, masks, trailers, spectrum_counts = Counter(), Counter(), Counter(), Counter(), Counter()
    feature_flags, control18, control19 = Counter(), Counter(), Counter()
    cases, source_offsets = [], set()
    phase_errors, phase_steps, raw_voiced_steps = [], [], []
    pitch_meta = Counter()
    try:
        for index, (side, descriptor, label, sample) in enumerate(catalog(native, root)[:limit]):
            entries = {name(node): node for node in children(sample)}
            placeholder = entries['wbhsm']
            assert I.from_address(placeholder+8).value == 0x54504d45
            start = I.from_address(placeholder+0x10).value
            assert 0 <= start <= len(data)-8 and data[start:start+4] == b'GTRK'
            size = struct.unpack_from('<I', data, start+4)[0]
            end = start+size
            assert 8 <= size and end <= len(data)
            disk = frames_in_interval(data, start, end)
            assert disk
            disk_by_offset = {offset: wire for offset, _, wire in disk}
            assert len(disk_by_offset) == len(disk)
            assert not source_offsets.intersection(disk_by_offset)
            source_offsets.update(disk_by_offset)
            control = native.fn(0xb400, P)()
            assert control
            try:
                assert native.fn(0x100a0, Q, P, I, P, c.c_char_p, P)(
                    root, side, descriptor, label.encode('ascii'), control) == 0
                before = snapshot(control)
                regions = loaded_regions(control)
                assert len(regions) == 1
                frames = pointer_items(regions[0], 0x18, 0x20)
                assert len(frames) == len(disk)
                offsets = [I.from_address(frame+0x10).value for frame in frames]
                assert offsets == [offset for offset, _, _ in disk]
                counts, errors = Counter(), Counter()
                prior = None
                for frame, offset in zip(frames, offsets):
                    wire = disk_by_offset[offset]
                    found, bad = compare_fields(native, frame, wire, offset)
                    counts.update(found)
                    errors.update(bad)
                    masks[hex(wire['mask'])] += 1
                    trailers[str(wire['trailer'])] += 1
                    spectrum_counts[str(wire['spectrum_count'])] += 1
                    feature_flags[hex(struct.unpack_from('<I', wire['feature'])[0])] += 1
                    control18[str(int.from_bytes(wire['control18'], 'little'))] += 1
                    control19[str(int.from_bytes(wire['control19'], 'little'))] += 1
                    frequencies, amplitudes, phases = [struct.unpack('<'+str(wire['count'])+'f', x)
                                                       for x in wire['components']]
                    feature = wire['feature']
                    bit17 = struct.unpack_from('<f', feature, 0x21c)[0]
                    counts['wire_nonzero_bit17_frames'] += bit17 != 0.
                    feature_array_values = struct.unpack_from('<120f', feature, 0x2c)
                    assert all(math.isfinite(x) for x in feature_array_values)
                    counts['wire_frames_with_nonzero_feature_arrays'] += any(x != 0. for x in feature_array_values)
                    stored = struct.unpack('<f', wire['pitch'])[0]
                    f0 = physical_f0(stored, wire['mask'])
                    assert all(math.isfinite(x) for values in (frequencies, amplitudes, phases) for x in values)
                    assert 0 <= bit17 < math.tau and frequencies[0] == 0. and phases[0] in (0., 1.)
                    counts['wire_dc_slots'] += 1
                    if f0 > 0.:
                        assert wire['count'] >= 2 and abs(frequencies[1]/f0-1) <= 3e-7
                        phase_errors.append(circular(bit17-phases[1]))
                    if prior is not None and f0 > 0. and prior['f0'] > 0.:
                        delta = circular(bit17-prior['bit17'])
                        raw_voiced_steps.append(delta)
                        phase_steps.append(circular(delta-circular(phases[1]-prior['phase1'])))
                        counts['raw_both_voiced_pairs'] += 1
                        counts['raw_both_voiced_steps_above_quarter'] += abs(delta) > .25001
                    if wire['spectrum_count']:
                        smooth = struct.unpack_from('<f', wire['spectrum'][0])[0]
                        residual = struct.unpack_from('<f', wire['spectrum'][1])[0]
                        pitch_meta['frames_with_spectrum'] += 1
                        pitch_meta['raw_bit7_equals_spectrum_phase_zero'] += feature[0x20:0x24] == wire['spectrum'][1][:4]
                        pitch_meta['raw_bit18_equals_spectrum_amplitude_zero'] += feature[0x220:0x224] == wire['spectrum'][0][:4]
                        pitch_meta['raw_residual_equals_stored_pitch_minus_smooth'] += struct.pack('<f', residual) == struct.pack('<f', F(stored-smooth).value)
                    prior = dict(f0=f0, bit17=bit17, phase1=phases[1] if len(phases)>1 else None)
                after = snapshot(control)
                assert before == after
                totals.update(counts)
                mismatches.update(errors)
                cases.append(dict(sample_index=index, track_offset=hex(start), track_bytes=size,
                    counts=dict(counts), mismatches=dict(errors), loaded_state_sha256=before['sha256'],
                    source_unchanged=True,
                    frame_offset_binding='Native chunk +10 source offset, GTRK placeholder interval, exact frame order'))
            finally:
                native.fn(0xb480, None, P)(control)
    finally:
        assert native.fn(0x10080, Q, P)(root) == 0
    if limit == 119:
        assert len(source_offsets) == totals['frames'] == data.count(b'FRM2') == 26842
    paths = [Path(__file__), Path(__file__).with_name('audit_vqm_legacy_phase_relations.py'),
             Path(__file__).with_name('audit_vqm_legacy_live_features.py'),
             Path(__file__).with_name('audit_frm2_numeric.py'), Path(__file__).with_name('probe_frm2.py'),
             Path(__file__).with_name('verify_region_frame_blend.py')]
    return dict(schema=1, dse_sha256=SHA256, resource_sha256=RESOURCE_SHA256, resource_bytes=len(data),
        owned_checks=fixtures, samples=len(cases), summary=dict(totals), mismatches=dict(mismatches),
        raw_frame_masks=dict(masks), raw_feature_trailers=dict(trailers), raw_spectrum_counts=dict(spectrum_counts),
        raw_feature_flags=dict(feature_flags), raw_control18=dict(control18), raw_control19=dict(control19),
        original_bit17_to_fundamental_phase=metrics(phase_errors), original_bit17_steps_to_fundamental_steps=metrics(phase_steps),
        original_voiced_zero_step_control=metrics(raw_voiced_steps), raw_pitch_metadata_relations=dict(pitch_meta), cases=cases,
        source_sha256={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in paths},
        reviewed_reader_rvas=dict(lazy_chunk='0x269e0', chunk_header='0x93080', frame_read='0x93000', frame_body='0x87750', feature_read='0x6cef0'),
        scope='Hash-pinned installed compact DVQM; all selected GTRK placeholder intervals, native chunk source offsets '
              'and frame order bind independently parsed file fields to real loadSample output before configuration/audio. '
              'Byte identity is tested only for the listed fields; the discarded feature trailer is counted from file, '
              'not assigned to a native object field. Only two exact masks are accepted. All installed payloads stay in memory. '
              'Not original PCM analysis, maker identity, other formats/loaders, V4X Growl or whole Editor behavior.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'directory', 'output'):
        parser.add_argument('--'+name, type=Path, required=True)
    parser.add_argument('--limit', type=int, default=119)
    args = parser.parse_args()
    assert 1 <= args.limit <= 119
    result = run(args.dll, args.directory, args.limit)
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({key:result[key] for key in ('samples','summary','mismatches','raw_pitch_metadata_relations')}))


if __name__ == '__main__':
    main()
