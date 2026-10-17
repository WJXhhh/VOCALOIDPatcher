"""Owned spectral fixtures for detector fields consumed by candidate analysis."""
from collections import Counter
import ctypes as c
from itertools import product
import math
import random
import struct

from audit_frm2_numeric import Native, P, I, F, D, Q, f32
from frm2_candidate_peak_source import candidate_peak_source


def owned_peak_source_checks(dll):
    native = Native(dll)
    config = native.alloc(0x35f0)
    native.fn(0x90de0, P, P)(config)
    context = native.alloc(0xc00)
    P.from_address(context+0xad0).value = config
    source, destination = native.alloc(24), native.alloc(16)
    output = native.alloc(30000)
    P.from_address(destination+8).value = output
    wrapper = native.fn(0xd04c0, Q, P, P, P, D, D, F)
    detector = native.fn(0x51e60, Q, P, P, I, P, P, F, I, F, D, F)
    rng = random.Random(451)
    fixtures = []
    for size, rate, logarithmic, threshold_db, cutoff, shape in product(
            (16, 65, 257), (44100, 48000), (0, 1), (-300., -70.),
            (0., 4000.), range(8)):
        powers = [f32(100.+rng.random()*900.) for _ in range(size)]
        if shape == 0:
            powers = [0.]*size
        elif shape == 1:
            powers = [100.]*size
        elif shape == 2:
            powers = [f32(i*10.) for i in range(size)]
        elif shape == 3:
            powers = [f32(1000.*math.exp(-((i-size*.4)/2.)**2)) for i in range(size)]
        elif shape == 4:
            powers = ([0.,100.,100.,0.]*(size//4))+[0.]*(size%4)
        elif shape == 5:
            powers = [f32(10.+(i%5)*100.) for i in range(size)]
        elif shape == 6:
            powers = [f32(rng.uniform(0., 30.)) for _ in range(size)]
        fixtures.append((powers,rate,logarithmic,threshold_db,cutoff))
    # A final unresolved peak, plateau closure, trough closure and adjacent
    # maxima exercise span production separately from random spectral shapes.
    fixtures += [(powers,44100,0,-300.,0.) for powers in (
        [0.,1.,4.,1.,0.], [0.,4.,4.,0.,0.,0.],
        [0.,1.,9.,1.,0.,1.,4.,1.,0.], [0.,1.,4.,2.,5.,1.,0.],
        [1.,1.,1.,4.,1.,1.,1.,1.], [0.,1.,2.,3.,2.,1.,0.,0.])]
    fixtures.append(([0.,100.]*1604,44100,0,-300.,0.))
    totals = Counter()
    for case,(powers,rate,logarithmic,threshold_db,cutoff) in enumerate(fixtures):
        I.from_address(context+0xad8).value = rate
        I.from_address(config+0x520).value = logarithmic
        assert I.from_address(config+0x588).value == 0
        for at in (0.,1.):
            native.add(config+0x5d0+3*0xa0,at,threshold_db)
        power_pointer = native.array(F,powers)
        I.from_address(source).value = len(powers)
        P.from_address(source+8).value = power_pointer
        P.from_address(source+16).value = 0
        expected = candidate_peak_source(powers,rate,logarithmic,threshold_db,cutoff)
        records = expected['records']
        assert wrapper(context,source,destination,.5,.013,cutoff) == 0
        assert I.from_address(destination).value == len(records), (case,'wrapper count')
        assert P.from_address(destination+8).value == output
        for i,record in enumerate(records):
            assert struct.pack('<ffiI',*record) == c.string_at(output+i*20,8)+c.string_at(output+i*20+12,8), (case,'wrapper',i)
        pointer, count = P(output), I()
        assert detector(0,power_pointer,len(powers),c.byref(pointer),c.byref(count),
                        rate,logarithmic,expected['threshold'],.013,cutoff) == 0
        assert count.value == len(records) and pointer.value == output
        for i,record in enumerate(records):
            assert struct.pack('<ffiI',*record) == c.string_at(output+i*20,8)+c.string_at(output+i*20+12,8), (case,'detector',i)
        totals['records'] += len(records)
        totals['empty_cases'] += not records
        totals['sentinel_spans'] += sum(record[2] == -1 for record in records)
        totals['small_spans'] += sum(0 <= record[2] <= 2 for record in records)
        totals['large_spans'] += sum(record[2] > 2 for record in records)
        totals['capacity_cases'] += len(records) == 1500
    assert all(totals[key] > 0 for key in ('sentinel_spans','small_spans','large_spans','empty_cases'))
    assert totals['capacity_cases'] == 1
    return dict(cases=len(fixtures),wrapper_exact_cases=len(fixtures),detector_exact_cases=len(fixtures),
                **dict(totals),sample_rates=[44100,48000],interpolation_scales=[0,1],
                compared_offsets=['0x0','0x4','0xc','0x10'],phase_excluded=True,
                native_calls_on_owned_inputs_only=True)
