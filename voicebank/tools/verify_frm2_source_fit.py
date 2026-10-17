"""Feed source-PCM predicted E7 into ordinary FRM2 fitting comparisons."""
import argparse
import json
import math
from pathlib import Path
from audit_frm2_numeric import Native,F,P,Q,B,SHA256
from verify_frm2_pitch_state import verify
from verify_frm2_fit_schedule import schedule,snapshot


def frame_from_prediction(native,row):
    frame=native.alloc(4096);native.frame_ctor(frame)
    Q.from_address(frame+0x50).value=0x680
    F.from_address(frame+0xf0).value=row['f0'];F.from_address(frame+0xf8).value=row['gain']
    P.from_address(frame+0x108).value=native.env(row['points'],1,0,(-20000,20000,0))
    return frame


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--all-frames',action='store_true',help='Fit every positive source E7 instead of selected frames')
    parser.add_argument('--frequencies',type=float,nargs='+',default=[220.])
    parser.add_argument('--times',type=float,nargs='+',help='Only fit matching frame times (1e-8 tolerance)')
    args=parser.parse_args();cases=[];chains=[]
    if any(not 40<=f<=1500 for f in args.frequencies):parser.error('Frequency outside reviewed source range')
    for shape,frequency in ((s,f) for s in ('steady','attack','gap') for f in args.frequencies):
        inputs=[];chains.append(verify(args.dll,shape,frequency,True,fit_inputs=inputs))
        if not inputs:continue
        selected=range(len(inputs)) if args.all_frames else sorted(set([0,len(inputs)//2,len(inputs)-1]+[i for i,r in enumerate(inputs) if len(r['points'])==2][:1]+[i for i,r in enumerate(inputs) if len(r['points'])>=4][:1]))
        if args.times is not None:selected=[i for i,r in enumerate(inputs) if any(abs(r['time']-t)<1e-8 for t in args.times)]
        for index in selected:
            row=inputs[index];native=Native(args.dll)
            original=frame_from_prediction(native,row)
            code=native.fn(0x71e00,Q,P,B,F,F)(original,0,22050.,1000.)
            if code:raise AssertionError(('Native fit failed',code))
            mask,counts,values=snapshot(original)
            traces={}
            for independent in (False,True):
                staged=frame_from_prediction(native,row)
                traces[independent]=[]
                try:schedule(native,staged,22050.,1000.,independent,traces[independent])
                except (ValueError,ArithmeticError) as error:
                    cases.append(dict(shape=shape,frequency=frequency,time=row['time'],f0=row['f0'],e7_points=len(row['points']),independent=independent,
                                      mask=hex(mask),counts=counts,passed=False,error=str(error)))
                    continue
                other_mask,other_counts,other_values=snapshot(staged)
                structural=mask==other_mask and counts==other_counts and len(values)==len(other_values)
                errors=[abs(a-b) for a,b in zip(values,other_values)]
                finite=all(math.isfinite(x) for x in errors)
                passed=structural and finite and max(errors,default=0.)<1e-4
                cases.append(dict(shape=shape,frequency=frequency,time=row['time'],f0=row['f0'],e7_points=len(row['points']),independent=independent,
                                  mask=hex(mask),counts=counts,fields=len(values),max_error=max(errors,default=0.),passed=passed,
                                  differences=[dict(index=i,actual=values[i],predicted=other_values[i],error=e) for i,e in enumerate(errors) if e>1e-4]))
                if independent:
                    cases[-1]['first_stage_difference']=next((dict(stage=a['stage'],actual=a['value'],predicted=b['value']) for a,b in zip(traces[False],traces[True]) if max(abs(x-y) for x,y in zip(a['value'],b['value']))>1e-4),None)
    report=dict(dll_sha256=SHA256,cases=cases,source_chains=[{k:v for k,v in x.items() if k!='comparisons'} for x in chains],
                scope='Predicted source-PCM E7 with gain/F0 -> explicit set0 fitting versus monolithic native; spacing1000. Independent initial ABC, optimization, refinement and residual arithmetic; native allocation/setters and source-chain backend dependencies retained. Not native production preset or serialized file acceptance.')
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(cases=len(cases),passed=sum(x['passed'] for x in cases),max_error=max(x.get('max_error',0.) for x in cases))))
    if not all(x['passed'] for x in cases):raise AssertionError('Source fit mismatches saved')


if __name__=='__main__':main()
