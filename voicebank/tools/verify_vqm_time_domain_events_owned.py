"""Owned signal/chunk/feedback fixtures for retained event production."""
from collections import Counter
import ctypes as c
import math
import random
import struct

from audit_frm2_numeric import Native,P,I,F,D,Q,f32
from frm2_candidate_pcm_source import RING_SIZE
from frm2_time_domain_events import TimeDomainEvents


def owned_event_checks(dll):
    rng = random.Random(456)
    fixtures = {
        'zero':[0.]*2048,
        'tone':[5000.*math.sin(math.tau*i/200.) for i in range(4096)],
        'harmonics':[5000.*sum(math.sin(math.tau*k*i/200.+k*.23)/k for k in range(1,6)) for i in range(4096)],
        'noise':[rng.uniform(-10000.,10000.) for _ in range(2048)],
        'plateau':([0.,100.,100.,100.,0.,-100.,-100.,-100.]*512),
        'near_plateau':([0.,100.,100.+1e-8,100.,0.,-100.,-100.-1e-8,-100.]*512),
        'clip':([0.,32768.,32768.,0.,-32768.,-32768.]*512),
        'tiny':([0.,.01,-.01]*1024),
        'impulses':[10000. if i%301 == 0 else -10000. if i%401 == 0 else 0. for i in range(4096)]}
    native = Native(dll)
    analyze = native.fn(0xdff30,Q,P,P,P,D,I,D)
    config = native.alloc(0x35f0)
    native.fn(0x90de0,P,P)(config)
    for key,value in ((12,40.),(13,220.),(14,1000.),(20,0.)):
        for time in (0.,1.):
            native.add(config+0x5d0+key*0xa0,time,value)
    guard = bytes([0xa5])*32
    cases = calls = event_values = marker_values = 0
    events = Counter()
    for name,values in fixtures.items():
        for feedback_period,feedback_count in ((0.,0),(200.,5),(800.,12)):
            for chunk in (128,257,1024):
                model = TimeDomainEvents()
                obj_buffer = native.alloc(0x390+64)
                obj = obj_buffer+32
                ring_buffer = native.alloc(RING_SIZE*8+64)
                marker_buffer = native.alloc(RING_SIZE*4+64)
                ring,markers = ring_buffer+32,marker_buffer+32
                for buffer,size in ((obj_buffer,0x390),(ring_buffer,RING_SIZE*8),(marker_buffer,RING_SIZE*4)):
                    c.memmove(buffer,guard,32)
                    c.memmove(buffer+32+size,guard,32)
                P.from_address(obj).value = config
                P.from_address(obj+0x18).value = markers
                P.from_address(obj+0x20).value = ring
                I.from_address(obj+0x28).value = RING_SIZE
                I.from_address(obj+0x34).value = 44100
                I.from_address(obj+0x68).value = 10
                P.from_address(obj+0x60).value = native.array(F,[0.]*10)
                D.from_address(obj+0x2d0).value = .020
                I.from_address(obj+0x374).value = -1
                F.from_address(obj+0x2d8).value = F.from_address(obj+0x2e0).value = F.from_address(obj+0x2f4).value = 1.
                frame,feature,descriptor = (native.alloc(size) for size in (0x158,0x80,0x30))
                P.from_address(frame+0x128).value = feature
                P.from_address(descriptor+0x28).value = frame
                queue = native.array(P,[descriptor])
                output = native.alloc(4)
                for start in range(0,len(values),chunk):
                    block = values[start:start+chunk]
                    model.push(block)
                    model.scan(feedback_period,feedback_count)
                    c.memmove(ring,struct.pack('<'+str(RING_SIZE)+'d',*model.ring),RING_SIZE*8)
                    F.from_address(obj+0x370).value = feedback_period
                    I.from_address(obj+0x384).value = feedback_count
                    time = model.total/44100.
                    assert analyze(obj,queue,output,time,model.total%RING_SIZE,time) == 0
                    for k,(offset,want) in enumerate(zip((0x6c,0xe8,0x1d8,0x2c8,0x2c),model.snapshot())):
                        got = c.string_at(obj+offset,len(want))
                        assert want == got,(name,feedback_period,feedback_count,chunk,start,'event',k,
                            next((j for j,(a,b) in enumerate(zip(want,got)) if a!=b),None))
                    assert model.marker_bytes() == c.string_at(markers,RING_SIZE*4),(name,feedback_period,chunk,start,'markers')
                    for buffer,size in ((obj_buffer,0x390),(ring_buffer,RING_SIZE*8),(marker_buffer,RING_SIZE*4)):
                        assert c.string_at(buffer,32) == c.string_at(buffer+32+size,32) == guard
                    calls += 1
                    event_values += 93
                    marker_values += RING_SIZE
                events.update(model.events)
                cases += 1
    seeded = seeded_event_checks(native,config,analyze,guard)
    assert events['peak_plateau_closed'] > 0 and events['trough_plateau_closed'] > 0
    return dict(cases=cases,calls=calls,event_state_values_byte_exact=event_values,
        marker_ring_values_byte_exact=marker_values,guards_unchanged=True,
        feedback_profiles=3,chunk_patterns=3,signal_types=len(fixtures),
        events=dict(sorted(events.items())),seeded_merge_checks=seeded,owned_inputs_only=True)


