"""Owned native fixtures for ordinary candidate history repair and state.

Only generated scalar history records and descriptors are passed to native
leaves. Evidence consists of counts; no history payload is persisted.
"""
from collections import Counter
import ctypes as c
import random
import struct

from audit_frm2_numeric import Native,P,Q,I,F,D,f32
from frm2_candidate_history_state import repair_history,update_reference


def owned_history_checks(dll):
    native = Native(dll)
    repair = native.fn(0xe8c90,Q,P,P,I)
    rng = random.Random(450)
    fixtures = []
    for case in range(320):
        history = [(f32(rng.choice((0.,110.,220.,330.,440.,660.))*(.99+.02*rng.random())),
                    f32(rng.random()*10.)) for i in range(20)]
        fixtures.append((history,rng.choice((-5,-3,-2,-1,0,1,3,4,5,10)),
                         rng.choice((0.,220.)),f32(rng.choice((0.,.05,.1,.2)))))
    for count,prior,variation,last4 in (
        (-3,0.,0.,(0.,0.,220.,0.)),
        (0,220.,0.,(220.,220.,440.,220.)),
        (4,0.,0.,(220.,220.,440.,220.)),
        (0,0.,0.,(220.,220.,0.,220.))):
        history = [(0.,0.)]*16+[(hz,1.) for hz in last4]
        fixtures.append((history,count,prior,variation))
    events = Counter()
    record_comparisons = 0
    for case,(history,count,prior,variation) in enumerate(fixtures):
        obj = native.alloc(0x7c0)
        I.from_address(obj+0x710).value = count
        F.from_address(obj+0x714).value = prior
        F.from_address(obj+0x718).value = variation
        pointers = []
        for hz,cost in history:
            entry = native.alloc(0x20)
            c.memmove(entry,native.array(F,[1.,2.,3.,4.,5.,6.,hz,cost]),0x20)
            pointers.append(entry)
        queue = native.array(P,pointers)
        expected,reasons = repair_history(history,count,prior,variation)
        assert repair(obj,queue,20) == 0
        for i,(hz,cost) in enumerate(expected):
            assert struct.pack('<8f',1.,2.,3.,4.,5.,6.,hz,cost) == c.string_at(pointers[i],0x20),(case,i)
            record_comparisons += 1
        events.update(reasons)
    assert all(events[key] > 0 for key in ('clear_isolated_previous','repair_upward_spike',
                                         'repair_stable_spike','fill_single_gap'))
    dispatch = native.fn(0xe60a0,Q,P,P,P,D)
    state_cases = 320
    state_events = Counter()
    state_history_values = 0
    for case in range(state_cases):
        config = native.alloc(0x35f0)
        native.fn(0x90de0,P,P)(config)
        current = f32(rng.choice((0.,110.,220.,330.,440.))*(.99+.02*rng.random()))
        for key,value in ((20,0.),(13,current)):
            for time in (0.,1.):
                native.add(config+0x5d0+key*0xa0,time,value)
        obj = native.alloc(0x7c0)
        native.fn(0xe5ce0,P,P,P,F)(obj,config,44100.)
        assert I.from_address(obj+0x590).value == I.from_address(obj+0x784).value == 0
        assert I.from_address(obj+0x5bc).value == I.from_address(obj+0x5b8).value == 1
        history = [(f32(rng.choice((0.,110.,220.,330.,440.))*(.99+.02*rng.random())),0.) for i in range(20)]
        history[-1] = current,0.
        count = rng.choice((-5,-3,-1,0,1,3,4,5,10))
        I.from_address(obj+0x710).value = count
        queue = P.from_address(obj+0x5c0).value
        for i,(hz,cost) in enumerate(history):
            entry = P.from_address(queue+i*8).value
            F.from_address(entry+0x18).value = hz
            F.from_address(entry+0x1c).value = cost
        descriptors = []
        for i in range(2):
            descriptor,frame,feature = native.alloc(0x30),native.alloc(0x158),native.alloc(0x80)
            P.from_address(descriptor+0x28).value = frame
            P.from_address(frame+0x128).value = feature
            descriptors.append(descriptor)
        expected = update_reference(history,count)
        output = native.alloc(4)
        assert dispatch(obj,native.array(P,descriptors),output,0.) == 0
        assert I.from_address(obj+0x710).value == expected['voiced_count'],case
        assert struct.pack('<ff',expected['prior_pitch'],expected['variation']) == c.string_at(obj+0x714,8),case
        for i,(hz,cost) in enumerate(history[1:]):
            assert struct.pack('<f',hz) == c.string_at(P.from_address(queue+i*8).value+0x18,4),(case,i)
            state_history_values += 1
        state_events['count_caps'] += expected['capped']
        state_events['prior_extrapolations'] += expected['extrapolated']
    return dict(repair_cases=len(fixtures),repair_exact_cases=len(fixtures),
                complete_record_comparisons=record_comparisons,repair_events=dict(sorted(events.items())),
                state_cases=state_cases,state_exact_cases=state_cases,state_history_values=state_history_values,
                state_events=dict(sorted(state_events.items())),
                extended_repair_flag=0,reanalysis_flag=0,history_entries=20,delay=1,
                native_calls_on_owned_inputs_only=True)
