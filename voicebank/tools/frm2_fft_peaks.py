"""Independent 51e60 spectrum-to-peak numeric model, nonnegative powers."""
import math
from audit_frm2_numeric import f32, interpolate


def detect(power, complex_bins, sample_rate, logarithmic, threshold=0., time=0., cutoff=0.):
    n = len(power)
    scale = f32(f32(n+n)/f32(sample_rate))
    end = n-2
    if cutoff > 0:
        end = int(min(float(end), f32(scale*f32(cutoff))))
    threshold_squared = f32(f32(threshold)*f32(threshold))
    invalid = -3.4028234663852886e38

    def ordinate(value):
        amplitude = f32(math.sqrt(value))
        return (f32(20*math.log10(amplitude/32768)) if amplitude > 0 else invalid) if logarithmic else amplitude

    peaks, run, i = [], 0, 1
    while i < end and len(peaks) < 1500:
        left, middle, right = power[i-1:i+2]
        if left == middle == right:
            if peaks and peaks[-1][3] == -1:
                peaks[-1][3] = run
            run = 1
        else:
            if middle <= left and middle <= right:
                if peaks and peaks[-1][3] == -1:
                    peaks[-1][3] = run
                run = 0
            if left <= middle and right <= middle and threshold_squared < middle:
                prior = run
                if peaks and peaks[-1][3] == -1:
                    prior = int((i-int(f32(scale*peaks[-1][0])))*.5)
                    peaks[-1][3] = run-prior
                a, b, c = map(ordinate, (left,middle,right))
                if a == invalid:
                    a = b
                if c == invalid:
                    c = b
                denominator = f32(f32(a-f32(b+b))+c)
                shift = f32(f32(a-c)*.5/denominator) if denominator else 0.
                phase = 0.
                if complex_bins is not None:
                    at = i-1 if shift < 0 else i
                    p0, p1 = [f32(math.atan2(z.imag,z.real)) for z in complex_bins[at:at+2]]
                    if abs(f32(p1-p0)) > math.pi:
                        if p1 <= 0:
                            p1 = f32(p1+math.tau)
                        else:
                            p0 = f32(p0+math.tau)
                    weight = f32(shift+1) if shift < 0 else shift
                    phase = f32(f32(f32(p1-p0)*weight)+p0)
                height = f32(b-f32(a-c)*.25*shift)
                amplitude = height if logarithmic else (f32(20*math.log10(height/32768)) if height > 0 else invalid)
                frequency = f32(f32(i+shift)/scale)
                if time != 0:
                    phase = f32(phase-frequency*math.tau*time)
                    phase = f32(phase-math.trunc(phase/math.tau)*math.tau)
                if phase > math.pi:
                    phase = f32(phase-math.tau)
                peaks.append([frequency,amplitude,phase,-1,i])
                run = prior+1
                i += 1
            run += 1
        i += 1
    return peaks


def filter_slopes(peaks, harmonic_mode):
    """d04c0 enabled post-filter with cfg38=0; records retained verbatim."""
    peaks = [list(p) for p in peaks]
    i = 0
    while i < len(peaks)-2:
        gap = f32(peaks[i+1][0]-peaks[i][0])
        slope = f32(peaks[i+1][1]-peaks[i][1])/gap
        if (harmonic_mode == 1 and peaks[i][0] < 40) or slope > .4:
            del peaks[i]
            i = i-1 if i > 1 else i
        elif slope < -.4:
            del peaks[i+1]
        else:
            i += 1
    return peaks


def filter_adaptive(peaks, power, configured_frequency, diagnostics=None):
    """d04c0 cfg38!=0 weighted-power reference and amplitude rewrite."""
    cutoff = f32(configured_frequency)*1.2
    values=[]
    n=len(power)
    for k in range(221):
        frequency=float(k*100)
        if frequency>cutoff:
            break
        low=max(0,min(n-1,int(f32(frequency-max(50.,frequency*.34))/22050*(n-1)+.5)))
        high=min(n,max(low+1,int(f32(frequency+max(50.,frequency*.58))/22050*(n-1)+.5)))
        center=f32((low+high)*.5)
        radius=f32(high-center)
        numerator=denominator=0.
        for i in range(low,high):
            weight=f32(1.-f32(abs(f32(i-center))/radius))
            weight=f32(weight*weight)
            weight=f32(weight*weight)
            weighted=f32(weight*power[i])
            numerator=f32(numerator+f32(weighted*power[i]))
            denominator=f32(denominator+weighted)
        values.append(f32(numerator/denominator) if denominator else numerator)
    if len(values)<2:
        raise ValueError('Adaptive reference requires at least two grid points')
    points=[]
    for k,value in enumerate(values):
        amplitude=f32(math.sqrt(value))
        points.append((k*100/22050,f32(20*math.log10(amplitude/32768)) if amplitude>0 else -3.4028234663852886e38))
    # ENV retains its constructor's x=1 endpoint beyond the generated grid.
    # Native last-point copying fills that endpoint, not the last grid point.
    points.append((1.,points[-1][1]))
    result=[list(p) for p in peaks]
    if diagnostics is not None:
        diagnostics['points']=points
    for item in result[:-2]:
        frequency,amplitude=item[:2]
        if frequency>cutoff:
            break
        reference=interpolate(points,frequency/22050,regular=True)
        value=0.
        if amplitude<=reference:
            value=-200. if amplitude<=reference-30 else f32(amplitude-reference)
        item[1]=f32(value-frequency*20/4000)
    return result
