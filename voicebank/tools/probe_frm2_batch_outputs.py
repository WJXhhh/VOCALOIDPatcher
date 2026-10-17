"""Observe owned PCM at the complete DRS batch entry without injecting frames.

The default probe reads result track metadata after the batch returns, without
callbacks or output replacements. Research callers may install a step observer
explicitly through setup_observer; the command-line matrix does not do so.
"""
import argparse
from collections import Counter
from itertools import product
import json
import math
from pathlib import Path
import statistics
from audit_frm2_numeric import Native,SHA256,F,D,I,P,Q,B,f32


def owned_pcm(shape,frequency):
    pcm=[f32(5000*sum(math.sin(math.tau*frequency*k*i/44100+k*.23)/k for k in range(1,6))) for i in range(11025)]
    if shape=='attack':pcm=[f32(v*min(1,i/2205)) for i,v in enumerate(pcm)]
    if shape=='gap':pcm[3307:6615]=[0.]*(6615-3307)
    return pcm


def run(dll,automatic,features,shape,frequency,*,analysis_mode=None,estimator_variant=None,special_pcm=None,pitch_seed=None,pitch_curve=None,setup_observer=None,result_observer=None,before_step_observer=None,step_observer=None,phase_analysis=None,region_analysis=False,streaming=False,pcm_override=None,config_5a0=None,config_588=None,config_5b4=None,after_step_observer=None):
    native=Native(dll)
    config=native.alloc(0x35f0);native.fn(0x90de0,P,P)(config)
    for key,value in ((0,1),(0x14,automatic),(0xd,frequency if pitch_seed is None else pitch_seed),(4,110)):
        points=pitch_curve if key==0xd and pitch_curve is not None else ((0.,value),(1.,value))
        for x,y in points:native.add(config+0x5d0+key*0xa0,x,y)
    F.from_address(config+0x18).value=44100/256
    I.from_address(config+0x54c).value=features
    if phase_analysis is not None:
        I.from_address(config+0x5a8).value=int(bool(phase_analysis))
    if analysis_mode is not None:
        if analysis_mode not in range(6):raise ValueError('Unsupported analysis mode')
        B.from_address(config+0x558).value=analysis_mode
    if estimator_variant is not None:
        # fac30 explicitly selects 1/3/4/10; 0 and 2 use its generic branch.
        # Constructor geometry and the flag at +0x48 were checked in Ghidra.
        if estimator_variant not in (0,1,2,3,4,10):raise ValueError('Unreviewed estimator variant')
        I.from_address(config+0x5c4).value=estimator_variant
    if special_pcm is not None:
        # fac30 selects kind15, a shorter generic window and queue index zero;
        # fb4a0 additionally shifts the PCM start by -512/decimation_factor.
        if special_pcm not in (0, 1):raise ValueError('Unreviewed special PCM protocol')
        I.from_address(config+0x5c8).value=special_pcm
    if config_5a0 is not None:
        if config_5a0 not in (0, 1):raise ValueError('Unreviewed config +0x5a0 value')
        I.from_address(config+0x5a0).value=config_5a0
    if config_588 is not None:
        if config_588 not in (0, 1):raise ValueError('Unreviewed config +0x588 value')
        I.from_address(config+0x588).value=config_588
    if config_5b4 is not None:
        # e2b90 constructs geometry for the two reviewed classification profiles;
        # e3640/e4240 select matching 40/20 or 24/13 source/delta arrays.
        if config_5b4 not in (0, 1):raise ValueError('Unreviewed config +0x5b4 value')
        I.from_address(config+0x5b4).value=config_5b4
    collection=native.alloc(0x68);native.fn(0x938c0,P,P)(collection)
    context=native.alloc(0xce0);native.fn(0x956b0 if region_analysis else 0xcdde0,P,P,I,P)(context,44100,config)
    if setup_observer is not None:setup_observer(native,context)
    pcm=owned_pcm(shape,frequency) if pcm_override is None else pcm_override
    descriptor=native.alloc(16);P.from_address(descriptor).value=native.array(F,pcm)
    I.from_address(descriptor+8).value=len(pcm);I.from_address(descriptor+12).value=44100
    code=(stream_batch(native,context,collection,descriptor,before_step_observer,step_observer,after_step_observer) if streaming else
          native.fn(0xd9b10,Q,P,P,P,Q,P)(context,0,collection,0,descriptor))
    if code:raise AssertionError(('Native batch failed',code))
    if result_observer is not None:result_observer(native,context,collection)
    track=P.from_address(context+0xcd0).value
    if not track:raise AssertionError('Missing native result track')
    regions=I.from_address(track+0x140).value
    if not 0<regions<100:raise AssertionError(('Unexpected regions',regions))
    region_array=P.from_address(track+0x148).value
    rows=[]
    for region_index in range(regions):
        region=P.from_address(region_array+8*region_index).value
        frame_count=I.from_address(region+0x18).value
        if not 0<=frame_count<10000:raise AssertionError('Unexpected frame count')
        frame_array=P.from_address(region+0x20).value
        for index in range(frame_count):
            output=P.from_address(frame_array+8*index).value
            count=I.from_address(output+0x58).value
            if not 0<=count<=1500:raise AssertionError('Unexpected native harmonic count')
            mask=Q.from_address(output+0x50).value
            stored_pitch=F.from_address(output+0xf0).value
            f0=440*2**(stored_pitch/1200) if mask&(1<<31) else stored_pitch
            if not math.isfinite(f0):raise AssertionError('Nonfinite output F0')
            active=0
            if count:
                frequencies=P.from_address(output+0x60).value
                if not frequencies:raise AssertionError('Count without frequency array')
                active=sum(F.from_address(frequencies+4*i).value>0 for i in range(count))
            rows.append(dict(region=region_index,index=index,mask=hex(mask),harmonics=count,active=active,stored_pitch=stored_pitch,f0=f0,
                             bit37=bool(mask&(1<<37)),bit7=bool(mask&128)))
    if not rows:raise AssertionError('Empty native batch output')
    voiced=[r['f0'] for r in rows if r['f0']>0]
    return dict(automatic=automatic,features=features,shape=shape,frequency=frequency,regions=regions,frames=len(rows),
                masks=dict(Counter(r['mask'] for r in rows)),voiced=len(voiced),
                f0_median=statistics.median(voiced) if voiced else None,
                median_error_cents=statistics.median([1200*math.log2(v/frequency) for v in voiced]) if voiced else None,
                active_slots=sum(r['active'] for r in rows),bit37_frames=sum(r['bit37'] for r in rows),
                rows=rows)


