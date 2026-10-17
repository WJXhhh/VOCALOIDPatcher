"""Forty normalized triangular power bands for the DRS 7b4b0 path."""
import math
from audit_frm2_numeric import f32


def bands(power, nyquist, boundaries, floor=-100.):
    if len(boundaries) != 42 or any(a >= b for a,b in zip(boundaries,boundaries[1:])):
        raise ValueError('Expected 42 strictly increasing boundaries')
    values=[0.]*40; weights=[0.]*40
    for i in range(1,len(power)):
        hz=f32(f32(i*nyquist)/len(power))
        if hz > boundaries[-1]:break
        for j in range(41):
            lo,hi=boundaries[j:j+2]
            if lo < hz < hi:
                width=f32(hi-lo)
                contributions=[]
                if j > 0:contributions.append((j-1,f32(1-f32(f32(hz-lo)/width))))
                if j < 40:contributions.append((j,f32(1-f32(f32(hi-hz)/width))))
                for k,w in contributions:
                    weights[k]=f32(weights[k]+w)
                    values[k]=f32(values[k]+f32(w*power[i]))
                break
    values=[f32(v/w) if w else v for v,w in zip(values,weights)]
    return [f32(10*math.log10(v*(1/32768)*(1/32768))) if v>0 else floor for v in values]
