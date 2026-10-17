"""Independent ordinary DRS E7-to-residual generation on a 30-Hz grid."""
import bisect
import math

from audit_frm2_numeric import f32, interpolate
from frm2_resonance_optimizer import curve, response


def residual_envelope(points, f0, abc, primary, secondary, *, offset=0., maximum=22050., frequencies=None):
    count = int(f32(maximum / 30))
    xs = [i * 30 / maximum for i in range(count)] + [1.]
    ys = [0.] * len(xs)
    if frequencies is None:
        frequencies = [f32((i + 1) * f0) for i in range(int(f32(maximum / f0)))]
    else:
        multiplier = f0 if frequencies[0] < 5 else 1.
        frequencies = [f32(f * multiplier) for f in frequencies]
    previous_index, previous = 0, 0.
    for hz in frequencies:
        if hz <= 0:
            continue
        total = 0.
        for resonance in primary + secondary:
            total = f32(total + response(hz, resonance))
        level = f32(20 * math.log10(total / 32768)) if total > 0 else total
        # ADDSD twice, then one conversion: do not round A + curve first.
        if secondary:
            level = f32(abc[0] + curve(abc, hz) + level)
        x = f32(hz / maximum)
        value = f32(f32(interpolate(points, x) + offset) - level)
        right = min(len(xs) - 1, bisect.bisect_left(xs, x))
        index = right if right == 0 or xs[right] - x <= x - xs[right - 1] else right - 1
        for i in range(previous_index + 1, index + 1):
            ys[i] = min(10., max(-100., f32((i - previous_index) / (index - previous_index) * (value - previous) + previous)))
        previous_index, previous = index, value
    return list(zip(xs, ys))
