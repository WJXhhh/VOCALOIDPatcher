"""Candidate pool, reference caches and retained pairs from filtered peaks.

All scoring is independent arithmetic. Native candidate costs or caches are
not inputs; the preceding pitch history and filtered peaks are explicit.
"""
from audit_frm2_numeric import f32
from frm2_peak_candidate_score import candidate_score,peak_ranks
from frm2_candidate_reference_cache import candidate_eligible


def retain_candidates(pool):
    retained = []
    worst_index,worst_cost = -1,1e8
    replacements = duplicates = 0
    for pair in pool:
        near = next((i for i,(hz,cost) in enumerate(retained)
                     if float(hz)*.93 < pair[0] < float(hz)*1.07),None)
        if near is not None:
            duplicates += 1
            if pair[1] < retained[near][1]:
                retained[near] = pair
                replacements += 1
                if len(retained) == 3:
                    worst_index = max(range(3),key=lambda i:retained[i][1])
                    worst_cost = retained[worst_index][1]
        elif pair[1] < worst_cost:
            if len(retained) < 3:
                retained.append(pair)
            else:
                retained[worst_index] = pair
                replacements += 1
            if len(retained) == 3:
                # Reverse scan wins equal worst costs on a newly admitted pair.
                worst_index = max(reversed(range(3)),key=lambda i:retained[i][1])
                worst_cost = retained[worst_index][1]
    count = len(retained)
    if not count:
        raise ValueError('No retained candidate in reviewed finite-cost range')
    while len(retained) < 3:
        retained.append(retained[-1])
    # The three strict comparisons preserve the native tie order.
    for a,b in ((0,1),(1,2),(0,1)):
        if retained[b][1] < retained[a][1]:
            retained[a],retained[b] = retained[b],retained[a]
    return retained,count,dict(near_duplicates=duplicates,replacements=replacements)


def produce_candidates(peaks,prior_pitch,previous,voiced_count,lower,upper,fallback,feature_gate,eligibility):
    if not peaks:
        return None
    ranks = peak_ranks(peaks)
    seeds = []
    sources = []
    def eligible(hz):
        return candidate_eligible(peaks,hz,lower,upper,*eligibility)
    def add(hz,source):
        if eligible(hz):
            seeds.append(hz)
            sources.append(source)
            return True
        return False
    add(prior_pitch,'prior')
    extrapolated = False
    if voiced_count > 1:
        extrapolated = add(f32(f32(previous[0]-previous[1])+previous[0]),'extrapolated')
    for rank in ranks[:2]:
        hz = peaks[rank][0]
        divisor = 1
        while True:
            regular = f32(hz/f32(divisor))
            if regular < lower or regular < .5:
                break
            if add(regular,'dominant_peak_division') and len(seeds) > 38:
                break
            intermediate = f32(float(hz)/(float(divisor-1)+1.5))
            if add(intermediate,'dominant_peak_half_division') and len(seeds) > 38:
                break
            if len(seeds) > 38:
                break
            divisor += 1
            if divisor > 10000:
                raise ValueError('Candidate division search exceeds reviewed bound')
    if not seeds:
        if feature_gate > 0:
            return None
        seeds,sources = [fallback],['fallback']
    while len(seeds) < 3:
        seeds.append(seeds[0])
        sources.append('first_seed_padding')
    reference_db = max(-200.,max(db for hz,db in peaks))
    calls = 0
    def score(hz):
        nonlocal calls
        calls += 1
        result = candidate_score(peaks,hz,reference_db,ranks=ranks)
        return result['hz'],result['cost']
    def refine(pair,start,stop,step):
        best = pair
        at = start
        iterations = 0
        while at <= stop:
            result = score(at)
            if result[1] < best[1]:
                best = result
            at = f32(at+step)
            iterations += 1
            if iterations > 10000:
                raise ValueError('Candidate refinement exceeds reviewed bound')
        return best,iterations
    pool = [score(hz) for hz in seeds]
    initial = pool[0][0]
    pool[0],primary_steps = refine(pool[0],f32(initial*f32(.95)),f32(float(initial)*1.06),
                                   f32(max(1.,float(initial)*.01)))
    secondary_steps = 0
    if extrapolated:
        initial = pool[1][0]
        start,stop = f32(initial*f32(.95)),f32(float(initial)*1.06)
        primary = pool[0][0]
        if initial <= primary:
            stop = min(stop,f32(primary*f32(.95)))
        else:
            start = max(start,f32(primary*f32(1.06)))
        pool[1],secondary_steps = refine(pool[1],start,stop,f32(max(1.,float(initial)*.01)))
    pairs,count,selection = retain_candidates(pool)
    refined = skipped = 0
    def near(hz,other):
        return float(other)*.95 < hz < float(other)*1.06
    for i in range(3):
        initial = pairs[i][0]
        if any(near(initial,pairs[j][0]) for j in range(i)):
            skipped += 1
            continue
        pairs[i],steps = refine(pairs[i],f32(initial*f32(.97)),f32(initial*f32(1.03)),
                                f32(max(1.,float(initial)*.01)))
        refined += steps
    return dict(pool=pool,pairs=pairs,retained_count=count,score_calls=calls,
                seed_source=sources[0],seed_sources=sources,secondary_seed=extrapolated,
                primary_steps=primary_steps,secondary_steps=secondary_steps,
                final_steps=refined,final_skipped=skipped,**selection)
