#!/usr/bin/env python3
"""Verify DRS phase-origin search and smoothing on owned harmonic inputs.

Search, smoothing, and automatic lock acquisition are independently modeled.
Commercial bank pre-roll and final bit37 generation remain separate work.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

from audit_frm2_numeric import Native, SHA256, F, I, P, Q, f32, stats
from audit_frm2_phase_history import difference, wrap


def search_origin(frequencies, phases, f0, previous):
    count = min(len(phases), max(8, min(50, int(2000 / f0 + .5))))
    valid = [i for i in range(count) if frequencies[i] > 0]
    if len(valid) < 3:
        return previous
    best_error, best = 100000.0, previous
    for i in range(80):
        candidate = f32(f32(i / 80) * math.tau - math.pi)
        shifted = candidate
        if shifted > phases[0] + math.pi:
            shifted -= math.tau
        elif shifted < phases[0] - math.pi:
            shifted += math.tau
        delay = ((shifted - phases[0]) / math.tau) / f0
        values = [f32(wrap((k + 1) * (f0 * math.tau) * delay + phases[k])) for k in valid]
        total = 0.0
        for a, b in zip(values, values[1:]):
            total = f32(total + abs(difference(b, a)))
        error = f32(total / (len(values) - 1))
        if error < best_error:
            best_error, best = error, candidate
    return best


def smooth_origin(candidate, previous, locked, continuous):
    delta = f32(difference(candidate, previous))
    if not continuous:
        return f32(previous + delta * .04000002145767212) if locked else candidate
    distance = abs(delta)
    weight = .5
    if distance > .15 * math.pi:
        weight = f32(.95) if distance > .4 * math.pi else f32((distance - .15 * math.pi) * .45 / math.pi * 4 + .5)
    if locked:
        weight = max(weight, f32(.96))
    fraction = f32(1 - weight)
    step = f32(fraction * distance)
    cap = .025 if locked else .1
    if step > cap:
        fraction = f32(cap / step * fraction)
    return f32(f32(delta * fraction) + previous)


def update_lock(history, f0_history, processed, locked):
    """Sticky lock; history already includes this frame's search candidate."""
    if locked or processed <= 10:
        return locked
    checked = min(len(f0_history) - 2, 10)
    if any(f0_history[-2 - i] <= 0 for i in range(checked)):
        return False
    if len(history) < 10:
        raise ValueError('Native lock acquisition requires ten candidate phases')
    values = list(reversed(history[-10:]))
    order = [9, 0, *range(1, 9)]
    best_mean, best_max = 10000., 0.
    for cut_index in range(6):
        cut = f32(cut_index * math.tau / 6 - math.pi)
        lifted = [f32(v + math.tau) if v < cut else v for v in values]
        total = 0.
        for index in order:
            total = f32(total + lifted[index])
        mean = f32(total / 10)
        if mean >= math.pi:
            mean = f32(mean - math.tau)
        deviations = [f32(abs(difference(v, mean))) for v in values]
        total = 0.
        for index in order:
            total = f32(total + deviations[index])
        average = f32(total / 10)
        if average < best_mean:
            best_mean, best_max = average, max(deviations)
    return best_mean <= .2 and best_max < .35


