"""Phase controls for the verified default raw-energy analysis profile."""
import math
from audit_frm2_numeric import f32


class PhaseControls:
    def __init__(self):
        self.previous = 0.
        self.before_previous = 0.

    def advance(self, energy):
        level = min(0., f32(f32(math.log10(energy)) * 10)) if energy > 0 else -500.
        derivative = f32(.5 * f32(self.previous - self.before_previous)
                         + .5 * f32(level - self.previous))
        self.before_previous, self.previous = self.previous, level
        return dict(level=level, derivative=derivative, classification=0.)
