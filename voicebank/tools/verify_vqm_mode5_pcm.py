"""Verify default mode5 PCM production against complete normal native batches.

Installed PCM/spectra/scores remain in memory. Reports contain only aggregate
errors, counts, geometry and identities. Numerical backends are explicit.
"""
import argparse
import ctypes as c
import hashlib
import json
import math
import os
from pathlib import Path
import random
import statistics
import struct
import subprocess
import sys
import traceback

from audit_frm2_numeric import P, Q, I, F, D, B, SHA256, f32
from frm2_mode5_pcm import Mode5PcmModel
from frm2_mode5_scores import prepare as prepare_scores
from frm2_live_power import PowerObserver
from probe_frm2_batch_outputs import owned_pcm, run as analyze_batch
from verify_vqm_mode5_scores import compare_prepared, compare_templates, geometry
from verify_vqm_alternate_score_paths import frames as final_frames
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences, metrics

FFT_RUNTIME_SHA256 = '7cae07fabcede5a9aa6ddbef596eafb73220d25ff7e343943322c93a4db60860'


def signal(shape, frequency):
    if shape in ('steady', 'attack', 'gap'):
        return owned_pcm(shape, frequency)
    if shape == 'silence':
        return [0.] * 11025
    if shape == 'dc':
        return [f32(5000.)] * 11025
    if shape == 'dc_tone':
        return [f32(5000. + 3000. * math.sin(math.tau * frequency * i / 44100)) for i in range(11025)]
    if shape == 'impulse':
        values = [0.] * 11025
        for at in (0, 511, 512, 513, 5500, 11024):
            values[at] = f32(9000.)
        return values
    if shape == 'noise':
        generator = random.Random(442)
        return [f32(generator.uniform(-8000., 8000.)) for _ in range(11025)]
    raise ValueError('Unsupported own signal')