def seeded_event_checks(native,config,analyze,guard):
    definitions = {
        'merge_four':{24:1000.,25:100.,26:100.,27:100.,28:100.,29:500.},
        'merge_earlier':{22:10000.,24:1000.,26:10000.,28:1000.},
        'merge_last':{26:10000.,28:1000.},
        'merge_pair':{22:10000.,24:7000.,26:10000.,28:7000.}}
    combined = Counter()
    cases = 0
    for name,amplitudes in definitions.items():
        for feedback_period,feedback_count in ((0.,0),(200.,5),(100.,12)):
            for shift in (0,RING_SIZE-2800,RING_SIZE-3002,RING_SIZE-3000):
                model = TimeDomainEvents()
                model.total = 3000+shift
                model.cursor = model.total%RING_SIZE
                positions = [0]*30
                positions[21:] = [2200,2250,2300,2500,2600,2700,2800,2900,2950]
                if name == 'merge_earlier':
                    positions[22],positions[26] = 2400,2700
                model.indices = [(value+shift)%RING_SIZE for value in positions]
                for slot,value in amplitudes.items():
                    model.amplitudes[slot] = value
                model.absolute = [(-1000. if i%2 else 1000.) for i in range(30)]
                model.absolute[29] = 0.
                for index,value in zip(model.indices,model.amplitudes):
                    model.mark(index,value)
                obj_buffer = native.alloc(0x390+64)
                ring_buffer = native.alloc(RING_SIZE*8+64)
                marker_buffer = native.alloc(RING_SIZE*4+64)
                obj,ring,markers = obj_buffer+32,ring_buffer+32,marker_buffer+32
                for buffer,size in ((obj_buffer,0x390),(ring_buffer,RING_SIZE*8),(marker_buffer,RING_SIZE*4)):
                    c.memmove(buffer,guard,32)
                    c.memmove(buffer+32+size,guard,32)
                P.from_address(obj).value = config
                P.from_address(obj+0x18).value = markers
                P.from_address(obj+0x20).value = ring
                I.from_address(obj+0x28).value = RING_SIZE
                I.from_address(obj+0x34).value = 44100
                I.from_address(obj+0x68).value = 10
                P.from_address(obj+0x60).value = native.array(F,[0.]*10)
                D.from_address(obj+0x2d0).value = .020
                I.from_address(obj+0x374).value = -1
                F.from_address(obj+0x370).value = feedback_period
                I.from_address(obj+0x384).value = feedback_count
                for offset,payload in zip((0x6c,0xe8,0x1d8,0x2c8,0x2c),model.snapshot()):
                    c.memmove(obj+offset,payload,len(payload))
                c.memmove(markers,model.marker_bytes(),RING_SIZE*4)
                frame,feature,descriptor = (native.alloc(size) for size in (0x158,0x80,0x30))
                P.from_address(frame+0x128).value = feature
                P.from_address(descriptor+0x28).value = frame
                queue = native.array(P,[descriptor])
                output = native.alloc(4)
                model.push([20000.,0.,-100.])
                model.scan(feedback_period,feedback_count)
                c.memmove(ring,struct.pack('<'+str(RING_SIZE)+'d',*model.ring),RING_SIZE*8)
                time = model.total/44100.
                assert analyze(obj,queue,output,time,model.total%RING_SIZE,time) == 0
                for k,(offset,want) in enumerate(zip((0x6c,0xe8,0x1d8,0x2c8,0x2c),model.snapshot())):
                    assert c.string_at(obj+offset,len(want)) == want,(name,feedback_period,shift,'seeded event',k)
                assert c.string_at(markers,RING_SIZE*4) == model.marker_bytes(),(name,feedback_period,shift,'seeded markers')
                for buffer,size in ((obj_buffer,0x390),(ring_buffer,RING_SIZE*8),(marker_buffer,RING_SIZE*4)):
                    assert c.string_at(buffer,32) == c.string_at(buffer+32+size,32) == guard
                combined.update(model.events)
                cases += 1
    assert all(combined[name] > 0 for name in definitions)
    return dict(cases=cases,event_state_values_byte_exact=cases*93,
        marker_ring_values_byte_exact=cases*RING_SIZE,guards_unchanged=True,
        events=dict(sorted(combined.items())),owned_histories_only=True)
