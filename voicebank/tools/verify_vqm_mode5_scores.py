"""Verify mode5 preparation/templates/scores using owned spectra and normal batches.

Commercial PCM, spectra, templates, prepared arrays and scores remain in memory.
Only aggregate numerical comparisons and source/target identities are persisted.
"""
import argparse
import ctypes as c
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import struct
import subprocess
import sys
import traceback

from audit_frm2_numeric import Native, P, Q, I, F, D, B, SHA256, f32
from frm2_mode5_scores import prepare, templates, score
from frm2_mode5_confidence import confidence, interval
from probe_frm2_batch_outputs import run as analyze_batch
from verify_vqm_alternate_score_paths import frames as final_frames
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences, metrics
from verify_vqm_mode5_confidence import review_writer
from audit_vqm_v4_module_leads import PE
from audit_vqm_feature_calls_cfg import runtime_groups


def geometry(obj):
    return (I.from_address(obj + 0x324).value, F.from_address(obj + 0x328).value,
            F.from_address(obj + 0x3c4).value, F.from_address(obj + 0x3b4).value)


def compare_prepared(obj, model):
    result = []
    size = len(model[0])
    for index, (offset, ty) in enumerate(((0x338, F), (0x340, F), (0x358, I))):
        address = P.from_address(obj + offset).value
        assert address
        differences = [ty.from_address(address + i * 4).value - model[index][i] for i in range(size)]
        encoded = bytes((ty * size)(*model[index]))
        assert c.string_at(address, size * 4) == encoded
        result.append(metrics(differences))
    assert c.string_at(obj + 0x348, 4) == struct.pack('<f', model[3])
    count = I.from_address(obj + 0x580).value
    assert count == len(model[4])
    assert all(F.from_address(obj + i * 4).value == model[4][i] for i in range(count))
    assert all(F.from_address(obj + 0x3f0 + i * 4).value == model[5][i] for i in range(count))
    assert c.string_at(obj, count * 4) == bytes((F * count)(*model[4]))
    assert c.string_at(obj + 0x3f0, count * 4) == bytes((F * count)(*model[5]))
    return result


def compare_templates(obj, rows):
    assert I.from_address(obj + 0x3b8).value == 1
    assert I.from_address(obj + 0x3c0).value == len(rows)
    address = P.from_address(obj + 0x3c8).value
    cells = 0
    for index in range(3, len(rows)):
        samples, normalization, attenuation = rows[index]
        at = address + index * 0x19c
        assert I.from_address(at).value == len(samples)
        assert F.from_address(at + 0x194).value == normalization
        assert F.from_address(at + 0x198).value == attenuation
        assert c.string_at(at + 0x194, 8) == struct.pack('<ff', normalization, attenuation)
        cells += 3
        for harmonic, sample in enumerate(samples):
            entry = at + 4 + harmonic * 20
            actual = (I.from_address(entry).value, I.from_address(entry + 4).value,
                      F.from_address(entry + 8).value, F.from_address(entry + 12).value,
                      F.from_address(entry + 16).value)
            assert actual == sample, (index, harmonic)
            assert c.string_at(entry, 20) == struct.pack('<iifff', *sample)
            cells += 5
    return cells


