"""Candidate leaf result, ordinary history repair and next reference state.

The reviewed mode has extended repair and reanalysis disabled. State and
history can be advanced entirely from independent candidate/gate outputs.
"""
import math

from audit_frm2_numeric import f32


def divide(numerator,denominator):
    if denominator == 0:
        return math.nan if numerator == 0 else math.copysign(math.inf,numerator)
    return f32(numerator/denominator)


def selected_pair(gate,best,confidence,manual_pitch=None):
    if manual_pitch is not None:
        return manual_pitch,0.
    hz,cost = best
    correction = gate['history_correction']
    if correction:
        hz,cost = correction['hz'],correction['cost']
    clear = gate['reason'] in ('no_original_peaks','no_filtered_peaks','candidate_cost_limit',
        'feature4_limit','amplitude_energy_pair','low_energy','rising_low_energy',
        'feature8_limit','maximum_peak_frequency','late_feature_pair','late_crossing_limit')
    if clear or confidence > .06:
        hz = 0.
    return hz,cost


def repair_history(history,voiced_count,prior_pitch,variation):
    """Four sequential normal-mode repairs; only the previous pitch is changed."""
    result = list(history)
    events = []
    older2,older,previous,current = [pair[0] for pair in result[-4:]]
    if voiced_count < -2 and previous > 0 and current == 0:
        previous = 0.
        events.append('clear_isolated_previous')
    if prior_pitch > 0 and variation < .1 and previous > 0 and older > 0:
        if abs(divide(previous,older)) > 1.4 and abs(divide(current,previous)) < f32(.75):
            previous = f32(float(older)*.5+float(current)*.5)
            events.append('repair_upward_spike')
    if voiced_count > 3:
        if (abs(float(divide(older,older2))-1.) < .2 and
                abs(float(divide(current,older))-1.) < .2 and
                abs(float(divide(previous,older))-1.) > .35):
            previous = f32(float(older)*.5+float(current)*.5)
            events.append('repair_stable_spike')
    if previous <= 0 and older > 0 and current > 0 and .9 < divide(current,older) < 1.1:
        previous = f32(float(older)*.5+float(current)*.5)
        events.append('fill_single_gap')
    result[-2] = previous,result[-2][1]
    return result,events


def update_reference(history,voiced_count,delay=1):
    current = history[-1][0]
    delayed_pitch = history[-1-delay][0]
    if delayed_pitch <= 0:
        count = voiced_count-1 if voiced_count < 1 else 0
    else:
        count = 1 if voiced_count < 0 else voiced_count+1
    prior,variation = 0.,0.
    capped = extrapolated = False
    if count > 3:
        older,previous = history[-3][0],history[-2][0]
        prior = f32(f32(older*f32(.3))+0.)
        prior = f32(f32(previous*f32(.3))+prior)
        prior = f32(f32(current*f32(.4))+prior)
        deviation = 0.
        for hz in (current,previous,older):
            deviation = f32(deviation+abs(f32(hz-prior)))
        variation = divide(f32(deviation/3.),prior)
        if divide(abs(f32(history[-5][0]-current)),current) > f32(.06):
            count = min(count,4)
            capped = True
        previous_change,current_change = f32(previous-older),f32(current-previous)
        if f32(current_change*previous_change) > 0:
            prior = f32(float(f32(current_change+previous_change))*.5+float(current))
            extrapolated = True
    return dict(voiced_count=count,prior_pitch=prior,variation=variation,
                capped=capped,extrapolated=extrapolated)
