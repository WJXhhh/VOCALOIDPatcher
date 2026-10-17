"""Check Growl pitch selection and transition state using the existing owned fixture.

The adapter intercepts only Python function binding, never native instructions.
Every tested wrapper call executes the unmodified installed DLL.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path

from audit_frm2_numeric import D, F, I, Native, P, SHA256, f32
import probe_vqm_growl_frequency_mix as fixture


def frequency(hz):
    cent = f32(math.log(f32(hz) / 440.) * 1200. / math.log(2.))
    return f32(440. * math.pow(2., cent / 1200.))


def choose(pitches, hz, previous, mode, locked):
    previous = -1 if mode < 2 else previous
    candidate = min(range(len(pitches)), key=lambda k: abs(f32(hz - pitches[k])))
    if previous >= 0 and candidate == previous + 1:
        ratio = f32(f32(hz - pitches[previous]) / f32(pitches[candidate] - pitches[previous]))
        if ratio < .75:
            candidate = previous
    elif previous >= 0 and candidate == previous - 1:
        ratio = f32(f32(hz - pitches[candidate]) / f32(pitches[previous] - pitches[candidate]))
        if ratio > .25:
            candidate = previous
    if locked and previous != -1:
        candidate = previous
    return candidate, previous


class SelectionNative(Native):
    rows = []

    def fn(self, rva, result, *types):
        original = super().fn(rva, result, *types)
        if rva != 0x158E40:
            return original

        def invoke(growl, working, hz, arg4, mode, output, gain, arg8):
            # The inherited fixture supplies 0x40 bytes; this wrapper also reads
            # descriptor byte +0x40, so use an explicitly owned 0x80-byte object.
            descriptor = self.alloc(0x80)
            c.memmove(descriptor, P.from_address(growl + 8).value, 0x40)
            P.from_address(growl + 8).value = descriptor
            original(growl, working, hz, arg4, mode, output, gain, arg8)
            record = P.from_address(descriptor + 0x28).value
            constructor = super(SelectionNative, self).fn(
                0x155E70, P, P, I, D, F, c.c_ubyte, I,
                c.c_ubyte, c.c_ubyte, c.c_ubyte)
            cases = [
                ('unsorted_nearest', [440., 220., 330.], False, [(340., 1, 1.)]),
                ('exact_tie', [220., 660.], False, [(440., 1, 1.)]),
                ('duplicate_pitch', [440., 440., 660.], False, [(440., 1, 1.)]),
                ('inharmonic_reference', [220., 390., 440.], False, [(340., 1, 1.)]),
                ('ascending_history', [220., 440., 660.], False,
                 [(220., 1, 1.), (350., 2, 1.), (384., 2, 1.)] + [(385., 2, 1.)] * 4 + [(440., 2, 1.)]),
                ('descending_history', [220., 440., 660.], False,
                 [(440., 1, 1.), (300., 2, 1.), (276., 2, 1.)] + [(275., 2, 1.)] * 4 + [(220., 2, 1.)]),
                ('nonadjacent_index', [220., 660., 440.], False,
                 [(220., 1, 1.)] + [(440., 2, 1.)] * 4),
                ('mode_one_resets_history', [220., 440., 660.], False,
                 [(220., 1, 1.), (350., 1, 1.), (300., 1, 1.)]),
                ('descriptor_lock', [220., 440., 660.], True,
                 [(220., 1, 1.), (440., 2, 1.), (660., 2, 1.)]),
                ('gain_zero_reset', [220., 440., 660.], False,
                 [(440., 1, 1.), (660., 2, 0.), (220., 2, 1.)]),
            ]
            for name, pitches, locked, inputs in cases:
                data = self.alloc(len(pitches) * 0x850)
                for index, pitch in enumerate(pitches):
                    c.memmove(data + index * 0x850, record, 0x850)
                    F.from_address(data + index * 0x850 + 8).value = pitch
                desc = self.alloc(0x80)
                c.memmove(desc, descriptor, 0x40)
                P.from_address(desc + 0x28).value = data
                P.from_address(desc + 0x30).value = data + len(pitches) * 0x850
                c.c_ubyte.from_address(desc + 0x40).value = locked
                obj = self.alloc(0x120)
                assert constructor(obj, 1024, .01, 22050., 0, 1, 0, 0, 0) == obj
                P.from_address(obj + 8).value = desc
                previous, counter, a_index, b_index = -1, 0, None, None
                for step, (input_hz, input_mode, input_gain) in enumerate(inputs):
                    second_hz = 880. if name == 'inharmonic_reference' else f32(2. * input_hz)
                    frequencies = P.from_address(working + 0x160).value
                    F.from_address(frequencies + 4).value = second_hz
                    # The wrapper first overwrites slot zero, then averages
                    # frequency[k]/(k+1) before Hz-to-cent conversion.
                    reference = f32(f32(f32(input_hz) + f32(second_hz / 2.)) / 2.)
                    target = frequency(reference)
                    candidate, previous = choose(pitches, target, previous, input_mode, locked)
                    weight = 0.
                    if input_gain <= 0:
                        previous = -1
                    elif candidate == previous or previous == -1:
                        if input_mode < 2 or previous == -1:
                            a_index = candidate
                    elif counter < 4:
                        if counter == 0:
                            counter = 1
                            a_index, b_index = candidate, a_index
                        weight = f32((4 - counter) * .25)
                        counter += 1
                    else:
                        counter, previous = 0, candidate
                    if previous == -1 and input_gain > 0:
                        previous = candidate
                    original(obj, working, input_hz, arg4, input_mode, output, input_gain, arg8)
                    actual_target = F.from_address(P.from_address(working + 0x160).value).value
                    actual = dict(previous=I.from_address(obj + 0x60).value,
                                  counter=I.from_address(obj + 0x34).value,
                                  weight=F.from_address(obj + 0x38).value)
                    expected = dict(previous=previous, counter=counter, weight=weight)
                    assert actual_target == target, (name, step, 'Hz', actual_target, target)
                    assert actual == expected, (name, step, actual, expected)
                    for offset, index in ((0x20, a_index), (0x28, b_index)):
                        if index is not None:
                            state = P.from_address(obj + offset).value
                            assert I.from_address(state + 0x5C).value == index, (name, step, 'state', offset, index)
                    if input_gain == 0:
                        assert all(I.from_address(P.from_address(obj + off).value + 0x58).value == 0 for off in (0x20, 0x28))
                    self.rows.append(dict(case=name, step=step, pitches_hz=pitches,
                                          input_hz=input_hz, reconstructed_hz=target,
                                          second_component_hz=second_hz, reference_hz=reference,
                                          mode=input_mode, gain=input_gain, locked=locked,
                                          candidate_before_gain_gate=candidate, **actual,
                                          independent_model_exact=True))
        return invoke


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--scratch', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.scratch.mkdir(parents=True, exist_ok=True)
    # Binding adapter is confined to this disposable Python process.
    fixture.Native = SelectionNative
    fixture.run(args.dll, args.scratch, True, False, 1., .1)
    rows = SelectionNative.rows
    result = dict(dse_sha256=SHA256, wrapper_rva='0x158e40', hz_to_cent_rva='0x1589d0',
                  script_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                  fixture_sha256=hashlib.sha256(Path(fixture.__file__).read_bytes()).hexdigest(),
                  summary=dict(scenarios=len({r['case'] for r in rows}), verified_wrapper_calls=len(rows),
                               additional_fixture_baseline_calls=1,
                               all_frequency_selection_and_transition_checks_exact=True),
                  cases=rows,
                  limits=['Owned PCM/FRM2 reused; source audio is identical across metadata records.',
                          'Verifies metadata selection and state, not audio crossfade quality.',
                          'No pitch1 maker formula or pitch2 maker assignment is established.',
                          'Does not cover arbitrary history reversals during an active transition.'])
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result['summary']))


if __name__ == '__main__':
    main()
