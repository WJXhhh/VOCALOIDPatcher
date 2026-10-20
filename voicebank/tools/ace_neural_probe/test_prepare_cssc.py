"""Offline adapter contract checks using a generated audio fixture."""
import argparse
import hashlib
import json
import math
import struct
import tempfile
import unittest
import wave
from pathlib import Path

from prepare_cssc import HOP, RATE, prepare

CSSC_ROOT = None


class AdapterContractTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="ace-cssc-contract-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        with wave.open(str(self.root / "fixture.wav"), "wb") as writer:
            writer.setparams((1, 2, 44100, 0, "NONE", "not compressed"))
            writer.writeframes(b"".join(struct.pack("<h", round(4000 * math.sin(2 * math.pi * 200 * i / 44100))) for i in range(45423)))
        self.request = {
            "schema_version": 1, "source_wave": "fixture.wav", "air_units": "ace-signed",
            "air": [{"seconds": t, "value": v} for t, v in [(0, 0), (.256, 1), (.512, 0), (1.024, 0)]],
            "notes": [{"start": .128, "end": .896, "midi": 60}],
            "phonemes": [{"start": .128, "end": .896, "model_token": "a"}],
        }

    def run_request(self, request=None, output="prepared"):
        path = self.root / "request.json"
        path.write_text(json.dumps(request or self.request), encoding="utf-8")
        return prepare(path, CSSC_ROOT, self.root / output)

    def test_upstream_frame_intervals_and_fractional_curve(self):
        original = hashlib.sha256((self.root / "fixture.wav").read_bytes()).digest()
        report = self.run_request()
        annotation = json.loads((self.root / "prepared/sample.json").read_text())[0]
        self.assertEqual(report["stage"], "prepared_not_inferred")
        self.assertEqual(report["model_frames"], 96)
        self.assertEqual(report["tail_samples_not_covered_by_model"], 144)
        # Reconstruct exactly as the external annotation loader places intervals.
        placed = [None] * 96
        for value, a, b in zip(annotation["breathy"], annotation["breathy_start"], annotation["breathy_end"]):
            for i in range(round(a * RATE / HOP), round(b * RATE / HOP)):
                self.assertIsNone(placed[i])
                placed[i] = value
        self.assertNotIn(None, placed)
        self.assertEqual(placed[24], 1)
        self.assertAlmostEqual(placed[36], .5)
        self.assertEqual(placed[:12], [0] * 12)
        self.assertEqual(placed[48:], [0] * 48)
        self.assertEqual(original, hashlib.sha256((self.root / "fixture.wav").read_bytes()).digest())
        self.assertEqual(report["upstream_cli_technique"], "original")
        self.assertFalse(report["continuous_control_perceptually_validated"])

    def test_negative_air_is_not_relabelled_as_neutral(self):
        self.request["air"][1]["value"] = -.2
        with self.assertRaisesRegex(ValueError, "positive Air"):
            self.run_request()
        self.assertFalse((self.root / "prepared").exists())

    def test_unknown_phoneme_is_not_silently_mapped(self):
        self.request["phonemes"][0]["model_token"] = "unmapped-vocaloid-phone"
        with self.assertRaisesRegex(ValueError, "Unmapped"):
            self.run_request()

    def test_existing_output_is_preserved(self):
        self.run_request()
        annotation = (self.root / "prepared/sample.json").read_bytes()
        with self.assertRaisesRegex(ValueError, "must be new"):
            self.run_request()
        self.assertEqual(annotation, (self.root / "prepared/sample.json").read_bytes())

    def test_pause_frames_have_no_added_breathy_condition(self):
        self.request["phonemes"][0]["model_token"] = "<SP>"
        self.assertEqual(self.run_request()["breathy_max"], 0)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--cssc-root", required=True)
    args, rest = parser.parse_known_args()
    CSSC_ROOT = args.cssc_root
    unittest.main(argv=[__file__, *rest])
