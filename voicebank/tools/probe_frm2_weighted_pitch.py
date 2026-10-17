"""Observe default pre-tracking peak-weighted F0 on owned batch signals."""
import argparse
import ctypes as c
import json
import math
import os
from pathlib import Path
import sys
import traceback
from audit_frm2_numeric import F,D,I,P,Q,f32,SHA256
from probe_frm2_batch_outputs import run
from frm2_fft_peaks import detect


def capture(context,modeled=False,power_frames=None,complex_frames=None):
    tracker=P.from_address(context+0xc98).value
    index=I.from_address(tracker+0x18).value
    if not 0<=index<32:raise AssertionError(('tracker index',index))
    queue=P.from_address(context+0xc50).value
    item=P.from_address(queue+8*index).value
    frame=P.from_address(item+0x28).value
    count=I.from_address(item+0x18).value
    if not 0<=count<=4096:raise AssertionError(('peaks',count))
    ptr=P.from_address(item+0x20).value
    peaks=[(F.from_address(ptr+20*i).value,F.from_address(ptr+20*i+4).value) for i in range(count)]
    if modeled:
        config=P.from_address(context+0xad0).value
        if I.from_address(config+0x520).value or I.from_address(config+0x588).value:
            raise AssertionError('Spectrum replay requires default linear detector without post-filter')
        size=I.from_address(item).value;power_ptr=P.from_address(item+8).value
        if size==0:
            if peaks:raise AssertionError('Peaks without spectrum')
            return frame,[]
        if not power_ptr or not 4<=size<=32768:raise AssertionError(('Spectrum geometry',size))
        if power_frames is None:
            power=[F.from_address(power_ptr+4*i).value for i in range(size)]
        else:
            if frame not in power_frames:raise AssertionError('Missing independently computed power')
            power=power_frames[frame]
            if len(power)!=size:raise AssertionError('Independent power length')
        bins=complex_frames[frame] if complex_frames is not None else None
        predicted=[tuple(p[:3] if bins is not None else p[:2]) for p in detect(power,bins,44100,0,f32(math.exp(-300*.1151292546497)*32768),0.,22050.)]
        if len(predicted)!=len(peaks):raise AssertionError(('Peak count',len(predicted),len(peaks)))
        error=max((abs(a-b) for x,y in zip(predicted,peaks) for a,b in zip(x,y)),default=0.)
        if error>1e-3:
            worst=max((abs(a-b),i,j,a,b) for i,(x,y) in enumerate(zip(predicted,peaks)) for j,(a,b) in enumerate(zip(x,y)))
            raise AssertionError(dict(reason='Peak replay error',time=D.from_address(frame+0x48).value,
                                      error=error,worst=worst,fields=['Hz','dB']))
        peaks=predicted
        if bins is not None:
            error=max((abs((p[2]-F.from_address(ptr+20*i+8).value+math.pi)%math.tau-math.pi) for i,p in enumerate(peaks)),default=0.)
            if error>1e-4:raise AssertionError(('Live peak phase mismatch',error))
    return frame,peaks


def weighted_pitch(hz,peaks):
    if hz<=0 or not peaks:return hz
    total=0.;weight=0.;cursor=0
    for harmonic in range(1,16):
        best=None;amplitude=-100000.
        for index in range(cursor,len(peaks)):
            frequency,db=peaks[index][:2]
            if abs(f32(frequency-f32(harmonic*hz)))<hz*.5 and db>amplitude:
                best=index;amplitude=db
            if frequency>f32(f32(harmonic+.5)*hz):break
        if best is not None:
            cursor=best
            w=f32(math.exp(amplitude*0.1151292546497)*32768.)
            total=f32(total+peaks[best][0]/harmonic*w)
            weight=f32(weight+w)
    return f32(total/weight) if weight else float('nan')


def verify(dll,shape,frequency):
    rows=[];held=[]
    def install(native,ctx):
        held.append(native)
        vt=P.from_address(ctx).value;original=c.CFUNCTYPE(Q,P)(P.from_address(vt+8).value)
        private=native.alloc(0x100);c.memmove(private,vt,0x100)
        def step(context):
            try:
                frame,peaks=capture(context)
                before=F.from_address(frame+0xf0).value
                time=D.from_address(frame+0x48).value
                result=original(context)
                after=F.from_address(frame+0xf0).value
                if Q.from_address(frame+0x50).value&(1<<31):after=440*2**(after/1200)
                if before>0 and peaks and time>=0 and before!=after:
                    predicted=weighted_pitch(before,peaks)
                    rows.append(dict(time=time,before=before,actual=after,predicted=predicted,error=abs(predicted-after),peaks=peaks))
                return result
            except BaseException:
                traceback.print_exc();sys.stderr.flush();os._exit(2)
        callback=c.CFUNCTYPE(Q,P)(step);P.from_address(private+8).value=c.cast(callback,P).value
        P.from_address(ctx).value=private;native.keep.extend([original,callback])
    observed=run(dll,1,0,shape,frequency,analysis_mode=4,setup_observer=install)
    baseline=run(dll,1,0,shape,frequency,analysis_mode=4)
    if observed!=baseline:raise AssertionError('Observer changed batch')
    return dict(shape=shape,frequency=frequency,rows=rows,observer_matches_baseline=True)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--dll',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True)
    p.add_argument('--frequencies',type=float,nargs='+',default=[110,220,440])
    p.add_argument('--shapes',nargs='+',default=['steady','attack','gap'])
    a=p.parse_args();cases=[verify(a.dll,s,f) for s in a.shapes for f in a.frequencies]
    a.output.write_text(json.dumps(dict(dll_sha256=SHA256,cases=cases),indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(cases=len(cases),changed=sum(len(x['rows']) for x in cases),max_error=max((r['error'] for x in cases for r in x['rows']),default=0))))
