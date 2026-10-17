"""Primary reference cache from filtered peaks and pre-step pitch history.

Candidate eligibility includes the harmonic amplitude-pattern curves. This
module produces its own costs/cache; native costs/cache are never inputs.
"""
from audit_frm2_numeric import f32
from frm2_peak_candidate_score import candidate_score,peak_ranks


def candidate_eligible(peaks,hz,lower,upper,pattern_threshold,peak_threshold):
    if hz < lower or hz > upper:
        return False
    count = len(peaks)
    if count <= 6 or (pattern_threshold <= 0 and peak_threshold <= 0):
        return True
    nearest = []
    for harmonic in range(1,min(10,count)+1):
        target = f32(f32(harmonic)*hz)
        best = 0
        minimum = 1e8
        for i,(frequency,db) in enumerate(peaks):
            difference = abs(f32(target-frequency))
            if minimum <= difference:
                best = i-1
                break
            best,minimum = i,difference
        nearest.append(best)
    if peak_threshold > 0:
        for i in range(5):
            up = f32(peaks[nearest[i+1]][1]-peaks[nearest[i]][1])
            down = f32(peaks[nearest[i+2]][1]-peaks[nearest[i+1]][1])
            # Retain the reviewed strict comparisons. With a positive
            # threshold, the final down>threshold test cannot reject.
            if up > 0 and down < 0 and up > peak_threshold and down > peak_threshold:
                return False
    if pattern_threshold > 0:
        contrast = 0.
        for i in (0,2,4):
            up = f32(peaks[nearest[i+1]][1]-peaks[nearest[i]][1])
            down = f32(peaks[nearest[i+2]][1]-peaks[nearest[i+1]][1])
            if up <= 0 or down >= 0:
                return True
            contrast = f32(f32(contrast+up)-down)
        if f32(float(contrast)/6.) > pattern_threshold:
            return False
    return True


def reference_cache(peaks,prior_pitch,previous,voiced_count,lower,upper,fallback,feature_gate,eligibility):
    if not peaks:
        return None
    ranks = peak_ranks(peaks)
    def eligible(hz):
        return candidate_eligible(peaks,hz,lower,upper,*eligibility)
    seed,source = None,None
    if eligible(prior_pitch):
        seed,source = prior_pitch,'prior'
    elif voiced_count > 1:
        extrapolated = f32(f32(previous[0]-previous[1])+previous[0])
        if eligible(extrapolated):
            seed,source = extrapolated,'extrapolated'
    if seed is None:
        for rank in ranks[:2]:
            maximum_hz = peaks[rank][0]
            divisor = 1
            while True:
                regular = f32(maximum_hz/f32(divisor))
                if regular < lower or regular < .5:
                    break
                if eligible(regular):
                    seed,source = regular,'dominant_peak_division'
                    break
                intermediate = f32(float(maximum_hz)/(float(divisor-1)+1.5))
                if eligible(intermediate):
                    seed,source = intermediate,'dominant_peak_half_division'
                    break
                divisor += 1
                if divisor > 10000:
                    raise ValueError('Candidate seed search exceeds reviewed bound')
            if seed is not None:
                break
    if seed is None:
        if feature_gate > 0:
            return None
        seed,source = fallback,'fallback'
    reference_db = max(-200.,max(db for hz,db in peaks))
    best = candidate_score(peaks,seed,reference_db,ranks=ranks)
    step = f32(max(1.,float(seed)*.01))
    candidate = f32(seed*f32(.95))
    stop = f32(float(seed)*1.06)
    calls = 1
    while candidate <= stop:
        result = candidate_score(peaks,candidate,reference_db,ranks=ranks)
        calls += 1
        if result['cost'] < best['cost']:
            best = result
        candidate = f32(candidate+step)
        if calls > 10000:
            raise ValueError('Reference refinement exceeds reviewed bound')
    return dict(**best,seed_source=source,score_calls=calls)
