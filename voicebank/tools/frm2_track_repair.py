"""Independent mode-1 short-track removal and internal gap interpolation.

Frames contain mutable [frequency, amplitude, phase] triples. Frames may be
empty; all nonempty frames have the same slot count.
"""
import math
from audit_frm2_numeric import f32


def repair(frames, rate, threshold, minimum_run, maximum_gap, first_time=0.):
    rate, threshold, minimum_run, maximum_gap = map(f32, (rate, threshold, minimum_run, maximum_gap))
    removed = filled = 0
    if len(frames) < 3:
        return dict(removed=0, filled=0)
    width = max(map(len, frames))
    if any(len(frame) not in (0, width) for frame in frames):
        raise ValueError('Nonempty frame widths must agree')

    def active(t, slot):
        if slot >= len(frames[t]):
            return False
        amplitude = frames[t][slot][1]
        return amplitude > threshold and amplitude != 10000.

    for slot in range(width):
        if minimum_run > 0:
            exempt = first_time > 0 and active(0, slot)
            start = None
            for t in range(len(frames)):
                if exempt:
                    if not active(t, slot):
                        exempt = False
                    continue
                if start is None:
                    if active(t, slot):
                        start = t
                elif not active(t, slot):
                    if frames[t] and f32((t-start)/rate) < minimum_run:
                        for j in range(start, t):
                            frames[j][slot][0:2] = [0., 10000.]
                            removed += 1
                    start = None
    for slot in range(width):
        if maximum_gap <= 0:
            continue
        seen = False
        start = None
        for t in range(len(frames)):
            if not seen:
                seen = active(t, slot)
            elif start is None:
                if not active(t, slot):
                    start = t
            elif active(t, slot):
                length = t-start
                if start > 0 and f32(length/rate) < maximum_gap:
                    left, right = frames[start-1][slot], frames[t][slot]
                    frequency_step = f32(f32(right[0]-left[0])/(length+1))
                    amplitude_step = f32(f32(right[1]-left[1])/(length+1))
                    for j in range(start, t):
                        previous = frames[j-1][slot]
                        if not frames[j]:
                            frames[j] = [[0., 10000., 0.] for _ in range(width)]
                        item = frames[j][slot]
                        item[0] = f32(previous[0]+frequency_step)
                        item[1] = f32(previous[1]+amplitude_step)
                        phase = f32(f32(previous[0]+item[0])*math.tau*.5/rate + previous[2])
                        item[2] = f32(phase-math.trunc(phase/math.tau)*math.tau)
                        filled += 1
                start = None
    return dict(removed=removed, filled=filled)
