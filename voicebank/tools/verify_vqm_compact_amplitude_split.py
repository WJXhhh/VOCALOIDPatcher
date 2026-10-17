"""Audit compact amplitude decomposition and its real seeded audio consumers.

Installed payloads remain in memory. Only independently loaded source arrays
are varied; no native code or installed file is changed. Evidence consists of
hashes, counts and aggregate errors, never commercial arrays or PCM samples.
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
from audit_vqm_legacy_live_features import RESOURCE_SHA256, catalog, children, loaded_regions, name, pointer_items
from audit_vqm_legacy_phase_relations import circular, metrics
from verify_vqm_legacy_bit17_consumption import audio, load_configure
from verify_vqm_legacy_seeded_consumption import random_reset


VARIANTS = ('baseline', 'baseline_plus6', 'residual_plus6', 'balanced_repartition', 'main_plus6')
TRIALS = ('baseline', 'baseline_plus6', 'residual_plus6', 'balanced_repartition', 'main_plus6',
          'baseline', 'baseline_plus6', 'residual_plus6', 'balanced_repartition', 'main_plus6', 'baseline')


def repartition(first, second):
    total = first+second
    changed_first = f32(first+6.)
    changed_second = f32(total-changed_first)
    if changed_first+changed_second == total and changed_first != first:
        return changed_first, changed_second
    return first, second


def owned_checks():
    rows = []
    for first, second in ((-40., 3.), (-80.5, -1.25), (-120., 0.), (-12.25, 6.5), (-6., .125)):
        a, b = repartition(first, second)
        assert a != first and a+b == first+second
        rows.append(dict(case='exact_sum_repartition', passed=True))
    return rows


def serialized(resource, reference):
    data = resource.read_bytes()
    assert hashlib.sha256(data).hexdigest() == RESOURCE_SHA256
    prior = json.loads(reference.read_text(encoding='utf-8'))
    assert prior['samples'] == 119 and prior['resource_sha256'] == RESOURCE_SHA256
    counts, differences, errors, phase_differences = Counter(), Counter(), [], []
    first_range, second_range, tail_range = [], [], []
    for case in prior['cases']:
        start = int(case['track_offset'], 16)
        for _, _, wire in frames_in_interval(data, start, start+case['track_bytes']):
            counts['all_frames'] += 1
            n, m = wire['count'], wire['spectrum_count']
            if not m:
                continue
            counts['frames_with_optional_arrays'] += 1
            assert m >= n >= 2
            differences[str(m-n)] += 1
            amplitudes = struct.unpack('<'+str(n)+'f', wire['components'][1])
            first, second = [struct.unpack('<'+str(m)+'f', x) for x in wire['spectrum']]
            bit17 = struct.unpack_from('<f', wire['feature'], 0x21c)[0]
            phase_differences.append(circular(bit17-second[1]))
            for k in range(1, n):
                assert all(math.isfinite(x) for x in (amplitudes[k], first[k], second[k]))
                errors.append(amplitudes[k]-(first[k]+second[k]))
                counts['common_nonfirst_slots'] += 1
                counts['sum_float64_exact'] += amplitudes[k] == first[k]+second[k]
                counts['residual_float32_bytes_exact'] += struct.pack('<f', second[k]) == struct.pack('<f', amplitudes[k]-first[k])
                first_range.append(first[k]); second_range.append(second[k])
            for k in range(n, m):
                counts['optional_tail_slots'] += 1
                counts['optional_tail_residual_zero'] += second[k] == 0.
                counts['optional_tail_first_nonzero'] += first[k] != 0.
                assert math.isfinite(first[k]) and math.isfinite(second[k])
                tail_range.append(first[k]+second[k])
    assert counts['all_frames'] == 26842 and counts['frames_with_optional_arrays'] == 22038
    return dict(counts=dict(counts), optional_count_minus_component_count=dict(differences),
        amplitude_sum_error=metrics(errors), first_array_common_range=[min(first_range), max(first_range)],
        second_array_common_range=[min(second_range), max(second_range)],
        optional_tail_sum_range=[min(tail_range), max(tail_range)],
        bit17_vs_second_array_slot1_circular_diagnostic=metrics(phase_differences),
        scope='Raw compact fields via425 source intervals. Common indices>0 bind amplitude '
            'baseline and residual; first index is pitch metadata. Extra optional indices have '
            'no counterpart in the saved component list. Tail derivation and original smoothing '
            'configuration remain unidentified. The diagnostic circular comparison does not '
            'assert that amplitude residuals are angles.')


def frames(control):
    return [frame for region in loaded_regions(control) for frame in pointer_items(region, 0x18, 0x20)]


def positive_array_digest(control):
    digest = hashlib.sha256()
    for frame in frames(control):
        n, m = I.from_address(frame+0x58).value, I.from_address(frame+0xa8).value
        assert 0 <= n <= 10000 and 0 <= m <= 100000
        digest.update(struct.pack('<II', n, m))
        for count, offset in ((n, 0x68), (m, 0xb0), (m, 0xb8)):
            if count > 1:
                pointer = P.from_address(frame+offset).value
                assert pointer
                digest.update(c.string_at(pointer+4, 4*(count-1)))
    return digest.hexdigest()


def mutate(control, variant):
    assert variant in VARIANTS
    counts = Counter()
    for frame in frames(control):
        n, m = I.from_address(frame+0x58).value, I.from_address(frame+0xa8).value
        assert 1 <= n <= 10000 and 0 <= m <= 100000
        feature = P.from_address(frame+0x128).value
        before = c.string_at(feature, 0x228)
        frequency, phase = P.from_address(frame+0x60).value, P.from_address(frame+0x70).value
        untouched = (c.string_at(frequency, n*4), c.string_at(phase, n*4), c.string_at(frame+0xf0, 4))
        counts['frames'] += 1
        if m:
            assert m >= n
            counts['frames_with_optional_arrays'] += 1
            first, second = P.from_address(frame+0xb0).value, P.from_address(frame+0xb8).value
            first_metadata, second_metadata = c.string_at(first, 4), c.string_at(second, 4)
            for k in range(1, m):
                a, b = F.from_address(first+4*k).value, F.from_address(second+4*k).value
                assert math.isfinite(a) and math.isfinite(b)
                counts['optional_nonfirst_slots'] += 1
                if variant == 'baseline_plus6':
                    F.from_address(first+4*k).value = f32(a+6.)
                    counts['changed_first_values'] += F.from_address(first+4*k).value != a
                elif variant == 'residual_plus6':
                    F.from_address(second+4*k).value = f32(b+6.)
                    counts['changed_second_values'] += F.from_address(second+4*k).value != b
                elif variant == 'balanced_repartition':
                    new_a, new_b = repartition(a, b)
                    F.from_address(first+4*k).value = new_a
                    F.from_address(second+4*k).value = new_b
                    assert F.from_address(first+4*k).value+F.from_address(second+4*k).value == a+b
                    counts['pair_sums_preserved'] += 1
                    counts['changed_exact_sum_pairs'] += new_a != a
            assert c.string_at(first, 4) == first_metadata and c.string_at(second, 4) == second_metadata
        if variant == 'main_plus6':
            amplitude = P.from_address(frame+0x68).value
            dc = c.string_at(amplitude, 4)
            for k in range(1, n):
                old = F.from_address(amplitude+4*k).value
                assert math.isfinite(old)
                F.from_address(amplitude+4*k).value = f32(old+6.)
                counts['changed_main_values'] += F.from_address(amplitude+4*k).value != old
            assert c.string_at(amplitude, 4) == dc
        assert before == c.string_at(feature, 0x228)
        assert untouched == (c.string_at(frequency, n*4), c.string_at(phase, n*4), c.string_at(frame+0xf0, 4))
    return dict(counts)


def compact_audio(result):
    details = result.pop('blocks')
    result['status_counts'] = dict(Counter(str(row['status']) for row in details))
    result['finite_guarded_values'] = len(details)*256
    result['nonzero_samples'] = sum(row['nonzero'] for row in details)
    result['peak'] = max(row['peak'] for row in details)
    return result


def consumer(dll, directory, limit, blocks):
    native = Native(dll)
    encoded = str(directory.resolve()).encode('utf-8')
    root = native.fn(0xfea0, P, c.c_char_p)(encoded)
    assert root
    reports, identities = [], []
    try:
        entries = catalog(native, root)
        assert len(entries) == 119
        for seed in (428, 429):
            reset, identity = random_reset(dll, seed)
            identities.append(identity)
            for index, (side, descriptor, label, _) in enumerate(entries[:limit]):
                trials = []
                for variant in TRIALS:
                    owned_root = native.fn(0xfea0, P, c.c_char_p)(encoded)
                    assert owned_root and owned_root != root
                    control = None
                    try:
                        node = native.fn(0x23f00, P, P, I, P)(owned_root, side, descriptor)
                        sample = {name(item):item for item in children(node)}[label]
                        control, _ = load_configure(native, owned_root, (side, descriptor, label, sample), 'baseline')
                        changes = mutate(control, variant)
                        before = positive_array_digest(control)
                        result = compact_audio(audio(native, control, blocks, reset))
                        after = positive_array_digest(control)
                        trials.append(dict(variant=variant, changes=changes, audio=result,
                            positive_source_arrays_preserved_through_audio=before == after))
                    finally:
                        if control:
                            native.fn(0xb480, None, P)(control)
                        assert native.fn(0x10080, Q, P)(owned_root) == 0
                groups = {variant:[trial for trial in trials if trial['variant'] == variant] for variant in VARIANTS}
                repeatable = {variant:len({trial['audio']['sha256'] for trial in group}) == 1
                              for variant, group in groups.items()}
                baseline_hash = groups['baseline'][0]['audio']['sha256']
                changed = {variant:groups[variant][0]['audio']['sha256'] != baseline_hash
                           if repeatable['baseline'] and repeatable[variant] else None for variant in VARIANTS[1:]}
                reports.append(dict(seed=seed, sample_index=index, repeatable=repeatable,
                    changed=changed, trials=trials))
                print(json.dumps(dict(seed=seed, sample_index=index, repeatable=all(repeatable.values()), changed=changed)), flush=True)
    finally:
        assert native.fn(0x10080, Q, P)(root) == 0
    summaries = []
    for seed in (428, 429):
        selected = [row for row in reports if row['seed'] == seed]
        summaries.append(dict(seed=seed, samples=len(selected),
            all_variants_repeatable=sum(all(row['repeatable'].values()) for row in selected),
            changed_samples={variant:sum(row['changed'][variant] is True for row in selected) for variant in VARIANTS[1:]},
            source_array_preserved_trials=sum(trial['positive_source_arrays_preserved_through_audio'] for row in selected for trial in row['trials']),
            runs=len(selected)*len(TRIALS),
            audio_calls=sum(trial['audio']['calls'] for row in selected for trial in row['trials']),
            finite_guarded_values=sum(trial['audio']['finite_guarded_values'] for row in selected for trial in row['trials'])))
    first = [row for row in reports if row['seed'] == 428]
    second = [row for row in reports if row['seed'] == 429]
    cross_seed = sum(a['trials'][0]['audio']['sha256'] != b['trials'][0]['audio']['sha256'] for a, b in zip(first, second))
    return dict(seeds=identities, max_blocks=blocks, per_seed=summaries, cases=reports,
        different_seed_baselines=cross_seed,
        scope='Established sampleInfo/configure/audio protocol. Source perturbations after configure, '
            'before native audio, on fresh owned roots. No first optional slots, F0, frequency, '
            'phase or feature field changed by the perturbation. Three baselines and two runs per '
            'variant per seed. Native rand/srand, code and resources unchanged. Balanced pairs '
            'preserve every exact float64 sum but need not preserve separately weighted consumers. '
            'This is a bounded consumption test, not an authoring formula or V4X/full Editor test.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'directory', 'reference', 'output'):
        parser.add_argument('--'+name, type=Path, required=True)
    parser.add_argument('--limit', type=int, default=119)
    parser.add_argument('--blocks', type=int, default=64)
    args = parser.parse_args()
    assert 1 <= args.limit <= 119 and 4 <= args.blocks <= 1024
    result = dict(schema=1, dse_sha256=SHA256, resource_sha256=RESOURCE_SHA256,
        reference_sha256=hashlib.sha256(args.reference.read_bytes()).hexdigest(),
        owned_checks=owned_checks(), serialized=serialized(args.directory/'dvqm.dat', args.reference),
        consumer=consumer(args.dll, args.directory, args.limit, args.blocks))
    dependencies = ('verify_vqm_compact_amplitude_split.py','audit_vqm_compact_load_identity.py',
        'audit_vqm_legacy_live_features.py','audit_vqm_legacy_phase_relations.py','audit_frm2_numeric.py',
        'probe_frm2.py','verify_region_frame_blend.py','verify_vqm_legacy_bit17_consumption.py',
        'verify_vqm_legacy_seeded_consumption.py','audit_vqm_v4_module_leads.py')
    result['source_sha256'] = {name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                              for name in dependencies}
    result['scope'] = ('Compact DVQM amplitude decomposition and seeded native consumption. '
        'A resource-specific baseline/residual role, not a universal FRM2 phase reinterpretation. '
        'Tail derivation, original smoothing, bit17 production, BRWSP and other original VQM/Growl '
        'requirements remain open. Installed data read only; aggregates only, no PCM/arrays exported.')
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(serialized=result['serialized']['counts'], consumers=result['consumer']['per_seed'])))


if __name__ == '__main__':
    main()
