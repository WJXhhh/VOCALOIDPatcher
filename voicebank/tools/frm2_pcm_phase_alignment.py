"""Connect independently produced harmonic frames to the verified phase rules.

The default numerical profile disables band classification and initializes its
own delta history to zero. No DSE analysis context is read or modified here.
"""
import math
import struct

from audit_frm2_numeric import f32
from frm2_harmonic_origin import HarmonicOrigin
from frm2_phase_controls import PhaseControls
from verify_frm2_phase_pipeline import PhaseModel


class PcmPhaseAlignment:
    def __init__(self, capacity=110):
        self.model = PhaseModel(capacity)
        self.origin_model = HarmonicOrigin()
        self.controls = PhaseControls()
        self.capacity = capacity

    def advance(self, frame, energy, energy_enabled=True):
        output = frame['output']
        assert len(output) in (0, self.capacity)
        origin = self.origin_model.advance(output, frame['pitch'])
        derivative = self.controls.advance(energy)['derivative'] if energy_enabled else 0.
        harmonics = output or [(0., 10000., 0.)] * self.capacity
        frequency, amplitude, raw = map(list, zip(*harmonics))
        prediction = self.model.advance(frequency, amplitude, raw, frame['pitch'],
            derivative, 0., 256)
        assert struct.pack('<f', self.model.origin) == struct.pack('<f', origin['origin'])
        assert self.model.history == self.origin_model.history
        assert self.model.f0_history == self.origin_model.pitch_history
        assert self.model.locked == self.origin_model.locked
        row = dict(frame=frame, origin=origin, derivative=derivative,
            pitch_history=struct.pack('<20f', *self.model.f0_history),
            history=struct.pack('<15f', *self.model.history),
            locked=self.model.locked, voiced_run=self.model.voiced,
            aligned=None, knots=(), correction=list(self.model.correction),
            delta=list(self.model.delta), serializable_phase=False)
        if prediction is not None:
            aligned, xs, ys, threshold, decay = prediction
            knots = tuple(sorted({min(1., max(0., f32(x))): f32(y)
                for x, y in zip(xs, ys)}.items()))
            assert all(math.isfinite(x) and math.isfinite(y) for x, y in knots)
            row.update(aligned=tuple(aligned), knots=knots, threshold=threshold,
                decay=decay, serializable_phase=len(knots) >= 3
                and all(-20000. <= y <= 20000. for x, y in knots))
        return row
