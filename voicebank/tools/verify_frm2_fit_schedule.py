#!/usr/bin/env python3
"""Reconstruct the complete DRS model-fit scheduling on owned envelopes.

Compares monolithic entry 0x71e00 with an explicit schedule of native primitives.
The independent path replaces initial ABC, optimizers and residual arithmetic;
native allocation/setters remain. This does not recover authoring settings.
"""
from __future__ import annotations

import argparse
from collections import Counter
import json
import math
from pathlib import Path

from audit_frm2_numeric import Native, SHA256, F, D, I, P, Q, B, f32, stats, read_frame


def source(native, f0, shape, arrays):
    frame = native.alloc(4096)
    native.frame_ctor(frame)
    Q.from_address(frame + 0x50).value = 0x280
    F.from_address(frame + 0xf0).value = f0

    def amplitude(hz):
        x = hz / 22050
        return f32(-20 - 35 * x + (7 * math.sin(16 * x) if shape == 'ripple'
                                    else 12 * math.exp(-((hz - 1100) / 500) ** 2)
                                    if shape == 'peak' else 0))

    points = [(i / 100, amplitude(i * 220.5)) for i in range(101)]
    P.from_address(frame + 0x108).value = native.env(points, 1, 0, (-20000, 20000, 0))
    if arrays:
        count = int(22050 / f0)
        descriptor = native.alloc(0x40)
        Q.from_address(descriptor).value = 3
        I.from_address(descriptor + 8).value = count
        native.fn(0x7f1a0, I, P, P, I)(frame, descriptor, 1)
        for offset, values in ((0x60, [(k + 1) * f0 for k in range(count)]),
                               (0x68, [amplitude((k + 1) * f0) for k in range(count)])):
            p = P.from_address(frame + offset).value
            for k, v in enumerate(values):
                F.from_address(p + 4 * k).value = v
    return frame


