"""Signal-level acceptance checks for the offline residual experiment."""
import unittest

import numpy as np

import residual_air as air


class ResidualAirTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.sr = 44100
        cls.t = np.arange(cls.sr * 2) / cls.sr
        cls.f0 = 260 + 3 * np.sin(2 * np.pi * 5 * cls.t)
        phase = 2 * np.pi * np.cumsum(cls.f0) / cls.sr
        cls.tone = sum(.2 / k * np.sin(k * phase + .2 * k) for k in range(1, 21))
        cls.interior = slice(cls.sr // 4, -cls.sr // 4)

    def test_pure_tone_with_vibrato_does_not_become_air(self):
        gate = np.ones(len(self.tone))
        _, residual, softened = air.decompose(self.tone, self.sr, self.f0, gate)
        y = air.apply(self.tone, gate, residual, softened, 1)
        ratio = air.rms((y - self.tone)[self.interior]) / air.rms(self.tone[self.interior])
        self.assertLess(ratio, .005)

    def test_known_noise_is_extracted_without_losing_pitch(self):
        noise = np.random.default_rng(42).normal(0, .015, len(self.tone))
        x = self.tone + noise
        h, residual, softened = air.decompose(x, self.sr, self.f0, np.ones(len(x)))
        clean_h, _, _ = air.decompose(self.tone, self.sr, self.f0, np.ones(len(x)))
        # Noise is passed through a defined band; compare against the same band.
        from scipy import signal
        sos = signal.butter(3, [500, 8000], btype="bandpass", fs=self.sr, output="sos")
        expected = signal.sosfiltfilt(sos, noise)[self.interior]
        correlation = np.corrcoef(residual[self.interior], expected)[0, 1]
        self.assertGreater(correlation, .85)
        self.assertLess(air.rms((h - clean_h)[self.interior]), air.rms(noise))
        self.assertTrue(np.array_equal(x, air.apply(x, np.ones(len(x)), residual, softened, 0, True)))

    def test_nonvoiced_samples_remain_exact(self):
        x = self.tone
        gate = np.zeros(len(x))
        gate[self.sr // 2:self.sr] = 1
        _, residual, softened = air.decompose(x, self.sr, self.f0, gate)
        y = air.apply(x, gate, residual, softened, .7, True)
        self.assertTrue(np.array_equal(x[gate == 0], y[gate == 0]))
        self.assertTrue(np.isfinite(y).all())
        self.assertEqual(len(y), len(x))

    def test_silence_stays_exactly_silent(self):
        x = np.zeros(self.sr)
        f0, gate = air.pitch_track(x, self.sr)
        _, residual, softened = air.decompose(x, self.sr, f0, gate)
        self.assertTrue(np.array_equal(x, air.apply(x, gate, residual, softened, 1, True)))

    def test_amount_range(self):
        for value in (-.01, 1.01, float("nan")):
            with self.assertRaises(ValueError):
                air.apply(self.tone, np.ones(len(self.tone)), self.tone, self.tone, value)


if __name__ == "__main__":
    unittest.main()
