"""Raw bit15: relative deviation of ten temporal zero-crossing counts.

The mode4 score estimator can overwrite this value later. Preserve the physical
ring eligibility and float32 operations of d92e0; this is not an RMS strength.
"""
import math

from audit_frm2_numeric import f32


def crossing_distribution(ring, start, hop):
    size = len(ring)
    count = 2 * hop
    if not 0 <= start < size or hop < 2 or count >= size:
        raise ValueError('Invalid bounded ring geometry')
    indices = [(start + index) % size for index in range(count)]
    end = indices[-1]
    total = 0.
    for index in indices:
        total = f32(total + ring[index])
    mean = f32(total / count)
    histogram = [0] * 10
    for offset, index in enumerate(indices):
        current = f32(ring[index] - mean)
        following = f32(ring[(index + 1) % size] - mean)
        if index < end and (f32(following * current) < 0 or current == 0):
            bin_index = math.floor(f32(f32(offset / (count - 1)) * 10))
            assert 0 <= bin_index < 10
            histogram[bin_index] += 1
    crossings = sum(histogram)
    if crossings:
        average = f32(crossings / 10)
        deviation = 0.
        for value in histogram:
            deviation = f32(deviation + abs(f32(value - average)))
        imbalance = f32(f32(deviation / 10) / average)
    else:
        imbalance = 0.
    return dict(imbalance=imbalance, crossings=crossings, histogram=histogram)
