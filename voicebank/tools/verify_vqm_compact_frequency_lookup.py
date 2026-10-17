"""Verify compact baseline/residual frequency lookup against independent arithmetic.

Installed binaries and banks are read only. Native weights and bank arrays stay
in memory; evidence contains only counts, hashes and aggregate errors. No audio
is generated. The independent model regenerates every weight from arithmetic.
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

from audit_frm2_numeric import F, I, Native, P, Q, SHA256, f32
from audit_vqm_compact_load_identity import frames_in_interval
from audit_vqm_legacy_live_features import RESOURCE_SHA256, catalog, loaded_regions, pointer_items
from audit_vqm_legacy_phase_relations import snapshot


def bits(value):
    return struct.pack('<f', value)


def weights(t):
    """Arithmetic definition, preserving the reviewed scalar float32 order."""
    t = f32(t)
    u = f32(1.-t)
    sixth, third = f32(1./6.), f32(1./3.)
    t3 = f32(f32(t*t)*t)
    u3 = f32(f32(u*u)*u)
    right_far = f32(f32(t3/6.)-f32(t/6.))
    left_far = f32(f32(u3/6.)-f32(u*sixth))
    a, b = f32(t+1.), f32(2.-t)
    a3, b3 = f32(f32(a*a)*a), f32(f32(b*b)*b)
    twice_t3 = f32(f32(f32(t+t)*t)*t)
    twice_u3 = f32(f32(f32(u+u)*u)*u)
    right_near = f32(f32(f32(a3/6.)-f32(twice_t3/3.))-f32(u*sixth))
    right_near = f32(right_near+f32(t*third))
    left_near = f32(f32(f32(b3/6.)-f32(twice_u3/3.))-f32(t*sixth))
    left_near = f32(left_near+f32(u*third))
    return right_far, right_near, left_near, left_far


WEIGHTS = tuple(weights(f32(j/499.)) for j in range(500))


def weighted(values, index, row):
    result = 0.
    for j, w in zip((index-1, index, index+1, index+2), reversed(row)):
        result = f32(result+f32(values[min(len(values)-1, max(0, j))]*w))
    return result


def lookup(values, position):
    assert values and math.isfinite(position)
    position = f32(position)
    if position <= 0.:
        return values[0]
    i = int(position)
    if i >= len(values)-1:
        return values[-1]
    t = f32(position-f32(i))
    if t < 1e-8:
        return values[i]
    if len(values) <= 2:
        return f32(values[0]+f32(f32(values[1]-values[0])*t))
    row = int(f32(t*499.))
    assert 0 <= row < 500
    return weighted(values, i, WEIGHTS[row])


def coordinate(frequency, base):
    assert base > 0. and math.isfinite(base) and math.isfinite(frequency)
    return f32(f32(f32(frequency)/f32(base))-1.)


def next_float(value, direction):
    assert value >= 0. and direction in (-1, 1)
    word = struct.unpack('<I', bits(value))[0]
    assert word+direction >= 0
    return struct.unpack('<f', struct.pack('<I', word+direction))[0]


def compare(counts, observed, expected):
    assert math.isfinite(observed) and math.isfinite(expected)
    counts['comparisons'] += 1
    counts['float32_bytes_exact'] += bits(observed) == bits(expected)
    counts['max_absolute_error'] = max(counts['max_absolute_error'], abs(observed-expected))
    assert bits(observed) == bits(expected), (counts['comparisons'], observed, expected)


def owned(native):
    native.fn(0x63060, None)()
    table = P.from_address(native.lib._handle+0xaa9670).value
    assert table
    observed = c.string_at(table, 500*16)
    expected = b''.join(struct.pack('<4f', *row) for row in WEIGHTS)
    assert observed == expected
    table_hash = hashlib.sha256(observed).hexdigest()
    helper = native.fn(0x2c1e0, F, P, P, I, F)
    getters = [native.fn(rva, F, P, F) for rva in (0x2b5a0, 0x2b5d0)]
    counts = Counter()
    template_count, guard_checks = 0, 0
    active_rows, controls = set(), Counter()
    for n in (1, 2, 3, 4, 5, 9, 64):
        templates = ([f32(-40.+j*3.25) for j in range(n)],
                     [f32((-1.)**j*(1.+j*j/7.)) for j in range(n)],
                     [f32(1. if j == n//2 else 0.) for j in range(n)],
                     [-17.]*n)
        for values in templates:
            template_count += 1
            guard_values = [1234567.]+values+[-7654321.]
            address = native.array(F, guard_values)
            before = c.string_at(address, len(guard_values)*4)
            positions = [-4., -0., 0., 1e-9, f32(1e-8), 1e-7, .125, .5, .999, n-1., n-.125, n+2.]
            for row in range(500):
                x = f32(row/499.)
                positions.extend((x, next_float(x, 1), next_float(x, -1) if x else 0.))
            if n > 2:
                positions.extend(f32((n-2)+f32((row+.25)/499.)) for row in range(499))
            for position in positions:
                actual = helper(None, address+4, n, position)
                compare(counts, actual, lookup(values, position))
                at = f32(position)
                i = int(max(0., at))
                t = f32(at-f32(i))
                if n > 2 and 0. < at < n-1 and t >= 1e-8:
                    active_rows.add(int(f32(t*499.)))
                    controls['cubic_calls'] += 1
                    alternatives = dict(
                        linear=f32(values[i]+f32(f32(values[i+1]-values[i])*t)),
                        unquantized_cubic=weighted(values, i, weights(t)))
                    for key, alternative in alternatives.items():
                        controls[key+'_different_float32_bytes'] += bits(actual) != bits(alternative)
                        controls[key+'_max_absolute_error'] = max(
                            controls[key+'_max_absolute_error'], abs(actual-alternative))
            assert before == c.string_at(address, len(guard_values)*4)
            guard_checks += 1
    helper_counts = dict(counts)
    wrapper_counts = Counter()
    sentinel_checks = 0
    for n in (1, 2, 3, 4, 9, 64):
        for base in (80., 220., f32(523.2511306)):
            frame = native.alloc(0x230)
            native.frame_ctor(frame)
            I.from_address(frame+0x58).value = 3
            frequency_address = native.array(F, [0., base, f32(base*2.37)])
            P.from_address(frame+0x60).value = frequency_address
            I.from_address(frame+0xa8).value = n+1
            arrays = ([f32(-40.+j*j*.37) for j in range(n)],
                      [f32((-1.)**j*(j+.125)) for j in range(n)])
            pointers = [native.array(F, [sentinel]+values)
                        for sentinel, values in zip((12345., -98765.), arrays)]
            for offset, address in zip((0xb0, 0xb8), pointers):
                P.from_address(frame+offset).value = address
            before = [c.string_at(address, (n+1)*4) for address in pointers]
            frequencies = [f32(base*(position+1.)) for position in (-2., -.5, 0., .125, .5, 1.375, n-1.125, n+1.)]
            for frequency in frequencies:
                for getter, values in zip(getters, arrays):
                    compare(wrapper_counts, getter(frame, frequency), lookup(values, coordinate(frequency, base)))
            assert before == [c.string_at(address, (n+1)*4) for address in pointers]
            for address in pointers:
                F.from_address(address).value = -111111.
            for frequency in frequencies:
                for getter, values in zip(getters, arrays):
                    compare(wrapper_counts, getter(frame, frequency), lookup(values, coordinate(frequency, base)))
                    sentinel_checks += 1
    return dict(weight_rows=500, weight_values=2000, weights_float32_bytes_exact=True,
        regenerated_table_sha256=table_hash, helper_templates=template_count,
        helper=helper_counts, owned_source_and_guard_checks=guard_checks,
        active_quantized_rows=len(active_rows), cubic_alternative_controls=dict(controls),
        wrappers=dict(wrapper_counts), first_slot_sentinel_invariance=sentinel_checks)


def real_frames(native, directory, reference, limit):
    data = (directory/'dvqm.dat').read_bytes()
    assert hashlib.sha256(data).hexdigest() == RESOURCE_SHA256
    prior = json.loads(reference.read_text(encoding='utf-8'))
    assert prior['samples'] == 119 and prior['resource_sha256'] == RESOURCE_SHA256
    root = native.fn(0xfea0, P, c.c_char_p)(str(directory.resolve()).encode('utf-8'))
    assert root
    getters = [native.fn(rva, F, P, F) for rva in (0x2b5a0, 0x2b5d0)]
    totals, cases = Counter(), []
    try:
        entries = catalog(native, root)
        assert len(entries) == 119
        for index, (side, descriptor, label, _) in enumerate(entries[:limit]):
            binding = prior['cases'][index]
            start = int(binding['track_offset'], 16)
            wire_frames = frames_in_interval(data, start, start+binding['track_bytes'])
            control = native.fn(0xb400, P)()
            assert control
            try:
                assert native.fn(0x100a0, Q, P, I, P, c.c_char_p, P)(
                    root, side, descriptor, label.encode('ascii'), control) == 0
                before = snapshot(control)
                regions = loaded_regions(control)
                assert len(regions) == 1
                frames = pointer_items(regions[0], 0x18, 0x20)
                assert len(frames) == len(wire_frames)
                counts = Counter()
                for frame, (offset, _, wire) in zip(frames, wire_frames):
                    assert I.from_address(frame+0x10).value == offset
                    counts['bound_frames'] += 1
                    m = wire['spectrum_count']
                    assert I.from_address(frame+0xa8).value == m
                    if not m:
                        counts['missing_arrays_not_called'] += 1
                        continue
                    assert m >= 2 and wire['count'] >= 2
                    base = struct.unpack_from('<f', wire['components'][0], 4)[0]
                    assert base > 0. and math.isfinite(base)
                    pointer = P.from_address(frame+0x60).value
                    assert c.string_at(pointer, len(wire['components'][0])) == wire['components'][0]
                    arrays = [struct.unpack('<'+str(m)+'f', raw)[1:] for raw in wire['spectrum']]
                    for pointer_offset, raw in zip((0xb0, 0xb8), wire['spectrum']):
                        assert c.string_at(P.from_address(frame+pointer_offset).value, len(raw)) == raw
                    counts['optional_frames'] += 1
                    positions = (-.5, 0., .125, .5, 1.375, (m-2)*.37, m-2-.125, m-2+.5)
                    for position in positions:
                        frequency = f32(base*f32(position+1.))
                        at = coordinate(frequency, base)
                        for getter, values in zip(getters, arrays):
                            compare(counts, getter(frame, frequency), lookup(values, at))
                    assert I.from_address(frame+0xa8).value == m
                    for pointer_offset, raw in zip((0xb0, 0xb8), wire['spectrum']):
                        assert c.string_at(P.from_address(frame+pointer_offset).value, len(raw)) == raw
                    counts['optional_arrays_preserved'] += 1
                after = snapshot(control)
                assert before == after
                totals.update({k:v for k,v in counts.items() if k != 'max_absolute_error'})
                totals['max_absolute_error'] = max(totals['max_absolute_error'], counts['max_absolute_error'])
                cases.append(dict(sample_index=index, counts=dict(counts), source_unchanged=True))
            finally:
                native.fn(0xb480, None, P)(control)
    finally:
        assert native.fn(0x10080, Q, P)(root) == 0
    if limit == 119:
        assert totals['bound_frames'] == 26842 and totals['optional_frames'] == 22038
        assert totals['comparisons'] == totals['float32_bytes_exact'] == 352608
    return dict(samples=len(cases), queries_per_optional_frame_per_array=8, totals=dict(totals), cases=cases,
        scope='Actual native loaded frames, rechecked source offsets, frequencies and both optional arrays. '
              'Eight bounded frequencies per array, no configure/audio, missing arrays never passed to getters.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'directory', 'reference', 'output'):
        parser.add_argument('--'+name, required=True, type=Path)
    parser.add_argument('--limit', type=int, default=119)
    args = parser.parse_args()
    assert 1 <= args.limit <= 119
    native = Native(args.dll)
    result = dict(schema=1, dse_sha256=SHA256, resource_sha256=RESOURCE_SHA256,
        reference_sha256=hashlib.sha256(args.reference.read_bytes()).hexdigest(),
        owned=owned(native), loaded=real_frames(native, args.directory, args.reference, args.limit),
        reviewed_rvas=dict(first_getter='0x2b5a0', second_getter='0x2b5d0', lookup='0x2c1e0',
                           weight_constructor='0x63060', weight_pointer='0xaa9670'),
        scope='Hash-pinned compact VQM lookup consumption. Independent weight generation, float rounding, '
              'first-slot exclusion and bounded source-frame queries. Not maker smoothing, tail production, '
              'bit17 production, other layouts, missing-array getter behavior or complete Editor validation. '
              'Installed files unchanged; no native code or bank arrays/PCM serialized; no audio generated.')
    dependencies = ('verify_vqm_compact_frequency_lookup.py', 'audit_frm2_numeric.py',
                    'audit_vqm_compact_load_identity.py', 'audit_vqm_legacy_live_features.py',
                    'audit_vqm_legacy_phase_relations.py', 'probe_frm2.py', 'verify_region_frame_blend.py')
    result['source_sha256'] = {name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                              for name in dependencies}
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(owned=result['owned'], loaded=result['loaded']['totals'])))


if __name__ == '__main__':
    main()
