"""Owned PCM/chunk/state tests of the normal time-domain input producer."""
import ctypes as c
import math
import random
import struct

from audit_frm2_numeric import Native,P,I,F,D,Q,B,f32
from frm2_candidate_pcm_source import RING_SIZE
from frm2_time_domain_filter import TimeDomainFilter


def owned_filter_checks(dll):
    rng = random.Random(454)
    fixtures = {
        'zero':[0.]*513,
        'signed_zero':[0.,-0.]*129,
        'impulse':[32768. if i in (0,255,256,2047) else 0. for i in range(2048)],
        'tone':[f32(5000.*math.sin(math.tau*220.*i/44100.)) for i in range(2048)],
        'noise':[f32(rng.uniform(-5000.,5000.)) for _ in range(1025)],
        'dc_step':[32768.]*256+[0.]*769,
        'clip_step':[1e8]*512+[-1e8]*512+[0.]*512,
        'alternating':[15000.,-15000.]*512,
        'tiny':[f32(1e-30*math.sin(i*.13)) for i in range(257)]}
    patterns = ((1,),(2,3),(4,),(5,17),(127,128,129),(255,256,257))
    seeds = ([0.]*12,[rng.uniform(-1000.,1000.) for _ in range(12)])
    native = Native(dll)
    push = native.fn(0xd1140,Q,P,P,I)
    cases = values = state_values = raw_values = wraps = clipped = calls = 0
    guard = bytes([0xa5])*32
    rows = []
    for name,pcm in fixtures.items():
        for seed_index,seed in enumerate(seeds):
            # Every expected value and state is produced before calling the oracle.
            model = TimeDomainFilter()
            model.state = list(seed)
            predicted,states = [],[]
            for sample in pcm:
                predicted.append(model.sample(sample))
                states.append(model.snapshot())
            clipped += model.clipped
            expected = struct.pack('<'+str(len(predicted))+'d',*predicted)
            raw_expected = bytes((F*len(pcm))(*pcm))
            for pattern in patterns:
                for initial in (0,RING_SIZE-3):
                    context,config = native.alloc(0xce0),native.alloc(0x35f0)
                    raw_buffer = native.alloc(RING_SIZE*4+64)
                    filtered_buffer = native.alloc(RING_SIZE*8+64)
                    for buffer,size in ((raw_buffer,RING_SIZE*4),(filtered_buffer,RING_SIZE*8)):
                        c.memmove(buffer,guard,32)
                        c.memmove(buffer+32+size,guard,32)
                    raw,filtered = raw_buffer+32,filtered_buffer+32
                    P.from_address(context+0xad0).value = config
                    P.from_address(context+0xc90).value = native.alloc(0x390)
                    P.from_address(context+0xae0).value = raw
                    P.from_address(context+0xae8).value = filtered
                    I.from_address(context+0xaf0).value = RING_SIZE
                    I.from_address(context+0xaf4).value = initial
                    I.from_address(context+0xad8).value = 44100
                    B.from_address(context+0xcb8).value = 2
                    c.memmove(context+0xb28,struct.pack('<12d',*seed),96)
                    final_raw = bytearray(RING_SIZE*4)
                    final_filtered = bytearray(RING_SIZE*8)
                    cursor = block = case_wraps = 0
                    while cursor < len(pcm):
                        count = min(pattern[block%len(pattern)],len(pcm)-cursor)
                        at = (initial+cursor)%RING_SIZE
                        first = min(count,RING_SIZE-at)
                        assert push(context,native.array(F,pcm[cursor:cursor+count]),count) == 0
                        assert I.from_address(context+0xaf4).value == (at+count)%RING_SIZE
                        for pointer,stride,want,full in ((raw,4,raw_expected,final_raw),(filtered,8,expected,final_filtered)):
                            segment = want[cursor*stride:(cursor+first)*stride]
                            assert c.string_at(pointer+at*stride,first*stride) == segment,(name,seed_index,pattern,initial,cursor,'first',stride)
                            full[at*stride:(at+first)*stride] = segment
                            if first < count:
                                segment = want[(cursor+first)*stride:(cursor+count)*stride]
                                assert c.string_at(pointer,(count-first)*stride) == segment,(name,seed_index,pattern,initial,cursor,'wrap',stride)
                                full[:(count-first)*stride] = segment
                        assert c.string_at(context+0xb28,96) == states[cursor+count-1],(name,seed_index,pattern,initial,cursor,'state')
                        for buffer,size in ((raw_buffer,RING_SIZE*4),(filtered_buffer,RING_SIZE*8)):
                            assert c.string_at(buffer,32) == c.string_at(buffer+32+size,32) == guard
                        values += count
                        raw_values += count
                        state_values += 12
                        wraps += first < count
                        case_wraps += first < count
                        calls += 1
                        cursor += count
                        block += 1
                    assert c.string_at(raw,RING_SIZE*4) == final_raw
                    assert c.string_at(filtered,RING_SIZE*8) == final_filtered
                    cases += 1
                    rows.append(dict(shape=name,seed=seed_index,chunk_pattern=pattern,
                        initial_ring_position=initial,values=len(pcm),push_calls=block,wrapped_pushes=case_wraps))
    # Producer-disabled contracts preserve existing filter output and memories.
    disabled = 0
    for selector,no_estimator in ((1,False),(0,True)):
        context,config = native.alloc(0xce0),native.alloc(0x35f0)
        P.from_address(context+0xad0).value = config
        I.from_address(config+0x5a0).value = selector
        P.from_address(context+0xc90).value = 0 if no_estimator else native.alloc(0x390)
        P.from_address(context+0xae0).value = native.alloc(RING_SIZE*4)
        filtered = native.array(D,[123.]*RING_SIZE)
        P.from_address(context+0xae8).value = filtered
        I.from_address(context+0xaf0).value = RING_SIZE
        I.from_address(context+0xad8).value = 44100
        c.memmove(context+0xb28,struct.pack('<12d',*seeds[1]),96)
        before = c.string_at(filtered,RING_SIZE*8),c.string_at(context+0xb28,96)
        assert push(context,native.array(F,fixtures['tone'][:17]),17) == 0
        assert (c.string_at(filtered,RING_SIZE*8),c.string_at(context+0xb28,96)) == before
        disabled += 1
    assert clipped > 0 and wraps > 0
    return dict(cases=cases,rows=rows,filtered_values_byte_exact=values,
        raw_values_byte_exact=raw_values,state_values_byte_exact=state_values,
        push_calls=calls,physical_wrap_pushes=wraps,independent_clipped_fixture_values=clipped,
        disabled_cases=disabled,disabled_state_and_ring_preserved=True,
        source_payloads_owned=True,guard_bytes_unchanged=True)
