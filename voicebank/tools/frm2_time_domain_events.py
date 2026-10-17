"""Retained extrema and event markers from the reviewed filtered PCM ring.

The event queue owns its initial zero state. Period length and stable count
are explicit feedback inputs; no native event queue is required.
"""
from collections import Counter
import struct

from audit_frm2_numeric import f32
from frm2_candidate_pcm_source import RING_SIZE


class TimeDomainEvents:
    def __init__(self):
        self.ring = [0.]*RING_SIZE
        self.markers = [0.]*RING_SIZE
        self.indices = [0]*30
        self.absolute = [0.]*30
        self.amplitudes = [0.]*30
        self.cursor = self.total = 0
        self.peak_plateau = self.trough_plateau = False
        self.events = Counter()

    def push(self, values):
        for value in values:
            self.ring[self.total%RING_SIZE] = value
            self.total += 1

    @staticmethod
    def move(values,destination,source,count):
        values[destination:destination+count] = values[source:source+count]

    def move_all(self,destination,source,count):
        for values in (self.indices,self.absolute,self.amplitudes):
            self.move(values,destination,source,count)

    def mark(self,index,value):
        integer = int(value)
        self.markers[index] = float((integer+32768)%65536-32768)

    def clear(self,*slots):
        for slot in slots:
            self.markers[self.indices[slot]] = 0.

    def append(self,index,value,absolute):
        self.move_all(0,1,29)
        self.indices[-1],self.amplitudes[-1],self.absolute[-1] = index,value,absolute

    def peak(self,index,position,period,count):
        pos,absolute,amplitude = self.indices,self.absolute,self.amplitudes
        value = self.ring[index]*.5-absolute[-1]
        size = abs(f32(value))
        a = [abs(f32(item)) for item in amplitude]
        nearest = (index-pos[28])%RING_SIZE
        distance26 = (index-pos[26])%RING_SIZE
        distance25 = (index-pos[25])%RING_SIZE
        distance24 = (index-pos[24])%RING_SIZE
        previous29 = (pos[29]-pos[27])%RING_SIZE
        previous25 = (pos[25]-pos[21])%RING_SIZE
        short_period = count < 4 or nearest < float(period)*.7
        ignore = (size < a[28]*.25 and short_period) or (size < a[28]*.15 and short_period)
        merge_last = a[28] < size*.6 and a[28] < a[26]*.6 and a[26]*.8 < size < a[26]*1.3 and short_period
        raw_span = pos[26]-pos[22]
        similar_timing = raw_span*.7 < distance26 < raw_span*1.35 and short_period
        thin = (a[28] < size*.9 and a[28] < a[26]*.9 and a[26]*.93 < size < a[26]*1.1
                and a[24] < a[26]*.9 and a[24] < a[22]*.9 and a[22]*.93 < a[26] < a[22]*1.1)
        loose = (a[28] < size*.8 and a[28] < a[26]*.8 and a[26]*.85 < size < a[26]*1.2
                 and a[24] < a[26]*.8 and a[24] < a[22]*.8 and a[22]*.85 < a[26] < a[22]*1.2)
        merge_earlier = similar_timing and (thin or loose)
        merge_four = (44100.*.006 < distance24 < 44100.*.020 and a[28] < size*.5 and a[26] < size*.5
                      and a[28] < a[24]*.5 and a[26] < a[24]*.5
                      and a[27] < a[29] and a[25] < a[29])
        if nearest < 44100.*.0014 and size < a[28]*.9:
            ignore = True
            force_ignore = True
        else:
            force_ignore = False
        if force_ignore or (ignore and not merge_four and not merge_earlier and not merge_last):
            self.clear(29)
            self.move_all(1,0,29)
            self.events['ignored_peak'] += 1
        else:
            self.mark(position,max(1.,value+.5))
            if merge_four:
                self.clear(25,26,27,28)
                absolute[25] = min(absolute[25],absolute[27],absolute[29])
                pos[25] = pos[29]
                amplitude[25] = absolute[25]-absolute[24]
                self.mark(pos[25],amplitude[25])
                self.move_all(3,0,26)
                pos[29],amplitude[29] = position,value
                self.events['merge_four'] += 1
            elif merge_earlier:
                self.clear(25,24,23)
                absolute[23] = min(absolute[23],absolute[25])
                pos[23] = int(float(pos[25])*.5+float(pos[23])*.5)
                amplitude[23] = absolute[23]-absolute[22]
                self.mark(pos[23],amplitude[23])
                self.move(pos,1,0,24)
                self.move(amplitude,1,0,26)
                self.move(absolute,1,0,26)
                self.move_all(25,26,4)
                pos[29],amplitude[29] = position,self.ring[index]*.5-absolute[28]
                self.events['merge_earlier'] += 1
            elif merge_last:
                self.clear(29,28,27)
                absolute[27] = min(absolute[27],absolute[29])
                pos[27] = int(float(pos[29])*.5+float(pos[27])*.5)
                amplitude[27] = absolute[27]-absolute[26]
                self.mark(pos[27],amplitude[27])
                self.move_all(1,0,29)
                pos[29],amplitude[29] = position,self.ring[index]*.5-absolute[28]
                self.events['merge_last'] += 1
            else:
                self.append(position,value,self.ring[index]*.5)
                self.events['retained_peak'] += 1
            absolute[29] = self.ring[index]*.5
        a = [abs(f32(item)) for item in amplitude]
        paired = (a[27] < a[29]*.8 and a[27] < a[25]*.8 and a[25]*.85 < a[29] < a[25]*1.2
                  and a[23] < a[25]*.8 and a[23] < a[21]*.8 and a[21]*.85 < a[25] < a[21]*1.2
                  and previous25*.7 < distance25 < previous25*1.35
                  and (count < 4 or previous29 < float(period)*.7))
        if paired:
            self.clear(24,23,22)
            absolute[22] = min(absolute[22],absolute[24])
            pos[22] = int(float(pos[24])*.5+float(pos[22])*.5)
            amplitude[22] = absolute[22]-absolute[21]
            self.mark(pos[22],amplitude[22])
            self.move_all(2,0,23)
            self.clear(28,27,26)
            absolute[26] = min(absolute[26],absolute[28])
            pos[26] = int(float(pos[28])*.5+float(pos[26])*.5)
            amplitude[26] = absolute[26]-absolute[25]
            self.mark(pos[26],amplitude[26])
            self.move_all(2,0,27)
            self.events['merge_pair'] += 1
        if pos[0] > 0:
            self.markers[pos[0]] = 0.

    def trough(self,index,position):
        value = self.ring[index]*.5-self.absolute[-1]
        self.mark(position,min(-1.,value-.5))
        self.append(position,value,self.ring[index]*.5)
        if self.indices[0] > 0:
            self.markers[self.indices[0]] = 0.
        self.events['retained_trough'] += 1

    def scan(self,period,count):
        target = self.total%RING_SIZE
        samples = (target-self.cursor)%RING_SIZE
        index = (self.cursor-1)%RING_SIZE
        for _ in range(samples):
            left,right = (index-1)%RING_SIZE,(index+1)%RING_SIZE
            previous,current,following = self.ring[left],self.ring[index],self.ring[right]
            peak_position = None
            if self.peak_plateau:
                if current <= following:
                    if current < following:
                        self.peak_plateau = False
                else:
                    self.peak_plateau = False
                    peak_position = left
                    self.events['peak_plateau_closed'] += 1
            if peak_position is None and not self.peak_plateau:
                if previous < current:
                    if following < current:
                        peak_position = index
                    elif following == current:
                        self.peak_plateau = True
                        self.events['peak_plateau_started'] += 1
            if peak_position is not None:
                self.peak(index,peak_position,period,count)
            trough_position = None
            if self.trough_plateau:
                if current >= following:
                    if current > following:
                        self.trough_plateau = False
                else:
                    self.trough_plateau = False
                    trough_position = left
                    self.events['trough_plateau_closed'] += 1
            if trough_position is None and not self.trough_plateau:
                if current < previous:
                    if current < following:
                        trough_position = index
                    elif current == following:
                        self.trough_plateau = True
                        self.events['trough_plateau_started'] += 1
            if trough_position is not None:
                self.trough(index,trough_position)
            index = right
        if samples:
            self.cursor = target
        self.events['scanned_samples'] += samples

    def snapshot(self):
        return (struct.pack('<30i',*self.indices),struct.pack('<30d',*self.absolute),
                struct.pack('<30d',*self.amplitudes),
                struct.pack('<ii',int(self.peak_plateau),int(self.trough_plateau)),
                struct.pack('<i',self.cursor))

    def marker_bytes(self):
        return struct.pack('<'+str(RING_SIZE)+'f',*self.markers)
