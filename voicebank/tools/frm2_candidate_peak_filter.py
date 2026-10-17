"""Candidate analyzer's frequency/dB/shape peak filter.

Input is the original frequency-ordered peak table, including its signed
integer field at +0xc. Output pairs and all shadow deletions are independent.
"""
import math

from audit_frm2_numeric import f32


def filter_candidate_peaks(raw_peaks,lower):
    peaks = [(f32(hz),f32(db),int(shape)) for hz,db,shape in raw_peaks]
    if any(not math.isfinite(hz) or not math.isfinite(db) or hz < 0 or db <= -3.4028234663852886e38
           or not -(2**31) <= shape < 2**31 for hz,db,shape in peaks):
        raise ValueError('Unsupported original peak range')
    if any(peaks[i][0] > peaks[i+1][0] for i in range(len(peaks)-1)):
        raise ValueError('Expected frequency-ordered original peaks')
    controls = dict(examined=0,frequency_rejects=0,shape_rejects=0,level_rejects=0,
                    previous_shadow_rejects=0,admitted=0,backward_deletions=0,capacity_stops=0)
    if not peaks:
        return dict(peaks=[],controls=controls,ceiling=None)
    strongest = max(range(len(peaks)),key=lambda i:peaks[i][1])
    ceiling = f32(min(5000.,max(3000.,float(peaks[strongest][0])*4.4)))
    floor_db = f32(peaks[strongest][1]-50.)
    result = []
    for hz,db,shape in peaks:
        controls['examined'] += 1
        if not float(lower)*.9 < hz < ceiling:
            controls['frequency_rejects'] += 1
            continue
        if not (f32(shape) > 2. or shape == -1 or hz > 1000.):
            controls['shape_rejects'] += 1
            continue
        if db <= floor_db:
            controls['level_rejects'] += 1
            continue
        if result:
            previous_hz,previous_db = result[-1]
            projected = f32(previous_db-f32(f32(hz-previous_hz)*f32(.75)))
            if projected > db:
                controls['previous_shadow_rejects'] += 1
                continue
        result.append((hz,db))
        controls['admitted'] += 1
        for i in reversed(range(len(result)-1)):
            projected = f32(db-f32(f32(hz-result[i][0])*f32(.75)))
            if result[i][1] < projected:
                del result[i]
                controls['backward_deletions'] += 1
        if len(result) > 1499:
            controls['capacity_stops'] += 1
            break
    return dict(peaks=result,controls=controls,ceiling=ceiling)
