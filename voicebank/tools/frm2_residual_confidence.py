"""Float32 residual/source absolute-sum confidence for reviewed finite inputs."""
import math

from audit_frm2_numeric import f32


def residual_confidence(source, residual):
    if len(source) != len(residual) or not source:
        raise ValueError('Expected equally sized nonempty sample windows')
    numerator = denominator = 0.
    for original, difference in zip(source, residual):
        if not math.isfinite(original) or not math.isfinite(difference):
            raise ValueError('Expected finite samples')
        original, difference = f32(original), f32(difference)
        if not math.isfinite(original) or not math.isfinite(difference):
            raise ValueError('Samples exceed the finite float32 contract')
        denominator = f32(denominator + abs(original))
        numerator = f32(numerator + abs(difference))
        if not math.isfinite(denominator) or not math.isfinite(numerator):
            raise ValueError('Accumulation exceeds the finite float32 contract')
    return min(1., f32(numerator / denominator)) if denominator > 0. else 0.
