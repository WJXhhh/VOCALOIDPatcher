"""Run pitch/voicing/repair/refinement from owned PCM and explicit FIR assets.

Native scheduling and selected embedded FIR assets remain inputs. Native pitch,
probability, score, prepared PCM, raw ring, power and peaks are comparison targets.
"""
import argparse
import ctypes as c
import json
import math
import os
from pathlib import Path
import sys
import traceback
from audit_frm2_numeric import F,D,I,P,Q,B,SHA256,f32
from probe_frm2_batch_outputs import run,owned_pcm
from frm2_raw_feature_observer import RawFeatureObserver
from frm2_mode4_spectrum import build_kernel_bank,convolve_packed
from verify_frm2_analysis_windows import prepare
from frm2_pitch_preprocess import preprocess
from frm2_pitch_score import autocorrelation_score
from frm2_pitch_candidate import candidate_with_history
from frm2_pitch_voicing import score_features,probability,gate
from frm2_pitch_repair import repair
from frm2_pitch_native_fft import NativeFftBackend
from probe_frm2_weighted_pitch import capture,weighted_pitch
from frm2_live_power import PowerObserver
from frm2_pitch_pcm import prepare_pitch_pcm
from frm2_decimator import DecimatedSource,runtime_coefficients,selected_coefficients
from frm2_peak_selection import advance_outer
from frm2_harmonic_gain import harmonic_gain,analysis_envelope


class PitchState:
    def __init__(self,fft_backend=None):
        self.fft_backend=fft_backend
        self.history=[22050.]+[0.]*10
        self.probability_history=[0.]*11
        self.accepted_count=0
        self.frames={};self.probabilities={};self.kernels={}
    def process(self,row):
        ids=row['ids'];previous=self.frames.get(ids[3],0.)
        zero=not any(row['samples'])
        p=0.;hz=0.;accepted=False;score_error=0.
        if not zero:
            samples=row['samples'];rate=row['rate'];factor=row['factor'];kind=row['kind']
            key=(len(samples),rate,factor,kind)
            if key not in self.kernels:self.kernels[key]=build_kernel_bank(*key)
            bank=self.kernels[key]
            packed=convolve_packed(prepare(samples,bank['initial_window'],factor,True),bank['selections'],bank['kernels'],bank['center'])
            half=len(packed)//2;nyquist=f32(rate*.5)
            scalar=[abs(packed[0])]+[f32(f32(packed[2*i]**2)+f32(packed[2*i+1]**2)) for i in range(1,half)]+[abs(packed[1])]
            processed=preprocess(scalar[:-1],nyquist)['processed']
            limit=int(f32(f32(min(3000.,nyquist)/nyquist)*half))
            score=autocorrelation_score(processed,normalization_bins=limit,fft_backend=self.fft_backend)
            score_error=max(abs(a-b) for a,b in zip(score,row['native_score']))
            candidate=candidate_with_history(score,nyquist,self.history,self.accepted_count,5,previous)
            derived=score_features(score,candidate['index'],nyquist,limit,factor)
            native_score_derived=score_features(row['native_score'],candidate['index'],nyquist,limit,factor)
            p=probability(dict(row['raw_features'],**derived))
            previous_p=self.probabilities.get(ids[3],0.) if row['time']>0 else 0.
            decision=gate(p,previous_p,self.history,self.accepted_count,previous,self.probability_history[-1])
            accepted=decision['accepted'];hz=candidate['hertz'] if accepted else 0.
        self.accepted_count=self.accepted_count+1 if accepted else 0
        self.history[1:]=self.history[2:]+[hz]
        self.probability_history[1:]=self.probability_history[2:]+[p]
        self.probability_history[0]=hz
        self.probabilities[ids[4]]=p
        values=[self.frames.get(i,0.) for i in ids[:4]]+[hz]
        repaired=repair(values)
        for identity,value in zip(ids,repaired['hertz']):self.frames[identity]=value
        error=max(abs(a-b) for a,b in zip(repaired['hertz'],row['native_f0']))
        history_error=max(abs(a-b) for a,b in zip(self.history,row['native_history']))
        probability_error=abs(p-row['native_probability'])
        probability_history_error=max(abs(a-b) for a,b in zip(self.probability_history[1:],row['native_probability_history']))
        if error>1e-3 or history_error>1e-3 or probability_error>3e-6 or probability_history_error>3e-6 or score_error>3e-6 or self.accepted_count!=row['native_count']:
            raise AssertionError(dict(time=row['time'],zero=zero,error=error,history_error=history_error,
                                      probability_error=probability_error,score_error=score_error,
                                      candidate=candidate if not zero else None,minima=([(i,score[i],row['native_score'][i]) for i in range(candidate['index'],min(2*candidate['index'],limit-1)) if score[i]<0.01] if not zero else []),derived=derived if not zero else None,native_score_derived=native_score_derived if not zero else None,native_features=row['native_features'],raw=row['raw_features'],count=self.accepted_count,native_count=row['native_count'],predicted=repaired['hertz'],actual=row['native_f0']))
        return dict(time=row['time'],silent=zero,max_error_hz=error,history_error_hz=history_error,
                    probability_error=probability_error,probability_history_error=probability_history_error,
                    score_error=score_error,events=repaired['events'])


