"""Synthetic reference integrity tests; no commercial voicebank fixtures."""

import struct
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

from verify_reference_ddi import AuditError, audit, index_ddb, pointer
from probe_frm2 import ProbeError


class ReferenceAuditTests(unittest.TestCase):
    def run_fixture(self, *, target=0, orphan=False, count=256, duplicate=False):
        frame = struct.pack('<4sIIdQf', b'FRM2', 32, 1, 0.0, 0x200, 0.0)
        sound = struct.pack('<4sIIHI', b'SND ', 530, 44100, 1, 256) + bytes(512)
        ddi = struct.pack('<QI Q', target, count, 32)
        sample = {'epr': [f'0={target:x}'], 'snd': f'c=20_{count:x}', 'fs': 44100}
        if duplicate:
            sample['epr'] *= 2
        model = SimpleNamespace(sta_data={}, art_data={}, vqm_data={0: sample}, read=lambda: None)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'test.ddi').write_bytes(ddi)
            (root / 'test.ddb').write_bytes(frame + sound + (frame if orphan else b''))
            return audit(root / 'test.ddi', root / 'test.ddb', lambda _: model)

    def test_complete_reference_sets(self):
        result = self.run_fixture()
        self.assertTrue(result['reference_sets_complete'])
        self.assertEqual(result['frame_reference_multiplicity'], {1: 1})
        self.assertEqual(result['pcm_minus_frames_times_256']['VQM'], {0: 1})

    def test_pointer_inside_frame_is_not_a_frame_start(self):
        with self.assertRaises(AuditError):
            self.run_fixture(target=1)

    def test_unreferenced_frame_is_rejected(self):
        with self.assertRaises(AuditError):
            self.run_fixture(orphan=True)

    def test_inline_pcm_count_mismatch_is_rejected(self):
        with self.assertRaises(AuditError):
            self.run_fixture(count=255)

    def test_duplicate_reference_is_reported(self):
        result = self.run_fixture(duplicate=True)
        self.assertEqual(result['frame_reference_multiplicity'], {2: 1})

    def test_art_pointer_adjustment_checks_original_bytes(self):
        ddi = struct.pack('<IQ', 256, 50)
        self.assertEqual(pointer('4=20_100', ddi, 18), (32, 256))
        with self.assertRaises(AuditError):
            pointer('4=21_100', ddi, 18)
        with self.assertRaises(AuditError):
            pointer('4=20_101', ddi, 18)
        with self.assertRaises(AuditError):
            pointer('1000=20', ddi)

    def test_truncated_and_overflowing_ddb_chunks(self):
        for raw in (b'FRM2', struct.pack('<4sI', b'SND ', 0xFFFFFFFF),
                    struct.pack('<4sI', b'SND ', 8)):
            with self.subTest(size=len(raw)), self.assertRaises((AuditError, ProbeError)):
                index_ddb(raw)


if __name__ == '__main__':
    unittest.main()
