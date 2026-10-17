"""Test the reviewed ordinary harmonic-origin algorithm against VQM bit17.

Saved harmonics are placed only in isolated numerical-function fixtures. This
does not make them a PCM-produced native frame. Installed payloads stay in
memory; reports contain hashes and aggregate errors. No fitted coefficients.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import statistics

from audit_frm2_numeric import F, I, Native, P, Q, SHA256, f32
from audit_frm2_phase_history import difference
from verify_frm2_phase_origin import search_origin, smooth_origin, update_lock
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences


COORDINATES = ('origin', 'relative_first_phase')
BRANCHES = ((False, False), (False, True), (True, False), (True, True))
AUX_SHA256 = 'a629b36e0b4d571c37f947809d34dcfa283a5af42f12d635cd5f75143ee6203c'


def metrics(errors):
    values = [abs(value) for value in errors]
    if not values:
        raise AssertionError('Empty comparison')
    return dict(compared=len(values), mean_absolute_error=statistics.fmean(values),
                max_absolute_error=max(values),
                within_1e_6=sum(value <= 1e-6 for value in values))


def circular(left, right):
    return math.remainder(left - right, math.tau)


def signed(value):
    return f32(math.remainder(value, math.tau))


def gate(frequencies, amplitudes):
    other = -10000.
    for hz, amp in zip(frequencies[1:10], amplitudes[1:10]):
        if hz > 1600:
            break
        if hz > 0:
            other = max(other, amp)
    return f32(amplitudes[0] - 20) > other


def output_coordinate(origin, first_phase, coordinate):
    return origin if coordinate == 'origin' else difference(origin, first_phase)


def run(args):
    parser_hash, ddi_hash, sequences = load_sequences(
        args.manifest, args.voice_root, args.ddb_tools, include_component_phases=True)
    aux_hash = hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest()
    if aux_hash != AUX_SHA256:
        raise ValueError('Auxiliary source differs from reviewed VQM.vdb')
    sequences += auxiliary_sequences(args.aux_vdb, include_component_phases=True)
    native = Native(args.dll)
    origin = native.fn(0xda730, F, P, F, F, P)
    calls = 0
    rows = []
    model_search_errors, model_output_errors = [], []
    model_lock_checks = 0
    for sequence_index, sequence in enumerate(sequences):
        frames = sequence['frames']
        count = len(frames[0]['components'])
        if not 3 <= count <= 1500:
            raise AssertionError('Unreviewed component count')
        ctx = native.alloc(0x1000)
        phase_history = native.array(F, [0.] * 32)
        f0_history_pointer = native.array(F, [0.] * 32)
        I.from_address(ctx + 0xa80).value = 32
        P.from_address(ctx + 0xa88).value = phase_history
        I.from_address(ctx + 0x428).value = 32
        P.from_address(ctx + 0x420).value = f0_history_pointer
        fixture = native.alloc(0x230)
        native.frame_ctor(fixture)
        Q.from_address(fixture + 0x50).value = 7
        I.from_address(fixture + 0x58).value = count
        frequency_pointer = native.array(F, [0.] * count)
        amplitude_pointer = native.array(F, [0.] * count)
        phase_pointer = native.array(F, [0.] * count)
        for offset, pointer in ((0x60, frequency_pointer), (0x68, amplitude_pointer),
                                (0x70, phase_pointer)):
            P.from_address(fixture + offset).value = pointer
        candidate_errors = {coordinate: [] for coordinate in COORDINATES}
        conditional = {coordinate: {branch: [] for branch in BRANCHES}
                       for coordinate in COORDINATES}
        attainable = {coordinate: [] for coordinate in COORDINATES}
        free_errors = {coordinate: [] for coordinate in COORDINATES}
        native_previous = 0.
        model_previous = 0.
        free_history, f0_history = [0.] * 32, [0.] * 32
        free_locked = False
        gated_count, capped_step_count = 0, 0
        first_frequency_errors = []
        search_valid = []
        harmonic_frequency_errors = []
        for index, frame in enumerate(frames):
            frequencies, amplitudes = map(tuple, zip(*frame['components']))
            phases = frame['component_phases']
            f0 = f32(frame['f0'])
            saved = frame['features'][17]
            if (len(frequencies) != count or len(phases) != count or f0 <= 0 or
                    frequencies[0] <= 0 or not all(math.isfinite(x) for x in
                    (*frequencies, *amplitudes, *phases, f0, saved))):
                raise AssertionError('Invalid harmonic fixture')
            limit = min(count, max(8, min(50, int(2000 / f0 + .5))))
            valid = sum(hz > 0 for hz in frequencies[:limit])
            if valid < 3:
                raise AssertionError('Origin search requires three valid slots')
            search_valid.append(valid)
            first_frequency_errors.append(abs(frequencies[0] / f0 - 1))
            harmonic_frequency_errors.extend(abs(hz / ((k + 1) * f0) - 1)
                for k, hz in enumerate(frequencies[:limit]) if hz > 0)
            for pointer, values in ((frequency_pointer, frequencies),
                                    (amplitude_pointer, amplitudes),
                                    (phase_pointer, phases)):
                source = (F * count)(*values)
                c.memmove(pointer, source, count * 4)
            gated = gate(frequencies, amplitudes)
            gated_count += gated
            candidate = search_origin(frequencies, phases, f0, 0.)
            for coordinate in COORDINATES:
                candidate_errors[coordinate].append(circular(
                    output_coordinate(candidate, phases[0], coordinate), saved))
            if index:
                previous_frame = frames[index - 1]
                observed_step = circular(saved, previous_frame['features'][17])
                capped_step_count += abs(observed_step) > .100001
                for coordinate in COORDINATES:
                    previous = signed(previous_frame['features'][17] + (
                        previous_frame['component_phases'][0]
                        if coordinate == 'relative_first_phase' else 0.))
                    local_errors = []
                    for locked, continuous in BRANCHES:
                        I.from_address(ctx + 0xa90).value = 2 if continuous else 0
                        I.from_address(ctx + 0xa94).value = int(locked)
                        c.memmove(phase_history, (F * 32)(*([previous] * 32)), 128)
                        c.memmove(f0_history_pointer, (F * 32)(*([f0] * 32)), 128)
                        actual = origin(ctx, previous, f0, fixture)
                        calls += 1
                        search = F.from_address(phase_history + 31 * 4).value
                        expected = previous if gated else smooth_origin(
                            candidate, previous, locked, continuous)
                        model_search_errors.append(circular(search, candidate))
                        model_output_errors.append(circular(actual, expected))
                        error = circular(output_coordinate(actual, phases[0], coordinate), saved)
                        conditional[coordinate][(locked, continuous)].append(error)
                        local_errors.append(abs(error))
                    attainable[coordinate].append(min(local_errors))
            # The complete ordinary-origin state runs from zero without saved
            # bit17 input. Restore its histories after the conditional cases.
            f0_history = f0_history[1:] + [f0]
            c.memmove(phase_history, (F * 32)(*free_history), 128)
            c.memmove(f0_history_pointer, (F * 32)(*f0_history), 128)
            I.from_address(ctx + 0xa90).value = index
            I.from_address(ctx + 0xa94).value = int(free_locked)
            free_candidate = search_origin(frequencies, phases, f0, model_previous)
            next_history = free_history[1:] + [free_candidate]
            expected_lock = update_lock(next_history, f0_history, index, free_locked)
            continuous = index > 1 and f0_history[-3] > 0 and f0_history[-4] > 0
            model_previous = model_previous if gated else smooth_origin(
                free_candidate, model_previous, expected_lock, continuous)
            actual = origin(ctx, native_previous, f0, fixture)
            calls += 1
            actual_search = F.from_address(phase_history + 31 * 4).value
            actual_locked = bool(I.from_address(ctx + 0xa94).value)
            if actual_locked != expected_lock:
                raise AssertionError('Free ordinary-origin lock differs from model')
            model_lock_checks += 1
            model_search_errors.append(circular(actual_search, free_candidate))
            model_output_errors.append(circular(actual, model_previous))
            native_previous = actual
            free_history, free_locked = next_history, expected_lock
            for coordinate in COORDINATES:
                free_errors[coordinate].append(circular(
                    output_coordinate(actual, phases[0], coordinate), saved))
        rows.append(dict(sample=sequence.get('category', sequence_index), frames=len(frames),
            low_slot_harmonic_ratio_max_error=max(harmonic_frequency_errors),
            first_frequency_f0_ratio_max_error=max(first_frequency_errors),
            search_valid_count_range=[min(search_valid), max(search_valid)],
            amplitude_gated_frames=gated_count,
            observed_adjacent_steps_above_native_continuous_cap=capped_step_count,
            unsmoothed_search_to_saved={key: metrics(value) for key, value in candidate_errors.items()},
            conditional_branches={coordinate: {
                f'locked{int(locked)}_continuous{int(continuous)}': metrics(errors)
                for (locked, continuous), errors in branches.items()}
                for coordinate, branches in conditional.items()},
            best_branch_per_saved_transition={key: metrics(value) for key, value in attainable.items()},
            free_from_zero={key: metrics(value) for key, value in free_errors.items()},
            final_free_lock=free_locked))
        native.keep.clear()
    model_search, model_output = metrics(model_search_errors), metrics(model_output_errors)
    if model_search['max_absolute_error'] > 1e-6 or model_output['max_absolute_error'] > 1e-6:
        raise AssertionError(('Native ordinary-origin model mismatch', model_search, model_output))
    names = ('verify_vqm_multiharmonic_origin.py', 'verify_frm2_phase_origin.py',
             'audit_frm2_phase_history.py', 'verify_vqm_bit16_spectral_power.py',
             'verify_vqm_bit16_peak_interpolation.py')
    return dict(dse_sha256=SHA256, aux_sha256=aux_hash, ddi_sha256=ddi_hash,
        parser_sha256=parser_hash, native_origin_rva='0xda730', native_calls=calls,
        saved_frames=sum(row['frames'] for row in rows),
        saved_transitions=sum(row['frames'] - 1 for row in rows),
        source_sha256={name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                       for name in names},
        independent_to_native_search=model_search, independent_to_native_output=model_output,
        free_lock_checks=model_lock_checks, samples=rows,
        scope='Exact reviewed ordinary multi-harmonic origin search and branch formulas; '
              'Saved harmonic fixtures, conditional prior state supplied by saved bit17 '
              'and both physical angle coordinates, plus one free state run per sequence '
              'from zero. Conditional branch selection is an attainable superset, not '
              'recovered production history. The isolated fixtures do not prove PCM '
              'production, original VQM authoring harmonics/configuration, other '
              'algorithms or all pre-/post-processing. No payload or PCM is persisted.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    report = run(args)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(native_calls=report['native_calls'],
        native_model_search=report['independent_to_native_search'],
        native_model_output=report['independent_to_native_output'],
        best_branch=[dict(sample=row['sample'], results=row['best_branch_per_saved_transition'])
                     for row in report['samples']]), indent=2))


if __name__ == '__main__':
    main()
