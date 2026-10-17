"""Separate score-array differences from pitch selection in saved VQM fields.

Observe fresh offline DRS instances, compare with an unobserved baseline, and
evaluate the reviewed default scoring formula on each live score array. Saved
F0 is only a comparison coordinate; it never enters the native analysis. PCM,
scores and frame payloads remain in memory. Each case uses a disposable process.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import subprocess
import sys
import traceback

from audit_frm2_numeric import B, D, F, I, P, Q, SHA256, f32
from frm2_pitch_candidate import candidate_with_history
from frm2_pitch_voicing import score_features
from probe_frm2_batch_outputs import run as analyze_batch
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences, metrics
from verify_vqm_estimator_producer_variants import geometry


FIELDS = {4: ('confidence', 0x14), 14: ('ratio', 0x20c),
          15: ('strength', 0x214)}
MODES = ('floor', 'nearest', 'ceil', 'nearest_local_peak')


def feature_values(frame):
    pointer = P.from_address(frame + 0x128).value
    if not pointer:
        raise AssertionError('Missing live feature object')
    return {name: F.from_address(pointer + offset).value
            for name, offset in FIELDS.values()}


def final_values(native, context, collection):
    track = P.from_address(context + 0xcd0).value
    regions = P.from_address(track + 0x148).value
    result = {}
    for i in range(I.from_address(track + 0x140).value):
        region = P.from_address(regions + 8 * i).value
        frames = P.from_address(region + 0x20).value
        for j in range(I.from_address(region + 0x18).value):
            frame = P.from_address(frames + 8 * j).value
            at = round(D.from_address(frame + 0x48).value * 44100 / 256)
            if at in result:
                raise AssertionError('Duplicate final timestamp')
            result[at] = feature_values(frame)
    return result


def child(args):
    parser_hash, ddi_hash, sequences = load_sequences(
        args.manifest, args.voice_root, args.ddb_tools)
    sequences += auxiliary_sequences(args.aux_vdb)
    sequence = sequences[args.sample]
    reference = sequence.get('pitch1', statistics.median(
        frame['f0'] for frame in sequence['frames']))
    captured = {}
    flux = {}
    flux_divisor = []
    contracts = []
    calls = 0

    def install(native, context):
        estimator = P.from_address(context + 0xc80).value
        contract = geometry(native, estimator)
        contracts.append(contract)
        divisor = F.from_address(native.lib._handle + 0x5bfc54).value
        if divisor != 19.:
            raise AssertionError('Invalid reviewed flux divisor')
        flux_divisor.append(divisor)
        if not P.from_address(context + 0xca8).value:
            raise AssertionError('Expected feature-array producer')
        if contract['candidate_mode'] or contract['voicing_mode']:
            raise AssertionError('Default scoring formula requires mode zero')
        queue_index = contract['queue_index']
        if queue_index != (0 if args.special_pcm else 5):
            raise AssertionError('Unreviewed estimator queue index')
        table = P.from_address(context).value
        original = c.CFUNCTYPE(Q, P)(P.from_address(table + 8).value)
        private = native.alloc(0x100)
        c.memmove(private, table, 0x100)

        def step(ctx):
            nonlocal calls
            try:
                calls += 1
                if calls > 10000:
                    raise AssertionError('Step count bound')
                queue = P.from_address(ctx + 0xc40).value
                raw_queue = P.from_address(ctx + 0xc20).value
                flux_frames = [P.from_address(
                    P.from_address(raw_queue + i * 8).value + 0x28).value
                    for i in (0, 1)]
                link = P.from_address(queue + queue_index * 8).value
                frame = P.from_address(link + 0x28).value
                before = Q.from_address(frame + 0x50).value
                history = [F.from_address(estimator + 0x50 + 4 * i).value
                           for i in range(11)]
                voiced_count = I.from_address(estimator + 0x40).value
                previous = 0.
                if queue_index:
                    prior = P.from_address(queue + (queue_index - 1) * 8).value
                    previous = F.from_address(
                        P.from_address(prior + 0x28).value + 0xf0).value
                result = original(ctx)
                if result & 0xffffffff == 0:
                    times = [D.from_address(identity + 0x48).value
                             for identity in flux_frames]
                    if times[0] >= 0 and times[1] >= 0:
                        at_flux = round(times[1] * 44100 / 256)
                        arrays = [P.from_address(identity + 0x128).value
                                  for identity in flux_frames]
                        if not all(arrays):
                            raise AssertionError('Missing adjacent feature arrays')
                        total = 0.
                        for offset in range(0x170, 0x1bc, 4):
                            delta = f32(F.from_address(arrays[1] + offset).value
                                        - F.from_address(arrays[0] + offset).value)
                            total = f32(total + f32(delta * delta))
                        prediction = f32(f32(math.sqrt(total)) / divisor)
                        actual = F.from_address(arrays[1] + 0x20c).value
                        if abs(prediction - actual) > 1e-6:
                            raise AssertionError(('Adjacent-array branch mismatch',
                                                  at_flux, prediction, actual))
                        if 0 <= at_flux < len(sequence['frames']):
                            flux[at_flux] = (prediction, actual)
                if not Q.from_address(frame + 0x50).value & 512:
                    return result
                if before & 512:
                    raise AssertionError('Estimator flag already set')
                time = D.from_address(frame + 0x48).value
                at = round(time * 44100 / 256)
                if time < 0 or not 0 <= at < len(sequence['frames']):
                    return result
                if abs(time * 44100 / 256 - at) > 1e-5 or at in captured:
                    raise AssertionError('Estimator timestamp mismatch')
                count = I.from_address(estimator + 0x2c).value
                source = P.from_address(estimator + 0x190).value
                factor = F.from_address(estimator + 0x140).value
                if not source or not 4 <= count <= 8192:
                    raise AssertionError('Prepared PCM geometry bound')
                nfft = 1 << int(f32(f32(f32((count - 1) * factor) + 1) - 2)).bit_length()
                pointer = P.from_address(estimator + 0x100).value
                nonzero = any(F.from_address(source + 4 * i).value
                              for i in range(count))
                if nonzero and not pointer:
                    raise AssertionError('Nonzero PCM without scores')
                scores = [F.from_address(pointer + 4 * i).value
                          for i in range(nfft // 2)] if nonzero else []
                if not all(math.isfinite(value) for value in scores):
                    raise AssertionError('Nonfinite score')
                nyquist = f32(D.from_address(estimator + 0x188).value * .5)
                captured[at] = dict(scores=scores, nyquist=nyquist, factor=factor,
                    history=history, voiced_count=voiced_count, previous=previous,
                    features=feature_values(frame))
                return result
            except BaseException:
                traceback.print_exc()
                sys.stderr.flush()
                os._exit(2)

        callback = c.CFUNCTYPE(Q, P)(step)
        P.from_address(private + 8).value = c.cast(callback, P).value
        P.from_address(context).value = private
        native.keep.extend([original, callback])

    finals = []
    def finish(native, context, collection):
        finals.append(final_values(native, context, collection))
    options = dict(analysis_mode=4, estimator_variant=0,
                   special_pcm=args.special_pcm, pcm_override=sequence['pcm'],
                   result_observer=finish)
    observed = analyze_batch(args.dll, 1, 1, 'steady', reference,
                             setup_observer=install, **options)
    baseline = analyze_batch(args.dll, 1, 1, 'steady', reference, **options)
    if observed != baseline or finals[0] != finals[1]:
        raise AssertionError('Score observer changed final metadata or features')
    if len(captured) != len(sequence['frames']):
        raise AssertionError(('Missing estimator frames', len(captured),
                              len(sequence['frames'])))
    replay = {str(bit): [] for bit in FIELDS}
    changed_after_estimator = {str(bit): 0 for bit in FIELDS}
    native_errors = {str(bit): [] for bit in FIELDS}
    final_errors = {str(bit): [] for bit in FIELDS}
    conditioned = {mode: {str(bit): [] for bit in FIELDS} for mode in MODES}
    arbitrary = {str(bit): [] for bit in FIELDS}
    outside_range = {str(bit): 0 for bit in FIELDS}
    joint_matches = 0
    skipped = 0
    indices_checked = 0
    selected_offsets = []
    score_geometry = set()
    for at, row in captured.items():
        for bit, (name, _) in FIELDS.items():
            changed_after_estimator[str(bit)] += row['features'][name] != finals[0][at][name]
        scores = row['scores']
        if not scores:
            skipped += 1
            continue
        nyquist = row['nyquist']
        candidate = candidate_with_history(scores, nyquist, row['history'],
            row['voiced_count'], contracts[0]['queue_index'], row['previous'])
        limit = candidate['normalization_bins']
        produced = score_features(scores, candidate['index'], nyquist, limit, row['factor'])
        for bit, (name, _) in FIELDS.items():
            replay[str(bit)].append(produced[name] - row['features'][name])
        score_geometry.add((len(scores), nyquist, limit))
        if at < 12 or at >= len(sequence['frames']) - 12:
            continue
        saved = sequence['frames'][at]
        coordinate = f32(f32(saved['f0'] / nyquist) * len(scores))
        selected_offsets.append(abs(candidate['refined_bin'] - coordinate))
        for bit, (name, _) in FIELDS.items():
            native_errors[str(bit)].append(row['features'][name] - saved['features'][bit])
            final_errors[str(bit)].append(finals[0][at][name] - saved['features'][bit])
        low = max(1, int(f32(f32(len(scores) * f32(40 / nyquist)) + 1e-8)))
        high = min(limit - 1, int(f32(f32(len(scores) * f32(1000 / nyquist)) + 1e-8)) + 1)
        # Superset of all reviewed 40..1000 Hz selections, including fallback 0.
        # No score/field target determines the candidate set or evaluation rule.
        all_indices = [0] + list(range(low, high + 1))
        alternatives = {i: score_features(scores, i, nyquist, limit, row['factor'])
                        for i in all_indices}
        indices_checked += len(alternatives)
        joint_matches += any(all(abs(values[name] - saved['features'][bit]) <= 1e-6
            for bit, (name, _) in FIELDS.items()) for values in alternatives.values())
        for bit, (name, _) in FIELDS.items():
            values = [row[name] for row in alternatives.values()]
            target = saved['features'][bit]
            arbitrary[str(bit)].append(min(abs(value - target) for value in values))
            outside_range[str(bit)] += target < min(values) - 1e-6 or target > max(values) + 1e-6
        local_peaks = [i for i in range(1, limit - 1)
                       if scores[i] > scores[i - 1] and scores[i] > scores[i + 1]]
        selected = dict(floor=math.floor(coordinate), nearest=math.floor(coordinate + .5),
                        ceil=math.ceil(coordinate))
        if not local_peaks:
            raise AssertionError('No local peak for saved-F0 comparison')
        selected['nearest_local_peak'] = min(local_peaks, key=lambda i: abs(i - coordinate))
        for mode, index in selected.items():
            if not 0 <= index < limit:
                raise AssertionError('Saved F0 coordinate outside reviewed scores')
            values = score_features(scores, index, nyquist, limit, row['factor'])
            for bit, (name, _) in FIELDS.items():
                conditioned[mode][str(bit)].append(values[name] - saved['features'][bit])
    if any(max(abs(error) for error in values) > 3e-6 for values in replay.values()):
        raise AssertionError(('Live default scoring replay failed',
                              {bit: metrics(values) for bit, values in replay.items()}))
    return dict(case=sequence.get('category', args.sample), special_pcm=args.special_pcm,
        frames=observed['frames'], calls=calls, contract=contracts[0],
        observer_matches_baseline=True, score_geometries=sorted(score_geometry),
        zero_pcm_frames_skipped=skipped,
        independent_formula_to_native={bit: metrics(errors) for bit, errors in replay.items()},
        later_overwrites=changed_after_estimator,
        adjacent_array_bit14=dict(divisor=flux_divisor[0],
            live_formula_to_native=metrics([a - b for a, b in flux.values()]),
            survives_to_final=sum(b == finals[0][at]['ratio'] for at, (a, b) in flux.items()),
            later_estimator_replacements=sum(b != finals[0][at]['ratio'] for at, (a, b) in flux.items())),
        native_to_saved={bit: metrics(errors) for bit, errors in native_errors.items()},
        final_to_saved={bit: metrics(errors) for bit, errors in final_errors.items()},
        saved_f0_score_bin_distance=metrics(selected_offsets),
        conditioned={mode: {bit: metrics(errors) for bit, errors in fields.items()}
                     for mode, fields in conditioned.items()},
        selection_superset=dict(evaluated_indices=indices_checked,
            simultaneous_three_field_matches=joint_matches,
            per_field_minimum_error={bit: metrics(errors) for bit, errors in arbitrary.items()},
            saved_values_outside_produced_range=outside_range),
        parser_sha256=parser_hash, ddi_sha256=ddi_hash)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--child', action='store_true')
    parser.add_argument('--sample', type=int, choices=range(5))
    parser.add_argument('--special-pcm', type=int, choices=(0, 1), default=0)
    args = parser.parse_args()
    if args.child:
        if args.sample is None:
            parser.error('--sample required with --child')
        print(json.dumps(child(args)))
        return
    if args.output is None:
        parser.error('--output required for matrix')
    rows = []
    for special in (0, 1):
        for sample in range(5):
            command = [sys.executable, '-B', str(Path(__file__).resolve())]
            for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb'):
                command += ['--' + name, str(getattr(args, name.replace('-', '_')))]
            command += ['--child', '--sample', str(sample), '--special-pcm', str(special)]
            process = subprocess.run(command, capture_output=True, text=True, timeout=120)
            if process.returncode:
                raise RuntimeError(f'sample={sample}, special={special}: {process.stderr[-2400:]}')
            row = json.loads(process.stdout)
            rows.append(row)
            print(json.dumps(dict(case=row['case'], special_pcm=special,
                frames=row['frames'], joint_matches=row['selection_superset'][
                    'simultaneous_three_field_matches'])), flush=True)
    report = dict(dse_sha256=SHA256,
        auxiliary_sha256=hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest(),
        source_sha256={name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
            for name in ('verify_vqm_score_conditioning.py', 'frm2_pitch_voicing.py',
                         'frm2_pitch_candidate.py', 'probe_frm2_batch_outputs.py')},
        cases=rows, analyzed_frames=sum(row['frames'] for row in rows),
        native_batches=2 * len(rows),
        scope='Live scores observed at the actual estimator frame, both reviewed '
              'generic mode4 PCM protocols, automatic pitch only. Scoring replay '
              'uses independently implemented arithmetic on native score arrays; '
              'this is not independent score production. Saved F0 is a read-only '
              'comparison coordinate, never injected. Final metadata and three '
              'fields equal an unobserved baseline. Saved-field comparisons '
              'exclude 12 frames per edge. The arbitrary-index check enumerates '
              'a superset of reviewed 40..1000 Hz selection and fallback0: failure '
              'excludes selection-only explanations for these arrays and formula, '
              'not other score producers, formulas, input histories or configurations.')
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