def owned_checks(dll):
    native = Native(dll)
    logf = native.fn(0x217e3e, F, F)
    constructor = native.fn(0xf2620, P, P)
    preparation = native.fn(0xf2830, None, P, P, I, P, B, B, F)
    evaluate = native.fn(0xf3ce0, F, P, I)
    cases = prepared_cells = template_cells = score_cells = repeated_cache = 0
    candidates = {}
    for size in (128, 512, 2048):
        for rate in (11025., 22050.):
            for scale in (1., 2. ** -30):
                for shape in range(6):
                    if shape == 0:
                        source = [0.] * size
                    elif shape == 1:
                        source = [f32(.03)] * size
                    elif shape == 2:
                        source = [f32(.002 * (1 + math.sin(i * .47)) ** 2) for i in range(size)]
                    elif shape == 3:
                        source = [f32(1e6 * (1 + math.sin(i * .13)) ** 4) for i in range(size)]
                    elif shape == 4:
                        source = [f32(1e-15 if i % 9 else 500.) for i in range(size)]
                    else:
                        source = [f32((i % 17) ** 2 * .004) for i in range(size)]
                    source[-1] = 0.  # Last-bin floor makes the right guard irrelevant.
                    obj = native.alloc(0x590)
                    constructor(obj)
                    F.from_address(obj + 0x328).value = rate
                    address = native.array(F, source + [12345.] * 8)
                    before = c.string_at(address, (size + 8) * 4)
                    preparation(obj, address, size, None, 0, 0, scale)
                    assert c.string_at(address, (size + 8) * 4) == before
                    model = prepare(source, rate, 6000., scale, logf)
                    comparisons = compare_prepared(obj, model)
                    assert all(row['max_absolute_error'] == 0. for row in comparisons)
                    prepared_cells += 3 * size + 2 * len(model[4]) + 1
                    key = geometry(obj)
                    if key not in candidates:
                        candidates[key] = templates(*key, logf)
                    candidate = candidates[key]
                    template_cells += compare_templates(obj, candidate)
                    cache = P.from_address(obj + 0x3e0).value
                    flags = P.from_address(obj + 0x3e8).value
                    for index in (0, 1, 2, *range(3, len(candidate)), len(candidate), len(candidate) + 1):
                        predicted = score(index, model, candidate)
                        actual = evaluate(obj, index)
                        assert predicted == actual, (size, rate, scale, shape, index)
                        assert struct.pack('<f', predicted) == struct.pack('<f', actual)
                        score_cells += 1
                        if 3 <= index < len(candidate):
                            assert B.from_address(flags + index).value == 1
                            assert F.from_address(cache + index * 4).value == predicted
                            assert evaluate(obj, index) == predicted
                            assert c.string_at(cache + index * 4, 4) == struct.pack('<f', predicted)
                            repeated_cache += 1
                    cases += 1
    return dict(cases=cases, prepared_values_exact=prepared_cells, template_fields_exact=template_cells,
                scores_exact=score_cells, cache_repeats_exact=repeated_cache,
                source_and_guards_unchanged=True, logarithm='native float32 logf only',
                scope='Independent preparation, templates and scoring; owned finite nonnegative spectra, '
                      'last bin zero, native helper lazily fills only its owned cache.')


def owned_boundary_checks(dll):
    native = Native(dll)
    logf = native.fn(0x217e3e, F, F)
    constructor = native.fn(0xf2620, P, P)
    preparation = native.fn(0xf2830, None, P, P, I, P, B, B, F)
    allocate = native.fn(0x2161ac, P, Q)
    release = native.fn(0x2161b4, None, P)
    cases = exact = distinct = 0
    for shape in range(4):
        size = 512
        source = [f32(.01 * (2 + math.sin(i * (.07 + shape * .03))) ** 2) for i in range(size)]
        source[-2] = f32(.8)
        source[-1] = f32(.1)
        address = native.array(F, source)
        obj = native.alloc(0x590)
        constructor(obj)
        preparation(obj, address, size, None, 0, 0, 1.)
        # This allocation belongs to this fixture only. Extend the prepared
        # buffer through its right-neighbour probe; never pad a live batch.
        release(P.from_address(obj + 0x338).value)
        padded = allocate((size + 8) * 4)
        assert padded
        P.from_address(obj + 0x338).value = padded
        prior = None
        for guard in (-1e30, 0., 1e30):
            for at in range(size, size + 8):
                F.from_address(padded + at * 4).value = guard
            preparation(obj, address, size, None, 0, 0, 1.)
            assert all(F.from_address(padded + at * 4).value == f32(guard) for at in range(size, size + 8))
            model = prepare(source, 22050., 6000., 1., logf, right_neighbor=f32(guard))
            rows = compare_prepared(obj, model)
            assert all(row['max_absolute_error'] == 0. for row in rows)
            exact += 3 * size + 2 * len(model[4]) + 1
            if prior is not None:
                distinct += model[:3] != prior
            prior = model[:3]
            cases += 1
    assert distinct > 0
    return dict(cases=cases, prepared_values_exact=exact, changed_boundary_results=distinct,
                padded_prepared_allocation_owned=True, guards_preserved=True,
                scope='Controlled right neighbour past logical prepared length inside a padded owned allocation; '
                      'no out-of-bounds read and no live batch padding.')


