"""Mode5 spectral preparation and candidate scoring, with explicit float32 steps.

This starts at the caller's spectrum, not at PCM or a fabricated score cache.
The logarithm is supplied separately so libm rounding can be audited independently.
"""
import math

from audit_frm2_numeric import f32


def weights():
    return [f32(1. / f32(max(0., f32(i - .5)) + 1.)) for i in range(20)]


def templates(size, rate, ceiling, harmonic_ceiling, logf):
    ratio = f32(f32(size) / rate)
    count = int(min(ceiling * ratio, size - 1) * 8.)
    step = f32(rate / f32(size * 8))
    coefficients = weights()
    result = [None] * count
    for index in range(3, count):
        frequency = f32(f32(index) * step)
        attenuation = f32(1. - f32(logf(f32(max(40., min(frequency, harmonic_ceiling)) - 29.)) / logf(12000.)))
        coordinate = f32(f32(index) - .5)
        samples = []
        total = 0.
        for harmonic in range(15):
            position = f32(f32(f32(harmonic + 1) * coordinate) / 8.)
            lower = math.floor(position)
            if (lower + 1) * 8 > count:
                break
            fraction = f32(position - f32(lower))
            emphasis = 1. if harmonic <= 2 else f32((6 - harmonic) * .25) if harmonic <= 6 else 0.
            samples.append((lower, lower + 1, fraction, f32(1. - fraction), emphasis))
            total = f32(total + coefficients[harmonic])
        normalization = f32(1. / total) if total else math.inf
        if frequency < 30.:
            attenuation = f32(f32(frequency / 30.) * attenuation)
        result[index] = (samples, normalization, attenuation)
    return result


def prepare(source, rate, ceiling, scale, logf, *, right_neighbor=0.):
    """Prepare excess spectrum, original log spectrum, and peak-region identities.

    right_neighbor is the value past the logical end of the prepared log array,
    used by the native peak test. It is not an additional input-spectrum bin.
    Its allocation/initialization is a separate boundary contract.
    """
    size = len(source)
    if size < 4 or not all(math.isfinite(x) and x >= 0 for x in source):
        raise ValueError('Unsupported spectral input')
    original = [max(-5., f32(logf(f32(x * scale)) + f32(20.794414520263672))) if x > 0 else -5.
                for x in source[:size]]
    transformed = original.copy()
    maximum = max(0., max(original))
    frequencies = [0., 5., 10.]
    while len(frequencies) < 100:
        value = f32(frequencies[-1] * 1.125)
        frequencies.append(value)
        if value > ceiling or value > rate:
            break
    else:
        raise ValueError('Unsupported envelope range')
    envelope = []
    def bin_at(frequency):
        return math.floor(f32(f32(f32(frequency / rate) * f32(size)) + .5))
    for index in range(len(frequencies) - 1):
        lower = 0 if index == 0 else bin_at(frequencies[max(index - 2, 0)])
        center = bin_at(frequencies[index])
        upper = min(size - 1, bin_at(frequencies[index + 1]))
        total = normalization = 0.
        if center > lower:
            for at in range(lower, center):
                factor = f32(1. - f32(f32(center - at) / f32(center - lower)))
                factor = f32(factor * factor)
                normalization = f32(normalization + factor)
                total = f32(total + f32(factor * source[at]))
        width = max(1., f32(upper - center))
        for at in range(center, upper + 1):
            factor = f32(1. - f32(f32(at - center) / width))
            factor = f32(factor * factor)
            normalization = f32(normalization + factor)
            total = f32(total + f32(factor * source[at]))
        if normalization > 0.:
            total = f32(total / normalization)
        total = f32(f32(total * scale) * f32(.08))
        envelope.append(max(-5., f32(logf(total) + f32(20.794414520263672))) if total > 0 else -5.)
    floor = f32(max(0., max(envelope)) - 10.)
    envelope = [max(x, floor) for x in envelope]
    unsmoothed = envelope.copy()
    for index, value in enumerate(unsmoothed):
        slope = f32(f32(f32(min(1., f32(frequencies[index] / 2000.)) * f32(5.050000190734863)) - 5.5) / 200.)
        for other in range(len(envelope)):
            if other != index:
                distance = f32(abs(f32(frequencies[other] - frequencies[index])))
                envelope[other] = max(envelope[other], f32(f32(distance * slope) + value))
    identities = [0] * size
    segment_start = left_peak = 0
    envelope_index = 0
    envelope_left = bin_at(frequencies[0])
    envelope_right = bin_at(frequencies[1])
    # Only this right-neighbour comparison consumes the extra input value.
    right_guard = right_neighbor
    for at in range(1, size):
        previous, current = original[at - 1], original[at]
        following = original[at + 1] if at + 1 < size else right_guard
        if current > previous and current > following:
            left_peak = at
        elif (previous > current and following > current) or current <= -5.:
            if left_peak < bin_at(frequencies[0]):
                baseline = envelope[envelope_index]
            else:
                while left_peak >= envelope_right and envelope_index < len(envelope) - 1:
                    envelope_left = envelope_right
                    envelope_index += 1
                    envelope_right = bin_at(frequencies[envelope_index + 1])
                if envelope_index >= len(envelope) - 1:
                    baseline = envelope[envelope_index]
                else:
                    baseline = f32(f32(f32(f32(left_peak - envelope_left) * f32(envelope[envelope_index + 1] - envelope[envelope_index])) / f32(envelope_right - envelope_left)) + envelope[envelope_index])
            for target in range(segment_start, at):
                transformed[target] = max(0., f32(original[target] - baseline))
                identities[target] = left_peak
            segment_start = at
            left_peak = at
    for target in range(segment_start, size):
        transformed[target] = f32(original[target] - envelope[-1])
        identities[target] = 0
    return transformed, original, identities, maximum, envelope, frequencies[:len(envelope)]


def score(index, prepared, candidate_templates):
    excess, original, identities, maximum = prepared[:4]
    if index <= 2 or index >= len(candidate_templates):
        return 0.
    samples, normalization, attenuation = candidate_templates[index]
    if not samples:
        return 0.
    coefficients = weights()
    total = maximum_harmonic = previous_value = 0.
    previous_identity = -1
    for harmonic, (lower, upper, upper_weight, lower_weight, emphasis) in enumerate(samples):
        value = f32(f32(f32(upper_weight * excess[upper]) + f32(lower_weight * excess[lower])) * coefficients[harmonic])
        amplitude = f32(f32(upper_weight * original[upper]) + f32(lower_weight * original[lower]))
        identity = identities[lower]
        if identity == previous_identity:
            if previous_value > value:
                continue
            total = f32(total - previous_value)
        total = f32(total + value)
        previous_identity, previous_value = identity, value
        maximum_harmonic = max(maximum_harmonic, f32(amplitude * emphasis))
    total = f32(total * normalization)
    if maximum > 0.:
        total = f32(total * min(1., f32(f32(maximum_harmonic + 3.) / maximum)))
    total = max(0., total)
    return f32(f32(total * attenuation) * 200.)
