"""Independent set-0 primary/secondary fitting and refinement on analysis ENV.

Only arithmetic and the previously verified ENV/resonance formulas are used.
No native optimizer, grid search, or native ENV evaluator is called.
"""
from __future__ import annotations

import math

from audit_frm2_numeric import f32, interpolate

FLOOR = -3.4028234663852886e38


def magnitude(real, imaginary):
    return math.sqrt(imaginary * imaginary + real * real)


def response(hz, resonance):
    center, amplitude, bandwidth = resonance
    # DRS setter 0x62760 uses double exp, then rounds the scaled amplitude.
    # Do not reuse audit_frm2_numeric.response's float-exponent conversion.
    return grid_response(hz, center, amplitude, bandwidth)


def linear(value):
    return 0. if value <= FLOOR else f32(32768 * math.exp(f32(value) * (math.log(10) / 20)))


def grid_response(hz, center, amplitude, bandwidth):
    bandwidth = min(1500, max(20, bandwidth))
    a2 = -math.exp(-2 * math.pi * bandwidth / 22050)
    a1 = 2 * math.exp(-math.pi * bandwidth / 22050) * math.cos(math.pi)
    gain = 1 - a1 - a2
    norm = gain / magnitude(1 - a1 * math.cos(math.pi) - a2 * math.cos(2 * math.pi),
                            a1 * math.sin(math.pi) + a2 * math.sin(2 * math.pi))
    theta = (11025 + center - hz) * math.tau / 22050
    denominator = magnitude(1 - a1 * math.cos(theta) - a2 * math.cos(2 * theta),
                             a1 * math.sin(theta) + a2 * math.sin(2 * theta))
    delta = hz - center
    return gain / denominator * linear(amplitude) / norm * math.exp((-delta * delta / 2000) / 2000)


def curve(abc, hz):
    return f32(f32(f32(math.exp(f32(f32(hz) * abc[2]))) - 1) * abc[1])



def grid_search(target, axis, low, high, count, center, level, width):
    if axis == 2:
        high = min(500., high)
        low = min(low, high - 10)
    step = (high - low) / count
    value, best, best_value = low, math.inf, low
    for k in range(count):
        candidate = [center, level, width]
        candidate[axis] = value
        error = sum((grid_response(hz, *candidate) - y) ** 2 for hz, y in target)
        if error < best:
            best, best_value = error, low + k * step
        value += step
    return best_value


def fit(points, f0, abc, primary, secondary, index, *, optimize=True, movement=100., offset=0.):
    if not 0 <= index < len(secondary):
        raise ValueError('Secondary index out of range')
    if len(points) < 2 or any(not math.isfinite(v) for p in points for v in p):
        raise ValueError('Require a finite analysis envelope with at least two points')
    old_center, _, old_width = secondary[index]
    residual = []
    for k, (x, value) in enumerate(points):
        value = f32(value)
        if k < len(points) - 1:
            value = f32(f32(value + offset) - f32(abc[0] + curve(abc, x * 22050)))
        if 0 < k < len(points) - 1:
            others = sum(response(x * 22050, res) for res in primary)
            others += sum(response(x * 22050, res) for j, res in enumerate(secondary) if j != index)
            remaining = f32(linear(value) - others)
            value = f32(20 * math.log10(remaining / 32768)) if remaining > 0 else FLOOR
        residual.append((x, value))

    def nearest(x):
        x = min(1., max(0., x))
        return min(range(len(points)), key=lambda i: (abs(points[i][0] - x), -i))

    start = max(1, nearest((old_center - 300) / 22050))
    end = min(len(points) - 1, max(start + 2, nearest((old_center + 300) / 22050)))
    target = [(x * 22050, linear(y)) for x, y in residual[start:end + 1]]
    amplitude = max(interpolate(points, f32(f0 / 22050)),
                    interpolate(points, old_center / 22050) + offset) - (abc[0] + curve(abc, old_center))
    if not optimize:
        return (old_center, amplitude, old_width), {}

    low, high = max(30, old_width - 100), old_width + 100
    width = max(200., grid_search(target, 2, low, high, int(high - low), old_center, amplitude, old_width))
    low = max(points[start][0] * 22050, secondary[0][0] + 80)
    high = points[end][0] * 22050
    center = grid_search(target, 0, low, high, 40, old_center, amplitude, width)
    center = max(old_center - movement, min(old_center + movement, center))
    coarse = grid_search(target, 1, amplitude - 10, amplitude + 10, 40, center, amplitude, width)
    amplitude = grid_search(target, 1, coarse - 2, coarse + 2, 40, center, coarse, width)
    low, high = max(30, width - 50), width + 50
    final_width = max(200., grid_search(target, 2, low, high, int(high - low), center, amplitude, width))
    return (center, amplitude, final_width), dict(start=start, end=end, first_width=width, coarse_amplitude=coarse)


