"""Synthetic timing and F0 inference regressions."""

import math
import struct
import unittest

from analyze_reference_regions import read_frame, region_clock_errors, voiced_mean
from verify_reference_ddi import AuditError
from probe_frm2 import ProbeError


class RegionAnalysisTests(unittest.TestCase):
    def test_two_region_time_resets(self):
        times = [0, 256 / 44100, 0, 256 / 44100, 512 / 44100]
        groups = [{'start': 0, 'end': 2}, {'start': 2, 'end': 5}]
        count, error = region_clock_errors(times, groups, 44100)
        self.assertEqual(count, 0)
        self.assertLess(error, 1e-10)

    def test_four_regions_are_not_three_phoneme_bins(self):
        times = [0, 256 / 44100, 0, 0, 0, 256 / 44100]
        groups = [{'start': a, 'end': b} for a, b in [(0, 2), (2, 3), (3, 4), (4, 6)]]
        self.assertEqual(region_clock_errors(times, groups, 44100)[0], 0)

    def test_global_clock_is_not_a_region_clock(self):
        times = [i * 256 / 44100 for i in range(4)]
        groups = [{'start': 0, 'end': 2}, {'start': 2, 'end': 4}]
        count, error = region_clock_errors(times, groups, 44100)
        self.assertEqual(count, 2)
        self.assertAlmostEqual(error, 512)

    def test_partition_gap_is_rejected(self):
        with self.assertRaises(AuditError):
            region_clock_errors([0, 0, 0], [{'start': 0, 'end': 1}, {'start': 2, 'end': 3}], 44100)

    def test_mean_uses_cents_not_hz_and_ignores_zero(self):
        self.assertAlmostEqual(voiced_mean([220, 0, 880], range(3)), 0)
        self.assertIsNone(voiced_mean([0, 0], range(2)))

    def test_excluding_unvoiced_region_removes_spurious_pitch(self):
        f0 = [220, 220, 1760]
        self.assertAlmostEqual(voiced_mean(f0, range(2)), -1200)
        self.assertAlmostEqual(voiced_mean(f0, range(3)), 0)

    def test_frame_f0_stays_inside_declared_chunk(self):
        raw = struct.pack('<4sIIdQf', b'FRM2', 32, 1, 0.0, 0x200, 0.0)
        self.assertEqual(read_frame(raw, 0), (0.0, 0.0))
        bad = bytearray(raw)
        struct.pack_into('<Q', bad, 20, 0x2000e00207)
        struct.pack_into('<I', bad, 28, 0xFFFFFFFF)
        with self.assertRaises(ProbeError):
            read_frame(bytes(bad) + raw, 0)
        struct.pack_into('<Q', bad, 20, 0x200)
        struct.pack_into('<f', bad, 28, math.nan)
        with self.assertRaises(AuditError):
            read_frame(bytes(bad), 0)


if __name__ == '__main__':
    unittest.main()
