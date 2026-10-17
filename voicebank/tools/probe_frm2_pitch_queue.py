"""Observe complete mode4 queue histories on owned PCM without replacing outputs."""
import argparse
from collections import Counter
import ctypes as c
import json
import os
from pathlib import Path
import sys
import traceback
from audit_frm2_numeric import F,D,I,P,Q,SHA256
from probe_frm2_batch_outputs import run
from frm2_pitch_repair import repair
from frm2_raw_feature_observer import RawFeatureObserver


def observe(dll,shape,frequency):
    steps=[];raw_observer=RawFeatureObserver()
    def install(native,context):
        vtable=P.from_address(context).value
        original=c.CFUNCTYPE(Q,P)(P.from_address(vtable+8).value)
        private=native.alloc(0x100);c.memmove(private,vtable,0x100)
        def read(frame):
            feature=P.from_address(frame+0x128).value
            return dict(time=D.from_address(frame+0x48).value,f0=F.from_address(frame+0xf0).value,
                        mask=hex(Q.from_address(frame+0x50).value),
                        probability=F.from_address(feature+0x224).value if feature else None)
        def step(ctx):
            try:
                if len(steps)>10000:raise AssertionError('Step bound')
                estimator=P.from_address(ctx+0xc80).value
                queue=P.from_address(ctx+0xc40).value
                index=I.from_address(estimator+0x18).value
                if index!=5:raise AssertionError(('Unexpected mode4 queue index',index))
                frames=[P.from_address(P.from_address(queue+8*j).value+0x28).value for j in range(1,6)]
                before=[read(frame) for frame in frames]
                raw_token=raw_observer.before(ctx)
                result=original(ctx)
                raw_observer.after(ctx,raw_token)
                after=[read(frame) for frame in frames]
                # Reading the same objects separates queue rotation from mutation.
                steps.append(dict(before=before,after=after,result=int(result)))
                return result
            except BaseException:
                traceback.print_exc();sys.stderr.flush();os._exit(2)
        callback=c.CFUNCTYPE(Q,P)(step)
        P.from_address(private+8).value=c.cast(callback,P).value
        P.from_address(context).value=private
        native.keep.extend([original,callback])
    observed=run(dll,1,0,shape,frequency,analysis_mode=4,setup_observer=install)
    baseline=run(dll,1,0,shape,frequency,analysis_mode=4)
    if observed!=baseline:raise AssertionError('Queue observer changed batch output')
    for row in steps:
        before=row['before'];after=row['after']
        if int(after[4]['mask'],16)&512:
            if int(before[4]['mask'],16)&512:raise AssertionError('Reused already-estimated frame')
            values=[x['f0'] for x in before]
            values[4]=after[4]['f0']
            prediction=repair(values)
            prediction['max_error_hz']=max(abs(a-b['f0']) for a,b in zip(prediction['hertz'],after))
            if prediction['max_error_hz']>1e-3:
                raise AssertionError(('Repair differs',shape,frequency,values,prediction,after))
            row['repair']=prediction
        elif any(a['f0']!=b['f0'] for a,b in zip(before,after)):
            raise AssertionError('Queue mutated outside newly estimated frame stage')
    stages=[row for row in steps if 'repair' in row]
    admitted=sum(row['after'][4]['f0']>0 for row in stages)
    erased=sum(a['f0']>0 and b['f0']==0 for row in stages for a,b in zip(row['before'][:4],row['after'][:4]))
    if admitted-erased!=observed['voiced']:raise AssertionError('Final voiced count not explained by queue repair')
    summary=dict(shape=shape,frequency=frequency,steps=len(steps),repair_stages=len(stages),
                 max_error_hz=max(row['repair']['max_error_hz'] for row in stages),
                 admitted=admitted,erased=erased,final_voiced=observed['voiced'],
                 raw_feature_frames=len(raw_observer.comparisons),raw_feature_max_error=0.,
                 events=dict(Counter(event for row in stages for event in row['repair']['events'])))
    return dict(shape=shape,frequency=frequency,observer_matches_baseline=True,steps=steps,summary=summary,
                final=observed)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',required=True,type=Path)
    parser.add_argument('--output',required=True,type=Path)
    parser.add_argument('--frequencies',type=float,nargs='+',default=[110,220,440])
    args=parser.parse_args()
    if any(not 40<=f<=1500 for f in args.frequencies):parser.error('Frequency outside reviewed 40..1500 Hz range')
    cases=[observe(args.dll,shape,f) for shape in ('steady','attack','gap') for f in args.frequencies]
    report=dict(dll_sha256=SHA256,cases=cases,summary=[case['summary'] for case in cases],
                scope='Native first four queue F0 values plus native current gated F0; independent five-frame repair. '
                      'No independent full-estimator history claim. Only observed branches dynamically validated.')
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(cases=len(cases),steps=sum(len(x['steps']) for x in cases),
                         observer_matches_baseline=True)))


if __name__=='__main__':main()
