"""Peak phase from owned FFT bins, with the reviewed detector rounding order.

The peak table supplies frequency and bin indices already produced from power.
No reference phase or analysis object is used. The optional atan2 backend only
provides the elementary float32 operation, not interpolation or phase tracking.
"""
import ctypes as c
import math

from audit_frm2_numeric import f32
from frm2_candidate_peak_source import candidate_peak_source
from frm2_candidate_pcm_source import HOP, CandidatePcmSource
from verify_frm2_analysis_windows import prepare


class SystemFloatAtan2:
    def __init__(self):
        self.library = c.CDLL('ucrtbase.dll')
        self.function = self.library.atan2f
        self.function.argtypes, self.function.restype = [c.c_float, c.c_float], c.c_float

    def __call__(self, y, x):
        return self.function(y, x)


def peak_phases(power, bins, records, logarithmic=0, time=0., atan2=None):
    if logarithmic not in (0, 1) or not math.isfinite(time):
        raise ValueError('Unsupported phase controls')
    if bins is not None and len(bins) < len(power):
        raise ValueError('Incomplete complex spectrum')
    atan2 = atan2 or (lambda y, x: f32(math.atan2(y, x)))
    invalid = -3.4028234663852886e38

    def ordinate(value):
        value = f32(math.sqrt(value))
        if logarithmic:
            return f32(20.*math.log10(value/32768.)) if value > 0. else invalid
        return value

    output, branches = [], dict(negative_shift=0, nonnegative_shift=0,
        zero_denominator=0, unwrap_first=0, unwrap_second=0, time_correction=0,
        positive_wrap=0, below_negative_pi=0, missing_complex=0)
    for hz, db, span, index in records:
        if not 0 < index < len(power)-1:
            raise ValueError('Invalid peak bin')
        a, b, d = map(ordinate, power[index-1:index+2])
        if a == invalid:
            a = b
        if d == invalid:
            d = b
        denominator = f32(f32(a-f32(b+b))+d)
        shift = f32(float(f32(a-d))*.5/float(denominator)) if denominator else 0.
        branches['zero_denominator'] += denominator == 0.
        negative = shift < 0.
        branches['negative_shift' if negative else 'nonnegative_shift'] += 1
        first = second = 0.
        if bins is None:
            branches['missing_complex'] += 1
        else:
            at = index-1 if negative else index
            first = atan2(f32(bins[at].imag), f32(bins[at].real))
            second = atan2(f32(bins[at+1].imag), f32(bins[at+1].real))
            if abs(f32(second-first)) > math.pi:
                if second <= 0.:
                    second = f32(float(second)+math.tau)
                    branches['unwrap_second'] += 1
                else:
                    first = f32(float(first)+math.tau)
                    branches['unwrap_first'] += 1
        weight = f32(shift+1.) if negative else shift
        value = f32(f32(f32(second-first)*weight)+first)
        if time != 0.:
            value = f32(float(value)-(float(hz)*math.tau)*time)
            quotient = math.trunc(float(value)/math.tau)
            if not -(2**31) <= quotient < 2**31:
                raise ValueError('Phase correction exceeds native integer range')
            value = f32(float(value)-float(quotient)*math.tau)
            branches['time_correction'] += 1
        if value > math.pi:
            value = f32(float(value)-math.tau)
            branches['positive_wrap'] += 1
        branches['below_negative_pi'] += value < -math.pi
        output.append((hz, db, value, span, index))
    return dict(records=output, branches=branches)


class CandidatePcmPhaseSource(CandidatePcmSource):
    def __init__(self, transform=None, atan2=None):
        super().__init__(transform)
        self.atan2 = atan2

    def spectrum_at(self, pcm, index):
        if not isinstance(index, int) or index < 0:
            raise ValueError('Expected a nonnegative hop index')
        start = index*HOP-512
        samples = [f32(pcm[at]) if 0 <= at < len(pcm) else 0.
            for at in range(start, start+1025)]
        bins = [complex(f32(value.real), f32(value.imag))
            for value in self.transform(prepare(samples, self.weights, 4., True))]
        assert len(bins) == 2049
        power = [abs(bins[0].real)]+[
            f32(f32(value.real*value.real)+f32(value.imag*value.imag))
            for value in bins[1:2048]]
        records = candidate_peak_source(power)['records']
        return dict(power=power, bins=bins,
            **peak_phases(power, bins, records, atan2=self.atan2))
