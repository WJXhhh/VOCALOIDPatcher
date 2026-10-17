"""Default complete raw peak records to harmonic state and phase output.

Pitch comes from the next independent candidate-history update before peak
weighting. All tracking state starts here, never in a reference analysis object.
"""
import math
import struct

from audit_frm2_numeric import f32
from frm2_peak_selection import advance_outer
from probe_frm2_weighted_pitch import weighted_pitch


TRACK_FIELDS = (
    ('amplitude', 0, 'f'), ('center', 4, 'f'), ('previous', 8, 'i'),
    ('current', 12, 'i'), ('rank', 16, 'i'), ('candidates', 20, 'i'),
    ('distance', 32, 'f'), ('metric', 44, 'f'), ('cost', 56, 'f'),
    ('order', 68, 'i'),
)


def harmonic_controls(frequency):
    return {0:1., 2:22050., 3:-300., 4:110., 6:0., 7:0., 8:1.,
        9:f32(.3), 10:1., 13:f32(frequency), 17:f32(.7), 18:0., 19:1.,
        33:.5, 34:f32(.01), 39:20., 40:50., 41:f32(.01), 51:0.}


def track_bytes(tracks):
    return [tuple((offset, struct.pack('<'+str(len(values)) + kind, *values))
        for name, offset, kind in TRACK_FIELDS
        for values in [track[name] if isinstance(track[name], list) else [track[name]]])
        for track in tracks]


class HarmonicPhasePipeline:
    def __init__(self, frequency):
        self.controls = harmonic_controls(frequency)
        self.state = dict(tracks=[], history=0., initialized=False)
        self.owners = [0]*1500

    def process(self, records, coarse_pitch, index):
        peaks = [tuple(row[:3]) for row in records]
        pitch = weighted_pitch(coarse_pitch, peaks)
        if not math.isfinite(pitch):
            raise ValueError('Nonfinite weighted pitch is outside this matrix')
        owners = self.owners[:len(peaks)]
        previous_initialized = self.state['initialized']
        result = advance_outer(self.state, peaks, pitch, self.controls,
            owners, timestamp=index*256/44100.)
        self.owners[:len(peaks)] = result.get('owners', owners)
        output = [tuple(row) if row is not None else (0., 10000., 0.)
            for row in result['output']]
        return dict(coarse_pitch=coarse_pitch, pitch=pitch, output=output,
            phase_bytes=struct.pack('<'+str(len(output))+'f', *(row[2] for row in output)),
            tracks=track_bytes(self.state['tracks']), initialized=self.state['initialized'],
            initialized_now=not previous_initialized and pitch > 0.,
            reset_now=previous_initialized and not self.state['initialized'],
            history=self.state['history'], claimed=result.get('claimed'),
            owners=list(self.owners[:len(peaks)]),
            selected=[row['current'] for row in self.state['tracks']],
            collisions=result.get('collisions', 0), missing=sum(row is None for row in result['output']))