def schedule(native, frame, maximum, spacing, independent=False,trace=None):
    if F.from_address(frame + 0xf0).value <= 0:
        return
    native.fn(0x88f40, Q, P, Q, I)(frame, 1 << 21, 0)
    native.fn(0x62e60, None, P, B)(frame, 0)
    native.fn(0x63420, None, P, B, I)(frame, 0, int(f32(maximum / spacing)))
    if independent:
        from frm2_initial_curve import initial_curve
        env = P.from_address(frame + 0x108).value
        ptr = P.from_address(env + 0x78).value
        points = [(D.from_address(ptr + k * 16).value, F.from_address(ptr + k * 16 + 8).value)
                  for k in range(I.from_address(env + 0x5c).value)]
        arrays = {}
        if Q.from_address(frame + 0x50).value & 3 == 3:
            for name, off in (('frequencies', 0x60), ('amplitudes', 0x68)):
                p = P.from_address(frame + off).value
                arrays[name] = [F.from_address(p + k * 4).value for k in range(I.from_address(frame + 0x58).value)]
        abc, fitted = initial_curve(points, F.from_address(frame + 0xf0).value,
                                   F.from_address(frame + 0xf8).value if Q.from_address(frame + 0x50).value & 1024 else 0.,
                                   maximum=maximum, **arrays)
        for off, value in zip((0x198, 0x1a0, 0x1a8), abc):
            F.from_address(frame + off).value = value
        if fitted:Q.from_address(frame + 0x50).value |= 1 << 22
    else:
        native.fn(0x759a0, None, P, B, F, I, P, I)(frame, 0, maximum, 1, 0, 1)
    if trace is not None:
        trace.append(dict(stage=('initial', 0), value=tuple(F.from_address(frame + o).value for o in (0x198, 0x1a0, 0x1a8))))
    count = I.from_address(frame + 0x180).value
    primary = P.from_address(frame + 0x188).value
    f0 = F.from_address(frame + 0xf0).value
    for k in range(count):
        hz, bw = ((min(500., 2 * f0), 240.) if k == 0 else (1500., 260.) if k == 1
                  else (f32(f32(k * spacing) + 300),
                        200 + f32(f32(k * spacing) + 300) / 22050 * 800))
        native.res_set(P.from_address(primary + k * 8).value, hz, 0, bw)
    descriptor = native.alloc(0x40)
    Q.from_address(descriptor).value = 1 << 22
    I.from_address(descriptor + 0x2c).value = 1
    native.fn(0x7f1a0, I, P, P, I)(frame, descriptor, 0)
    secondary = P.from_address(frame + 0x1b8).value
    native.res_set(P.from_address(secondary).value, 250, 0, 350)
    if independent:
        from frm2_resonance_optimizer import fit, fit_primary, refine
        env = P.from_address(frame + 0x108).value
        ptr = P.from_address(env + 0x78).value
        points = [(D.from_address(ptr + k * 16).value, F.from_address(ptr + k * 16 + 8).value)
                  for k in range(I.from_address(env + 0x5c).value)]
        abc = tuple(F.from_address(frame + off).value for off in (0x198, 0x1a0, 0x1a8))
        def read_list(ptr, count):
            return [tuple(D.from_address(P.from_address(ptr + k * 8).value + off).value
                          for off in (0x40, 0x48, 0x58)) for k in range(count)]
        first, second = read_list(primary, count), read_list(secondary, 1)
        offset=F.from_address(frame+0xf8).value if Q.from_address(frame+0x50).value&1024 else 0.
        for round_index in range(2):
            for k in reversed(range(count)):
                first[k], _ = fit_primary(points, abc, first, second, k, movement=300,offset=offset)
                if trace is not None:trace.append(dict(stage=(round_index,k),value=first[k]))
            first.sort(key=lambda res: res[0])
            second[0], _ = fit(points, f0, abc, first, second, 0, optimize=False,offset=offset)
        first, second, _ = refine(points, f0, abc, first, second,offset=offset)
        for ptr, values in ((primary, first), (secondary, second)):
            for k, value in enumerate(values):
                native.res_set(P.from_address(ptr + k * 8).value, *value)
        from frm2_drs_residual import residual_envelope
        residual = residual_envelope(points, f0, abc, first, second, offset=offset,
                                     maximum=maximum, frequencies=arrays.get('frequencies'))
        P.from_address(frame + 0x1c8).value = native.env(residual, 1, 1, (-100, 10, 0))
        Q.from_address(frame + 0x50).value |= 1 << 23
        return
    fit_primary = native.fn(0x6f770, Q, P, B, I, P, P, I, P, P, F, I, I, F)
    fit_secondary = native.fn(0x6de40, Q, P, B, I, P, P, I, P, P, F, I, I, F)
    for round_index in range(2):
        for index in reversed(range(I.from_address(frame + 0x180).value)):
            fit_primary(frame, 0, 1 << 21, frame + 0x180, primary,
                        1 << 22, frame + 0x1b0, secondary, maximum, index, 1, 300)
            if trace is not None:
                p=P.from_address(primary+8*index).value
                trace.append(dict(stage=(round_index,index),value=tuple(D.from_address(p+o).value for o in (0x40,0x48,0x58))))
        native.fn(0x77970, Q, P)(frame)
        fit_secondary(frame, 0, 1 << 21, frame + 0x180, primary,
                      1 << 22, frame + 0x1b0, secondary, maximum, 0, 0, 0)
    native.fn(0x70f10, Q, P, B, F)(frame, 0, maximum)
    native.generate(frame, 0, maximum)
    native.generate(frame, 0, maximum)


def snapshot(frame):
    values = [F.from_address(frame + offset).value for offset in (0x198, 0x1a0, 0x1a8)]
    counts = []
    for count_at, list_at in ((0x180, 0x188), (0x1b0, 0x1b8)):
        count = I.from_address(frame + count_at).value
        counts.append(count)
        p = P.from_address(frame + list_at).value
        for k in range(count):
            resonance = P.from_address(p + k * 8).value
            values.extend(D.from_address(resonance + offset).value for offset in (0x40, 0x48, 0x58))
    env = P.from_address(frame + 0x1c8).value
    count = I.from_address(env + 0x5c).value
    points = P.from_address(env + 0x78).value
    counts.append(count)
    for k in range(count):
        values.extend((D.from_address(points + k * 16).value, F.from_address(points + k * 16 + 8).value))
    return Q.from_address(frame + 0x50).value, counts, values