def verify_lock(native):
    fn = native.fn(0xda730, F, P, F, F, P)
    frequencies = [f32(220 * k) for k in range(1, 13)]
    phases = [f32(math.sin(k) * 3) for k in range(12)]
    candidate = search_origin(frequencies, phases, 220., 0.)
    cases, output_errors, locked_count = 0, [], 0
    for spread in (0., .1, .199999, .2, .200001, .34, .35, .36, 1., 3.):
        for shape in ('alternating', 'single_outlier', 'ramp'):
            history = [f32(math.remainder(candidate + (spread * (-1) ** i if shape == 'alternating'
                            else spread if shape == 'single_outlier' and i == 13
                            else 0 if shape == 'single_outlier' else spread * (i - 7) / 7), math.tau))
                       for i in range(15)]
            for processed in (10, 11, 30):
                for zero_at in (None, -1, -2, -11, -12):
                    for was_locked in (False, True):
                        context = native.alloc(4096)
                        I.from_address(context + 0xa80).value = len(history)
                        hp = native.array(F, history)
                        P.from_address(context + 0xa88).value = hp
                        f0_history = [220.] * 32
                        if zero_at is not None:
                            f0_history[zero_at] = 0.
                        I.from_address(context + 0x428).value = len(f0_history)
                        P.from_address(context + 0x420).value = native.array(F, f0_history)
                        I.from_address(context + 0xa90).value = processed
                        I.from_address(context + 0xa94).value = int(was_locked)
                        frame = native.frame(frequencies, [-20.] * 12, 220., (0, 0, 0), [], [])
                        Q.from_address(frame + 0x50).value = 7
                        P.from_address(frame + 0x70).value = native.array(F, phases)
                        actual = fn(context, 0., 220., frame)
                        expected_lock = update_lock(history[1:] + [candidate], f0_history, processed, was_locked)
                        actual_lock = I.from_address(context + 0xa94).value
                        if actual_lock != expected_lock:
                            raise AssertionError(f'Lock mismatch: {spread}, {shape}, {processed}, {zero_at}, {was_locked}')
                        expected = smooth_origin(candidate, 0., expected_lock,
                                                 processed > 1 and f0_history[-3] > 0 and f0_history[-4] > 0)
                        output_errors.append(abs(actual - expected))
                        cases += 1
                        locked_count += actual_lock
                        native.keep.clear()
    if max(output_errors) >= 1e-6:
        raise AssertionError('Native origin output after automatic lock differs')
    return dict(cases=cases, locked_cases=locked_count, returned_phase=stats(output_errors))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    native = Native(args.dll)
    origin = native.fn(0xda730, F, P, F, F, P)
    search_errors, output_errors, cases = [], [], []
    for f0 in (80., 220., 440., 1000.):
        for count in (4, 12, 55):
            for shape in ('sinusoidal', 'linear', 'alternating'):
                frequencies = [f32(k * f0) for k in range(1, count + 1)]
                phases = [f32(math.sin(k) * 3 if shape == 'sinusoidal' else
                              math.remainder(k * .381 + .6, math.tau) if shape == 'linear' else
                              (-1) ** k * (3 - k * .01)) for k in range(count)]
                for previous in (-2., 0., 2.):
                    expected_search = search_origin(frequencies, phases, f0, previous)
                    for locked, continuous in ((False, False), (False, True), (True, False), (True, True)):
                        context = native.alloc(4096)
                        I.from_address(context + 0xa80).value = 32
                        history = native.array(F, [previous] * 32)
                        P.from_address(context + 0xa88).value = history
                        I.from_address(context + 0x428).value = 32
                        P.from_address(context + 0x420).value = native.array(F, [f0] * 32)
                        I.from_address(context + 0xa90).value = 2 if continuous else 0
                        I.from_address(context + 0xa94).value = int(locked)
                        frame = native.frame(frequencies, [-20.] * count, f0, (0, 0, 0), [], [])
                        Q.from_address(frame + 0x50).value = 7
                        P.from_address(frame + 0x70).value = native.array(F, phases)
                        actual = origin(context, previous, f0, frame)
                        actual_search = F.from_address(history + 31 * 4).value
                        expected = smooth_origin(expected_search, previous, locked, continuous)
                        # Native amplitude gate: the first harmonic exceeds every
                        # other valid harmonic among slots 2..10 up to 1600 Hz by
                        # more than 20 dB. An empty comparison range also gates.
                        other_max = max((-20. for hz in frequencies[1:10] if 0 < hz <= 1600), default=-10000.)
                        if f32(-20. - 20.) > other_max:
                            expected = previous
                        se, oe = abs(actual_search - expected_search), abs(actual - expected)
                        search_errors.append(se)
                        output_errors.append(oe)
                        if se >= 1e-6 or oe >= 1e-6:
                            cases.append(dict(f0=f0, count=count, shape=shape, previous=previous,
                                              locked=locked, continuous=continuous,
                                              search_error=se, output_error=oe))
                        native.keep.clear()
    lock = verify_lock(native)
    report = dict(dll_sha256=SHA256, search=stats(search_errors), smoothing=stats(output_errors),
                  automatic_lock=lock, failed_cases=cases,
                  scope='balanced amplitudes; search, smoothing and automatic lock acquisition')
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: report[key] for key in ('search', 'smoothing', 'automatic_lock')}, indent=2))
    if cases:
        raise AssertionError(f'{len(cases)} origin cases failed; inspect the numeric report')


if __name__ == '__main__':
    main()
