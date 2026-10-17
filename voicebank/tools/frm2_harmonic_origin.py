"""Independent ordinary origin state for a produced harmonic sequence."""
from verify_frm2_phase_origin import search_origin, smooth_origin, update_lock
from verify_vqm_compact_origin_stage import amplitude_gate


class HarmonicOrigin:
    def __init__(self):
        self.history = [0.]*15
        self.pitch_history = [0.]*20
        self.origin = 0.
        self.locked = False
        self.processed = 0

    def advance(self, output, pitch):
        self.pitch_history = self.pitch_history[1:]+[pitch]
        result = dict(called=False, origin=self.origin, amplitude_gate=False,
            continuous=False, candidate=None)
        if pitch > 0.:
            frequency, amplitude, phase = map(list, zip(*output))
            candidate = search_origin(frequency, phase, pitch, self.origin)
            self.history = self.history[1:]+[candidate]
            self.locked = update_lock(self.history, self.pitch_history, self.processed, self.locked)
            continuous = (self.processed > 1 and self.pitch_history[-3] > 0.
                and self.pitch_history[-4] > 0.)
            proposed = smooth_origin(candidate, self.origin, self.locked, continuous)
            gated = amplitude_gate(frequency, amplitude)
            if not gated:
                self.origin = proposed
            result = dict(called=True, origin=self.origin, amplitude_gate=gated,
                continuous=continuous, candidate=candidate)
        self.processed += 1
        result['locked'] = self.locked
        return result
