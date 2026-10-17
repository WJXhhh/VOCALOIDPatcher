"""Default mode4 (+44=0, +48=0) voicing probability and history hold."""
import math
from audit_frm2_numeric import f32


def score_features(scores,index,nyquist,limit,factor=1.):
    """Default score contrasts and 2000..5000 Hz score-coordinate moments.

    The moment interval is a score-array interval, not FFT spectral energy.
    Reject unsupported geometry rather than silently clipping native indices.
    """
    peak=scores[index]
    right_min=peak;minimum_index=index
    for i in range(index,min(2*index,limit-1)):
        if scores[i]<right_min:right_min=scores[i];minimum_index=i
    right_max=right_min
    for i in range(minimum_index,min(int(index*2.3),limit-1)):
        right_max=max(right_max,scores[i])
    left_min=min(scores[int(index*.2):index+1])
    confidence=f32(peak-right_min)
    if f32(index*f32(2.3))<limit:
        confidence=f32(confidence*f32(right_max-right_min))
    low=f32(factor*4);high=f32(factor*8)
    contrast=f32(peak-left_min)
    if index<high:
        weight=max(0.,f32(f32(index-low)/f32(high-low)))
        contrast=f32(f32(f32(contrast-1)*weight)+1)
    confidence=f32(confidence*contrast)
    lo=int(f32(f32(2000/nyquist)*len(scores)))
    hi=int(f32(f32(5000/nyquist)*len(scores)))
    if not 0<=lo<=hi<len(scores):raise ValueError('Unsupported score moment interval')
    total=0.;squares=0.
    for value in scores[lo:hi+1]:
        total=f32(total+value);squares=f32(squares+f32(value*value))
    if total<=0:raise ValueError('Nonpositive score moment denominator not modeled')
    mean=f32(squares/total)
    variance=0.
    for value in scores[lo:hi+1]:
        delta=f32(value-mean)
        variance=f32(variance+f32(delta*delta))
    deviation=f32(math.sqrt(f32(variance/(hi-lo+1))))
    strength=f32(f32(f32(deviation*mean)*min(confidence,f32(.3)))/f32(.3))
    ratio=f32(f32(peak/mean)*f32(deviation/mean))
    return dict(confidence=confidence,strength=strength,ratio=ratio)


def probability(features):
    confidence=features['confidence']
    p=f32(math.exp(-.5*((confidence-f32(.2))/f32(.15))**2)) if confidence<f32(.2) else 1.
    if features['feature4']>f32(.1):
        p=f32(p*math.exp(-.5*((features['feature4']-f32(.1))/f32(.2))**2))
    noise=features['feature8']
    if noise>f32(.2):
        if features['strength']>f32(.015):
            suppression=min(1.,f32(f32(features['strength']-f32(.015))/f32(.008)))
            noise=f32(noise*f32(1-suppression))
        if noise>f32(.2):
            p=f32(p*math.exp(-.5*((noise-f32(.2))/f32(.16))**2))
    # Both noise outcomes join the low-energy test (ff66c -> ff674).
    if features['feature12']<f32(.00002):
        p=f32(p*math.exp(-.5*((features['feature12']-f32(.00002))/f32(.00001))**2))
    if features['ratio']<.52:
        p=f32(p*math.exp(-.5*((features['ratio']-.52)/f32(.04))**2))
    return p


def gate(current_probability,previous_probability,history,voiced_count,previous_frame_f0,history_probability):
    """Positive-time, nonzero queue index, default configuration40/41/42=0."""
    threshold=f32(.44)
    combined=max(current_probability,previous_probability)
    sentinel=f32(-3.4028234663852886e38)
    hold=(len(history)>=6 and voiced_count>=5 and previous_frame_f0>sentinel and history[-1]>sentinel)
    if hold:
        hold=all(history[-1-j]>sentinel and abs(f32(history[-1-j]-history[-j]))<=20 for j in range(1,6))
        hold=hold and history_probability>=threshold
    return dict(accepted=combined>=threshold or hold,combined_probability=combined,history_hold=hold,threshold=threshold)