def stream_batch(native, context, collection, descriptor,
                 before_step_observer=None, step_observer=None, after_step_observer=None):
    """Use the actual push/step/output virtual protocol without AnalyzeBatch."""
    import ctypes as c
    config=P.from_address(context+0xad0).value
    I.from_address(context+0x418).value=0
    D.from_address(context+0xc10).value=0.
    I.from_address(context+0xaf4).value=0
    D.from_address(context+0xaf8).value=0.
    I.from_address(context+0xb00).value=0
    Q.from_address(context+0xbbc).value=0
    I.from_address(context+0xb88).value=0
    capacity=I.from_address(context+0xaf0).value
    c.memset(P.from_address(context+0xae0).value,0,capacity*4)
    filtered=P.from_address(context+0xae8).value
    if filtered:c.memset(filtered,0,capacity*8)
    queue=P.from_address(context+0xc20).value
    for k in range(I.from_address(context+0xc18).value):
        link=P.from_address(queue+8*k).value
        I.from_address(link).value=0
        I.from_address(link+0x18).value=0
        D.from_address(P.from_address(link+0x28).value+0x48).value=-1.
    entry=native.fn(0x93a30,P,P,I)(collection,-1)
    B.from_address(entry+0x44).value=1
    track=native.fn(0x90930,P,P)(entry)
    P.from_address(context+0xcd0).value=track
    F.from_address(track+0x174).value=22050.
    F.from_address(track+0x188).value=2.
    Q.from_address(track+0x180).value=Q.from_address(context+0xba8).value
    B.from_address(track+0x18c).value=B.from_address(config+0x548).value
    table=P.from_address(context).value
    virtual=lambda offset:c.CFUNCTYPE(Q,P)(P.from_address(table+offset).value)
    initialize,step,commit,finish=(virtual(x) for x in (0x10,8,0x18,0x20))
    # The decompiler omits RDX, but da037 passes the output frame explicitly.
    commit=c.CFUNCTYPE(Q,P,P)(P.from_address(table+0x18).value)
    initialize(context)
    length=I.from_address(descriptor+8).value
    rate=I.from_address(descriptor+12).value
    B.from_address(config).value=2
    D.from_address(config+8).value=f32(f32(length)/f32(rate))
    push=native.fn(0xd1140,Q,P,P,I)
    source=P.from_address(descriptor).value
    cursor=0;last_time=0.;steps=0
    while steps<10000:
        if not I.from_address(context+0xb88).value:
            count=min(128,length-cursor)
            if count>0:push(context,source+cursor*4,count)
        while True:
            steps+=1
            if steps>=10000:raise AssertionError('Streaming step bound')
            if before_step_observer is not None:before_step_observer(native,context)
            status=step(context)&0xffffffff
            if after_step_observer is not None:after_step_observer(native,context,status)
            if status==1:break
            frame=P.from_address(context+0xcc8).value
            if frame:
                last_time=D.from_address(frame+0x48).value
                if step_observer is not None:step_observer(native,context,frame)
                commit(context,frame)
            if I.from_address(context+0xb88).value:break
        cursor+=128 if not I.from_address(context+0xb88).value else I.from_address(context+0xb98).value
        if cursor>=length-1:
            I.from_address(context+0xb88).value=1
            if (length-1)/rate-1e-8<=last_time+D.from_address(context+0xba8).value:
                finish(context)
                return 0
    raise AssertionError('Streaming did not finish')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    cases=[run(args.dll,*case) for case in product((0,1),(0,1),('steady','attack','gap'),(110,220,440))]
    for case in cases:
        peer=next(x for x in cases if all(x[k]==case[k] for k in ('automatic','shape','frequency')) and x['features']!=case['features'])
        case['feature_toggle_metadata_equal']=case['rows']==peer['rows']
    report=dict(dll_sha256=SHA256,cases=cases,scope='Observed complete d9b10 batch; owned PCM, normal mode1, no frame injection or builder rewrite; metadata observations are not independent algorithm equivalence')
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(cases=len(cases),frames=sum(x['frames'] for x in cases),
                          bit37_frames=sum(x['bit37_frames'] for x in cases),
                          feature_toggle_metadata_equal=all(x['feature_toggle_metadata_equal'] for x in cases))))


if __name__=='__main__':main()