def child(args, sequence=None, parser_hash=None, ddi_hash=None):
    frequency = (sequence.get('pitch1', statistics.median(row['f0'] for row in sequence['frames']))
                 if sequence else args.frequency)
    pcm = [f32(value) for value in sequence['pcm']] if sequence else signal(args.shape, frequency)
    captured = {}
    finished = []
    power_errors = []
    score_errors = []
    confidence_errors = []
    independent_powers = {}
    controls = dict(fft_frames=0, power_values_exact=0, prepared_values_compared=0,
                    template_fields_exact=0, cache_scores_exact=0, confidence_exact=0,
                    portable_power_values=0, leading_windows=0, trailing_windows=0,
                    prepared_tail_zero_matches=0, prepared_tail_low_matches=0,
                    prepared_tail_high_matches=0, prepared_tail_affected_ready_scores=0)
    geometric = set()
    power_geometry = set()
    maximum_relative_error = 0.
    rounding_isolation = sequence is None and args.shape in ('dc_tone', 'impulse')
    isolation_errors = dict(portable_fft_shared_log=[], shared_fft_portable_log=[])
    region_differences = dict(portable_fft_shared_log=0, shared_fft_portable_log=0, portable_both=0)
    log_differences = dict(portable_fft_shared_log=0, shared_fft_portable_log=0, portable_both=0)

    def setup(native, ctx):
        obj = P.from_address(ctx + 0xc70).value
        source_observer = PowerObserver(native, True, pcm)
        logf = native.fn(0x217e3e, F, F)
        # PowerObserver's explicit shared FFT path independently prepares PCM,
        # checks actual ring samples, and compares the complete power spectrum.
        shared = Mode5PcmModel(log_backend=logf)
        portable = Mode5PcmModel()
        table = P.from_address(ctx).value
        original = c.CFUNCTYPE(Q, P)(P.from_address(table + 8).value)
        private = native.alloc(0x100)
        c.memmove(private, table, 0x100)
        templates_checked = False

        def step(ctx):
            nonlocal maximum_relative_error, templates_checked
            try:
                queue = P.from_address(ctx + 0xc20).value
                count = I.from_address(ctx + 0xc18).value
                slot = P.from_address(ctx + 0xc48).value
                assert queue <= slot < queue + count * 8
                descriptor = P.from_address(slot).value
                source_frame = P.from_address(descriptor + 0x28).value
                source_time = D.from_address(source_frame + 0x48).value
                token = source_observer.before(ctx)
                previous_rows = len(source_observer.rows)
                result = original(ctx)
                source_observer.after(ctx, token)
                if len(source_observer.rows) != previous_rows:
                    assert len(source_observer.rows) == previous_rows + 1
                    row = source_observer.rows[-1]
                    key = (row['size'], row['kind'], row['lookup'], row['factor'], row['half'])
                    assert key == (1025, 11, 0, 4., 2048)
                    power_geometry.add(key)
                    item, latest_frame, ring_center = token
                    time = D.from_address(latest_frame + 0x48).value
                    at = round(time * 44100 / 256)
                    assert abs(time * 44100 / 256 - at) < 1e-5
                    predicted = portable.power_at(pcm, at)
                    exact_power = source_observer.frames[latest_frame]
                    assert len(predicted) == len(exact_power) == 2048
                    address = P.from_address(item + 8).value
                    assert c.string_at(address, 2048 * 4) == bytes((F * 2048)(*exact_power))
                    difference = [left - right for left, right in zip(predicted, exact_power)]
                    relative = max(abs(value) for value in difference) / max(max(exact_power), 1.)
                    assert relative <= 1e-6
                    maximum_relative_error = max(maximum_relative_error, relative)
                    power_errors.extend(difference)
                    independent_powers[latest_frame] = predicted
                    controls['fft_frames'] += 1
                    controls['power_values_exact'] += 2048
                    controls['portable_power_values'] += 2048
                    controls['leading_windows'] += at * 256 - 512 < 0
                    controls['trailing_windows'] += at * 256 + 512 >= len(pcm)
                output = P.from_address(ctx + 0xcc8).value
                if result & 0xffffffff or not output or D.from_address(output + 0x48).value < 0:
                    return result
                time = D.from_address(output + 0x48).value
                at = round(time * 44100 / 256)
                assert abs(time * 44100 / 256 - at) < 1e-5 and at not in captured
                assert source_time == time
                retained = slot - 8 if slot > queue else queue + (count - 1) * 8
                assert P.from_address(retained).value == descriptor
                exact_power = source_observer.frames.pop(source_frame)
                independent_power = independent_powers.pop(source_frame)
                source_observer.complex_frames.pop(source_frame)
                assert len(exact_power) == 2048
                assert c.string_at(P.from_address(descriptor + 8).value, 2048 * 4) == bytes((F * 2048)(*exact_power))
                key = geometry(obj)
                assert key == (2048, 22050., 6000., 22050.)
                geometric.add(key)
                flags = P.from_address(obj + 0x3e8).value
                cache = P.from_address(obj + 0x3e0).value
                ready = [index for index in range(3, len(shared.templates)) if B.from_address(flags + index).value]
                exact = shared.from_power(exact_power, ready)
                independent = portable.from_power(independent_power, ready)
                if rounding_isolation:
                    alternatives = dict(portable_fft_shared_log=shared.from_power(independent_power, ready),
                                        shared_fft_portable_log=portable.from_power(exact_power, ready),
                                        portable_both=independent)
                    for name, alternative in alternatives.items():
                        region_differences[name] += sum(left != right for left, right in zip(
                            alternative['prepared'][2], exact['prepared'][2]))
                        log_differences[name] += sum(struct.pack('<f', left) != struct.pack('<f', right)
                                                    for left, right in zip(alternative['prepared'][1], exact['prepared'][1]))
                        if name in isolation_errors:
                            isolation_errors[name].append(alternative['confidence'] - exact['confidence'])
                # The native last-bin peak test consumes a neighbour outside
                # the logical array. Do not read that location or assume its
                # value survives changes to the process allocation history.
                low_tail = prepare_scores(exact_power, 22050., 6000., 2. ** -30, logf, right_neighbor=-1e30)
                high_tail = prepare_scores(exact_power, 22050., 6000., 2. ** -30, logf, right_neighbor=1e30)
                actual_excess = c.string_at(P.from_address(obj + 0x338).value, 2048 * 4)
                actual_regions = c.string_at(P.from_address(obj + 0x358).value, 2048 * 4)
                def tail_matches(model):
                    return (actual_excess == bytes((F * 2048)(*model[0]))
                            and actual_regions == bytes((I * 2048)(*model[2])))
                zero_matches = tail_matches(exact['prepared'])
                low_matches = tail_matches(low_tail)
                high_matches = tail_matches(high_tail)
                assert low_matches or high_matches
                controls['prepared_tail_zero_matches'] += zero_matches
                controls['prepared_tail_low_matches'] += low_matches
                controls['prepared_tail_high_matches'] += high_matches
                changed = {index for index in range(2048) if low_tail[0][index] != high_tail[0][index]
                           or low_tail[2][index] != high_tail[2][index]}
                affected = sum(any(lower in changed or upper in changed for lower, upper, *_ in shared.templates[index][0])
                               for index in ready)
                assert affected == 0
                controls['prepared_tail_affected_ready_scores'] += affected
                # Tail branch selection is a comparison control only. The
                # production prediction above still uses PCM and default zero,
                # and its sampled scores are independent of either tail branch.
                comparisons = compare_prepared(obj, low_tail if low_matches else high_tail)
                assert all(row['max_absolute_error'] == 0. for row in comparisons)
                controls['prepared_values_compared'] += 3 * 2048 + 2 * len(exact['prepared'][4]) + 1
                if not templates_checked:
                    controls['template_fields_exact'] += compare_templates(obj, shared.templates)
                    templates_checked = True
                for index in ready:
                    assert c.string_at(cache + index * 4, 4) == struct.pack('<f', exact['scores'][index])
                    score_errors.append(independent['scores'][index] - exact['scores'][index])
                feature = P.from_address(output + 0x128).value
                assert c.string_at(feature + 0x14, 4) == struct.pack('<f', exact['confidence'])
                error = independent['confidence'] - exact['confidence']
                confidence_errors.append(error)
                captured[at] = dict(actual=exact['confidence'], portable=independent['confidence'])
                controls['cache_scores_exact'] += len(ready)
                controls['confidence_exact'] += 1
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

    options = dict(analysis_mode=5, pcm_override=pcm, result_observer=finish)
    observed = analyze_batch(args.dll, 1, 1, args.shape, frequency, setup_observer=setup, **options)
    baseline = analyze_batch(args.dll, 1, 1, args.shape, frequency, **options)
    assert observed == baseline and len(finished) == 2 and finished[0] == finished[1]
    assert set(captured) == set(finished[0]) == set(range(observed['frames']))
    assert all(captured[at]['actual'] == finished[0][at]['values'][4] for at in captured)
    assert controls['leading_windows'] > 0 and controls['trailing_windows'] > 0
    if sequence:
        assert len(captured) == len(sequence['frames'])
    saved = ([captured[at]['actual'] - sequence['frames'][at]['features'][4]
              for at in range(12, len(captured) - 12)] if sequence else None)
    independent_saved = ([captured[at]['portable'] - sequence['frames'][at]['features'][4]
                          for at in range(12, len(captured) - 12)] if sequence else None)
    saved_margin = (min(abs(captured[at]['actual'] - sequence['frames'][at]['features'][4])
                        - abs(captured[at]['portable'] - captured[at]['actual'])
                        for at in range(12, len(captured) - 12)) if sequence else None)
    return dict(case=sequence.get('category', args.sample) if sequence else f'{args.shape}_{args.frequency:g}',
                frames=len(captured), controls=controls, geometries=sorted(geometric),
                power_geometries=sorted(power_geometry), portable_power_error=metrics(power_errors),
                portable_power_max_relative_error=maximum_relative_error,
                portable_score_error=metrics(score_errors), portable_confidence_error=metrics(confidence_errors),
                portable_confidence_float32_exact=sum(error == 0. for error in confidence_errors),
                rounding_isolation=(dict(confidence_errors={name: metrics(errors) for name, errors in isolation_errors.items()},
                                         prepared_region_mismatches=region_differences, log_spectrum_mismatches=log_differences)
                                    if rounding_isolation else None),
                observed_matches_fresh_baseline=True,
                saved_confidence_comparison=metrics(saved) if saved is not None else None,
                portable_saved_confidence_comparison=metrics(independent_saved) if independent_saved is not None else None,
                minimum_saved_error_minus_portable_error=saved_margin,
                parser_sha256=parser_hash, ddi_sha256=ddi_hash)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--child', action='store_true')
    parser.add_argument('--sample', type=int, choices=range(5))
    parser.add_argument('--shape', choices=('steady', 'attack', 'gap', 'silence', 'dc', 'dc_tone', 'impulse', 'noise'), default='steady')
    parser.add_argument('--frequency', type=float, default=220.)
    args = parser.parse_args()
    assert hashlib.sha256((args.dll.parent / 'DSE_DFT.dll').read_bytes()).hexdigest() == FFT_RUNTIME_SHA256
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
    rows = []
    specifications = [dict(shape=shape, frequency=frequency) for shape in ('steady', 'attack')
                      for frequency in (110., 220., 440.)]
    specifications += [dict(shape=shape, frequency=220.) for shape in ('gap', 'silence', 'dc', 'dc_tone', 'impulse', 'noise')]
    specifications += [dict(sample=index) for index in range(5)]
    for specification in specifications:
        command = [sys.executable, '-B', str(Path(__file__).resolve()), '--child']
        for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb'):
            command += ['--' + name, str(getattr(args, name.replace('-', '_')))]
        for name, value in specification.items():
            command += ['--' + name, str(value)]
        process = subprocess.run(command, capture_output=True, text=True, timeout=120)
        assert process.returncode == 0, process.stderr[-3000:]
        row = json.loads(process.stdout)
        rows.append(row)
        print(json.dumps(dict(case=row['case'], frames=row['frames'], portable_confidence_max_error=row['portable_confidence_error']['max_absolute_error'])), flush=True)
    sources = ('verify_vqm_mode5_pcm.py', 'frm2_mode5_pcm.py', 'frm2_live_power.py',
               'frm2_mode5_scores.py', 'frm2_mode5_confidence.py', 'verify_frm2_analysis_windows.py',
               'frm2_raw_feature_observer.py', 'verify_vqm_mode5_scores.py', 'verify_vqm_mode5_confidence.py',
               'probe_frm2_batch_outputs.py', 'verify_vqm_alternate_score_paths.py',
               'verify_vqm_bit16_spectral_power.py', 'verify_vqm_bit16_peak_interpolation.py',
               'audit_frm2_numeric.py', 'audit_vqm_v4_module_leads.py', 'audit_vqm_feature_calls_cfg.py')
    report = dict(schema=1, dse_sha256=SHA256, fft_runtime_sha256=FFT_RUNTIME_SHA256,
                  auxiliary_sha256=hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest(),
                  source_sha256={name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
                  cases=rows, summary=dict(cases=len(rows), frames=sum(row['frames'] for row in rows),
                    controls={name: sum(row['controls'][name] for row in rows) for name in rows[0]['controls']},
                    portable_confidence_max_error=max(row['portable_confidence_error']['max_absolute_error'] for row in rows),
                    portable_confidence_within_1e_6=sum(row['portable_confidence_error']['within_1e_6'] for row in rows),
                    portable_score_max_error=max(row['portable_score_error']['max_absolute_error'] for row in rows),
                    portable_power_max_relative_error=max(row['portable_power_max_relative_error'] for row in rows)),
                  scope='Default mode5 PCM to power spectrum, prepared arrays, scores and confidence; shared FFT/log '
                        'backends are byte-exact comparison controls for power/sampled scores/confidence; prepared tail '
                        'is bracketed by its two known branches; default model uses independent Python FFT/log.',
                  limits=['Original maker confidence still differs; original input and authoring contract are unverified',
                          'Shared FFT/log controls reproduce current rounding but are not native-free computation',
                          'Portable numerical errors are reported separately and are not described as byte-exact',
                          'Native scheduling, frame index and source descriptor identities remain observation inputs',
                          'Only default 44.1 kHz, hop256, window1025/kind11/lookup0/factor4 geometry modeled',
                          'Native prepared-spectrum right-neighbour allocation contract remains as stated in441',
                          'Complete prepared-tail arrays are compared conditionally against the two endpoint branches; '
                          'no logical-outside neighbour is read and neither branch alters any ready score input',
                          'Fresh baseline covers returned metadata, six score scalars and four feature arrays, not all frame payloads',
                          'No commercial PCM, spectra, score arrays or proprietary code persisted'],
                  installed_files_unchanged=True, no_windows_temp=True)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__ == '__main__':
    main()
