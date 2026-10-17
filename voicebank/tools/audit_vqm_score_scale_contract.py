"""Bound uniform score scaling without choosing gains from commercial targets.

Capture the same live arrays and native checks as evidence395, in memory only.
Positive gain is eliminated analytically through confidence polynomial bounds.
Certificates are conditional on the same array/formula and finite normal-or-zero
float32 multiplication, accumulation and division; no maker recovery is claimed.
"""
from __future__ import annotations

import argparse
from collections import OrderedDict
import hashlib
import inspect
import json
import math
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256, f32
from frm2_pitch_voicing import score_features
import verify_vqm_score_conditioning as observed_scores
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences


U = 2.0**-24
TOLERANCE = 1e-6


def outward(lo,hi):
    assert math.isfinite(lo) and math.isfinite(hi) and lo <= hi
    if lo == hi == 0:
        return 0.,0.
    return math.nextafter(lo,-math.inf),math.nextafter(hi,math.inf)


def rounded(interval):
    lo,hi = interval
    radius = max(abs(lo),abs(hi))*U
    result = outward(lo-radius,hi+radius)
    return (max(0.,result[0]),result[1]) if lo >= 0 else result


def add(a,b):
    return rounded(outward(a[0]+b[0],a[1]+b[1]))


def subtract(a,b):
    return rounded(outward(a[0]-b[1],a[1]-b[0]))


def multiply(a,b):
    values = [x*y for x in a for y in b]
    return rounded(outward(min(values),max(values)))


def square(a):
    lo,hi = a
    low = 0. if lo <= 0 <= hi else min(lo*lo,hi*hi)
    result = rounded(outward(low,max(lo*lo,hi*hi)))
    return max(0.,result[0]),result[1]


def divide(a,b):
    assert b[0] > 0
    values = [x/y for x in a for y in b]
    return rounded(outward(min(values),max(values)))


def nonnegative(a):
    return max(0.,a[0]),max(0.,a[1])


def extrema(values,minimum):
    operation = min if minimum else max
    return operation(p[0] for p in values),operation(p[1] for p in values)


def moments(scores,nyquist):
    low = int(f32(f32(2000/nyquist)*len(scores)))
    high = int(f32(f32(5000/nyquist)*len(scores)))
    assert 0 <= low <= high < len(scores)
    intervals = [rounded((s,s)) for s in scores]
    total,squares = (0.,0.),(0.,0.)
    for value in intervals[low:high+1]:
        total = add(total,value)
        squares = add(squares,square(value))
    if total[0] <= 0:
        return None
    mean = divide(squares,total)
    variance = (0.,0.)
    for value in intervals[low:high+1]:
        variance = add(variance,square(subtract(value,mean)))
    variance = nonnegative(divide(variance,(high-low+1.,high-low+1.)))
    deviation = nonnegative(rounded(outward(math.sqrt(variance[0]),math.sqrt(variance[1]))))
    return dict(intervals=intervals,strength_coefficient=nonnegative(multiply(mean,deviation)))


