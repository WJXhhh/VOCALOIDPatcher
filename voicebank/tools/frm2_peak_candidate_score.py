"""Peak-to-harmonic mismatch score for the reviewed candidate analyzer.

Independent float32 arithmetic from a frequency-ordered peak list. The rank
construction and both mismatch directions are explicit; no native cost input.
"""
import math

from audit_frm2_numeric import f32


def peak_ranks(peaks):
    if any(not math.isfinite(hz) or not math.isfinite(db) or hz < 0 or db <= -1000. for hz,db in peaks):
        raise ValueError('Unsupported peak range')
    if any(peaks[i][0] > peaks[i+1][0] for i in range(len(peaks)-1)):
        raise ValueError('Expected frequency-ordered peaks')
    return sorted(range(len(peaks)),key=lambda i:-peaks[i][1])


def candidate_score(peaks,frequency,reference_db,*,ranks=None):
    frequency = f32(frequency)
    if not math.isfinite(frequency) or not math.isfinite(reference_db):
        raise ValueError('Expected finite candidate inputs')
    if frequency <= 1e-8:
        frequency = f32(1e-8)
    count = len(peaks)
    if count == 0:
        return dict(hz=frequency,cost=0.)
    ranks = peak_ranks(peaks) if ranks is None else ranks
    dominant_hz = peaks[ranks[0]][0]
    main = 0.
    main_count = 0
    preferred = nearest = 0
    harmonic = frequency
    for order in range(min(10,count)):
        if f32(dominant_hz*3.5) < harmonic or f32(f32(frequency*.5)+peaks[-1][0]) < harmonic:
            break
        chosen = None
        highest_db = -10000.
        width = f32(frequency*f32(.35))
        for i in range(max(0,preferred-1),count):
            hz,db = peaks[i]
            if f32(width+harmonic) < hz:
                break
            if f32(harmonic-width) < hz and db > highest_db:
                chosen,highest_db = i,db
        if chosen is None:
            chosen = preferred
            minimum = 1e8
            for i in range(preferred,count):
                difference = abs(f32(harmonic-peaks[i][0]))
                if difference >= minimum:
                    chosen = i-1
                    break
                chosen,minimum = i,difference
        preferred = chosen
        minimum = 1e8
        for i in range(nearest,count):
            difference = abs(f32(harmonic-peaks[i][0]))
            if difference >= minimum:
                nearest = i-1
                break
            nearest,minimum = i,difference
        chosen_hz,chosen_db = peaks[preferred]
        difference = abs(f32(chosen_hz-harmonic))
        closest = abs(f32(peaks[nearest][0]-harmonic))
        if nearest != preferred and closest < difference and f32(chosen_db-15.) < peaks[nearest][1]:
            weight = f32(1.-float(f32(f32(chosen_db-peaks[nearest][1])/15.)))
            if count > 2:
                floor_db = f32(peaks[ranks[2]][1]-12.)
                if peaks[nearest][1] < floor_db:
                    drop = min(1.,float(f32(floor_db-peaks[nearest][1]))/10.)
                    weight = f32((1.-drop)*float(weight))
            difference = f32(f32(f32(closest-difference)*weight)+difference)
        tolerance = f32(frequency*f32(.05))
        if difference < tolerance:
            compression = f32(difference/tolerance)
            difference = f32(difference*f32(f32(compression*compression)*compression))
        if (harmonic < f32(frequency*f32(1.2)) and float(harmonic)*1.8 < chosen_hz
                and chosen_hz < f32(harmonic*f32(2.2))):
            difference = f32(difference*f32(.4))
        if order == 0:
            difference = f32(difference*f32(.7))
        normalized = f32(difference/harmonic)
        main = f32(main+f32(f32(normalized*20.)*4.))
        main_count += 1
        harmonic = f32(harmonic+frequency)
    reverse = 0.
    lower_deviation = 0.
    reverse_count = 0
    for order,index in enumerate(ranks[:4]):
        hz,db = peaks[index]
        if db < f32(reference_db-30.):
            break
        multiple = max(1,int(float(f32(hz/frequency))+.5))
        difference = abs(f32(hz-f32(f32(multiple)*frequency)))
        tolerance = f32(frequency*f32(.05))
        if difference < tolerance:
            compression = f32(difference/tolerance)
            difference = f32(f32(f32(compression*compression)*compression)*difference)
        if float(f32(hz/frequency)) < 3.3:
            lower_deviation = f32(lower_deviation+f32(difference/frequency))
        if frequency > 1000. and hz < f32(frequency*f32(.6)) and hz < f32(dominant_hz*f32(.4)):
            difference = 0.
        if order > 0 and frequency > 600. and float(frequency)*.1 < difference and hz < frequency:
            preferred_rank = 0
            for i in range(1,order):
                if abs(f32(peaks[ranks[i]][0]-frequency)) < abs(f32(peaks[ranks[preferred_rank]][0]-frequency)):
                    preferred_rank = i
            preferred_hz,preferred_db = peaks[ranks[preferred_rank]]
            if hz < preferred_hz:
                gradient = f32(-10./(float(frequency)*.5))
                estimate = f32(f32(f32(gradient*f32(preferred_hz-hz))+preferred_db)-db)
                attenuation = f32(min(1.,max(0.,float(estimate)/10.)))
                difference = f32(difference*f32(1.-attenuation))
        complexity = f32(f32(f32(f32(multiple)*frequency)/frequency)-1.)
        complexity = f32(complexity*f32(.125))
        reverse = f32(reverse+f32(complexity+f32(difference/20.)))
        reverse_count += 1
    reverse = f32(reverse+f32(f32(lower_deviation+lower_deviation)*f32(main_count)))
    dominant_index = ranks[0]
    ratio = f32(abs(f32(frequency-dominant_hz))/dominant_hz)
    if count > 1 and peaks[ranks[1]][1] < f32(reference_db-25.):
        penalty = f32(f32(ratio*f32(f32(reference_db-25.)-peaks[ranks[1]][1]))*25.)
        reverse = f32(reverse+penalty)
    if dominant_index > 0:
        deficit = min(f32(reference_db-peaks[i][1]) for i in range(dominant_index))
        if deficit > 40.:
            penalty = ((float(ratio)*(float(deficit)-40.))/15.)*40.
            reverse = f32(penalty+float(reverse))
    if dominant_index > 2 and dominant_hz > 800.:
        low_rank = max(range(dominant_index),key=lambda i:peaks[i][1])
        low_hz,low_db = peaks[low_rank]
        if float(low_hz) < float(dominant_hz)*.2 and frequency < dominant_hz:
            stop = 1
            while stop < dominant_index and float(peaks[stop][0]) < float(dominant_hz)*.2:
                if low_db < peaks[stop][1]:
                    low_hz,low_db = peaks[stop]
                stop += 1
            slope = f32(f32(f32(reference_db-55.)-low_db)/f32(dominant_hz-low_hz))
            for i in range(stop,dominant_index):
                slope = max(slope,f32(f32(peaks[i][1]-low_db)/f32(peaks[i][0]-low_hz)))
            estimate = f32(f32(f32(dominant_hz-low_hz)*slope)+low_db)
            if estimate < -45.:
                penalty = f32(f32(f32(f32(dominant_hz-frequency)*f32(-45.-estimate))/dominant_hz)*20.)
                reverse = f32(reverse+penalty)
    excess = f32(dominant_hz*f32(1.2))
    if excess < frequency:
        reverse = f32(reverse+f32(f32(f32(frequency-excess)/dominant_hz)*20.))
    reverse = f32(reverse/f32(max(1,reverse_count)))
    return dict(hz=frequency,cost=f32(f32(main/f32(max(1,main_count)))+reverse))
