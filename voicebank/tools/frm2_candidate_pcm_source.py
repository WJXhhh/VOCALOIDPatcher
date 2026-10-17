"""PCM inputs for the reviewed default candidate-analysis spectrum protocol.

Window reuse selects size1025/kind11; hop256, factor4 and centered placement
give a 4096-point real transform. The optional FFT backend owns its descriptor.
The physical-index rule in raw_features is retained with the default ring size.
"""
import math

from audit_frm2_numeric import f32
from frm2_raw_features import raw_features
from frm2_raw_feature_observer import OwnedRingView
from verify_frm2_analysis_windows import window, prepare, fft
from verify_vqm_bit6_component_ratio import predict


RING_SIZE = 45124
HOP = 256


class CandidatePcmSource:
    def __init__(self, transform=None):
        self.transform = transform or fft
        self.weights = window(None,1025,11)

    def power_at(self, pcm, index):
        if not isinstance(index,int) or index < 0:
            raise ValueError('Expected a nonnegative hop index')
        start = index*HOP-512
        samples = [f32(pcm[at]) if 0 <= at < len(pcm) else 0.
                   for at in range(start,start+1025)]
        data = prepare(samples,self.weights,4.,True)
        assert len(data) == 4096
        bins = self.transform(data)
        assert len(bins) >= 2048
        power = [abs(f32(bins[0].real))]
        for value in bins[1:2048]:
            real,imaginary = f32(value.real),f32(value.imag)
            power.append(f32(f32(real*real)+f32(imaginary*imaginary)))
        return power


def raw_at(pcm, index, enabled=True):
    if not isinstance(index,int) or index < 0:
        raise ValueError('Expected a nonnegative hop index')
    if not enabled:
        return dict(feature4=0.,feature8=0.,feature12=0.)
    absolute = index*HOP
    center = absolute%RING_SIZE
    ring = OwnedRingView(pcm,absolute,center,RING_SIZE)
    return raw_features(ring,(center-HOP)%RING_SIZE,HOP,44100)


def band_ratio(records):
    components = [(hz,db) for hz,db,span,bin_index in records]
    return predict(components,lambda db:f32(32768.*math.exp(f32(db)*.1151292546497)))