def polynomial(scores,index,limit,factor,intervals):
    peak = intervals[index]
    right_indices = [index]+list(range(index,min(2*index,limit-1)))
    earliest_minimum = min(right_indices,key=lambda i:scores[i])
    # Positive constant scaling followed by float32 rounding is monotone.
    # Equal source values remain equal: do not invent independent errors in
    # exact-zero contrasts, even when the allowed gain is arbitrarily large.
    if scores[index] == scores[earliest_minimum]:
        return 1,(0.,0.),(0.,0.)
    right_min = intervals[earliest_minimum]
    difference = nonnegative(subtract(peak,right_min))
    degree = 1
    if f32(index*f32(2.3)) < limit:
        # Rounding may tie adjacent minima and change the selected minimum
        # index. Include every interval-compatible minimum, not one raw index.
        possible = [i for i in right_indices if i <= earliest_minimum and intervals[i][0] <= right_min[1]
                    and intervals[i][1] >= right_min[0]]
        maxima = []
        for start in possible:
            maxima.append(extrema([right_min]+intervals[start:min(int(index*2.3),limit-1)],False))
        right_max = min(p[0] for p in maxima),max(p[1] for p in maxima)
        if len(set(possible)) == 1 and max([scores[earliest_minimum]]+
                scores[earliest_minimum:min(int(index*2.3),limit-1)]) == scores[earliest_minimum]:
            return 1,(0.,0.),(0.,0.)
        difference = nonnegative(multiply(difference,nonnegative(subtract(right_max,right_min))))
        degree = 2
    left_index = min(range(int(index*.2),index+1),key=lambda i:scores[i])
    contrast = ((0.,0.) if scores[index] == scores[left_index]
                else nonnegative(subtract(peak,intervals[left_index])))
    low,high = f32(factor*4),f32(factor*8)
    if index >= high:
        a,b = (0.,0.),contrast
    else:
        weight = max(0.,f32(f32(index-low)/f32(high-low)))
        if weight == 0:
            a,b = (1.,1.),(0.,0.)
        else:
            # Enclose three rounded operations in (gain*C-1)*w+1.
            # 8u*(gain*C_upper+1) dominates their absolute roundoff.
            a = outward(1-weight-8*U,1-weight+8*U)
            b = outward(weight*contrast[0]-8*U*contrast[1],
                        weight*contrast[1]+8*U*contrast[1])
    aa,bb = multiply(difference,a),multiply(difference,b)
    return degree,aa,bb


def root(target,degree,a,b):
    if target <= 0:
        return 0.
    # a may be slightly negative in a lower envelope. For b>0 and target>0
    # there is exactly one root after the polynomial crosses zero.
    if b <= 0 and a <= 0:
        return None
    if b < 0:
        return None
    def value(gain):
        return gain**degree*(a+b*gain)
    upper = 1.
    for _ in range(128):
        if value(upper) >= target:
            break
        upper *= 2
    else:
        return None
    lower = 0.
    for _ in range(80):
        middle = (lower+upper)*.5
        if value(middle) < target:
            lower = middle
        else:
            upper = middle
    # Enclose binary64 evaluation and root-bracketing roundoff as well as
    # float32 arithmetic. Degree<=2; the positive-root slope is at least
    # target/gain, including a negative lower-envelope constant.
    padding = max(abs(lower),abs(upper))*2.0**-48
    return max(0.,math.nextafter(lower-padding,-math.inf)),math.nextafter(upper+padding,math.inf)


def bounds(scores,index,nyquist,limit,factor,target_confidence,moment):
    if target_confidence < -TOLERANCE:
        return dict(confidence_infeasible=True)
    if moment is None:
        return None
    degree,a,b = polynomial(scores,index,limit,factor,moment['intervals'])
    low_target = max(0.,target_confidence-TOLERANCE)
    high_target = max(0.,target_confidence+TOLERANCE)
    lower_root = root(low_target,degree,a[1],b[1])
    upper_root = root(high_target,degree,a[0],b[0])
    if lower_root is None:
        return dict(confidence_infeasible=True)
    if upper_root is None:
        return None
    gain_low = lower_root[0] if isinstance(lower_root,tuple) else lower_root
    gain_high = upper_root[1] if isinstance(upper_root,tuple) else upper_root
    gain_low = max(0.,math.nextafter(gain_low,-math.inf))
    gain_high = math.nextafter(gain_high,math.inf)
    coefficient = moment['strength_coefficient']
    multiplier = min(low_target,f32(.3)),min(high_target,f32(.3))
    # strength = round(round(round(dev*mean)*min(conf,.3))/.3).
    coefficient = divide(nonnegative(multiply(coefficient,multiplier)),(f32(.3),f32(.3)))
    strength = outward(gain_low*gain_low*coefficient[0],gain_high*gain_high*coefficient[1])
    return dict(gain_interval=(gain_low,gain_high),strength_interval=strength,
                confidence_infeasible=False)


