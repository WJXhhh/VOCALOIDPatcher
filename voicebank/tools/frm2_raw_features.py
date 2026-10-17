"""Raw ring-buffer features consumed by default mode4 voicing.

Preserve native physical-index crossing eligibility and duplicated end sample
in energy. These are numerical contracts, not conventional ZCR/RMS definitions.
"""
from audit_frm2_numeric import f32


def raw_features(ring,start,hop,sample_rate=44100):
    size=len(ring);count=2*hop
    if not 0<=start<size or hop<2 or count>=size or sample_rate<=0:
        raise ValueError('Invalid bounded ring geometry')
    end=(start+count-1)%size
    indices=[(start+i)%size for i in range(count)]
    total=0.
    for i in indices:total=f32(total+ring[i])
    mean=f32(total/count)
    deviation=[f32(ring[i]-mean) for i in indices]
    threshold=max(1.,f32(max(abs(x) for x in deviation)*f32(.1)))
    crossing=0;turns=0;energy=0.
    state=float(f32(ring[(start+1)%size]-ring[start])>=0)
    for i,value in zip(indices,deviation):
        energy=f32(energy+f32(value*value))
        next_value=ring[(i+1)%size]
        if i<end and (f32(f32(next_value-mean)*value)<0 or value==0):crossing+=1
        outgoing=f32(next_value-ring[i]);incoming=f32(ring[i]-ring[(i-1)%size])
        next_state=1. if outgoing>f32(incoming+threshold) else (0. if outgoing<f32(incoming-threshold) else state)
        if next_state!=state:turns+=1
        state=next_state
    energy=f32(energy+f32(deviation[-1]*deviation[-1]))
    zero_rate=f32(crossing/(count-2));turn_rate=f32(turns/(count-2))
    if sample_rate!=44100:
        scale=f32(sample_rate/44100)
        zero_rate=f32(zero_rate*scale);turn_rate=f32(turn_rate*scale)
    return dict(feature4=zero_rate,feature8=turn_rate,
                feature12=f32(energy*(1/32768)*(1/32768)/(count-1)))
