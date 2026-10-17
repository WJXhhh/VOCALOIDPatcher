"""Candidate confidence write gate from caller features and native candidates.

Candidate/filter production, prior history and reference cache are inputs.
The late history cost and write gate are outputs. Observed bit4 and the
native corrected current cost are never decision inputs.
"""
import math
import struct

from audit_frm2_numeric import f32
from frm2_candidate_history_cost import corrected_candidate


def confidence_gate(original_peaks, filtered_peaks, best_hz, best_cost,
                    feature4, feature8, feature12, feature28, previous_energy, curves, history):
    correction = None
    def result(reason, mask=0):
        return dict(writes=reason == 'write', reason=reason, spectral_mask=mask,history_correction=correction)
    if curves[0x14] == 0:
        return result('manual_pitch')
    if not original_peaks:
        return result('no_original_peaks')
    if not filtered_peaks:
        return result('no_filtered_peaks')
    if best_hz <= 0 or best_cost == 1e8 or best_cost > curves[0xf]:
        return result('candidate_cost_limit')
    mask = 0
    if feature4 > curves[0x17] or feature28 > .9:
        index = 0
        for i,(hz,db) in enumerate(filtered_peaks):
            if 45. < hz < 450. and db > filtered_peaks[index][1]:
                index = i
        hz,db = filtered_peaks[index]
        later = sum(other_hz < 1000. and other_db > float(db) - float(f32(f32(other_hz-hz)*30.))/1000.
                    for other_hz,other_db in filtered_peaks[index+1:])
        high = max((db for hz,db in original_peaks if 6500. < hz < 12500.), default=-1000.)
        mask = int(later == 0 and f32(high-5.) < db)
    if curves[0x17] > 0 and not mask:
        if feature4 > curves[0x17]:
            return result('feature4_limit')
        if curves[0x18] > 0 and curves[0x19] > 0:
            if feature4 > curves[0x18] and feature12 < curves[0x19]:
                return result('amplitude_energy_pair')
        if curves[0x19] > 0:
            if feature12 < float(curves[0x19])/5.:
                return result('low_energy')
            if previous_energy is not None and feature12 < float(curves[0x19])/2.5 and previous_energy < feature12:
                return result('rising_low_energy')
        if feature8 > f32(.37):
            return result('feature8_limit')
    index = 0
    maximum_db = -200.
    found = False
    for i,(hz,db) in enumerate(filtered_peaks):
        if db > maximum_db:
            maximum_db, index, found = db, i, True
    if found and not mask and filtered_peaks[index][0] > curves[0x16]:
        return result('maximum_peak_frequency')
    correction = corrected_candidate(history['pairs'],best_hz,best_cost,history['prior_pitch'],
        history['voiced_count'],history['variation'],history['reference_cache'],history['previous'])
    corrected_cost = correction['cost']
    if not mask and corrected_cost > 3.5 and feature4 > .12 and feature28 > .15:
        return result('late_feature_pair')
    if mask:
        return result('spectral_mask',mask)
    if curves[0x1c] != 0 and corrected_cost > f32(.5) and feature28 > .9:
        return result('late_crossing_limit')
    if corrected_cost <= 2.5:
        return result('confidence_cost_floor')
    return result('write')


def exceptional_confidence(psd, peaks, candidate_hz, lower_hz, upper_hz, ceiling, feature4):
    """Reviewed zero-energy continuation with x64 scalar division semantics.

The finite445 model rejects a zero feature+c denominator. Disabling the
feature gate can genuinely reach it; do not replace it by a finite fallback.
"""
    index = 0
    for i,(hz,db) in enumerate(peaks):
        if hz >= upper_hz:
            break
        if hz > lower_hz and db > peaks[index][1]:
            index = i
    hz,db = peaks[index]
    size = len(psd)
    start = int(f32(f32(hz*f32(size))/ceiling))
    stop = f32(f32(f32(candidate_hz+hz)*f32(size))/ceiling)
    gap = 0.
    for at in range(start,size,2):
        if f32(at) > stop:
            break
        spectrum_db = f32(20.*math.log10(float(psd[at])/32768.)) if psd[at] > 0 else -3.4028234663852886e38
        gap = f32(gap + f32(db-spectrum_db))
    stage1 = f32(float(f32(gap/candidate_hz))/500.)
    denominator = f32(stage1+1.)
    stage2 = f32(feature4/denominator)
    # The current control corpus has +0 energy, and a positive scale after
    # this division. Scalar SSE produces its negative indefinite quiet NaN
    # on 0/0, or signed infinity for nonzero/0.
    if stage2 == 0. or math.isnan(stage2):
        return struct.unpack('<f',bytes.fromhex('0000c0ff'))[0]
    return math.copysign(math.inf,stage2)
