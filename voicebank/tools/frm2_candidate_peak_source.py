"""Candidate-relevant peak fields from a supplied nonnegative scalar spectrum.

Phase is deliberately excluded: the candidate consumer reads frequency, dB
and detector span only. FFT/PCM preparation and optional post-filters are
outside this function's contract.
"""
import math

from audit_frm2_numeric import f32
from frm2_fft_peaks import detect


def candidate_peak_source(power, sample_rate=44100., logarithmic=0,
                          threshold_db=-300., cutoff=22050.):
    if len(power) < 3 or not 0 < sample_rate < math.inf:
        raise ValueError('Invalid spectrum geometry')
    if logarithmic not in (0, 1):
        raise ValueError('Unsupported interpolation scale')
    if any(not math.isfinite(value) or value < 0 for value in power):
        raise ValueError('Spectrum powers must be finite and nonnegative')
    if not math.isfinite(threshold_db) or not math.isfinite(cutoff):
        raise ValueError('Detector controls must be finite')
    threshold_db = f32(threshold_db)
    threshold = (0. if threshold_db <= -3.4028234663852886e38 else
                 f32(32768. * math.exp(threshold_db * .1151292546497)))
    records = detect(power, None, sample_rate, logarithmic, threshold, 0., cutoff)
    return dict(records=[(hz, db, span, bin_index)
                         for hz, db, phase, span, bin_index in records],
                threshold=threshold)
