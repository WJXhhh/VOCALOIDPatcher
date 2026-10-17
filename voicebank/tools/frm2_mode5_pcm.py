"""PCM-to-mode5 scores and confidence for the verified default 44.1 kHz contract.

The default implementation loads no DSE and uses Python FFT/logarithm plus
explicit float32 rounding. It is not bit-compatible with every FFT backend:
near-flat spectra can change peak regions discontinuously. Optional numerical
backends isolate libm/FFT error; their compatibility must be stated separately.
"""
import math

from audit_frm2_numeric import f32
from verify_frm2_analysis_windows import window, prepare as prepare_fft, fft
from frm2_mode5_scores import prepare, templates, score
from frm2_mode5_confidence import interval, confidence


def logarithm(value):
    return f32(math.log(value)) if value > 0. else -math.inf if value == 0. else math.nan


class Mode5PcmModel:
    def __init__(self, *, fft_backend=None, log_backend=None):
        self.transform = fft_backend or fft
        self.log = log_backend or logarithm
        self.window = window(None, 1025, 11)
        self.templates = templates(2048, 22050., 6000., 22050., self.log)
        self.low, self.high = interval(len(self.templates), 2048, 22050., 40., 1000.)

    def power_at(self, pcm, frame_index):
        if not isinstance(frame_index, int):
            raise ValueError('Expected an integer hop index')
        start = frame_index * 256 - 512
        signal = [f32(pcm[at]) if 0 <= at < len(pcm) else 0. for at in range(start, start + 1025)]
        data = prepare_fft(signal, self.window, 4., True)
        assert len(data) == 4096
        bins = self.transform(data)
        assert len(bins) >= 2048
        # The native packed representation treats DC differently from other
        # slots and does not include Nyquist in the candidate input spectrum.
        power = [abs(f32(bins[0].real))]
        for value in bins[1:2048]:
            real, imaginary = f32(value.real), f32(value.imag)
            power.append(f32(f32(real * real) + f32(imaginary * imaginary)))
        return power

    def from_power(self, power, indices=None):
        if len(power) != 2048:
            raise ValueError('Unsupported default mode5 spectrum length')
        prepared = prepare(power, 22050., 6000., 2. ** -30, self.log)
        selected = range(self.low, self.high) if indices is None else tuple(indices)
        values = [0.] * len(self.templates)
        for index in selected:
            if not isinstance(index, int) or not 0 <= index < len(values):
                raise ValueError('Unsupported candidate index')
            values[index] = score(index, prepared, self.templates)
        if indices is not None and not all(index in selected for index in range(self.low, self.high)):
            raise ValueError('Requested scores omit confidence inputs')
        return dict(scores=values, confidence=confidence(values, self.low, self.high), prepared=prepared)

    def analyze(self, pcm, frame_index, indices=None):
        return self.from_power(self.power_at(pcm, frame_index), indices)
