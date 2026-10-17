"""Owned spectra check complete peak records, phase wrapping and math precision."""
from collections import Counter
import ctypes as c
from itertools import product
import math
import random
import struct

from audit_frm2_numeric import Native, P, I, F, D, Q, f32
from frm2_candidate_peak_source import candidate_peak_source
from frm2_peak_phase import SystemFloatAtan2, peak_phases
from frm2_mode5_math_backend import SystemFloatLog


def guarded(native, payload):
    data = native.alloc(len(payload)+64)
    c.memset(data, 0x5a, 32)
    c.memmove(data+32, payload, len(payload))
    c.memset(data+32+len(payload), 0xa5, 32)
    return data+32


def check_guard(pointer, size):
    assert c.string_at(pointer-32, 32) == b'\x5a'*32
    assert c.string_at(pointer+size, 32) == b'\xa5'*32


def owned_phase_checks(dll):
    native = Native(dll)
    atan2 = SystemFloatAtan2()
    config = native.alloc(0x35f0)
    native.fn(0x90de0, P, P)(config)
    context = native.alloc(0xc00)
    P.from_address(context+0xad0).value = config
    source, destination = native.alloc(24), native.alloc(16)
    output = guarded(native, bytes(30000))
    P.from_address(destination+8).value = output
    wrapper = native.fn(0xd04c0, Q, P, P, P, D, D, F)
    detector = native.fn(0x51e60, Q, P, P, I, P, P, F, I, F, D, F)
    rng = random.Random(457)
    fixtures = []
    for size, rate, logarithmic, time, shape in product(
            (16, 65, 257), (44100, 48000), (0, 1), (0., .013, -.013), range(5)):
        power = [f32(rng.uniform(1., 1000.)) for _ in range(size)]
        if shape == 0:
            power = [0.]*size
        elif shape == 1:
            power = [0., 100., 100., 0.]*(size//4)+[0.]*(size%4)
        elif shape == 2:
            power = [f32(1000.*math.exp(-((i-size*.4)/2.)**2)) for i in range(size)]
        elif shape == 3:
            power = [f32(10.+(i%5)*100.) for i in range(size)]
        fixtures.append((power, rate, logarithmic, time, 0., 'spectrum'))
    fixtures.extend((power, 44100, logarithmic, time, 0., 'boundary')
        for power, logarithmic, time in product(
            ([0., 1., 4., 1., 0.], [0., 4., 4., 0., 0., 0.],
             [0., 1., 1.0000001192092896, 1., 0., 0.],
             [0., 1., 4., 2., 5., 1., 0.], [0., 100.]*1604),
            (0, 1), (0., .013, -.013, .5, -.5)))
    fixtures.extend((power, 44100, logarithmic, time, 0., 'no_complex')
        for power, logarithmic, time in product(
            ([0., 1., 4., 1., 0.], [0., 4., 4., 0., 0., 0.]),
            (0, 1), (0., .013, -.013)))
    phases = (
        (complex(1., 0.), complex(1., -0.)),
        (complex(-1., 0.), complex(-1., -0.)),
        (complex(0., 1.), complex(0., -1.)),
        (complex(-1., 1e-7), complex(-1., -1e-7)),
        (complex(1., 0.), complex(-1., 0.)),
        (complex(1., -0.), complex(-1., -0.)),
        (complex(0., 0.), complex(-0., -0.)),
        (complex(1., 1.), complex(-1., -1.)),
    )
    totals, branches = Counter(), Counter()
    max_math_error = max_math_circular_error = 0.
    minimum_phase = math.inf
    maximum_phase = -math.inf
    for case, (power, rate, logarithmic, time, cutoff, kind) in enumerate(fixtures):
        pair = phases[case%len(phases)]
        bins = [pair[i%2] for i in range(len(power))]
        if case%3 == 1:
            bins = [complex(f32(math.sqrt(value)*math.cos((-1)**i*(3.05-i%4*.01))),
                f32(math.sqrt(value)*math.sin((-1)**i*(3.05-i%4*.01))))
                for i, value in enumerate(power)]
        if kind == 'no_complex':
            bins = None
        threshold_db = -70. if kind == 'spectrum' and case%7 == 0 else -300.
        if kind == 'spectrum' and case%11 == 0:
            cutoff = 4000.
        source_records = candidate_peak_source(power, rate, logarithmic, threshold_db, cutoff)
        expected = peak_phases(power, bins, source_records['records'], logarithmic, time, atan2)
        pure = peak_phases(power, bins, source_records['records'], logarithmic, time)
        power_bytes = struct.pack('<'+str(len(power))+'f', *power)
        complex_bytes = b''.join(struct.pack('<ff', value.real, value.imag) for value in (bins or []))
        power_pointer, complex_pointer = (guarded(native, payload)
            for payload in (power_bytes, complex_bytes))
        I.from_address(source).value = len(power)
        P.from_address(source+8).value = power_pointer
        P.from_address(source+16).value = complex_pointer if bins is not None else 0
        I.from_address(context+0xad8).value = rate
        I.from_address(config+0x520).value = logarithmic
        assert I.from_address(config+0x588).value == 0
        for at in (0., 1.):
            native.add(config+0x5d0+3*0xa0, at, threshold_db)
        expected_bytes = b''.join(struct.pack('<fffiI', *row) for row in expected['records'])
        assert wrapper(context, source, destination, .37, time, cutoff) == 0
        assert I.from_address(destination).value == len(expected['records']), (case, 'wrapper count')
        assert P.from_address(destination+8).value == output
        actual = c.string_at(output, len(expected_bytes))
        assert actual == expected_bytes, (case, 'wrapper record',
            next((i//20 for i, (a, b) in enumerate(zip(actual, expected_bytes)) if a != b), None))
        pointer, count = P(output), I()
        assert detector(complex_pointer if bins is not None else 0, power_pointer, len(power), c.byref(pointer), c.byref(count),
            rate, logarithmic, source_records['threshold'], time, cutoff) == 0
        assert count.value == len(expected['records']) and pointer.value == output
        assert c.string_at(output, len(expected_bytes)) == expected_bytes, (case, 'detector records')
        assert c.string_at(power_pointer, len(power_bytes)) == power_bytes
        assert c.string_at(complex_pointer, len(complex_bytes)) == complex_bytes
        check_guard(power_pointer, len(power_bytes))
        check_guard(complex_pointer, len(complex_bytes))
        check_guard(output, 30000)
        totals['records'] += len(expected['records'])
        totals['phase_nonzero'] += sum(row[2] != 0. for row in expected['records'])
        totals['empty_cases'] += not expected['records']
        totals['capacity_cases'] += len(expected['records']) == 1500
        branches.update(expected['branches'])
        for a, b in zip(expected['records'], pure['records']):
            minimum_phase = min(minimum_phase, a[2])
            maximum_phase = max(maximum_phase, a[2])
            delta = a[2]-b[2]
            max_math_error = max(max_math_error, abs(delta))
            max_math_circular_error = max(max_math_circular_error, abs(math.remainder(delta, math.tau)))
            totals['system_vs_double_atan2_different_phase_values'] += struct.pack('<f', a[2]) != struct.pack('<f', b[2])
    assert all(branches[key] > 0 for key in ('negative_shift', 'nonnegative_shift',
        'zero_denominator', 'unwrap_first', 'unwrap_second', 'time_correction',
        'positive_wrap', 'below_negative_pi', 'missing_complex'))
    assert totals['empty_cases'] and totals['capacity_cases'] == 10
    assert minimum_phase < -math.pi-.1 and maximum_phase <= math.pi
    return dict(cases=len(fixtures), native_calls=len(fixtures)*2,
        complete_record_exact_cases=len(fixtures)*2, values_per_record=5,
        branches=dict(sorted(branches.items())), **dict(totals),
        system_math_runtime=SystemFloatLog.metadata(atan2),
        system_vs_double_atan2_max_absolute_error=max_math_error,
        system_vs_double_atan2_max_circular_error=max_math_circular_error,
        minimum_phase=minimum_phase, maximum_phase=maximum_phase,
        all_owned_inputs_and_guards_unchanged=True, native_leaf_owned_inputs_only=True,
        phase_below_negative_pi_is_preserved=True,
        scope='Owned nonnegative spectra and independent complex bins; both scales/rates, '
            'phase correction signs, signed zeros, wrap decisions and1500-record capacity. '
            'No commercial frame enters these extra native calls.')