def fit_primary(points, abc, primary, secondary, index, *, optimize=True, movement=100., offset=0.):
    """Independent ordinary E7 branch of primary entry 0x6f770."""
    if not 0 <= index < len(primary):
        raise ValueError('Primary index out of range')
    old_center, _, old_width = primary[index]
    other_primary = [res for j, res in enumerate(primary) if j != index]
    residual = []
    for k, (x, y) in enumerate(points):
        value = f32(y)
        if k < len(points) - 1:
            value = f32(value + offset)
            if secondary:
                value = f32(value - f32(abc[0] + curve(abc, x * 22050)))
        if 0 < k < len(points) - 1:
            others = sum(response(x * 22050, res) for res in other_primary + secondary)
            remaining = f32(linear(value) - others)
            value = f32(20 * math.log10(remaining / 32768)) if remaining > 0 else FLOOR
        residual.append((x, value))
    nearest = lambda x: min(range(len(points)), key=lambda i: (abs(points[i][0] - min(1., max(0., x))), -i))
    start = max(1, nearest((old_center - 300) / 22050))
    end = min(len(points) - 1, max(start + 2, nearest((old_center + 300) / 22050)))
    target = [(x * 22050, linear(y)) for x, y in residual[start:end + 1]]
    best, level = -math.inf, 0.
    for k in range(start, end + 1):
        total = 0.
        # This native ranking branch gates the other-primary loop on the
        # presence of secondary elements, independently of residual generation.
        for res in (other_primary if secondary else []) + secondary:
            total = f32(total + response(points[k][0] * 22050, res))
        db = f32(20 * math.log10(total / 32768)) if total > 0 else FLOOR
        score = f32(f32(points[k][1] - db) + offset)
        if score > best:
            best, level = score, residual[k][1]
    low = points[start][0] * 22050
    if secondary:
        low = max(low, secondary[0][0] + 80)
    high = points[end][0] * 22050
    center = grid_search(target, 0, low, high, 20, old_center, level, old_width)
    center = max(old_center - movement, min(old_center + movement, center))
    amp = grid_search(target, 1, f32(level - 10), f32(level + 10), 40, center, level, old_width)
    amp = grid_search(target, 1, amp - 2, amp + 2, 40, center, amp, old_width)
    if not optimize:
        return (center, amp, old_width), dict(start=start, end=end, initial_level=level)
    width_low, width_high = max(100, old_width - 150), min(550, old_width + 100)
    width = max(200., grid_search(target, 2, width_low, width_high, int(width_high - width_low), center, amp, old_width))
    center = grid_search(target, 0, low, high, 40, center, amp, width)
    center = max(old_center - movement, min(old_center + movement, center))
    amp = grid_search(target, 1, amp - 10, amp + 10, 40, center, amp, width)
    amp = grid_search(target, 1, amp - 2, amp + 2, 40, center, amp, width)
    width_low, width_high = max(30, width - 50), width + 50
    final_width = max(200., grid_search(target, 2, width_low, width_high, int(width_high - width_low), center, amp, width))
    return (center, amp, final_width), dict(start=start, end=end, initial_level=level, first_width=width)


def refine(points, f0, abc, primary, secondary, *, offset=0.):
    """Model set-0 refinement 0x70f10 with E7 present and positive Hz F0."""
    primary, secondary = list(primary), list(secondary)
    restored = floors = 0
    for i in range(len(secondary)):
        secondary[i], _ = fit(points, f0, abc, primary, secondary, i, optimize=False, offset=offset)
    previous_original = 0.
    for i in range(len(primary)):
        original = f32(primary[i][0])
        primary[i], _ = fit_primary(points, abc, primary, secondary, i, movement=150, offset=offset)
        if i:
            previous = primary[i - 1][0]
            previous_movement = f32(previous - previous_original)
            distance = f32(previous - original)
            movement = f32(abs(primary[i][0] - original))
            if previous_movement > 0 and distance < 0 and abs(distance) < movement and -distance < previous_movement:
                primary[i - 1] = (previous_original, *primary[i - 1][1:])
                primary[i] = (original, *primary[i][1:])
                primary[i], _ = fit_primary(points, abc, primary, secondary, i, movement=300, offset=offset)
                primary[i - 1], _ = fit_primary(points, abc, primary, secondary, i - 1, movement=300, offset=offset)
                restored += 1
        center, amplitude, bandwidth = primary[i]
        baseline = f32(abc[0] + curve(abc, center))
        # 0x71259 DIVSS: both the center conversion and normalized coordinate
        # round to float before the double-coordinate ENV lookup.
        measured = f32(interpolate(points, f32(f32(center) / 22050)) + offset)
        if f32(baseline + amplitude) < measured:
            amplitude = f32(measured - baseline)
            primary[i] = (center, amplitude, bandwidth)
            floors += 1
        previous_original = original
    return primary, secondary, dict(restored_pairs=restored, amplitude_floors=floors)
