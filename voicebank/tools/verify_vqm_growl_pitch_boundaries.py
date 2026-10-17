"""Bounded reference-frequency and transition-reversal checks with owned data.

No native instruction patching. Reuses the old fixture and independent nearest
layer model without changing their source or previous evidence.
"""
from __future__ import annotations

import argparse
import ctypes as c
from dataclasses import dataclass
import hashlib
import json
import math
from pathlib import Path
import struct

from audit_frm2_numeric import D, F, I, Native, P, Q, SHA256, f32
import probe_vqm_growl_frequency_mix as fixture
from verify_vqm_growl_pitch_selection import choose

INVALID_PITCH = f32(-3.4028234663852886e38)
MISSING_AMPLITUDE = 10000.


def bits(value):
    return struct.pack('<f', value).hex()


def reference_model(frequencies, amplitudes, input_hz, stored_cent=1200.):
    fs = list(map(f32, frequencies))
    amps = list(map(f32, amplitudes))
    fs[0] = f32(input_hz)
    if amps[0] == MISSING_AMPLITUDE:
        amps[0] = -200.
    included, total = [], 0.
    if fs[0] > 0:
        for k in range(min(len(fs), 15)):
            if fs[k] > 0 or amps[k] != MISSING_AMPLITUDE:
                included.append(k)
                total = f32(total + f32(float(fs[k]) / (k + 1.)))
        mean = f32(total / len(included))
        cent = (f32(math.log(mean / 440.) * 1200. / math.log(2.))
                if mean > 0 else INVALID_PITCH)
    else:
        mean, cent = None, f32(stored_cent)
    hz = f32(440. * math.pow(2., cent / 1200.)) if cent != INVALID_PITCH else 0.
    result = [f32((k + 1.) * hz) if value > 0 else value for k, value in enumerate(fs)]
    return dict(mean_hz=mean, cent=cent, reconstructed_hz=hz,
                frequencies=result, amplitudes=amps, included_indices=included)


@dataclass
class Transition:
    previous: int = -1
    counter: int = 0
    a: int | None = None
    b: int | None = None

    def advance(self, candidate):
        # Derived from the wrapper's branches, not fitted to observed output.
        weight, branch, swapped = 0., 'same_previous', False
        if self.previous == -1:
            self.a, self.previous, branch = candidate, candidate, 'initialize'
        elif candidate != self.previous:
            if self.counter >= 4:
                self.counter, self.previous, self.a, branch = 0, candidate, candidate, 'commit'
            else:
                branch = 'transition'
                if self.counter == 0:
                    self.counter, self.a, self.b, swapped = 1, candidate, self.a, True
                # The event callee writes State+0x5C from its index argument on
                # every call. It retargets A even when no state swap occurs.
                self.a, self.b = candidate, self.previous
                weight = f32((4 - self.counter) * .25)
                self.counter += 1
        else:
            self.a = candidate
        return dict(previous=self.previous, counter=self.counter, weight=weight), branch, swapped


def reference_cases():
    cases = []
    for count in (1, 15, 16):
        fs = [440. * (k + 1) for k in range(count)]
        cases.append((f'harmonic_{count}', fs, [-20.] * count, 440.))
        fs = list(fs)
        fs[min(count - 1, 14)] *= 2.
        if count == 16:
            fs[15] = 64000.
        cases.append((f'detuned_last_included_{count}', fs, [-20.] * count,
                      fs[0] if count == 1 else 440.))
    cases.extend([
        ('ignored_16th_tail_a', [440.*(k+1) for k in range(15)] + [16000.], [-20.]*16, 440.),
        ('ignored_16th_tail_b', [440.*(k+1) for k in range(15)] + [64000.], [-20.]*16, 440.),
        ('zero_missing_excluded', [440., 0., 1320.], [-20., 10000., -20.], 440.),
        ('zero_present_included', [440., 0., 1320.], [-20., -20., -20.], 440.),
        ('negative_missing_excluded', [440., -440., 1320.], [-20., 10000., -20.], 440.),
        ('negative_present_included', [440., -440., 1320.], [-20., -20., -20.], 440.),
        ('positive_missing_included', [440., 1760., 1320.], [-20., 10000., -20.], 440.),
        ('first_missing_reset', [440.], [10000.], 440.),
        ('zero_first_keeps_stored_pitch', [440., 880., 1320.], [-20.]*3, 0.),
        ('negative_first_keeps_stored_pitch', [440., 880., 1320.], [-20.]*3, -220.),
        ('zero_computed_reference', [440., -880.], [-20.]*2, 440.),
        ('negative_computed_reference', [440., -1760.], [-20.]*2, 440.),
    ])
    for count in (15, 16):
        fs = [440.*(k+1) for k in range(count)]
        amps = [-20.]*count
        for k in (1, 7, 14):
            fs[k], amps[k] = 0., 10000.
        fs[3] = -440.
        if count == 16:
            fs[15], amps[15] = -64000., -20.
        cases.append((f'sparse_indices_{count}', fs, amps, 440.))
    return cases