def audit_presets(root, manifest_path):
    data = json.loads(manifest_path.read_text(encoding='utf-8'))
    manifest = data['manifest'] if isinstance(data, dict) else data
    if not isinstance(manifest, list) or not 1 <= len(manifest) <= 1000:
        raise ValueError('Require 1..1000 frame identifiers')
    paths = {path.stem: path for path in root.rglob('*V4X*.ddb')}
    primary, secondary, geometry = Counter(), Counter(), Counter()
    old_count_matches = 0
    line_errors = []
    for row in manifest:
        frame = read_frame(paths[row['bank']], int(row['offset'], 16))
        primary[len(frame.resonances)] += 1
        secondary[len(frame.secondary_resonances)] += 1
        old_count_matches += len(frame.resonances) == min(25, int(22050 / frame.f0_hz))
        for center, amplitude, bandwidth in frame.secondary_resonances:
            key = ('200/200' if center == 200 and bandwidth == 200 else
                   '250/350' if center == 250 and bandwidth == 350 else 'intermediate_or_other')
            geometry[key] += 1
            line_errors.append(abs(bandwidth - (center * 3 - 400)))
    return dict(frames=len(manifest), manifest=manifest, primary_counts=dict(primary),
                secondary_counts=dict(secondary), secondary_geometry=dict(geometry),
                harmonic_spacing_count_matches=old_count_matches,
                secondary_affine_candidate=stats(line_errors),
                status='Observed counts/geometries; does not identify a unique commercial authoring preset')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path)
    parser.add_argument('--manifest', type=Path)
    parser.add_argument('--independent', action='store_true', help='Replace all primary/secondary optimizers and refinement with Python')
    args = parser.parse_args()
    native = Native(args.dll)
    monolithic = native.fn(0x71e00, Q, P, B, F, F)
    cases, errors = [], []
    for f0 in (80., 220., 440.):
        for spacing in (500., 1000., 2000.):
            for shape in ('slope', 'ripple', 'peak'):
                for arrays in (False, True):
                    original, staged = source(native, f0, shape, arrays), source(native, f0, shape, arrays)
                    if monolithic(original, 0, 22050, spacing):
                        raise AssertionError('Native fit failed')
                    schedule(native, staged, 22050, spacing, args.independent)
                    mask1, counts1, values1 = snapshot(original)
                    mask2, counts2, values2 = snapshot(staged)
                    if mask1 != mask2 or counts1 != counts2 or len(values1) != len(values2):
                        raise AssertionError('Fit output structure diverged')
                    delta = [abs(a - b) for a, b in zip(values1, values2)]
                    if any(not math.isfinite(e) or e > (1e-4 if args.independent else 1e-6) for e in delta):
                        raise AssertionError(f'{f0}/{spacing}/{shape}/{arrays}: {max(delta)}')
                    errors.extend(delta)
                    cases.append(dict(f0=f0, spacing=spacing, shape=shape, harmonic_arrays=arrays,
                                      primary_count=counts1[0], secondary_count=counts1[1],
                                      residual_points=counts1[2], errors=stats(delta)))
    report = dict(dll_sha256=SHA256, cases=cases, numeric_fields=stats(errors),
                  scope=('Independent initial ABC, primary/secondary optimization, refinement and residual generation; native allocation/setters retained'
                         if args.independent else 'Explicit full scheduling versus native entry; native optimizer primitives retained'))
    if bool(args.voice_root) != bool(args.manifest):
        parser.error('--voice-root and --manifest must be specified together')
    if args.manifest:
        report['commercial_observations'] = audit_presets(args.voice_root, args.manifest)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(cases=len(cases), numeric_fields=report['numeric_fields']), indent=2))


if __name__ == '__main__':
    main()