def owned_checks():
    checks = 0
    for shape in range(3):
        scores = [f32(.1+.01*((i*(shape+3))%17)) for i in range(256)]
        for index in (4,6,12,70):
            source = list(scores);source[index] = 1.25
            moment = moments(source,6000.)
            for gain in (.001,.3,1.,16.,1000.):
                scaled = [f32(gain*s) for s in source]
                produced = score_features(scaled,index,6000.,128,1.)
                result = bounds(source,index,6000.,128,1.,produced['confidence'],moment)
                assert result and not result['confidence_infeasible'],(shape,index,gain)
                assert result['gain_interval'][0] <= gain <= result['gain_interval'][1],(shape,index,gain,result)
                assert result['strength_interval'][0] <= produced['strength'] <= result['strength_interval'][1]
                checks += 1
    flat = [f32(.2)]*256
    assert bounds(flat,12,6000.,128,1.,.3,moments(flat,6000.))['confidence_infeasible']
    assert bounds(flat,12,6000.,128,1.,-1.,moments(flat,6000.))['confidence_infeasible']
    checks += 2
    tied = [f32(.1)]*256
    tied[12:28] = [f32(.6)]*16
    tied[12],tied[15],tied[17] = 1.25,f32(.9),f32(.2)
    tied[14] = f32(.20000002)
    for gain in (.001,.125,.3,1.,16.,1000.):
        produced = score_features([f32(gain*s) for s in tied],12,6000.,128,1.)
        result = bounds(tied,12,6000.,128,1.,produced['confidence'],moments(tied,6000.))
        assert result and not result['confidence_infeasible']
        assert result['gain_interval'][0] <= gain <= result['gain_interval'][1]
        assert result['strength_interval'][0] <= produced['strength'] <= result['strength_interval'][1]
        checks += 1
    return dict(cases=checks,passed=True,
        scope='Scaled float32 fixtures: low-bin weights, both confidence degrees, rounding-tied minima, exact-zero and sign infeasibility; not native maker recovery')


