"""Owned retained-event fixtures for the period leaf's branch boundaries."""
import ctypes as c
from collections import Counter
import struct

from audit_frm2_numeric import Native,P,I,F,D,Q,f32
from frm2_candidate_geometry import controls
from frm2_candidate_pcm_source import RING_SIZE
from frm2_time_domain_period import TimeDomainPeriod


def owned_period_checks(dll):
    native = Native(dll)
    leaf = native.fn(0xe1930,Q,P,P,I)
    events = Counter()
    cases = state_values = 0
    guard = bytes([0xa5])*32
    profiles = {
        'default':{},'manual':{20:0.},'skip_feature':{23:0.},'skip_spectral':{28:0.}}
    sources = {
        'normal':([(220.,-20.,3,20),(6000.,-100.,3,500)],(.01,0.,.001,.1),0.),
        'empty':([], (.01,0.,.001,.1),0.),
        'crossing':([(220.,-20.,3,20)],(.5,0.,.001,.1),0.),
        'energy_pair':([(220.,-20.,3,20)],(.2,0.,4e-6,.1),0.),
        'energy':([(220.,-20.,3,20)],(.01,0.,1e-6,.1),0.),
        'spectral_ratio':([(220.,-20.,3,20),(6000.,-20.,3,500)],(.01,0.,.001,.6),0.),
        'spectral_pair':([(220.,-100.,3,20),(6000.,-20.,3,500)],(.01,0.,.001,.1),0.),
        'attack_reset':([(220.,-20.,3,20)],(.01,0.,.001,.1),4.)}
    seeds = ((0,200.,200.,0.),(4,200.,200.,0.),(12,200.,200.,0.),
             (12,100.,400.,0.),(12,200.,200.,.25))
    for profile,overrides in profiles.items():
        configured = controls(220.,overrides)
        config = native.alloc(0x35f0)
        native.fn(0x90de0,P,P)(config)
        for key,value in configured['curves'].items():
            for time in (0.,1.):
                native.add(config+0x5d0+key*0xa0,time,value)
        for name,(records,fields,derivative) in sources.items():
            source = dict(records=records,values=tuple(f32(value) for value in fields),derivative=f32(derivative))
            for count,previous,current,variation in seeds:
                for shift in (0,RING_SIZE-1050):
                    model = TimeDomainPeriod(configured)
                    model.cursor = (1000+shift)%RING_SIZE
                    model.last_peak = (950+shift)%RING_SIZE
                    model.periods[-2:] = [previous,current]
                    model.count = count
                    model.mean,model.variation = 220.,f32(variation)
                    model.level = model.held_level = 15000.
                    model.slope = -15000./.020
                    model.pitch = 220.
                    indices = [(i*100-1000+shift)%RING_SIZE for i in range(30)]
                    amplitudes = [10000. if i%2 == 0 else -10000. for i in range(30)]
                    obj_buffer = native.alloc(0x390+64)
                    obj = obj_buffer+32
                    c.memmove(obj_buffer,guard,32)
                    c.memmove(obj+0x390,guard,32)
                    P.from_address(obj).value = config
                    I.from_address(obj+0x28).value = RING_SIZE
                    I.from_address(obj+0x30).value = model.cursor
                    I.from_address(obj+0x34).value = 44100
                    D.from_address(obj+0x38).value = .25
                    D.from_address(obj+0x2d0).value = .020
                    F.from_address(obj+0x5c).value = model.pitch
                    for offset,payload in zip((0x2d8,0x2fc,0x374,0x384,0x48),model.state_bytes()[:5]):
                        c.memmove(obj+offset,payload,len(payload))
                    c.memmove(obj+0x6c,struct.pack('<30i',*indices),120)
                    c.memmove(obj+0x1d8,struct.pack('<30d',*amplitudes),240)
                    frame,feature,descriptor = (native.alloc(size) for size in (0x158,0x80,0x30))
                    P.from_address(frame+0x128).value = feature
                    for offset,value in zip((4,8,12,28),source['values']):
                        F.from_address(feature+offset).value = value
                    F.from_address(feature+40).value = derivative
                    I.from_address(descriptor+0x18).value = len(records)
                    payload = b''.join(struct.pack('<fffiI',hz,db,0.,span,bin_index) for hz,db,span,bin_index in records)
                    P.from_address(descriptor+0x20).value = native.array(c.c_ubyte,payload+bytes(20))
                    P.from_address(descriptor+0x28).value = frame
                    predicted = model.process((model.cursor+256)%RING_SIZE,indices,amplitudes,source,summarize=False)
                    assert leaf(obj,descriptor,256) == 0
                    assert struct.pack('<f',predicted) == c.string_at(obj+0x5c,4),(profile,name,count,shift,'pitch')
                    for k,(offset,want) in enumerate(zip((0x2d8,0x2fc,0x374,0x384,0x48),model.state_bytes()[:5])):
                        assert c.string_at(obj+offset,len(want)) == want,(profile,name,count,shift,'state',k)
                    assert c.string_at(obj+0x6c,120) == struct.pack('<30i',*indices)
                    assert c.string_at(obj+0x1d8,240) == struct.pack('<30d',*amplitudes)
                    assert c.string_at(obj_buffer,32) == c.string_at(obj+0x390,32) == guard
                    events.update(model.events)
                    cases += 1
                    state_values += 41
    assert all(events['period_weight_'+str(weight)] > 0 for weight in (.6,.1,.5))
    assert all(events['reset_'+reason] > 0 for reason in ('manual','no_peaks','crossing','energy_pair','energy','spectral_pair'))
    return dict(cases=cases,profiles=len(profiles),sources=len(sources),state_seeds=len(seeds),
        ring_starts=2,state_values_byte_exact=state_values,pitch_values_byte_exact=cases,
        event_input_values_unchanged=cases*60,guards_unchanged=True,
        events=dict(sorted(events.items())),owned_inputs_only=True,raw_leaf_before_caller_clamp_and_history=True)
