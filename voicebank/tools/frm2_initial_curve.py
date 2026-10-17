"""Independent initial set-0 ABC estimate for E7 or harmonic-array input."""
import math

from audit_frm2_numeric import f32


def initial_curve(points, f0, offset=0., *, frequencies=None, amplitudes=None, maximum=22050.):
    abc = (10000., 10000., 0.)
    fitted = False
    if frequencies is not None:
        multiplier = f0 if frequencies[0] < 5 else 1.
        knots = [(f32(f * multiplier), a) for f, a in zip(frequencies, amplitudes)
                 if a != 10000.]
        selected = [(f32(f32(f - frequencies[0]) * multiplier), f32(a + offset))
                    for f, a in zip(frequencies, amplitudes)
                    if 0 <= f32(f * multiplier) <= 10000 and a != 10000.]
        anchor = f32(frequencies[0] * multiplier)
    elif len(points) >= 5:
        knots = [(x * maximum, y) for x, y in points[1:-1]]
        selected = [(f32((x - points[1][0]) * maximum), f32(y + offset))
                    for x, y in points[1:-1] if x * maximum <= 10000]
        anchor = f0
    else:
        knots, selected, anchor = [], [], f0
    if len(selected) > 2:
        low = [a for hz, a in knots if hz <= 250]
        reference = f32((max([-1000.] + low) if low else knots[0][1]) + offset)
        sum_x = sum_y = 0.
        for x, y in selected:
            sum_x = f32(sum_x + x)
            sum_y = f32(sum_y + f32(math.log(max(y, -99.999) + 100)))
        value = f32(reference + 100)
        log_reference = f32(math.log(value)) if value > 0 else -math.inf if value == 0 else math.nan
        mean_x, mean_y = f32(sum_x / len(selected)), f32(sum_y / len(selected))
        slope = f32(-f32(f32(log_reference - mean_y) / mean_x)) if mean_x else math.nan
        try:
            level = f32(f32(math.exp(f32(-anchor * slope))) * f32(math.exp(log_reference)))
        except OverflowError:
            level = math.inf
        abc = (f32(level - 100), level, slope)
        fitted = True
    a, b, slope = [default if math.isnan(v) else v for v, default in zip(abc, (-3., 80., -.004))]
    return (f32(min(a, -3)), f32(min(100, max(20, b))), f32(max(-.0009, min(-.00003, slope)))), fitted
