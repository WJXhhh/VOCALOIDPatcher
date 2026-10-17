"""Independent eff80 post-tracking frequency correction."""
from audit_frm2_numeric import f32, interpolate


def correct(frequencies, amplitudes, f0, strength, window_length, frequency_scale, mask=3):
    frequencies=list(frequencies)
    # Native default cfg24 ENV clamps supplied values to [0,1].
    strength=max(0.,min(1.,f32(strength)))
    if strength<=0 or mask&3!=3 or not frequencies:
        return frequencies,None
    if window_length<=0 or frequency_scale<=0:
        raise ValueError('Positive window length and scale required')
    points=[(0.,0.),(2/9,f32(-1.6)),(4/9,f32(-6.3)),(2/3,f32(-15.2)),(1.,f32(-57.6))]
    width=f32(4.5/window_length*f32(frequency_scale))
    marked=[0]*len(frequencies)
    for i,source in enumerate(frequencies):
        if source<=0:
            continue
        for j,target in enumerate(frequencies):
            if i==j or marked[j] or target<=0:
                continue
            distance=f32(abs(f32(target-source))/width)
            envelope=interpolate(points,distance)
            threshold=f32(f32(envelope+amplitudes[i])+10.)
            if amplitudes[j]<threshold:
                marked[j]=1
    for i,flag in enumerate(marked):
        if flag:
            target=f32((i+1)*f32(f0))
            frequencies[i]=f32(f32(f32(target-frequencies[i])*f32(strength))+frequencies[i])
    return frequencies,marked
