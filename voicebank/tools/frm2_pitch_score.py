"""Independent spectrum autocorrelation stage used for mode4 pitch scoring."""
import math
from audit_frm2_numeric import f32
from verify_frm2_analysis_windows import fft


def autocorrelation_score(values,*,normalization_bins=None,standard_nyquist=False,fft_backend=None):
    half=len(values)
    if half<2 or half&(half-1):raise ValueError('Power-of-two half length required')
    if normalization_bins is None:normalization_bins=half
    if not 1<=normalization_bins<=half:raise ValueError('Invalid normalization range')
    data=list(values)+[values[-1]]+list(reversed(values[1:]))
    for i in range(1,half):
        sine=f32(math.sin(f32((half-i)/half)*math.pi*.5))
        taper=f32(sine*sine)
        data[i]=f32(data[i]*taper)
        data[2*half-i]=f32(data[2*half-i]*taper)
    if fft_backend is not None:
        result=fft_backend(data,standard_nyquist)[:half]
    else:
        transformed=fft(data)
        power=[complex(f32(f32(z.real*z.real)+f32(z.imag*z.imag))) for z in transformed]
        # The native square loop stops before CCS[N], leaving Nyquist's real value
        # from the forward transform in place. Retain the standard variant as a
        # negative control; it is not interchangeable with this DLL's behavior.
        if not standard_nyquist:power[half]=complex(f32(transformed[half].real))
        inverse=fft(power)
        result=[z.real for z in inverse[:half]]
    maximum=max(0.,max(result[:normalization_bins]))
    return [f32(v/maximum) for v in result] if maximum>0 else [0.]*half
