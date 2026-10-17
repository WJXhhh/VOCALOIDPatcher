"""Verify raw bit3 bipolar peak gating, shared state and batch preservation.

Only generated PCM and owned config/context/Frame objects are used. The
module-local latch is restored after each suite in this disposable process.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import itertools
import json
import math
from pathlib import Path
import struct

from audit_frm2_numeric import F,I,Native,P,Q,SHA256,f32
from probe_frm2_batch_outputs import run as analyze_batch

LATCH=0xAA9AB8


def next_float(value,direction):
    bits=struct.unpack('<I',struct.pack('<f',value))[0]
    return struct.unpack('<f',struct.pack('<I',bits+direction))[0]


def context(native,config):
    result=native.alloc(0xCE0)
    P.from_address(result+0xAD0).value=config
    I.from_address(result+0xAD8).value=44100
    I.from_address(result+0xAF0).value=64
    I.from_address(result+0xBB0).value=8
    P.from_address(result+0xAE0).value=native.array(F,[0.]*64)
    return result


def analyze(native,ctx,frame,feature,signal,start,thresholds,state,enabled=True,reset_latch=True):
    config=P.from_address(ctx+0xAD0).value
    I.from_address(config+0x510).value=thresholds[0]
    I.from_address(config+0x514).value=thresholds[1]
    curve=config+0x5D0+23*0xA0
    native.fn(0x61500,None,P)(curve)
    for at in (0.,1.):native.add(curve,at,float(enabled))
    ring=P.from_address(ctx+0xAE0).value
    c.memset(ring,0,64*4)
    assert len(signal)==16
    for k,value in enumerate(signal):F.from_address(ring+4*((start+k)%64)).value=value
    I.from_address(ctx+0xB00).value=(start+8)%64
    c.memset(feature,0xA5,0x228)
    Q.from_address(frame+0x50).value=1<<13
    if reset_latch:I.from_address(native.lib._handle+LATCH).value=state
    else:assert I.from_address(native.lib._handle+LATCH).value==state
    if enabled:
        threshold=float(thresholds[1] if state else thresholds[0])
        # Raw kernel initializes extrema to +/-100000; ordinary PCM16 is
        # inside these bounds. No centering is applied to the gate extrema.
        positive=max(-100000.,max(signal))
        negative=-min(100000.,min(signal))
        expected=float(positive>=threshold and negative>=threshold)
        expected_state=int(expected)
    else:
        threshold=None;expected=0.;expected_state=state
    assert native.fn(0xD92E0,Q,P,P)(ctx,frame)==0
    actual=F.from_address(feature+0x10).value
    actual_state=I.from_address(native.lib._handle+LATCH).value
    assert actual==expected and actual_state==expected_state,(thresholds,state,signal,start,actual,expected,actual_state)
    assert list((F*64).from_address(ring))==[next((signal[k] for k in range(16) if (start+k)%64==i),0.) for i in range(64)]
    return dict(initial_state=state,enabled=enabled,thresholds=list(thresholds),selected_threshold=threshold,
        raw_positive_peak=max(signal),raw_negative_peak=-min(signal),physical_start=start,
        expected=expected,actual=actual,expected_next_state=expected_state,actual_next_state=actual_state,
        feature_flags=I.from_address(feature).value,exact=True)


def kernel_suite(native):
    saved=I.from_address(native.lib._handle+LATCH).value
    config=native.alloc(0x35F0);native.fn(0x90DE0,P,P)(config)
    defaults=[I.from_address(config+offset).value for offset in (0x510,0x514)]
    assert defaults==[2600,3400]
    ctx=context(native,config);other=context(native,config)
    frame=native.alloc(0x1D8);feature=native.alloc(0x228);P.from_address(frame+0x128).value=feature
    signals=[('silence',[0.]*16),('positive_dc',[5000.]*16),('negative_dc',[-5000.]*16),
        ('one_positive_peak',[10000.]+[0.]*15),('one_negative_peak',[-10000.]+[0.]*15),
        ('asymmetric',[5000.,-1000.]*8),('last_negative', [5000.]*15+[-5000.])]
    for amplitude in (next_float(2600.,-1),2600.,next_float(2600.,1),3000.,
                      next_float(3400.,-1),3400.,next_float(3400.,1)):
        signals.append(('bipolar_'+str(amplitude),[amplitude,-amplitude]*8))
    rows=[];sequences=[]
    try:
        for (label,signal),start,thresholds,state,enabled in itertools.product(signals,(0,1,63),
            ((2600,3400),(3400,2600),(0,0),(-1,-1),(100,100),(100,200)),(0,1),(False,True)):
            row=analyze(native,ctx,frame,feature,signal,start,thresholds,state,enabled)
            row['signal']=label;rows.append(row)
        for label,thresholds,amplitudes,contexts in (
            ('default_midrange',(2600,3400),[3000.]*6,[ctx]*6),
            ('reversed_thresholds',(3400,2600),[3000.,3500.,3000.,2500.,3000.,3500.],[ctx]*6),
            ('cross_context_default',(2600,3400),[3500.,3000.,3000.,3500.,2500.,3000.],[ctx,other]*3)):
            state=0;chain=[]
            I.from_address(native.lib._handle+LATCH).value=state
            for k,(amplitude,current) in enumerate(zip(amplitudes,contexts)):
                # The previous actual global result is the next context's input.
                row=analyze(native,current,frame,feature,[amplitude,-amplitude]*8,k%2*63,thresholds,state,reset_latch=False)
                row['context_number']=int(current==other);row['amplitude']=amplitude
                state=row['actual_next_state'];chain.append(row)
            sequences.append(dict(label=label,cases=chain))
        assert [r['actual'] for r in sequences[0]['cases']]==[1.,0.,1.,0.,1.,0.]
    finally:I.from_address(native.lib._handle+LATCH).value=saved
    return dict(default_thresholds=defaults,cases=rows,sequences=sequences,
        summary=dict(independent_cases=len(rows),sequence_calls=sum(len(s['cases']) for s in sequences),
            all_exact=True,shared_latch_across_two_contexts=True,latch_restored=True,
            raw_feature_flags=sorted(set(r['feature_flags'] for r in rows))))


def batch_suite(dll):
    rows=[]
    for amplitude in (1000.,5000.):
        pcm=[f32(amplitude*math.sin(math.tau*220*k/44100.)) for k in range(16384)]
        captured=[]
        def capture(native,ctx,collection):
            track=P.from_address(ctx+0xCD0).value;assert track
            values=[]
            for i in range(I.from_address(track+0x140).value):
                region=P.from_address(P.from_address(track+0x148).value+8*i).value
                for j in range(I.from_address(region+0x18).value):
                    frame=P.from_address(P.from_address(region+0x20).value+8*j).value
                    feature=P.from_address(frame+0x128).value
                    assert feature and Q.from_address(frame+0x50).value&(1<<13)
                    values.append((F.from_address(feature+0x10).value,I.from_address(feature).value))
            captured.append(values)
        result=analyze_batch(dll,0,1,'steady',220.,analysis_mode=4,pcm_override=pcm,result_observer=capture)
        assert len(captured)==1 and len(captured[0])==result['frames']
        assert len(captured[0])>24
        interior=captured[0][12:-12];expected=float(amplitude>3400.)
        assert all(v==expected and flags&(1<<3) for v,flags in interior),(amplitude,interior)
        rows.append(dict(amplitude=amplitude,source_samples=len(pcm),frames=result['frames'],
            interior_frames=len(interior),expected_bit3=expected,interior_bit3_exact=len(interior),
            internal_bit3_enabled=True,scope='Mode4 manual220Hz; 12-frame margins excluded, no full boundary claim.'))
    return rows


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for key in ('dll','output'):parser.add_argument('--'+key,type=Path,required=True)
    args=parser.parse_args();native=Native(args.dll)
    kernels=kernel_suite(native)
    saved=I.from_address(native.lib._handle+LATCH).value
    try:batches=batch_suite(args.dll)
    finally:I.from_address(native.lib._handle+LATCH).value=saved
    sources=(Path(__file__).name,'audit_frm2_numeric.py','probe_frm2_batch_outputs.py',
        'probe_frm2_batch_outputs.py','probe_frm2.py')
    report=dict(dse_sha256=SHA256,source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        producer_rva='0xd92e0',positive_write_rva='0xd9a9d',zero_write_rva='0xd9aad',latch_rva=hex(LATCH),
        latch_update_rvas=['0xd9acb','0xd9ad7'],config_offsets=['0x510','0x514'],config_ids=[88,89],
        kernel=kernels,batches=batches,
        summary=dict(raw_calls=kernels['summary']['independent_cases']+kernels['summary']['sequence_calls'],
            raw_all_exact=True,batch_runs=len(batches),batch_frames=sum(r['frames'] for r in batches),
            enabled_interior_bit3_exact=sum(r['interior_bit3_exact'] for r in batches)),
        limits=['Full native raw-feature entry, generated finite PCM only; no original V4X maker claim.',
            'Raw entry initializes feature flags to0; batch check separately verifies enabled final internal bit3.',
            'Both extrema are from uncentered PCM, not energy, probability or a symmetric absolute maximum alone.',
            'Global module latch is shared across contexts; threading/concurrency behavior not tested.',
            'Default thresholds can alternate at midrange amplitude; do not name this conventional hysteresis.',
            'Batch output uses fixed mode4/manual F0; original commercial analysis configuration remains unknown.'])
    args.output.write_text(json.dumps(report,indent=2,allow_nan=False)+'\n',encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__=='__main__':main()