def child(args, sequence=None, parser_hash=None, ddi_hash=None):
    frequency = (sequence.get('pitch1', statistics.median(row['f0'] for row in sequence['frames']))
                 if sequence else args.frequency)
    captured = {}
    finished = []
    prepared_values = template_fields = scores = nonfloor_last = 0
    maximum_errors = [0., 0., 0.]
    guard_dependency = 0
    guard_affected_ready_scores = guard_changed_score_values = 0
    guard_affected_minimum_bin = None

    def setup(native, ctx):
        obj = P.from_address(ctx + 0xc70).value
        logf = native.fn(0x217e3e, F, F)
        table = P.from_address(ctx).value
        original = c.CFUNCTYPE(Q, P)(P.from_address(table + 8).value)
        private = native.alloc(0x100)
        c.memmove(private, table, 0x100)
        candidates = {}

        def step(ctx):
            nonlocal prepared_values, template_fields, scores, nonfloor_last, guard_dependency
            nonlocal guard_affected_ready_scores, guard_changed_score_values, guard_affected_minimum_bin
            try:
                source_slot = P.from_address(ctx + 0xc48).value
                queue = P.from_address(ctx + 0xc20).value
                queue_count = I.from_address(ctx + 0xc18).value
                assert queue <= source_slot < queue + queue_count * 8
                descriptor = P.from_address(source_slot).value
                source_size = I.from_address(descriptor).value
                result = original(ctx)
                frame = P.from_address(ctx + 0xcc8).value
                if result & 0xffffffff or not frame or D.from_address(frame + 0x48).value < 0:
                    return result
                time = D.from_address(frame + 0x48).value
                at = round(time * 44100 / 256)
                assert abs(time * 44100 / 256 - at) < 1e-5 and at not in captured
                key = geometry(obj)
                size, rate, ceiling, _ = key
                # d91ce/d91d2 rotate the queue left before the step returns.
                # Retain the pre-step descriptor; using the new first slot
                # would compare the next spectrum with the previous cache.
                retained_slot = source_slot - 8 if source_slot > queue else queue + (queue_count - 1) * 8
                assert P.from_address(retained_slot).value == descriptor
                assert source_size == size
                source_pointer = P.from_address(descriptor + 8).value
                source = [F.from_address(source_pointer + i * 4).value for i in range(size)]
                model = prepare(source, rate, ceiling, 2. ** -30, logf)
                nonfloor_last += model[1][-1] > -5.
                # Compare both possible last-bin peak tests without reading past
                # any allocation. If they differ, the boundary contract is open.
                alternate = prepare(source, rate, ceiling, 2. ** -30, logf, right_neighbor=1e30)
                changed = {i for i in range(size) if model[0][i] != alternate[0][i] or model[2][i] != alternate[2][i]}
                guard_dependency += bool(changed)
                if changed:
                    lowest = min(changed)
                    guard_affected_minimum_bin = lowest if guard_affected_minimum_bin is None else min(guard_affected_minimum_bin, lowest)
                comparison = compare_prepared(obj, model)
                for index, row in enumerate(comparison):
                    maximum_errors[index] = max(maximum_errors[index], row['max_absolute_error'])
                assert all(row['max_absolute_error'] == 0. for row in comparison), (at, comparison)
                prepared_values += 3 * size + 2 * len(model[4]) + 1
                if key not in candidates:
                    candidates[key] = templates(*key, logf)
                    template_fields += compare_templates(obj, candidates[key])
                candidate = candidates[key]
                count = I.from_address(obj + 0x3c0).value
                low, high = interval(count, size, rate, F.from_address(obj + 0x34c).value,
                                     F.from_address(obj + 0x350).value)
                cache = P.from_address(obj + 0x3e0).value
                flags = P.from_address(obj + 0x3e8).value
                values = [0.] * count
                ready = [i for i in range(3, count) if B.from_address(flags + i).value]
                assert all(i in ready for i in range(low, high))
                for index in ready:
                    values[index] = score(index, model, candidate)
                    assert values[index] == F.from_address(cache + 4 * index).value, (at, index)
                    assert c.string_at(cache + index * 4, 4) == struct.pack('<f', values[index])
                    affected = any(lower in changed or upper in changed for lower, upper, *_ in candidate[index][0])
                    guard_affected_ready_scores += affected
                    if affected:
                        guard_changed_score_values += score(index, alternate, candidate) != values[index]
                feature = P.from_address(frame + 0x128).value
                predicted = confidence(values, low, high)
                actual = F.from_address(feature + 0x14).value
                assert predicted == actual
                assert c.string_at(feature + 0x14, 4) == struct.pack('<f', predicted)
                captured[at] = dict(actual=actual, selected_interval=(low, high), ready_slots=len(ready),
                                    ready_range=(min(ready), max(ready) + 1, len(ready)), geometry=key)
                scores += len(ready)
                return result
            except BaseException:
                traceback.print_exc()
                sys.stderr.flush()
                os._exit(2)

        callback = c.CFUNCTYPE(Q, P)(step)
        P.from_address(private + 8).value = c.cast(callback, P).value
        P.from_address(ctx).value = private
        native.keep.extend([original, callback])

    def finish(native, ctx, collection):
        finished.append(final_frames(native, ctx, collection))

    options = dict(analysis_mode=5, result_observer=finish)
    if sequence:
        options['pcm_override'] = sequence['pcm']
    observed = analyze_batch(args.dll, 1, 1, args.shape, frequency, setup_observer=setup, **options)
    baseline = analyze_batch(args.dll, 1, 1, args.shape, frequency, **options)
    assert observed == baseline and len(finished) == 2 and finished[0] == finished[1]
    assert set(captured) == set(finished[0]) == set(range(observed['frames']))
    assert all(captured[at]['actual'] == finished[0][at]['values'][4] for at in captured)
    if sequence:
        assert len(captured) == len(sequence['frames'])
    saved = ([captured[at]['actual'] - sequence['frames'][at]['features'][4]
              for at in range(12, len(captured) - 12)] if sequence else None)
    return dict(case=sequence.get('category', args.sample) if sequence else f'{args.shape}_{args.frequency:g}',
                frames=len(captured), prepared_values_exact=prepared_values, template_fields_exact=template_fields,
                scores_exact=scores, preparation_max_errors=maximum_errors,
                independent_confidence_exact=len(captured), nonfloor_last_bins=nonfloor_last,
                guard_dependent_frames=guard_dependency,
                guard_affected_ready_scores=guard_affected_ready_scores,
                guard_changed_score_values=guard_changed_score_values,
                guard_affected_minimum_bin=guard_affected_minimum_bin,
                intervals=sorted({row['selected_interval'] for row in captured.values()}),
                ready_ranges=sorted({row['ready_range'] for row in captured.values()}),
                geometries=sorted({row['geometry'] for row in captured.values()}),
                observed_matches_fresh_baseline=True, saved_confidence_comparison=metrics(saved) if saved is not None else None,
                parser_sha256=parser_hash, ddi_sha256=ddi_hash)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb', 'decoder-dir'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--child', action='store_true')
    parser.add_argument('--sample', type=int, choices=range(5))
    parser.add_argument('--shape', choices=('steady', 'attack'), default='steady')
    parser.add_argument('--frequency', type=float, default=220.)
    args = parser.parse_args()
    if args.child:
        if args.sample is None:
            report = child(args)
        else:
            parser_hash, ddi_hash, sequences = load_sequences(args.manifest, args.voice_root, args.ddb_tools)
            sequences += auxiliary_sequences(args.aux_vdb)
            report = child(args, sequences[args.sample], parser_hash, ddi_hash)
        print(json.dumps(report))
        return
    assert args.output
    review = review_writer(args.dll, args.decoder_dir)
    import capstone as cs
    data = args.dll.read_bytes()
    pe = PE(data)
    groups, _ = runtime_groups(data, pe)
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    decoder.detail = True
    caller = {}
    for start, end, _ in groups[0xd2cc0]:
        caller.update({op.address - pe.image_base: op for op in decoder.disasm(
            data[pe.offset(start):pe.offset(start) + end - start], pe.image_base + start)})
    assert caller[0xd5ede].operands[0].imm - pe.image_base == 0xf2830
    op = caller[0xd5eb4]
    target = op.address + op.size + op.operands[1].mem.disp - pe.image_base
    assert struct.unpack('<f', data[pe.offset(target):pe.offset(target) + 4])[0] == 2. ** -30
    review.update(preparation_call_verified=True, caller_spectral_scale=2. ** -30)
    owned = owned_checks(args.dll)
    boundary = owned_boundary_checks(args.dll)
    rows = []
    specifications = [dict(shape=shape, frequency=frequency) for shape in ('steady', 'attack')
                      for frequency in (110., 220., 440.)] + [dict(sample=i) for i in range(5)]
    for specification in specifications:
        command = [sys.executable, '-B', str(Path(__file__).resolve()), '--child']
        for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb', 'decoder-dir'):
            command += ['--' + name, str(getattr(args, name.replace('-', '_')))]
        for name, value in specification.items():
            command += ['--' + name, str(value)]
        process = subprocess.run(command, capture_output=True, text=True, timeout=120)
        assert process.returncode == 0, process.stderr[-3000:]
        row = json.loads(process.stdout)
        rows.append(row)
        print(json.dumps(dict(case=row['case'], frames=row['frames'], scores=row['scores_exact'])), flush=True)
    sources = ('verify_vqm_mode5_scores.py', 'frm2_mode5_scores.py', 'frm2_mode5_confidence.py',
               'verify_vqm_mode5_confidence.py', 'probe_frm2_batch_outputs.py',
               'verify_vqm_alternate_score_paths.py', 'verify_vqm_bit16_spectral_power.py',
               'verify_vqm_bit16_peak_interpolation.py', 'audit_frm2_numeric.py',
               'audit_vqm_v4_module_leads.py', 'audit_vqm_feature_calls_cfg.py')
    report = dict(schema=1, dse_sha256=SHA256,
                  auxiliary_sha256=hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest(),
                  source_sha256={name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
                  writer_review=review, owned_checks=owned, owned_boundary_checks=boundary, cases=rows,
                  byte_comparisons=True,
                  summary={name: sum(row[name] for row in rows) for name in
                           ('frames', 'prepared_values_exact', 'template_fields_exact', 'scores_exact',
                            'independent_confidence_exact', 'nonfloor_last_bins', 'guard_dependent_frames',
                            'guard_affected_ready_scores', 'guard_changed_score_values')},
                  scope='Independent caller spectrum to mode5 prepared arrays, templates, scores and confidence; '
                        'native float32 logf shared solely for libm rounding, PCM-to-spectrum still native.',
                  limits=['Original maker confidence still differs; source input and original authoring protocol unverified',
                          'The preparation last-bin test reads a right neighbour in native code; no out-of-bounds memory read '
                          'is added by this verifier; guard dependence is reported separately',
                          'Natural comparison covers all ready cache slots, not every possible score for every PCM frame',
                          'Owned geometry covers sample strides equal to one and the two stated rates; no other geometry inferred',
                          'Fresh baseline compares returned metadata, six score scalars and four feature arrays, not all frame payloads',
                          'Native logarithm rounding is a shared primitive; not a native-free PCM producer',
                          'No commercial arrays, PCM, proprietary bytes or instructions persisted'],
                  installed_files_unchanged=True, no_windows_temp=True)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__ == '__main__':
    main()
