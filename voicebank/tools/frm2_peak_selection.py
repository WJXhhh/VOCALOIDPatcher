"""Independent bounded model of the DSE harmonic track candidate selector.

Input peaks are (frequency, amplitude) pairs, not FFT bins. This module does
not detect peaks or prescribe the amplitude unit of the upstream detector.
"""
from audit_frm2_numeric import f32


def select(peaks, center, previous_amplitude, history, index, mode, config):
    center, previous_amplitude, history = map(f32, (center, previous_amplitude, history))
    c = lambda key: f32(config[key])
    alternate = c(0x33)
    if mode == 2:
        width = f32(c(0x28) + f32(c(0x29) * center))
    elif index < 0:
        width = f32((history if history > 0 else c(0xd)) * .5)
    else:
        if history <= 0:
            history = c(0xd)
        width = f32(f32(c(0x21) + f32(c(0x22) * index)) * history)
        if alternate:
            width = f32(width * .5)
    distance, candidates = [width] * 3, [-1] * 3
    metric, cost = [-1000. if alternate else 0.] * 3, [0.] * 3
    lower, upper = max(f32(center - width), c(0x27)), min(f32(center + width), c(2))
    result = dict(history=history, current=-1, rank=0, candidates=candidates,
                  distance=distance, metric=metric, cost=cost, order=[0, 0, 0], status=2)
    if lower >= upper:
        return result
    worst = width
    for i, (frequency, amplitude) in enumerate(peaks):
        frequency, amplitude = f32(frequency), f32(amplitude)
        delta = abs(f32(frequency - center))
        if lower <= frequency <= upper and delta < worst:
            if int(alternate) == 0:
                slot = max(range(3), key=lambda j: (distance[j], j))
                candidates[slot], distance[slot] = i, delta
                worst = max(0., *distance)
            else:
                slot = min(range(3), key=lambda j: (metric[j], -j))
                if amplitude > metric[slot]:
                    candidates[slot], distance[slot], metric[slot] = i, delta, amplitude
        if mode == 1 and frequency > upper and delta > worst:
            break
    for slot, candidate in enumerate(candidates):
        if candidate < 0:
            metric[slot] = cost[slot] = 1e8
            distance[slot] = f32(width + 1)
            continue
        amplitude = f32(peaks[candidate][1])
        if int(c(8)) == 2:
            cost[slot] = -amplitude
        elif int(c(8)) == 1:
            metric[slot] = 0. if previous_amplitude <= -3.4028234663852886e38 else abs(f32(amplitude - previous_amplitude))
            cost[slot] = f32(distance[slot] * .7 + metric[slot] * .3)
            if alternate:
                cost[slot] = 10000. if amplitude >= 0 else -amplitude
                if amplitude < 0 and mode == 1:
                    f0 = f32(center / (index + 1)) if index != -1 else float('inf')
                    if f0 > 0:
                        cost[slot] = f32(f32(f32(distance[slot] * 65) / f0) - amplitude)
        else:
            cost[slot] = distance[slot]
    order = [0, 1, 2]
    for left, right in ((0, 1), (0, 2), (1, 2)):
        if cost[order[right]] < cost[order[left]]:
            order[left], order[right] = order[right], order[left]
    result['order'] = order
    if candidates[order[0]] >= 0 and distance[order[0]] < width:
        result.update(current=candidates[order[0]], status=0)
    return result


def reject_crossing(tracks, claimed, owners, index):
    """Mode 1: the upper neighbor bound is exclusive, including the last peak."""
    track = tracks[index]
    current = track['current']
    for neighbor in range(max(0, current - 2), min(len(claimed) - 1, current + 2)):
        if neighbor == current or claimed[neighbor] != 1:
            continue
        previous = track['previous']
        other = tracks[owners[neighbor]]['previous']
        if previous == -1 or other == -1:
            return
        if (other < previous and current < neighbor) or (previous < other and neighbor < current):
            track['current'] = -1
            return


def resolve_collision(tracks, claimed, owners, incoming, existing, retry=True, trace=None, crossing=reject_crossing):
    """Mode 1 collision resolution. Mutates owned model state in place."""
    if trace is None:
        trace = []
    if len(trace) > 3 * len(tracks):
        raise ValueError('Invalid cyclic candidate state')
    shared = tracks[incoming]['current']

    def rank(track):
        for j, slot in enumerate(track['order']):
            if track['candidates'][slot] == track['current']:
                track['rank'] = j
                break

    for i in (incoming, existing):
        rank(tracks[i])
    score = lambda i: tracks[i]['cost'][tracks[i]['order'][tracks[i]['rank']]]
    winner, loser = (existing, incoming) if score(existing) <= score(incoming) else (incoming, existing)
    tracks[winner]['current'] = shared
    rank(tracks[winner])
    owners[shared] = winner
    track = tracks[loser]
    trace.append((winner, loser, shared))
    if track['rank'] == 2 or not retry:
        track['current'], track['center'] = -1, 0.
        return trace
    track['rank'] += 1
    track['current'] = track['candidates'][track['order'][track['rank']]]
    if track['current'] >= 0:
        crossing(tracks, claimed, owners, loser)
        peak = track['current']
        if peak >= 0:
            if claimed[peak] == 1:
                resolve_collision(tracks, claimed, owners, loser, owners[peak], retry, trace, crossing)
            else:
                claimed[peak], owners[peak] = 1, loser
    return trace


