"""Mode5's cache-moment confidence, distinct from default mode4 contrasts.

Scores are candidate-coordinate values, not assumed to be spectral energy.
This model starts at a prepared cache; it does not produce that cache from PCM.
"""
import math

from audit_frm2_numeric import f32


def interval(count,bins,rate,low_frequency,high_frequency):
    if not (count>3 and bins>0 and math.isfinite(rate) and rate>0):
        raise ValueError('Unsupported score geometry')
    if not all(math.isfinite(x) for x in (low_frequency,high_frequency)):
        raise ValueError('Nonfinite frequency boundary')
    if low_frequency>0:
        low=int(f32(f32(f32(low_frequency/rate)*f32(bins))+.5))
        high=min(count,int(f32(f32(f32(high_frequency/rate)*f32(bins))+.5)))
    else:
        low,high=3,count
    if not 0<=low<high<=count:
        raise ValueError('Empty or out-of-bounds candidate interval')
    return low,high


def confidence(scores,low,high):
    if not 0<=low<high<=len(scores):
        raise ValueError('Unsupported candidate interval')
    values=scores[low:high]
    if not all(math.isfinite(x) for x in values):
        raise ValueError('Nonfinite cached score')
    total=0.
    for value in values:
        total=f32(total+value)
    mean=f32(total/f32(high-low))
    variance=0.
    for value in values:
        delta=f32(value-mean)
        variance=f32(variance+f32(delta*delta))
    variance=f32(variance/f32(high-low))
    deviation=f32(math.sqrt(variance))
    return f32(max(0.,f32(deviation/30.))*.5)