def reversal_cases():
    cases = []
    for old, new, direction in ((220., 440., 'up'), (660., 440., 'down')):
        for pause_count in (2, 3, 4):
            # Pause twice on the old layer at the requested counter, then resume.
            sequence = [old] + [new]*(pause_count-1) + [old, old] + [new]*(5-pause_count)
            cases.append((f'{direction}_return_old_at_{pause_count}', sequence))
    cases.extend([
        ('middle_up_then_opposite', [440., 660., 220., 220., 220.]),
        ('middle_down_then_opposite', [440., 220., 660., 660., 660.]),
    ])
    return cases


class BoundaryNative(Native):
    reference_rows = []
    reversal_rows = []

    def guarded(self, values):
        base = self.alloc(64 + len(values)*4)
        c.memset(base, 0xA5, 32)
        c.memset(base + 32 + len(values)*4, 0x5A, 32)
        for k, value in enumerate(values):
            F.from_address(base+32+k*4).value = value
        return base+32, base, len(values)

    @staticmethod
    def check_guard(array):
        pointer, base, count = array
        assert c.string_at(base, 32) == bytes([0xA5])*32
        assert c.string_at(pointer+count*4, 32) == bytes([0x5A])*32

    def fn(self, rva, result, *types):
        original = super().fn(rva, result, *types)
        if rva != 0x158E40:
            return original

        def invoke(growl, working, hz, arg4, mode, output, gain, arg8):
            assert F.from_address(self.lib._handle + 0x5BFDCC).value == MISSING_AMPLITUDE
            descriptor = self.alloc(0x80)
            c.memmove(descriptor, P.from_address(growl+8).value, 0x40)
            P.from_address(growl+8).value = descriptor
            original(growl, working, hz, arg4, mode, output, gain, arg8)
            record = P.from_address(descriptor+0x28).value
            constructor = super(BoundaryNative, self).fn(0x155E70, P, P, I, D, F,
                c.c_ubyte, I, c.c_ubyte, c.c_ubyte, c.c_ubyte)
            pitches = [220., 440., 660.]
            data = self.alloc(3*0x850)
            for k, pitch in enumerate(pitches):
                c.memmove(data+k*0x850, record, 0x850)
                F.from_address(data+k*0x850+8).value = pitch
            P.from_address(descriptor+0x28).value = data
            P.from_address(descriptor+0x30).value = data+3*0x850

            def construct():
                obj = self.alloc(0x120)
                assert constructor(obj, 1024, .01, 22050., 0, 1, 0, 0, 0) == obj
                P.from_address(obj+8).value = descriptor
                return obj

            def prepare(fs, amps):
                arrays = [self.guarded(values) for values in (fs, amps, [0.]*len(fs))]
                for offset, array in zip((0x160, 0x168, 0x170), arrays):
                    P.from_address(working+offset).value = array[0]
                I.from_address(working+0x158).value = len(fs)
                Q.from_address(working+0x150).value |= 3
                F.from_address(working+0x1D0).value = 1200.
                return arrays

            for name, fs, amps, input_hz in reference_cases():
                expected = reference_model(fs, amps, input_hz)
                arrays, obj = prepare(fs, amps), construct()
                original(obj, working, input_hz, arg4, 1, output, 1., arg8)
                actual_fs = [F.from_address(arrays[0][0]+k*4).value for k in range(len(fs))]
                actual_amps = [F.from_address(arrays[1][0]+k*4).value for k in range(len(fs))]
                actual_cent = F.from_address(working+0x1D0).value
                assert bits(actual_cent) == bits(expected['cent']), (name, 'cent', actual_cent, expected)
                assert list(map(bits, actual_fs)) == list(map(bits, expected['frequencies'])), (name, 'frequency', actual_fs, expected)
                assert list(map(bits, actual_amps)) == list(map(bits, expected['amplitudes'])), (name, 'amplitude', actual_amps, expected)
                candidate, _ = choose(pitches, expected['frequencies'][0], -1, 1, False)
                assert I.from_address(obj+0x60).value == candidate, (name, 'selection')
                for array in arrays:
                    self.check_guard(array)
                assert c.string_at(arrays[2][0], len(fs)*4) == bytes(len(fs)*4)
                self.reference_rows.append(dict(case=name, count=len(fs), input_hz=input_hz,
                    input_frequencies=fs, input_amplitudes=amps, independently_expected=expected,
                    actual_cent=actual_cent, actual_frequencies=actual_fs, actual_amplitudes=actual_amps,
                    selected_index=candidate, all_float32_bits_and_guards_exact=True))

            for name, sequence in reversal_cases():
                obj, model = construct(), Transition()
                for step, input_hz in enumerate(sequence):
                    arrays = prepare([input_hz, 2.*input_hz], [-20., -30.])
                    ref = reference_model([input_hz, 2.*input_hz], [-20., -30.], input_hz)
                    candidate, _ = choose(pitches, ref['frequencies'][0], model.previous, 2, False)
                    expected, branch, swapped = model.advance(candidate)
                    before_states = [P.from_address(obj+off).value for off in (0x20, 0x28)]
                    original(obj, working, input_hz, arg4, 1 if step == 0 else 2, output, 1., arg8)
                    actual = dict(previous=I.from_address(obj+0x60).value,
                                  counter=I.from_address(obj+0x34).value,
                                  weight=F.from_address(obj+0x38).value)
                    assert actual == expected, (name, step, actual, expected)
                    after_states = [P.from_address(obj+off).value for off in (0x20, 0x28)]
                    assert after_states == (before_states[::-1] if swapped else before_states), (name, step, 'state_swap')
                    indices = [I.from_address(state+0x5C).value for state in after_states]
                    for index, predicted in zip(indices, (model.a, model.b)):
                        if predicted is not None:
                            assert index == predicted, (name, step, 'state_index', indices, model)
                    for array in arrays:
                        self.check_guard(array)
                    assert bits(F.from_address(working+0x1D0).value) == bits(ref['cent'])
                    self.reversal_rows.append(dict(case=name, step=step, input_hz=input_hz,
                        candidate=candidate, branch=branch, swapped=swapped,
                        state_a_index=indices[0], state_b_index=indices[1],
                        independently_expected=expected, actual=actual,
                        all_state_and_pitch_checks_exact=True))
        return invoke


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--scratch', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.scratch.mkdir(parents=True, exist_ok=True)
    fixture.Native = BoundaryNative
    fixture.run(args.dll, args.scratch, True, False, 1., .1)
    result = dict(dse_sha256=SHA256, wrapper_rva='0x158e40',
        missing_amplitude_constant_rva='0x5bfdcc',
        script_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        fixture_sha256=hashlib.sha256(Path(fixture.__file__).read_bytes()).hexdigest(),
        nearest_model_sha256=hashlib.sha256(Path(__file__).with_name('verify_vqm_growl_pitch_selection.py').read_bytes()).hexdigest(),
        reference_cases=BoundaryNative.reference_rows, reversal_steps=BoundaryNative.reversal_rows,
        summary=dict(reference_cases=len(BoundaryNative.reference_rows),
            reversal_trajectories=len(reversal_cases()), reversal_calls=len(BoundaryNative.reversal_rows),
            additional_fixture_baseline_calls=1, all_checks_passed=True),
        limits=['Only owned valid arrays with counts 1/2/3/15/16 and flags bit0/bit1 set.',
                'No NaN/Inf input, zero-count or null-pointer contract is claimed.',
                'Tests wrapper reference preparation and state transitions, not audio crossfade quality.',
                'Material audio is shared across records; state indices do not alone prove which PCM was rendered.',
                'No new maker metadata or bit17 production inference.'])
    args.output.write_text(json.dumps(result, indent=2, allow_nan=False)+'\n', encoding='utf-8')
    print(json.dumps(result['summary']))


if __name__ == '__main__':
    main()
