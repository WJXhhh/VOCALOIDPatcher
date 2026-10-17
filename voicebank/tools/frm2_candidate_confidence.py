"""Conditional candidate confidence arithmetic, with explicit float32 stages.

Inputs are the genuine caller's original PSD/peaks and candidate selection.
Candidate generation and the voicing/history gates remain separate contracts.
"""
import math

from audit_frm2_numeric import f32


def candidate_confidence(psd, peaks, candidate_hz, lower_hz, upper_hz,
                         frequency_ceiling, feature4, feature12, *, log10=math.log10):
    values = [candidate_hz, lower_hz, upper_hz, frequency_ceiling, feature4, feature12]
    if not psd or not peaks or not all(math.isfinite(x) for x in values):
        raise ValueError('Expected nonempty spectra and finite inputs')
    if candidate_hz <= 0 or frequency_ceiling <= 0 or feature12 == 0 or lower_hz >= upper_hz:
        raise ValueError('Unsupported frequency or feature geometry')
    if not all(math.isfinite(x) and x >= 0 for x in psd):
        raise ValueError('Expected finite nonnegative PSD values')
    if not all(math.isfinite(hz) and math.isfinite(db) and hz >= 0 for hz, db in peaks):
        raise ValueError('Expected finite peak pairs')
    if any(peaks[i][0] > peaks[i + 1][0] for i in range(len(peaks) - 1)):
        raise ValueError('Expected frequency-ordered peaks')
    # The first original peak is the initial amplitude comparator. A later
    # peak replaces it only inside the strict lower/upper frequency interval.
    index = 0
    for i, (hz, db) in enumerate(peaks):
        if hz >= upper_hz:
            break
        if hz > lower_hz and db > peaks[index][1]:
            index = i
    peak_hz, peak_db = peaks[index]
    size = len(psd)
    start = int(f32(f32(peak_hz * f32(size)) / frequency_ceiling))
    stop = f32(f32(f32(candidate_hz + peak_hz) * f32(size)) / frequency_ceiling)
    if start < 0:
        raise ValueError('Negative bin outside reviewed contract')
    gap = 0.
    bins = 0
    for at in range(start, size, 2):
        if f32(at) > stop:
            break
        db = f32(20. * log10(float(psd[at]) / 32768.)) if psd[at] > 0 else -3.4028234663852886e38
        gap = f32(gap + f32(peak_db - db))
        bins += 1
    stages = [f32(float(f32(gap / candidate_hz)) / 500.)]
    denominator = f32(stages[-1] + 1.)
    if denominator == 0:
        raise ValueError('Singular confidence denominator')
    stages.append(f32(feature4 / denominator))
    stages.append(f32(float(stages[-1]) / (float(feature12) * 32768. * 100.)))
    stages.append(f32(((float(peak_hz) * .5) / 1000. + .5) * float(stages[-1])))
    if not all(math.isfinite(x) for x in stages):
        raise ValueError('Confidence exceeds reviewed finite contract')
    return dict(confidence=stages[-1], rejects_selected_pitch=stages[-1] > .06,
                sampled_bins=bins, peak_index=index)