def advance_harmonics(tracks, peaks, f0, history, config, owners):
    """Mode-1 f0690 orchestration through f0cf0 output, with supplied peaks.

    Existing tracks are advanced in place. Missing output slots are left as
    None: allocation/default initialization belongs to the frame allocator.
    """
    claimed = [0] * len(peaks)
    for track in tracks:
        track['previous'], track['current'] = track['current'], -1
    trace = []
    if f0 > 0:
        for i, track in enumerate(tracks):
            if track['center'] <= 0 or not peaks:
                continue
            if config[0x12] > 0:
                numerator = denominator = 0.
                for j in range(max(0, i - int(config[0x13])), i):
                    peak = tracks[j]['current']
                    if peak >= 0:
                        frequency, amplitude = peaks[peak][:2]
                        numerator = f32(numerator + f32(f32(frequency - f32((j + 1) * f0)) / amplitude))
                        denominator = f32(denominator + 1. / amplitude)
                correction = f32(numerator / denominator) if denominator > 0 else 0.
                track['center'] = f32(track['center'] + f32(correction * f32(config[0x12])))
            result = select([p[:2] for p in peaks], track['center'], track['amplitude'], history, i, 1, config)
            history = result['history']
            for key in ('current', 'candidates', 'distance', 'metric', 'cost'):
                track[key] = result[key]
            if result['order'] != [0, 0, 0]:
                track['order'] = result['order']
            if result['status']:
                track['center'] = 0.
                continue
            track['rank'] = 0
            reject_crossing(tracks, claimed, owners, i)
            peak = track['current']
            if peak >= 0:
                if claimed[peak] == 1:
                    trace.extend(resolve_collision(tracks, claimed, owners, i, owners[peak], bool(config[10])))
                else:
                    claimed[peak], owners[peak] = 1, i
    output = []
    if f0 > 0:
        for track in tracks:
            peak = track['current']
            output.append(peaks[peak][:3] if peak >= 0 else None)
            if peak >= 0:
                track['amplitude'] = peaks[peak][1]
    return dict(history=history, claimed=claimed, owners=owners, output=output, collisions=len(trace))


def initialize_tracks(peaks, f0, mode, count, ratio=1.):
    """Mode-1 f01d0 with a static harmonic ratio."""
    if mode != 1:
        raise ValueError('Only harmonic initialization has been modeled')
    def blank():
        return dict(center=0., amplitude=0., previous=0, current=0, rank=0,
                    candidates=[0]*3, distance=[0.]*3, metric=[0.]*3,
                    cost=[0.]*3, order=[0]*3)
    tracks = []
    if mode == 1:
        for i in range(count):
            item = blank()
            item.update(center=f32(ratio*f32((i+1)*f0)) if f0 > 0 else 0.,
                        amplitude=-3.4028234663852886e38, current=-1)
            tracks.append(item)
    return tracks


def advance_outer(state, peaks, f0, config, owners, ratio=1., timestamp=0.):
    """Mode 0/1 outer entry at queue index zero (no temporal queue repair)."""
    if timestamp == -1:
        return dict(output=[], claimed=None, collisions=0)
    mode = int(config[0])
    if mode not in (0, 1):
        raise ValueError('Only disabled/harmonic outer modes have been modeled')
    result = dict(output=[], claimed=None, collisions=0)
    if mode == 0:
        if state['initialized']:
            state['initialized'], state['tracks'] = False, []
    elif not state['initialized']:
        state['tracks'] = initialize_tracks(peaks, f0, mode, int(config[4]), ratio)
        result = advance_harmonics(state['tracks'], peaks, f0, state['history'], config, owners)
        state['initialized'] = True
    else:
        if f0 > 0 and config[0x11] > 0:
            for i, track in enumerate(state['tracks']):
                target = f32(ratio*f32((i+1)*f0))
                track['center'] = f32(f32(f32(target-track['center'])*f32(config[0x11]))+track['center'])
        result = advance_harmonics(state['tracks'], peaks, f0, state['history'], config, owners)
        if f0 > 0 and config[9] > 0:
            for track, output in zip(state['tracks'], result['output']):
                if output is not None and output[0] > 0:
                    track['center'] = f32(f32(f32(output[0]-track['center'])*f32(config[9]))+track['center'])
    if mode == 1 and f0 <= 0:
        state['initialized'] = False
    state['history'] = f32(f0)
    return result
