"""Check fixed arithmetic and available overlap for saved compact decomposition.

Only counts, hashes, interval identifiers and numerical errors are serialized.
Installed frame and Fourier payloads remain in memory. No fitted coefficients.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import struct

from audit_frm2_numeric import F, SHA256, f32
from audit_vqm_compact_authoring_context import OwnedLowpass, selected
from audit_vqm_compact_load_identity import frames_in_interval
from audit_vqm_legacy_live_features import RESOURCE_SHA256
from audit_vqm_legacy_phase_relations import metrics
from verify_vqm_compact_runtime_decomposition import amplitude_strength, blend_strength, mix, pitch_hz, raw_float
from verify_vqm_temporal_lowpass import mirrored, weights


def rows_in_track(data, case):
    start = int(case['track_offset'], 16)
    result = []
    for _, _, wire in frames_in_interval(data, start, start+case['track_bytes']):
        n, m = wire['count'], wire['spectrum_count']
        fingerprint = hashlib.sha256(struct.pack('<I', n)+b''.join(wire['components'])+
            wire['pitch']+wire['feature'][:0x20]+wire['feature'][0x24:0x220]+wire['feature'][0x224:]).digest()
        result.append(dict(n=n, m=m, pitch=struct.unpack('<f', wire['pitch'])[0], time=wire['time'],
            feature=wire['feature'], amplitude=struct.unpack('<'+str(n)+'f', wire['components'][1]),
            first=struct.unpack('<'+str(m)+'f', wire['spectrum'][0]) if m else (),
            second=struct.unpack('<'+str(m)+'f', wire['spectrum'][1]) if m else (), fingerprint=fingerprint))
    return result


def overlap_audit(tracks):
    sequences = [[row['fingerprint'] for row in rows] for rows in tracks]
    anchors = defaultdict(list)
    for sample, sequence in enumerate(sequences):
        for index in range(len(sequence)-4):
            anchors[tuple(sequence[index:index+5])].append((sample, index))
    matches = []
    for sample, sequence in enumerate(sequences):
        for side, index in (('left', 0), ('right', len(sequence)-5)):
            for donor, other in anchors[tuple(sequence[index:index+5])]:
                if donor == sample:
                    continue
                offset = other-index
                lo, hi = max(0, -offset), min(len(sequence), len(sequences[donor])-offset)
                if hi-lo < 19 or any(sequence[k] != sequences[donor][k+offset] for k in range(lo, hi)):
                    continue
                extension = max(0, offset) if side == 'left' else max(0, len(sequences[donor])-len(sequence)-offset)
                matches.append(dict(sample_index=sample, side=side, donor_index=donor,
                    offset=offset, overlap_frames=hi-lo, extension_frames=extension))
    return dict(anchors=5, minimum_overlap=19, verified_matches=matches,
        extendable_matches=sum(row['extension_frames'] > 0 for row in matches),
        scope='Exact primary arrays, stored pitch and features except adapted bit7/18; no time/optional-array dependence. '
              'No extendable match does not exclude different analysis of the same recording or external context.')


def smooth_candidates(lowpass, sequence, center):
    n = len(sequence)
    length = 1 << (n.bit_length()+1)
    prepared = mirrored(sequence, length)
    pointer, h = lowpass.pointer, lowpass.harness
    c.memmove(pointer, struct.pack('<'+str(len(prepared))+'f', *prepared), len(prepared)*4)
    index = int(math.log2(length))
    assert h.compute_forward(h.forward[index], pointer) == 0
    forward = c.string_at(pointer, (length+2)*4)
    result = {}
    for formula in ('float32_complement', 'direct_ramp'):
        c.memmove(pointer, forward, len(forward))
        current = weights(length, min(n, 3.), min(n, 5.)) if formula == 'float32_complement' else [
            f32(min(1., max(0., (min(n, 3.)+min(n, 5.)-k)/min(n, 5.)))) for k in range(length//2)]
        for k, weight in enumerate(current):
            for component in (0, 1):
                address = pointer+8*k+4*component
                F.from_address(address).value = f32(F.from_address(address).value*weight) if weight else 0.
        F.from_address(pointer+4).value = 0.
        F.from_address(pointer+4*length).value = 0.
        F.from_address(pointer+4*(length+1)).value = 0.
        assert h.compute_backward(h.backward[index], pointer) == 0
        result[formula] = F.from_address(pointer+4*center).value
        lowpass.check_guard()
    return result


def mixed(smoothed, original, strength, formula):
    if original is None or strength <= 0.:
        return smoothed
    alpha = min(1., f32(strength/2.5))
    if formula == 'runtime':
        return mix(smoothed, original, strength)
    if formula == 'double_expression':
        return f32(smoothed+(original-smoothed)*alpha)
    if formula == 'weighted_float32':
        return f32(f32(f32(1.-alpha)*smoothed)+f32(alpha*original))
    assert formula == 'weighted_double'
    return f32((1.-alpha)*smoothed+alpha*original)


def owned_checks():
    reports = []
    values = [dict(fingerprint=hashlib.sha256(struct.pack('<I', j)).digest()) for j in range(60)]
    overlap = overlap_audit([values[9:41], values])
    crop = [row for row in overlap['verified_matches'] if row['sample_index'] == 0]
    assert len(crop) == 2 and {row['side'] for row in crop} == {'left', 'right'}
    assert all(row['offset'] == 9 and row['overlap_frames'] == 32 for row in crop)
    assert {row['extension_frames'] for row in crop} == {9, 19}
    reports.append(dict(case='known_crop_context_recovered', passed=True))
    overlap = overlap_audit([values[:25], values[:25]])
    assert len(overlap['verified_matches']) == 4 and overlap['extendable_matches'] == 0
    reports.append(dict(case='duplicate_without_extension', passed=True))
    broken = list(values[:25])
    broken[12] = dict(fingerprint=b'own altered interior')
    assert not overlap_audit([values[:25], broken])['verified_matches']
    reports.append(dict(case='matching_anchor_but_different_interior_rejected', passed=True))
    for formula in ('runtime', 'double_expression', 'weighted_float32', 'weighted_double'):
        assert mixed(-20., -30., 0., formula) == -20.
        assert mixed(-20., -30., 2.5, formula) == -30.
        assert mixed(-20., -30., 100., formula) == -30.
        assert mixed(-20., None, 100., formula) == -20.
        assert mixed(-20., -30., 1.25, formula) == -25.
        reports.append(dict(case='mixing_boundaries_'+formula, passed=True))
    return reports


def track_coverage(data, cases):
    expected = {int(case['track_offset'], 16):case['track_bytes'] for case in cases}
    found, raw_count, position = {}, 0, 0
    while (position := data.find(b'GTRK', position)) >= 0:
        raw_count += 1
        if position+8 <= len(data):
            size = struct.unpack_from('<I', data, position+4)[0]
            if size >= 8 and position+size <= len(data):
                found[position] = size
        position += 4
    assert found == expected
    return dict(raw_tag_occurrences=raw_count, size_valid_candidates=len(found),
        bound_intervals=len(expected), same_offsets_and_sizes=True,
        additional_size_valid_track_candidates=0,
        scope='Bounded GTRK tag/length crosscheck against425 native source intervals; not a generic chunk-tree parser.')


def run(dll, directory, reference, stride):
    data = (directory/'dvqm.dat').read_bytes()
    assert hashlib.sha256(data).hexdigest() == RESOURCE_SHA256
    prior = json.loads(reference.read_text(encoding='utf-8'))
    assert prior['samples'] == 119 and prior['resource_sha256'] == RESOURCE_SHA256
    coverage = track_coverage(data, prior['cases'])
    tracks = [rows_in_track(data, case) for case in prior['cases']]
    checks = owned_checks()
    overlap = overlap_audit(tracks)
    lowpass = OwnedLowpass(dll)
    formulas = ('runtime', 'double_expression', 'weighted_float32', 'weighted_double')
    names = [mask+'_'+formula for mask in ('float32_complement', 'direct_ramp') for formula in formulas]
    errors = {name:defaultdict(list) for name in names}
    exact = {name:Counter() for name in names}
    counts, cases = Counter(), []
    for sample, rows in enumerate(tracks):
        percase = Counter()
        for index, row in enumerate(rows):
            if not row['m'] or index < 9 or len(rows)-1-index < 9 or (index-9) % stride:
                continue
            pool = selected(rows, index)
            assert max(other['n'] for _, other in pool) == row['m']
            center = next(k for k, (j, _) in enumerate(pool) if j == index)
            starting = blend_strength(rows, index, pool, 9)
            ratios = [f32(pitch_hz(row['pitch'])/pitch_hz(other['pitch'])) for _, other in pool]
            harmonics = sorted({0, 1, 2, row['m']//4, row['m']//2, 3*row['m']//4,
                                row['n']-1, row['n'], row['m']-2, row['m']-1})
            percase['frames'] += 1
            for harmonic in harmonics:
                if not 0 <= harmonic < row['m']:
                    continue
                if harmonic == 0:
                    sequence = [other['pitch'] for _, other in pool]
                    original, strength, group = row['pitch'], starting, 'pitch'
                else:
                    sequence = [other['amplitude'][min(other['n']-1, max(1,
                        int(f32(f32(f32(harmonic)*ratio)+.5))))] for ratio, (_, other) in zip(ratios, pool)]
                    original = row['amplitude'][harmonic] if harmonic < row['n'] else None
                    strength = amplitude_strength(sequence, starting)
                    group = 'amplitude' if original is not None else 'tail'
                smoothed = smooth_candidates(lowpass, sequence, center)
                percase[group+'_values'] += 1
                percase['positive_strength_values'] += strength > 0.
                for mask, scalar in smoothed.items():
                    for formula in formulas:
                        name = mask+'_'+formula
                        value = mixed(scalar, original, strength, formula)
                        residual = f32(original-value) if original is not None else 0.
                        baseline = -200. if value == 0. else value
                        for part, actual, saved in [('baseline', baseline, row['first'][harmonic]),
                                                    ('residual', residual, row['second'][harmonic])]:
                            key = group+'_'+part
                            errors[name][key].append(actual-saved)
                            exact[name][key] += raw_float(actual) == raw_float(saved)
                percase['total_values'] += 1
        counts.update(percase)
        cases.append(dict(sample_index=sample, counts=dict(percase)))
    assert c.string_at(lowpass.harness.bank_base, 0x400+64) == lowpass.harness.bank_before
    assert c.string_at(lowpass.harness.context_base, 0x2de8+64) == lowpass.harness.context_before
    result = dict(schema=1, dse_sha256=SHA256, resource_sha256=RESOURCE_SHA256,
        reference_sha256=hashlib.sha256(reference.read_bytes()).hexdigest(),
        fft_identity=lowpass.harness.fft_identity, samples=119, stride=stride, owned_checks=checks,
        counts=dict(counts), cases=cases,
        resource_track_coverage=coverage, boundary_overlap=overlap,
        candidates={name:{key:dict(error=metrics(values), float32_bytes_exact=exact[name][key])
            for key, values in parts.items()} for name, parts in errors.items()},
        scope='Deterministic position/harmonic sampling across all119 intervals. Two algebraically equivalent masks '
              'and four mixing arithmetic orders; not arbitrary parameter sweeps or fitted kernels. '
              'Shared genuine FFTs only; no bank audio or source-object mutations. '
              'Interior rounding and exact saved overlap only; bit17 and original VQM/Growl scope remain unresolved.')
    sources = set(prior['source_sha256']) | {
        'audit_vqm_compact_saved_precision.py', 'audit_vqm_compact_authoring_context.py',
        'verify_vqm_compact_runtime_decomposition.py', 'verify_vqm_temporal_lowpass.py'}
    dependency = json.loads(Path(__file__).parents[2].joinpath(
        'voicebank_doc/evidence/431a_vqm_compact_saved_baseline.json').read_text())
    sources.update(dependency['source_sha256'])
    result['source_sha256'] = {name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                              for name in sorted(sources)}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'directory', 'reference', 'output'):
        parser.add_argument('--'+name, type=Path, required=True)
    parser.add_argument('--stride', type=int, default=17)
    args = parser.parse_args()
    assert args.stride > 0
    result = run(args.dll, args.directory, args.reference, args.stride)
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(counts=result['counts'], overlaps=result['boundary_overlap'], candidates=result['candidates'])))


if __name__ == '__main__':
    main()
