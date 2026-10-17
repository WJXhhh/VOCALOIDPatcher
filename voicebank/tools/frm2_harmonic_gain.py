"""86cb0 positive-F0 harmonic gain normalization in linear amplitude."""
import math
from audit_frm2_numeric import f32


def harmonic_gain(harmonics,nyquist=22050.):
    area=0.;previous_frequency=0.;previous_amplitude=0.
    for frequency,db,*_ in harmonics:
        if frequency<=0 or db==10000.:continue
        amplitude=f32(math.exp(db*.1151292546497)*32768.) if db>-3.4028234663852886e38 else 0.
        area=f32(area+f32(amplitude+previous_amplitude)*.5*f32(frequency-previous_frequency))
        previous_frequency=frequency;previous_amplitude=amplitude
    average=f32(f32(f32(nyquist-previous_frequency)*previous_amplitude*.5+area)/nyquist)
    return f32(20*math.log10(average/32768.)) if average>0 else -3.4028234663852886e38


def analysis_envelope(harmonics,gain,minimum,maximum,f0,nyquist=22050.):
    relative=harmonics[0][0]<5.
    values={0.:-200.,1.:-200.}
    for hz,db,*_ in harmonics:
        if hz<=0 or db==10000.:continue
        x=float(f32((f32(hz*f0) if relative else hz)/nyquist))
        values[min(1.,max(0.,x))]=min(maximum,max(minimum,f32(db-gain)))
    points=sorted(values.items())
    points[0]=(0.,min(maximum,max(minimum,f32(points[1][1]-3.))))
    points[-1]=(1.,f32(points[-2][1]-3.))
    return points
