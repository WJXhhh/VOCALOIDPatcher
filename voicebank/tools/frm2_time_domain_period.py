"""Period selection/decay/history from retained time-domain events.

Retained event times and amplitudes are explicit inputs. All period memories,
level decay, pitch history and pitch decisions start from owned zero state.
"""
import math
import struct
from collections import Counter

from audit_frm2_numeric import f32
from frm2_candidate_pcm_source import RING_SIZE


class TimeDomainPeriod:
    def __init__(self, configured):
        self.configured = configured
        self.cursor = 0
        self.mean = self.variation = self.pitch = 0.
        self.pitch_history = [0.]*10
        self.events = Counter()
        self.reset()
        self.unused_gain = 0.

    def reset(self):
        self.gain = self.previous_gain = 1.
        self.slope = self.level = 0.
        self.held_level = 1.
        self.periods = [0.]*30
        self.last_peak = -1
        self.count = 0
        self.pitch = 0.
        self.unused_gain = 1.

    @staticmethod
    def difference(a,b):
        delta = a-b
        if delta > RING_SIZE//2:
            delta -= RING_SIZE
        elif delta < -(RING_SIZE//2):
            delta += RING_SIZE
        return delta

    def decay(self, samples):
        self.level = f32((self.slope/44100.)*float(samples)+float(self.level))

    def local_gain(self, indices, amplitudes, index):
        left = right = index
        peak = 1.
        found = loops = 0
        left_active = right_active = True
        extent = int(44100.*.005)
        while found < 4 and loops < 30:
            if left_active:
                delta = self.difference(indices[index],indices[left])
                if delta < -extent:
                    left_active = False
                else:
                    value = amplitudes[left]
                    if value > 0:
                        if float(peak)*1.3 < value:
                            peak = f32(value)
                        found += 1
                    left -= 1
                    left_active = left >= 0
            if right_active:
                delta = self.difference(indices[right],indices[index])
                if extent < delta:
                    right_active = False
                else:
                    value = amplitudes[right]
                    if value > 0:
                        if float(peak)*1.3 < value:
                            peak = f32(value)
                        found += 1
                    right += 1
                    right_active = right < 30
            loops += 1
        self.events['local_gain'] += 1
        return min(400.,f32(30000./peak)) if peak < 30000. else 1.

    def event_gain(self, indices, amplitudes, index):
        current,previous = int(self.periods[-1]),int(self.periods[-2])
        if self.last_peak == -1 or current < 1 or previous < 1 or not current*.8 < previous < current*1.3:
            return self.local_gain(indices,amplitudes,index)
        expected = int(float(current+previous)*.5+float(self.last_peak))%RING_SIZE
        delta = self.difference(expected,indices[index])
        if abs(f32(delta)) < 44100.*.0015:
            nearest = 10000
            amplitude = 1.
            for i in range(index,30):
                if amplitudes[i] > 0:
                    distance = self.difference(expected,indices[i])
                    if nearest < distance:
                        break
                    amplitude,nearest = f32(amplitudes[i]),distance
            if amplitude > 1.:
                self.events['forecast_gain'] += 1
                return min(800.,f32(45000./amplitude))
        if delta < 1:
            return self.local_gain(indices,amplitudes,index)
        self.events['retained_gain'] += 1
        return self.previous_gain

    def accept_level(self, indices, index, scaled):
        if not self.level < scaled:
            return False
        self.level = scaled
        self.held_level = max(1.,scaled)
        self.slope = float(-scaled)/.020
        self.periods = self.periods[1:]+[0.]
        span = 0 if self.last_peak == -1 else (indices[index]-self.last_peak)%RING_SIZE
        self.periods[-1] = f32(span)
        self.last_peak = indices[index]
        self.events['level_accept'] += 1
        return True

    def accept_forecast(self, indices, index, scaled):
        average = int(f32(f32(self.periods[-1]+self.periods[-2])*.5))
        expected = (self.last_peak+average)%RING_SIZE
        tolerance = int(float(average)*.5)
        self.decay(1)
        first = self.difference(expected,indices[index])
        if first > 0:
            return None
        alternate = index-2 if index >= 2 else index
        second = self.difference(expected,indices[alternate])
        if first < tolerance:
            selected = alternate if second < tolerance and not abs(f32(first)) < abs(f32(second)) else index
        else:
            selected = alternate if second < tolerance else -1
        if selected == -1:
            self.count = 2
            self.events['forecast_miss'] += 1
            return False
        self.level = self.held_level = 30000.
        self.slope = -30000./.020
        if selected != index:
            distance = self.difference(indices[selected],indices[index])
            self.level = self.held_level = f32(30000.-(self.slope/44100.)*float(distance))
            self.events['alternate_peak'] += 1
        self.periods = self.periods[1:]+[0.]
        span = (indices[selected]-self.last_peak)%RING_SIZE
        previous = self.periods[-2]
        if self.count < 11 or self.variation >= .1:
            weight = .6
        else:
            ratio = f32(f32(span)/previous) if previous != 0 else math.inf
            weight = .1 if ratio < f32(.75) or 1.3 < ratio else .5
        self.periods[-1] = f32(float(previous)*(1.-weight)+float(span)*weight)
        self.last_peak = indices[selected]
        self.events['forecast_accept'] += 1
        self.events['period_weight_'+str(weight)] += 1
        return True

    def process(self, cursor, indices, amplitudes, source, summarize=True):
        assert len(indices) == len(amplitudes) == 30
        old_cursor,self.cursor = self.cursor,cursor
        start = -1
        distance = 0
        for i,amplitude in enumerate(amplitudes):
            if amplitude > 0:
                distance = self.difference(old_cursor,indices[i])
                if distance < 1:
                    start = i
                    break
        stop = -1
        for i,amplitude in enumerate(amplitudes):
            if amplitude > 0 and self.difference(cursor,indices[i]) < 1:
                stop = i-2
                break
        if distance < 0:
            self.decay(abs(f32(distance)))
        if start != -1:
            stop = max(stop,start)
            for index in range(start,stop+1):
                self.gain = 1.
                if self.difference(cursor,indices[index]) < 0:
                    break
                self.gain = self.previous_gain = self.event_gain(indices,amplitudes,index)
                amplitude = int(amplitudes[index])
                if amplitudes[index] > 0 and amplitude > 0:
                    scaled = min(30000.,f32(f32(amplitude)*self.gain))
                    accepted = self.accept_forecast(indices,index,scaled) if self.count >= 4 else False
                    if accepted is False:
                        accepted = self.accept_level(indices,index,scaled)
                    if self.level < 0:
                        self.level = 0.
                    period = int(self.periods[-1])
                    if accepted:
                        count = 0
                        previous = int(self.periods[-2])
                        if period > 0 and previous > 0:
                            lower,upper = (.9,1.2) if self.count < 4 else (.7,1.4)
                            count = self.count+1 if period*lower < previous < period*upper else 0
                        self.count = count
                        if self.last_peak != -1 and period > 0:
                            self.pitch = f32(f32(44100.)/f32(period))
                    self.events['positive_events'] += 1
                if index < stop:
                    self.decay(self.difference(indices[index+1],indices[index]))
            final_distance = self.difference(cursor,indices[stop])
            if final_distance > 0:
                self.decay(final_distance)
        if source.get('derivative',0.) > 3. or self.variation > .2:
            self.count = 0
        reason = self.gate(source)
        if reason:
            self.reset()
            self.events['reset_'+reason] += 1
        if not summarize:
            return self.pitch
        configured = self.configured
        if self.pitch > configured['upper']:
            self.pitch = configured['upper']
        if 0 < self.pitch < configured['lower']:
            self.pitch = configured['lower']
        # The caller's summary uses five previous pitches before shifting.
        previous = [value for value in reversed(self.pitch_history[4:9]) if value > 0]
        if previous:
            total = 0.
            for value in previous:
                total = f32(total+value)
            self.mean = f32(total/f32(len(previous)))
            deviation = 0.
            for value in previous:
                deviation = f32(deviation+abs(f32(value-self.mean)))
            self.variation = f32(f32(deviation/f32(len(previous)))/self.mean)
        self.pitch_history = self.pitch_history[1:]+[self.pitch]
        return self.pitch

    def gate(self, source):
        curves = self.configured['curves']
        if curves[20] == 0:
            return 'manual'
        if not source['records']:
            return 'no_peaks'
        crossing,turning,energy,ratio = source['values']
        if curves[23] > 0:
            if float(curves[23])*1.5 < crossing:
                return 'crossing'
            if curves[24] > 0 and curves[25] > 0 and curves[24] < crossing and energy < curves[25]:
                return 'energy_pair'
            if curves[25] > 0 and (energy < float(curves[25])/5. or energy < float(curves[25])/2.5):
                return 'energy'
        if curves[28] == 0:
            return None
        if not ratio <= .5:
            return 'spectral_pair'
        low = high = 0.
        low_count = high_count = 0
        for hz,db,span,bin_index in source['records']:
            amplitude = 0. if db <= -3.4028234663852886e38 else f32(math.exp(float(db)*.1151292546497)*32768.)
            if hz <= 1000.:
                low = f32(low+amplitude)
                low_count += 1
            if hz >= 5000.:
                high = f32(high+amplitude)
                high_count += 1
        if not low_count or not high_count:
            return None
        average = f32(low/f32(low_count))
        fraction = f32(f32(high/f32(high_count))/average) if average != 0 else math.inf
        if average >= 100.:
            if fraction <= .17:
                return None
            if self.mean != 0 and (self.mean <= 0 or self.variation <= .3):
                return None
        elif average > 30. and self.count > 5 and self.mean > 0 and self.variation < .1:
            if fraction <= .17 or self.variation <= .3:
                return None
        return 'spectral_pair'

    def state_bytes(self):
        return (struct.pack('<ffffdff',self.gain,self.unused_gain,self.previous_gain,self.unused_gain,self.slope,self.level,self.held_level),
                struct.pack('<30f',*self.periods),struct.pack('<i',self.last_peak),
                struct.pack('<i',self.count),struct.pack('<ff',self.mean,self.variation),
                struct.pack('<10f',*self.pitch_history))
