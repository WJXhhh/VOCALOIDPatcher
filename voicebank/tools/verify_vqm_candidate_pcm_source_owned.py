"""Owned PCM tests for the default candidate spectrum and raw feature inputs."""
import ctypes as c
import math
import random
import struct

from audit_frm2_numeric import Native,P,I,F,B,D,Q,f32
from frm2_mode5_math_backend import PublicRealFft,module_loaded
from frm2_candidate_pcm_source import CandidatePcmSource,raw_at,RING_SIZE,HOP
from frm2_raw_feature_observer import OwnedRingView
from verify_vqm_candidate_gate import owned_signal


def owned_pcm_source_checks(dll):
    assert not module_loaded('DSE.dll')
    fixtures = []
    with PublicRealFft(dll.parent/'DSE_DFT.dll') as transform:
        model = CandidatePcmSource(transform)
        for shape in ('steady','gap','noise','dc_tone','impulse','silence'):
            pcm = owned_signal(shape,220.)
            last = math.ceil(len(pcm)/HOP)
            for index in (0,last//2,last):
                start = index*HOP-512
                signal = [pcm[j] if 0 <= j < len(pcm) else 0.
                          for j in range(start,start+1025)]
                expected = bytes((F*2048)(*model.power_at(pcm,index)))
                fixtures.append((signal,expected))
        fft_calls,guards = transform.calls,transform.guards_checked
        assert not module_loaded('DSE.dll')
    assert not transform.handle and not module_loaded('DSE.dll')
    native = Native(dll)
    config,context = native.alloc(0x35f0),native.alloc(0x400)
    native.fn(0x90de0,P,P)(config)
    native.fn(0x49790,P,P,I,P,I)(context,0,config,0)
    analyze = native.fn(0x4e8e0,Q,P,P,I,B,F,P,P,P,B,P,F,I,I,I,I)
    values = 0
    for signal,expected in fixtures:
        scalar,packed,half = P(),P(),I()
        assert analyze(context,native.array(F,signal),1025,11,1.,c.byref(scalar),0,
                       c.byref(half),2,c.byref(packed),4.,0,0,0,1) == 0
        assert half.value == 2048
        assert c.string_at(scalar.value,2048*4) == expected
        values += 2048
    # Default physical ring geometry, including both sides of two wrap points.
    raw_context,frame,feature = native.alloc(0xce0),native.alloc(0x1a0),native.alloc(0x228)
    P.from_address(raw_context+0xad0).value = config
    P.from_address(frame+0x128).value = feature
    I.from_address(raw_context+0xaf0).value = RING_SIZE
    I.from_address(raw_context+0xbb0).value = HOP
    I.from_address(raw_context+0xad8).value = 44100
    for at in (0.,1.):
        native.add(config+0x5d0+0x17*0xa0,at,1.)
    raw = native.fn(0xd92e0,Q,P,P)
    rng = random.Random(452)
    size = 100000
    signals = [
        [0.]*size,
        [f32(1024.+4096.*math.sin(math.tau*220.*i/44100.)) for i in range(size)],
        [f32(rng.uniform(-5000.,5000.)) for _ in range(size)]]
    exact = wrapped = 0
    for pcm in signals:
        for index in (0,1,176,177,178,352,353,354):
            absolute = index*HOP
            center = absolute%RING_SIZE
            view = OwnedRingView(pcm,absolute,center,RING_SIZE)
            P.from_address(raw_context+0xae0).value = native.array(F,[view[i] for i in range(RING_SIZE)])
            I.from_address(raw_context+0xb00).value = center
            expected = raw_at(pcm,index)
            assert raw(raw_context,frame) == 0
            for name,offset in (('feature4',4),('feature8',8),('feature12',12)):
                assert struct.pack('<f',expected[name]) == c.string_at(feature+offset,4), (index,name)
                exact += 1
            # Disabling curve23 clears these fields even on an existing record.
            for at in (0.,1.):
                native.add(config+0x5d0+0x17*0xa0,at,0.)
            for offset in (4,8,12):
                F.from_address(feature+offset).value = 123.
            assert raw(raw_context,frame) == 0
            disabled = raw_at(pcm,index,False)
            for name,offset in (('feature4',4),('feature8',8),('feature12',12)):
                assert struct.pack('<f',disabled[name]) == c.string_at(feature+offset,4)
                exact += 1
            for at in (0.,1.):
                native.add(config+0x5d0+0x17*0xa0,at,1.)
            wrapped += (center-HOP)%RING_SIZE+2*HOP-1 >= RING_SIZE
    return dict(spectrum_cases=len(fixtures),spectrum_values_byte_exact=values,
                producer_dse_absent=True,fft_calls=fft_calls,ccs_guards_checked=guards,
                descriptor_freed_before_oracle=True,raw_cases=48,raw_enabled_cases=24,
                raw_disabled_cases=24,raw_values_byte_exact=exact,
                physical_wrap_cases=wrapped,native_calls_on_owned_inputs_only=True)