def verify(dll,shape,frequency,native_fft=False,failure_output=None,fit_inputs=None,source_outputs=None,pcm_override=None):
    pcm=owned_pcm(shape,frequency) if pcm_override is None else [f32(v) for v in pcm_override]
    rows=[];raw=RawFeatureObserver(pcm,phase_controls=True);held=[];peak_inputs={};power_observers=[];tracking_rows=[]
    def install(native,context):
        held.append((native,context))
        power=PowerObserver(native,native_fft,pcm);power_observers.append(power)
        config=P.from_address(context+0xad0).value
        for key,want in ((2,22050.),(3,-300.),(0x24,0.),(0x70,1.),(0x7e,0.),(0x87,0.),(0x54,0.),(0x76,0.),(0x35,0.),(0x84,0.),(0x85,0.),(0x7d,0.)):
            if native.fn(0x2b050,F,P,I,F)(config,key,0.)!=want:
                raise AssertionError(('Unmodeled final-pitch configuration',key))
        e=P.from_address(context+0xc80).value
        factor,coefficients=runtime_coefficients(P.from_address(e+0x148).value)
        selected=selected_coefficients(native,44100,factor)
        if selected!=coefficients:raise AssertionError('FIR asset differs from constructor')
        coefficients=selected
        decimated=DecimatedSource(pcm,factor,coefficients)
        if I.from_address(e+0xa4).value!=10 or I.from_address(e+0x40).value!=0:
            raise AssertionError('Unexpected initial estimator state')
        if [F.from_address(e+0x50+4*j).value for j in range(11)]!=[22050.]+[0.]*10:
            raise AssertionError('Unexpected initial history')
        vtable=P.from_address(context).value;original=c.CFUNCTYPE(Q,P)(P.from_address(vtable+8).value)
        private=native.alloc(0x100);c.memmove(private,vtable,0x100)
        calls=0
        def step(ctx):
            nonlocal calls
            try:
                calls+=1
                if calls>10000:raise AssertionError('Step bound')
                token=raw.before(ctx);queue=P.from_address(ctx+0xc40).value
                ids=[P.from_address(P.from_address(queue+8*j).value+0x28).value for j in range(1,6)]
                before=Q.from_address(ids[4]+0x50).value
                power_token=power.before(ctx)
                peak_frame,peaks=capture(ctx,modeled=True,power_frames=power.frames,complex_frames=power.complex_frames)
                result=original(ctx);raw.after(ctx,token)
                power.after(ctx,power_token)
                output=P.from_address(ctx+0xcc8).value
                if output:
                    if output!=peak_frame:raise AssertionError('Peak input/output identity differs')
                    if output in peak_inputs:raise AssertionError('Repeated final peak input')
                    peak_inputs[output]=peaks
                    tracker=P.from_address(ctx+0xc98).value
                    if I.from_address(tracker+0x18).value!=0:raise AssertionError('Unmodeled tracking queue index')
                    track_count=I.from_address(tracker+0x1c).value
                    if not 0<=track_count<=1500:raise AssertionError('Tracker count bound')
                    tracking_rows.append(dict(frame=output,time=D.from_address(output+0x48).value,
                        config={key:native.fn(0x2b050,F,P,I,F)(config,key,0.) for key in (0,2,4,8,9,10,0xd,0x11,0x12,0x13,0x21,0x22,0x27,0x28,0x29,0x33)},
                        tracks=[dict(center=F.from_address(tracker+0x24+i*0x54).value,
                                     amplitude=F.from_address(tracker+0x20+i*0x54).value,
                                     current=I.from_address(tracker+0x2c+i*0x54).value) for i in range(track_count)]))
                if not Q.from_address(ids[4]+0x50).value&512:return result
                if before&512:raise AssertionError('Repeated estimator frame')
                count=I.from_address(e+0x2c).value;source=P.from_address(e+0x190).value
                if not source or not 4<=count<=8192:raise AssertionError('Unexpected prepared PCM')
                if I.from_address(e+0x18).value!=5 or I.from_address(e+0x44).value or I.from_address(e+0x48).value:
                    raise AssertionError('Unmodeled estimator configuration')
                actual_samples=[F.from_address(source+4*j).value for j in range(count)]
                samples=prepare_pitch_pcm(e,D.from_address(ids[4]+0x48).value,decimated)
                if len(samples)!=len(actual_samples):raise AssertionError('Pitch PCM count differs')
                if samples!=actual_samples:raise AssertionError(('Prepared pitch PCM differs',max(abs(a-b) for a,b in zip(samples,actual_samples))))
                factor=F.from_address(e+0x140).value
                nfft=1<<int(f32(f32(f32((count-1)*factor)+1)-2)).bit_length()
                score=P.from_address(e+0x100).value;feature=P.from_address(ids[4]+0x128).value
                if any(samples) and ids[4] not in raw.frames:raise AssertionError('Missing independent raw features')
                rows.append(dict(ids=ids,time=D.from_address(ids[4]+0x48).value,samples=samples,
                                 rate=D.from_address(e+0x188).value,factor=factor,kind=B.from_address(e+0x144).value,
                                 raw_features=raw.frames.get(ids[4],dict(feature4=0.,feature8=0.,feature12=0.)),
                                 native_score=[F.from_address(score+4*j).value for j in range(nfft//2)] if any(samples) else [],
                                 native_f0=[F.from_address(frame+0xf0).value for frame in ids],
                                 native_features={name:F.from_address(feature+offset).value for name,offset in [('confidence',0x14),('strength',0x214),('ratio',0x20c),('feature4',4),('feature8',8),('feature12',12)]} if feature else {},
                                 native_probability=F.from_address(feature+0x224).value if feature else 0.,
                                 native_history=[F.from_address(e+0x50+4*j).value for j in range(11)],
                                 native_probability_history=[F.from_address(e+0x7c+4*j).value for j in range(10)],
                                 native_count=I.from_address(e+0x40).value))
                return result
            except BaseException:
                if failure_output is not None:
                    failure_output.write_text(json.dumps(dict(dll_sha256=SHA256,state_stage_passed=False,
                        shape=shape,frequency=frequency,fft_backend='DSE/MKL' if native_fft else 'independent Python',
                        observer_failure=traceback.format_exc()),indent=2)+'\n',encoding='utf-8')
                traceback.print_exc();sys.stderr.flush();os._exit(2)
        callback=c.CFUNCTYPE(Q,P)(step);P.from_address(private+8).value=c.cast(callback,P).value
        P.from_address(context).value=private;native.keep.extend([original,callback])
    observed=run(dll,1,0,shape,frequency,analysis_mode=4,setup_observer=install,pcm_override=pcm)
    baseline=run(dll,1,0,shape,frequency,analysis_mode=4,pcm_override=pcm)
    if observed!=baseline:raise AssertionError('State observer changed output')
    native,context=held[0]
    backend=NativeFftBackend(native,P.from_address(context+0xc80).value) if native_fft else None
    state=PitchState(backend);comparisons=[state.process(row) for row in rows]
    tracking_state=dict(tracks=[],history=0.,initialized=False);tracking_error=0.;tracking_assignments=0;harmonic_outputs={}
    for row in tracking_rows:
        frame=row['frame'];peaks=peak_inputs[frame];hz=weighted_pitch(state.frames[frame],peaks)
        tracked=advance_outer(tracking_state,[list(p) for p in peaks],hz,row['config'],[0]*len(peaks),timestamp=row['time'])
        harmonic_outputs[frame]=[p if p is not None else [0.,10000.,0.] for p in tracked['output']]
        if len(tracking_state['tracks'])!=len(row['tracks']):raise AssertionError('Live track count mismatch')
        for i,(predicted,actual) in enumerate(zip(tracking_state['tracks'],row['tracks'])):
            if predicted['current']!=actual['current']:raise AssertionError(('Live assignment mismatch',row['time'],i,predicted['current'],actual['current']))
            for key in ('center','amplitude'):
                error=abs(predicted[key]-actual[key]);tracking_error=max(tracking_error,error)
                if error>1e-3:raise AssertionError(('Live tracker value mismatch',row['time'],i,key,predicted[key],actual[key]))
            tracking_assignments+=1
    native,context=held[0];track=P.from_address(context+0xcd0).value
    region_count=I.from_address(track+0x140).value;regions=P.from_address(track+0x148).value
    final_errors=[];final_mismatches=[];coarse_mismatches=0;harmonic_errors=[];phase_errors=[];gain_errors=[]
    field_counts={str(bit):0 for bit in (7,22,23,37)};envelope_errors=[]
    for i in range(region_count):
        region=P.from_address(regions+8*i).value;frames=P.from_address(region+0x20).value
        for j in range(I.from_address(region+0x18).value):
            frame=P.from_address(frames+8*j).value
            if frame not in state.frames:raise AssertionError('Final frame missing from independent state')
            value=F.from_address(frame+0xf0).value
            mask=Q.from_address(frame+0x50).value
            for bit in field_counts:field_counts[bit]+=bool(mask&(1<<int(bit)))
            actual=440*2**(value/1200) if Q.from_address(frame+0x50).value&(1<<31) else value
            if frame not in peak_inputs:raise AssertionError('Missing pre-tracking peaks')
            predicted=weighted_pitch(state.frames[frame],peak_inputs[frame])
            if not math.isfinite(predicted):raise AssertionError('Nonfinite weighted pitch')
            if source_outputs is not None:
                source_outputs.append(dict(time=D.from_address(frame+0x48).value,f0=predicted,
                                           harmonics=list(harmonic_outputs[frame]),
                                           phase_controls=raw.frames[frame]['phase_controls']))
            if predicted>0 and mask&1024:
                gain=harmonic_gain(harmonic_outputs[frame]);actual_gain=F.from_address(frame+0xf8).value
                gain_error=abs(gain-actual_gain)
                if gain_error>1e-4:raise AssertionError(('Final harmonic gain',gain,actual_gain))
                gain_errors.append(gain_error)
                if mask&128:
                    env=P.from_address(frame+0x108).value
                    if B.from_address(env+0x40).value!=3:raise AssertionError('Unmodeled E7 storage')
                    points=analysis_envelope(harmonic_outputs[frame],gain,F.from_address(env+0x44).value,F.from_address(env+0x4c).value,predicted)
                    count=I.from_address(env+0x5c).value;ptr=P.from_address(env+0x78).value
                    if len(points)!=count:raise AssertionError(('E7 count',len(points),count))
                    for k,(x,y) in enumerate(points):
                        error=max(abs(x-D.from_address(ptr+16*k).value),abs(y-F.from_address(ptr+16*k+8).value))
                        if error>1e-4:raise AssertionError(('E7 value',k,x,y,D.from_address(ptr+16*k).value,F.from_address(ptr+16*k+8).value))
                        envelope_errors.append(error)
                    if fit_inputs is not None:
                        fit_inputs.append(dict(time=D.from_address(frame+0x48).value,f0=predicted,gain=gain,points=points,
                                               harmonics=list(harmonic_outputs[frame])))
            if abs(state.frames[frame]-actual)>1e-3:coarse_mismatches+=1
            error=abs(predicted-actual)
            harmonic_count=I.from_address(frame+0x58).value
            frequencies=[p[0] for p in harmonic_outputs[frame]]
            if len(frequencies)!=harmonic_count:raise AssertionError(('Final harmonic count',len(frequencies),harmonic_count))
            pointer=P.from_address(frame+0x60).value
            for k,value in enumerate(frequencies):
                wanted=f32(value/predicted) if predicted>0 and Q.from_address(frame+0x50).value&(1<<20) else value
                delta=abs(wanted-F.from_address(pointer+4*k).value)
                if delta>1e-4:raise AssertionError(('Final harmonic frequency',D.from_address(frame+0x48).value,k,wanted,F.from_address(pointer+4*k).value))
                harmonic_errors.append(delta)
                phase_ptr=P.from_address(frame+0x70).value
                if Q.from_address(frame+0x50).value&4 and phase_ptr:
                    phase=harmonic_outputs[frame][k][2]
                    phase_error=abs((phase-F.from_address(phase_ptr+4*k).value+math.pi)%math.tau-math.pi)
                    if phase_error>1e-4:raise AssertionError(('Final harmonic phase',D.from_address(frame+0x48).value,k,phase_error))
                    phase_errors.append(phase_error)
            if error>1e-3:
                final_mismatches.append(dict(time=D.from_address(frame+0x48).value,
                                            predicted=predicted,actual=actual,error_hz=error))
            final_errors.append(error)
    if len(final_errors)!=observed['frames']:raise AssertionError('Final frame count differs')
    return dict(shape=shape,frequency=frequency,frames=len(rows),raw_frames=len(raw.comparisons),
                phase_control_frames=len(raw.comparisons),phase_control_max_error=max(x['phase_control_error'] for x in raw.comparisons),
                final_frames=len(final_errors),final_max_error_hz=max(final_errors),
                coarse_mismatches=coarse_mismatches,
                tracking_assignments=tracking_assignments,tracking_max_error=tracking_error,
                final_harmonic_values=len(harmonic_errors),final_harmonic_max_error=max(harmonic_errors,default=0.),
                final_phase_values=len(phase_errors),final_phase_max_error=max(phase_errors,default=0.),
                gain_values=len(gain_errors),gain_max_error=max(gain_errors,default=0.),field_counts=field_counts,
                e7_points=len(envelope_errors),e7_max_error=max(envelope_errors,default=0.),
                power_frames=len(power_observers[0].rows),power_max_relative_error=max(r['relative_error'] for r in power_observers[0].rows),
                power_geometries=sorted({(r['size'],r['kind'],r['lookup'],r['factor'],r['half']) for r in power_observers[0].rows}),
                prepared_pcm_values=sum(len(row['samples']) for row in rows),
                final_matches=not final_mismatches,final_mismatches=final_mismatches,
                observer_matches_baseline=True,comparisons=comparisons)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',required=True,type=Path);parser.add_argument('--output',required=True,type=Path)
    parser.add_argument('--frequencies',type=float,nargs='+',default=[110,220,440])
    parser.add_argument('--shapes',nargs='+',choices=['steady','attack','gap'],default=['steady','attack','gap'])
    parser.add_argument('--native-fft',action='store_true',help='Explicitly reuse DSE/MKL FFT primitives on owned buffers')
    args=parser.parse_args()
    if any(not 40<=f<=1500 for f in args.frequencies):parser.error('Frequency outside reviewed range')
    report=dict(dll_sha256=SHA256,fft_backend='DSE/MKL' if args.native_fft else 'independent Python',cases=[],
                scope='Every default mode4 frame from prepared PCM plus independently calculated raw-ring features; '
                      'candidate/probability histories and prior repaired F0 are independently advanced. '
                      'Native scheduler and selected embedded FIR coefficient assets remain upstream inputs. '
                      'Resampling, pitch window extraction and mean removal are modeled from owned source PCM. '
                      'Raw features and power spectra are calculated from owned source PCM; '
                      'power uses independent window/preparation and the named FFT backend; '
                      'peak frequencies and amplitudes are calculated independently from those spectra. '
                      'Final F0 uses independent repaired pitch and a modeled peak-weighted refinement; '
                      'mode1 track history advances independently and final relative frequency/phase slots are asserted. '
                      'Harmonic gain and analysis bit7 ENV points are independently reconstructed. '
                      'Native states and final fields are comparison targets; ordinary bit23/37, resonance/save are not covered.')
    for shape in args.shapes:
        for frequency in args.frequencies:
            try:report['cases'].append(verify(args.dll,shape,frequency,args.native_fft,args.output))
            except AssertionError as error:
                report.update(state_stage_passed=False,failure=dict(shape=shape,frequency=frequency,details=error.args))
                args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
                raise
    report['state_stage_passed']=True
    report['final_output_matches']=all(x['final_matches'] for x in report['cases'])
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(cases=len(report['cases']),frames=sum(x['frames'] for x in report['cases']),
                          state_stage_passed=True,fft_backend=report['fft_backend'],
                          final_output_matches=all(x['final_matches'] for x in report['cases']),
                          final_mismatched_frames=sum(len(x['final_mismatches']) for x in report['cases']))))
    if not report['final_output_matches']:raise AssertionError('Final output pitch mismatches')


if __name__=='__main__':main()
