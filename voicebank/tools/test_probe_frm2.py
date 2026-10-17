"""Synthetic FRM2 boundary regressions; contains no voicebank data."""

import struct
import unittest
from dataclasses import replace

import probe_frm2 as probe


def fixture(with_spectrum: bool) -> bytes:
    mask = probe.VQM_MASK if with_spectrum else probe.VQM_NO_SPECTRUM_MASK
    body = struct.pack("<IdQI3f", 1, 0.25, mask, 1, 220.0, 0.5, 0.0)
    if with_spectrum:
        body += struct.pack("<I4f", 2, 0.25, 0.5, 0.0, 1.0)
    body += struct.pack("<fIBBI", 220.0, 0, 0, 1, 2)
    body += struct.pack("<4sIII3f", b"ENV ", 28, 0, 0, 0.0, 1.0, 1.0) * 2
    return struct.pack("<4sI", b"FRM2", 8 + len(body)) + body


class VqmLayoutTests(unittest.TestCase):
    def test_both_layouts_roundtrip_and_scan(self):
        for present in (False, True):
            with self.subTest(spectrum=present):
                raw = fixture(present)
                frame = probe.parse_frame(raw)
                self.assertEqual(len(frame.spectrum_amplitudes), 2 if present else 0)
                self.assertEqual(frame.f0_hz, 220.0)
                self.assertEqual(probe.serialize_frame(frame), raw)
                self.assertEqual(probe.validate_frame_at(raw, 0, len(raw))[0], len(raw))

    def test_truncation_cannot_consume_following_frame(self):
        for present in (False, True):
            raw = fixture(present)
            for length in range(28, len(raw)):
                broken = bytearray(raw[:length])
                struct.pack_into("<I", broken, 4, length)
                with self.subTest(spectrum=present, length=length):
                    with self.assertRaises(probe.ProbeError):
                        probe.parse_frame(bytes(broken))
                    combined = bytes(broken) + raw
                    with self.assertRaises(probe.ProbeError):
                        probe.validate_frame_at(combined, 0, len(combined))

    def test_huge_component_count_is_bounded(self):
        raw = bytearray(fixture(False))
        struct.pack_into("<I", raw, 28, 0xFFFFFFFF)
        with self.assertRaises(probe.ProbeError):
            probe.parse_frame(bytes(raw))
        with self.assertRaises(probe.ProbeError):
            probe.validate_frame_at(raw, 0, len(raw))

    def test_absent_spectrum_rejects_populated_arrays(self):
        frame = probe.parse_frame(fixture(True))
        with self.assertRaises(probe.ProbeError):
            probe.serialize_frame(replace(frame, mask=probe.VQM_NO_SPECTRUM_MASK))

    def test_unverified_masks_still_rejected(self):
        raw = bytearray(fixture(False))
        struct.pack_into("<Q", raw, 20, probe.VQM_NO_SPECTRUM_MASK | 0x10)
        with self.assertRaises(probe.ProbeError):
            probe.parse_frame(bytes(raw))
        with self.assertRaises(probe.ProbeError):
            probe.validate_frame_at(raw, 0, len(raw))


if __name__ == "__main__":
    unittest.main()