def child(args):
    captured = OrderedDict()
    original = observed_scores.score_features
    def spy(scores,index,nyquist,limit,factor=1.):
        frame = inspect.currentframe()
        try:
            caller = frame.f_back
            if caller.f_code.co_name == '<dictcomp>':
                assert caller.f_globals is observed_scores.__dict__
                caller = caller.f_back
            assert caller.f_code.co_name == 'child' and caller.f_globals is observed_scores.__dict__
            at = caller.f_locals['at']
        finally:
            del caller,frame
        assert isinstance(at,int)
        if at not in captured:
            captured[at] = (scores,nyquist,limit,factor)
        else:
            assert captured[at][0] is scores and captured[at][1:] == (nyquist,limit,factor)
        return original(scores,index,nyquist,limit,factor)
    observed_scores.score_features = spy
    try:
        baseline = observed_scores.child(args)
    finally:
        observed_scores.score_features = original
    assert baseline['zero_pcm_frames_skipped'] == 0 and set(captured) == set(range(baseline['frames']))
    _,_,sequences = load_sequences(args.manifest,args.voice_root,args.ddb_tools)
    sequences += auxiliary_sequences(args.aux_vdb)
    sequence = sequences[args.sample]
    counts = dict(frames=0,indices=0,unresolved_indices=0,compatible_indices=0,
        frames_with_scale_candidate=0,frames_with_unresolved_index=0,regular_scale_excluded_frames=0,
        negative_target_confidence_frames=0,confidence_infeasible_indices=0)
    strength_gap = []
    for at,(scores,nyquist,limit,factor) in captured.items():
        if at < 12 or at >= len(sequence['frames'])-12:
            continue
        moment = moments(scores,nyquist)
        target = sequence['frames'][at]['features']
        low = max(1,int(f32(f32(len(scores)*f32(40/nyquist))+1e-8)))
        high = min(limit-1,int(f32(f32(len(scores)*f32(1000/nyquist))+1e-8))+1)
        compatible = unresolved = 0
        gaps = []
        for index in [0]+list(range(low,high+1)):
            counts['indices'] += 1
            result = bounds(scores,index,nyquist,limit,factor,target[4],moment)
            if result is None:
                unresolved += 1;continue
            if result['confidence_infeasible']:
                counts['confidence_infeasible_indices'] += 1
                continue
            lo,hi = result['strength_interval']
            gap = max(lo-target[15],target[15]-hi,0.)
            gaps.append(gap)
            compatible += gap <= TOLERANCE
        counts['frames'] += 1
        counts['negative_target_confidence_frames'] += target[4] < -TOLERANCE
        counts['compatible_indices'] += compatible
        counts['unresolved_indices'] += unresolved
        counts['frames_with_scale_candidate'] += compatible > 0
        counts['frames_with_unresolved_index'] += unresolved > 0
        counts['regular_scale_excluded_frames'] += not compatible and not unresolved
        if not unresolved and gaps:
            strength_gap.append(min(gaps))
    return dict(case=sequence.get('category',args.sample),special_pcm=args.special_pcm,
        counts=counts,conditional_minimum_strength_gap=dict(count=len(strength_gap),
            minimum=min(strength_gap) if strength_gap else None,maximum=max(strength_gap) if strength_gap else None),
        live395_observer_matches_baseline=baseline['observer_matches_baseline'],
        original395_contract=baseline['contract'],original395_replay=baseline['independent_formula_to_native'],
        captured_score_rows=len(captured),parser_sha256=baseline['parser_sha256'],ddi_sha256=baseline['ddi_sha256'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll','manifest','voice-root','ddb-tools','aux-vdb'):
        parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--output',type=Path)
    parser.add_argument('--child',action='store_true')
    parser.add_argument('--sample',type=int,choices=range(5))
    parser.add_argument('--special-pcm',type=int,choices=(0,1),default=0)
    args = parser.parse_args()
    if args.child:
        print(json.dumps(child(args)));return
    assert args.output
    checks = owned_checks()
    rows = []
    for special in (0,1):
        for sample in range(5):
            command = [sys.executable,'-B',str(Path(__file__).resolve()),'--child',
                '--sample',str(sample),'--special-pcm',str(special)]
            for name in ('dll','manifest','voice-root','ddb-tools','aux-vdb'):
                command += ['--'+name,str(getattr(args,name.replace('-','_')))]
            result = subprocess.run(command,capture_output=True,text=True,timeout=120)
            assert result.returncode == 0,result.stderr[-2000:]
            row = json.loads(result.stdout);rows.append(row)
            print(json.dumps(row),flush=True)
    sources = ('audit_vqm_score_scale_contract.py','verify_vqm_score_conditioning.py',
        'frm2_pitch_voicing.py','frm2_pitch_candidate.py','probe_frm2_batch_outputs.py',
        'verify_vqm_estimator_producer_variants.py','verify_vqm_bit16_spectral_power.py',
        'verify_vqm_bit16_peak_interpolation.py','audit_frm2_numeric.py')
    report = dict(schema=1,dse_sha256=SHA256,
        auxiliary_sha256=hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest(),
        source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        owned_checks=checks,cases=rows,
        summary={key:sum(row['counts'][key] for row in rows) for key in rows[0]['counts']},
        scope='Actual395 live score arrays and native replay; superset40..1000Hz plus fallback0; '
            'gain eliminated by polynomial and conservative roundoff intervals, independently for each frame/index. '
            'Only bit4/bit15 pair is required; bit14 is not assumed to survive its later overwrites.',
        limits=['Finite regular float32 score scaling and intermediate operations; subnormal/overflow cases not certified',
            'Same score shape, moments, geometry and default scoring formula, not another producer or nonlinear transform',
            'An interval-compatible candidate is not an identified gain, original maker setting or reconstructed score array',
            'Positive gain allowed independently per frame; edges12 excluded; zero PCM cannot be silently reindexed',
            'No commercial score arrays, PCM, gain witnesses or proprietary code persisted'],
        installed_files_unchanged=True,no_windows_temp=True)
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__ == '__main__':
    main()
