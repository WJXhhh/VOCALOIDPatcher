"""Observe mode4's prepared PCM during an unchanged native batch step."""
import argparse
import ctypes as c
import json
import math
import os
from pathlib import Path
import sys
import traceback

from audit_frm2_numeric import F,D,I,P,Q,B,SHA256,f32
from probe_frm2_batch_outputs import run
from verify_frm2_analysis_windows import fft,prepare
from frm2_mode4_spectrum import convolve_packed,build_kernel_bank
from frm2_pitch_score import autocorrelation_score
from frm2_pitch_preprocess import preprocess
from frm2_pitch_candidate import candidate_with_history
from frm2_pitch_voicing import probability,gate,score_features
from frm2_pitch_repair import repair
from frm2_raw_feature_observer import RawFeatureObserver


def observe(dll,frequency,*,replay=False,score=False,dense=False):
    snapshots=[];holders=[];raw_observer=RawFeatureObserver()
    targets=(.03,.06,.09,.12,.15,.18,.21) if dense else (.08,.12,.18)
    def install(native,context):
        holders.append((native,context))
        vtable=P.from_address(context).value
        original=c.CFUNCTYPE(Q,P)(P.from_address(vtable+8).value)
        private=native.alloc(0x100);c.memmove(private,vtable,0x100)
        calls=0
        def step(ctx):
            nonlocal calls
            try:
                calls+=1
                if calls>10000:raise RuntimeError('Step bound exceeded')
                tracked_frame=None
                raw_token=raw_observer.before(ctx)
                if score:
                    e=P.from_address(ctx+0xc80).value
                    queue=P.from_address(ctx+0xc40).value
                    index=I.from_address(e+0x18).value
                    if I.from_address(e+0x44).value!=0:raise AssertionError('Alternate candidate branch not modeled')
                    if index!=5:raise AssertionError('Five-frame repair requires estimator index 5')
                    link=P.from_address(queue+index*8).value
                    tracked_frame=P.from_address(link+0x28).value
                    repair_frames=[P.from_address(P.from_address(queue+8*j).value+0x28).value for j in range(1,6)]
                    repair_before=[F.from_address(frame+0xf0).value for frame in repair_frames]
                    length=I.from_address(e+0xa4).value
                    if not 2<=length<=10:raise AssertionError('Unexpected F0 history size')
                    previous=P.from_address(P.from_address(queue+max(0,index-1)*8).value+0x28).value
                    history_context=dict(history=[F.from_address(e+0x50+4*j).value for j in range(length+1)],
                                         voiced_count=I.from_address(e+0x40).value,queue_index=index,
                                         previous_frame_f0=F.from_address(previous+0xf0).value)
                    previous_feature=P.from_address(previous+0x128).value
                    if I.from_address(e+0x48).value!=0:raise AssertionError('Unmodeled voicing mode')
                    voicing_context=dict(previous_probability=F.from_address(previous_feature+0x224).value if previous_feature else 0.,
                                         history_probability=F.from_address(e+0x78+4*length).value)
                result=original(ctx)
                raw_observer.after(ctx,raw_token)
                output=P.from_address(ctx+0xcc8).value
                if result==1 or not output:return result
                time=D.from_address(output+0x48).value
                if len(snapshots)==len(targets) or time<targets[len(snapshots)]:return result
                estimator=P.from_address(ctx+0xc80).value
                if not estimator:raise AssertionError('Missing mode4 object')
                size=I.from_address(estimator+0x2c).value
                rate=D.from_address(estimator+0x188).value
                source=P.from_address(estimator+0x190).value
                if not 4<=size<=8192 or not 1000<=rate<=96000 or not source:
                    raise AssertionError(('Unexpected prepared PCM',size,rate))
                samples=[F.from_address(source+4*i).value for i in range(size)]
                if not all(math.isfinite(v) for v in samples):raise AssertionError('Nonfinite PCM')
                snapshot=dict(time=time,sample_rate=rate,sample_count=size,samples=samples,
                              factor=F.from_address(estimator+0x140).value)
                if replay or score:
                    nfft=1<<int(f32(f32(f32((size-1)*snapshot['factor'])+1)-2)).bit_length()
                    scalar=P.from_address(estimator+0xa8).value
                    if not scalar or nfft>16384:raise AssertionError('Unexpected spectrum size')
                    snapshot['observed_scalar']=[F.from_address(scalar+4*i).value for i in range(nfft//2+1)]
                    snapshot['window_kind']=B.from_address(estimator+0x144).value
                    if score:
                        snapshot['estimator_frame_f0']=F.from_address(tracked_frame+0xf0).value
                        if Q.from_address(tracked_frame+0x50).value&(1<<31):raise AssertionError('Expected Hz queue frame')
                        snapshot['estimator_frame_time']=D.from_address(tracked_frame+0x48).value
                        snapshot['history_context']=history_context
                        snapshot['repair_before']=repair_before
                        snapshot['repair_after']=[F.from_address(frame+0xf0).value for frame in repair_frames]
                        feature=P.from_address(tracked_frame+0x128).value
                        if not feature:raise AssertionError('Missing frame features')
                        snapshot['voicing_features']={name:F.from_address(feature+offset).value for name,offset in
                            [('feature4',4),('feature8',8),('feature12',12),('confidence',0x14),('strength',0x214),('ratio',0x20c)]}
                        snapshot['independent_raw_features']=raw_observer.frames[tracked_frame]
                        snapshot['native_probability']=F.from_address(feature+0x224).value
                        snapshot['voicing_context']=voicing_context
                        values=P.from_address(estimator+0xc8).value
                        scores=P.from_address(estimator+0x100).value
                        if not values or not scores:raise AssertionError('Missing pitch score buffers')
                        snapshot['score_input']=[F.from_address(values+4*i).value for i in range(nfft//2)]
                        snapshot['score_actual']=[F.from_address(scores+4*i).value for i in range(nfft//2)]
                        for name,offset in [('log_actual',0xd0),('baseline_actual',0xd8)]:
                            array=P.from_address(estimator+offset).value
                            snapshot[name]=[F.from_address(array+4*i).value for i in range(nfft//2)]
                snapshots.append(snapshot)
                return result
            except BaseException:
                traceback.print_exc();sys.stderr.flush();os._exit(2)
        callback=c.CFUNCTYPE(Q,P)(step)
        P.from_address(private+8).value=c.cast(callback,P).value
        P.from_address(context).value=private
        native.keep.extend([original,callback])
    result=run(dll,1,0,'steady',frequency,analysis_mode=4,setup_observer=install)
    baseline=run(dll,1,0,'steady',frequency,analysis_mode=4)
    if result!=baseline:raise AssertionError('Observer changed native output')
    if len(snapshots)!=len(targets):raise AssertionError('Missing mid-unit snapshots')
    for snapshot in snapshots:
        generated=None
        if replay:
            native,context=holders[0]
            generated=replay_spectrum(native,P.from_address(context+0xc80).value,snapshot)
        if score:
            values=snapshot.pop('score_input')
            nyquist=f32(snapshot['sample_rate']*.5)
            pre=preprocess(generated[:-1],nyquist)
            snapshot['preprocess_comparison']=dict(
                log_max_error=max(abs(a-b) for a,b in zip(pre['logs'],snapshot.pop('log_actual'))),
                baseline_max_error=max(abs(a-b) for a,b in zip(pre['baseline'],snapshot.pop('baseline_actual'))),
                processed_max_error=max(abs(a-b) for a,b in zip(pre['processed'],values)))
            if max(snapshot['preprocess_comparison'].values())!=0:
                raise AssertionError(('Independent spectral preprocessing differs',snapshot['preprocess_comparison']))
            limit=int(f32(f32(min(3000.,nyquist)/nyquist)*len(values)))
            expected=autocorrelation_score(pre['processed'],normalization_bins=limit)
            standard=autocorrelation_score(pre['processed'],normalization_bins=limit,standard_nyquist=True)
            actual=snapshot.pop('score_actual')
            error=max(abs(a-b) for a,b in zip(expected,actual))
            if error>2e-6:raise AssertionError(('Pitch autocorrelation model differs',error))
            snapshot['score_comparison']=dict(values=len(actual),max_error=error,normalization_bins=limit,
                                              standard_nyquist_max_error=max(abs(a-b) for a,b in zip(standard,actual)),
                                              actual_max=max(actual),predicted_max=max(expected))
            history_context=snapshot.pop('history_context')
            snapshot['candidate']=candidate_with_history(expected,nyquist,**history_context)
            derived=score_features(expected,snapshot['candidate']['index'],nyquist,limit,snapshot['factor'])
            native_score_features=score_features(actual,snapshot['candidate']['index'],nyquist,limit,snapshot['factor'])
            snapshot['score_feature_comparison']={name:dict(
                independent_error=abs(value-snapshot['voicing_features'][name]),
                native_score_error=abs(native_score_features[name]-snapshot['voicing_features'][name]))
                for name,value in derived.items()}
            p=probability(dict(snapshot['independent_raw_features'],**derived))
            snapshot['native_feature_probability_error']=abs(probability(snapshot['voicing_features'])-snapshot['native_probability'])
            snapshot['voicing']=gate(p,history=history_context['history'],voiced_count=history_context['voiced_count'],
                                    previous_frame_f0=history_context['previous_frame_f0'],**snapshot.pop('voicing_context'))
            snapshot['voicing'].update(probability=p,probability_error=abs(p-snapshot['native_probability']))
            if history_context['queue_index']==0 or snapshot['estimator_frame_time']<=0:
                raise AssertionError('Voicing gate requires positive-time nonzero queue index')
            if snapshot['native_feature_probability_error']>2e-7 or snapshot['voicing']['probability_error']>2e-6:
                raise AssertionError('Voicing probability differs')
            for name,comparison in snapshot['score_feature_comparison'].items():
                if comparison['native_score_error']!=0 or comparison['independent_error']>3e-6:
                    raise AssertionError(('Score feature differs',name,comparison))
            candidate=snapshot['candidate'];actual_f0=snapshot['estimator_frame_f0']
            if actual_f0>0:
                difference=abs(candidate['hertz']-actual_f0)
                if difference>1e-3:raise AssertionError(('Candidate differs from positive queue frame',difference))
                candidate.update(comparison='positive_match',absolute_error_hz=difference)
            elif candidate['hertz']==0:candidate['comparison']='zero_consistent_not_selection_proof'
            else:candidate['comparison']='zero_frame_requires_downstream_analysis'
            gated=candidate['hertz'] if snapshot['voicing']['accepted'] else 0.
            snapshot['voicing'].update(gated_hertz=gated,gated_error_hz=abs(gated-actual_f0))
            if abs(gated-actual_f0)>1e-3:raise AssertionError('Gated queue F0 differs')
            if candidate['hertz']>0 and actual_f0==0:
                candidate['comparison']='zero_explained_by_voicing_gate'
            repair_input=snapshot.pop('repair_before');repair_input[4]=gated
            repaired=repair(repair_input)
            repaired['max_error_hz']=max(abs(a-b) for a,b in zip(repaired['hertz'],snapshot.pop('repair_after')))
            if repaired['max_error_hz']>1e-3:raise AssertionError('Independent chain through repair differs')
            snapshot['repair_comparison']=repaired
        samples=snapshot.pop('samples')
        # Independent diagnostic DFT, not ed230 window equivalence. The latest
        # estimator buffer may precede the emitted frame by the native latency.
        nfft=1<<(len(samples)*8-1).bit_length()
        windowed=[v*(.5-.5*math.cos(math.tau*i/(len(samples)-1))) for i,v in enumerate(samples)]
        spectrum=fft(windowed+[0.]*(nfft-len(samples)))
        best=max(range(1,nfft//2),key=lambda k:abs(spectrum[k]))
        bin_width=snapshot['sample_rate']/nfft
        dominant=best*bin_width
        # The original interior steady windows retain their diagnostic gate.
        # Dense capture also includes edge windows; record their peak deviation
        # rather than treating this diagnostic as a transform equivalence test.
        if not dense and abs(dominant-frequency)>bin_width:
            raise AssertionError(('Prepared PCM dominant frequency differs',frequency,dominant))
        snapshot.update(diagnostic_fft_size=nfft,diagnostic_bin_width_hz=bin_width,
                        diagnostic_dominant_hz=dominant)
        if dense:snapshot['diagnostic_within_one_bin']=abs(dominant-frequency)<=bin_width
    return dict(input_frequency=frequency,output_f0_median=result['f0_median'],
                voiced_frames=result['voiced'],observation_matches_baseline=True,snapshots=snapshots)


def replay_spectrum(native,estimator,snapshot):
    # Executed only after the observed batch and output comparison have finished.
    # It reuses that completed experiment's transform cache, never a live batch.
    transform=P.from_address(estimator+8).value
    call=native.fn(0xed230,Q,P,P,I,B,F,P,P,P,B,P,F,I,F)
    scalar,packed,half=P(),P(),I()
    code=call(transform,native.array(F,snapshot['samples']),snapshot['sample_count'],
              snapshot['window_kind'],F.from_address(native.lib._handle+0x5bffa4).value,
              c.byref(scalar),0,c.byref(half),2,c.byref(packed),snapshot['factor'],1,snapshot['sample_rate'])
    if code or not 1<=half.value<=8192:raise AssertionError(('Spectrum replay failed',code,half.value))
    actual=[F.from_address(scalar.value+4*i).value for i in range(half.value+1)]
    observed=snapshot.pop('observed_scalar')
    if len(actual)!=len(observed):raise AssertionError('Replay spectrum size differs')
    if not all(math.isfinite(v) for v in actual+observed):raise AssertionError('Nonfinite spectrum')
    if actual!=observed:raise AssertionError('Standalone replay differs from captured production spectrum')
    peaks=[i for i in range(1,half.value-1) if actual[i]>actual[i-1] and actual[i]>=actual[i+1]]
    peaks=sorted(peaks,key=lambda i:actual[i],reverse=True)[:5]
    snapshot['replay']=dict(half=half.value,max_scalar_difference=max(abs(a-b) for a,b in zip(actual,observed)),
                            equal_scalars=sum(a==b for a,b in zip(actual,observed)),
                            strongest_local_peaks_hz=[i*snapshot['sample_rate']/(2*half.value) for i in peaks])
    if snapshot['window_kind']==15:raise AssertionError('Complex kernel branch not modeled')
    width=I.from_address(transform+0x1c0).value
    center=I.from_address(transform+0x1c4).value
    groups=I.from_address(transform+0x1c8).value
    if not 1<=width<=1024 or not 1<=groups<=8192:raise AssertionError('Unexpected kernel dimensions')
    mapping=P.from_address(transform+0x198).value
    coefficients=P.from_address(transform+0x1a8).value
    selections=[I.from_address(mapping+4*i).value for i in range(half.value+1)]
    if not all(0<=i<groups for i in selections):raise AssertionError('Invalid kernel selection')
    kernels=[[F.from_address(coefficients+4*(g*width+j)).value for j in range(width)] for g in range(groups)]
    window=P.from_address(transform+0x168).value
    weights=[F.from_address(window+4*i).value for i in range(snapshot['sample_count'])]
    independent=build_kernel_bank(snapshot['sample_count'],snapshot['sample_rate'],snapshot['factor'],snapshot['window_kind'])
    native_lengths=P.from_address(transform+0x1a0).value
    lengths=[I.from_address(native_lengths+4*g).value for g in range(groups)]
    if (independent['width'],independent['center'],independent['lengths'],independent['selections'])!=(width,center,lengths,selections):
        raise AssertionError('Independent kernel layout differs')
    kernel_error=max(abs(a-b) for ka,kb in zip(kernels,independent['kernels']) for a,b in zip(ka,kb))
    if kernel_error or weights!=independent['initial_window']:
        raise AssertionError(('Independent kernel/window differs',kernel_error))
    # Only independently generated arrays feed the end-to-end prediction below.
    kernels=independent['kernels'];selections=independent['selections'];weights=independent['initial_window'];center=independent['center']
    prepared=prepare(snapshot['samples'],weights,snapshot['factor'],True)
    predicted=convolve_packed(prepared,selections,kernels,center)
    packed_actual=[F.from_address(packed.value+4*i).value for i in range(half.value*2)]
    error=max(abs(a-b) for a,b in zip(predicted,packed_actual))
    if error!=0:raise AssertionError(('Time-buffer convolution model differs',error))
    predicted_scalar=[abs(predicted[0])]
    predicted_scalar.extend(f32(f32(predicted[2*i]**2)+f32(predicted[2*i+1]**2)) for i in range(1,half.value))
    predicted_scalar.append(abs(predicted[1]))
    scalar_error=max(abs(a-b) for a,b in zip(actual,predicted_scalar))
    if scalar_error!=0:raise AssertionError(('Packed-to-scalar model differs',scalar_error))
    transformed=fft(prepared)
    dft_packed=[f32(transformed[0].real),f32(transformed[half.value].real)]
    for z in transformed[1:half.value]:dft_packed.extend([f32(z.real),f32(z.imag)])
    dft_candidate=convolve_packed(dft_packed,selections,kernels,center)
    snapshot['replay'].update(kernel_width=width,kernel_groups=groups,
                              independent_kernel_values=width*groups,independent_kernel_max_error=kernel_error,
                              packed_values=len(predicted),time_buffer_model_max_error=error,
                              scalar_model_max_error=scalar_error,
                              dft_candidate_max_error=max(abs(a-b) for a,b in zip(dft_candidate,packed_actual)))
    return predicted_scalar



def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll',required=True,type=Path)
    parser.add_argument('--output',required=True,type=Path)
    parser.add_argument('--replay-spectrum',action='store_true')
    parser.add_argument('--candidate-score',action='store_true')
    parser.add_argument('--dense-snapshots',action='store_true')
    args=parser.parse_args()
    if args.candidate_score and not args.replay_spectrum:parser.error('--candidate-score requires --replay-spectrum')
    report=dict(dll_sha256=SHA256,cases=[observe(args.dll,f,replay=args.replay_spectrum,score=args.candidate_score,dense=args.dense_snapshots) for f in (110,220,440)],
                scope='Prepared PCM after native mode4 step; optional standalone ed230 replay and '
                      'independent centered kind10 spectrum, preprocessing and optional autocorrelation from prepared PCM; '
                      'default candidate and voicing gate use captured pre-step history; raw features +4/+8/+c are independently calculated from native PCM rings; '
                      'score features and five-frame repair are independent; prior four frame values are native inputs')
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    snapshots=[s for case in report['cases'] for s in case['snapshots']]
    summary=dict(cases=len(report['cases']),snapshots=len(snapshots),
                 observer_matches_baseline=all(x['observation_matches_baseline'] for x in report['cases']))
    if args.replay_spectrum:
        summary.update(packed_values=sum(s['replay']['packed_values'] for s in snapshots),
                       max_error=max(s['replay']['time_buffer_model_max_error'] for s in snapshots))
    if args.candidate_score:
        summary.update(score_values=sum(s['score_comparison']['values'] for s in snapshots),
                       score_max_error=max(s['score_comparison']['max_error'] for s in snapshots))
        summary.update(positive_candidates=sum(s['candidate']['comparison']=='positive_match' for s in snapshots),
                       explained_zero_frames=sum(s['candidate']['comparison']=='zero_explained_by_voicing_gate' for s in snapshots),
                       probability_max_error=max(s['voicing']['probability_error'] for s in snapshots),
                       gated_f0_max_error_hz=max(s['voicing']['gated_error_hz'] for s in snapshots))
    print(json.dumps(summary))


if __name__=='__main__':main()
