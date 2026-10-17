"""Late candidate history correction before the confidence write gate.

Candidates and the prior-reference candidate cache are genuine native inputs.
The corrected current cost is predicted, never supplied as an oracle input.
"""
import math

from audit_frm2_numeric import f32


def ratio(numerator,denominator):
    if denominator == 0:
        return math.nan if numerator == 0 else math.copysign(math.inf,numerator)
    return f32(numerator/denominator)


def corrected_candidate(pairs,best_hz,best_cost,prior_pitch,voiced_count,variation,reference_cache,previous):
    def result(hz,cost,reason):
        return dict(hz=hz,cost=cost,reason=reason,changed=(hz,cost) != (best_hz,best_cost))
    if prior_pitch <= 0 or voiced_count < 4 or variation >= f32(.125):
        return result(best_hz,best_cost,'inactive_history')
    distance = 1e8
    hz,cost = 0.,1e8
    for candidate_hz,candidate_cost in reversed(pairs):
        difference = abs(f32(candidate_hz-prior_pitch))
        if difference < distance:
            hz,cost,distance = candidate_hz,candidate_cost,difference
    if ratio(hz,prior_pitch) < f32(.8) or ratio(hz,prior_pitch) > f32(1.25):
        hz,cost = reference_cache
    if voiced_count < 6 and ratio(abs(f32(hz-best_hz)),best_hz) > .06 and cost > best_cost:
        if best_cost < .8 and cost > 2.:
            return result(best_hz,best_cost,'early_stable_primary')
        older_hz,older_cost = previous[1]
        previous_hz,previous_cost = previous[0]
        threshold = float(best_cost)*.5 + float(best_cost) + 2.
        if (ratio(abs(f32(previous_hz-hz)),hz) < .06 and ratio(abs(f32(older_hz-hz)),hz) < .06
                and previous_cost > threshold and older_cost > threshold):
            return result(best_hz,best_cost,'previous_costs_keep_primary')
    margin_ok = cost < f32(best_cost+10.)
    related = ((float(best_hz)*1.9 < hz < float(best_hz)*2.12) or
               (float(best_hz)*2.9 < hz < float(best_hz)*3.12) or
               (f32(best_hz*f32(3.84)) < hz < f32(best_hz*f32(4.12))) or
               (float(best_hz)*1.43 < hz < float(best_hz)*1.57) or
               (f32(best_hz*f32(.45)) < hz < f32(best_hz*f32(.55))))
    if (related and margin_ok) or cost < float(best_cost)*.5 + float(best_cost) + 2.:
        return result(hz,cost,'reference_replacement')
    return result(best_hz,best_cost,'primary_kept')
