import io
import math
import struct
import unittest

import finalize_minimal_articulation_ddi as articulation
import finalize_stationary_ddi as stationary
import pitch2_reference
import probe_frm2


class Pitch2ReferenceTests(unittest.TestCase):
    def test_phdc_voiced_outer_excludes_positive_f0_in_unvoiced_region(self):
        frames = [220., 330., 880., 660.]
        groups = ((0, 2, 1, 2), (2, 4, 3, 4))
        result = pitch2_reference.reference_pitch_cents(
            frames, groups, ('a', 'p'), {'a'})
        expected = sum(1200. * math.log2(value / 440.) for value in frames[:2]) / 2
        self.assertAlmostEqual(result, expected)
        self.assertNotAlmostEqual(result, 1200. * math.log2(330. / 440.))

    def test_voiced_second_region_and_no_pitch_sentinel(self):
        groups = ((0, 2, 0, 2), (2, 4, 2, 4))
        frames = [880., 660., 0., 220.]
        self.assertEqual(pitch2_reference.reference_pitch_cents(
            frames, groups, ('p', 'a'), {'a'}), -1200.)
        self.assertEqual(pitch2_reference.reference_pitch_cents(
            frames, groups, ('p', 't'), {'a'}), pitch2_reference.NO_PITCH)

    def test_region_partition_is_required(self):
        with self.assertRaises(ValueError):
            pitch2_reference.reference_pitch_cents(
                [220., 440.], ((0, 1, 0, 1), (0, 2, 0, 2)), ('a', 'a'), {'a'})

    def test_reads_final_frm2_f0(self):
        first = probe_frm2.serialize_frame(probe_frm2.UnvoicedFrame(
            1, 0., probe_frm2.UNVOICED_MASK, 220.))
        second = probe_frm2.serialize_frame(probe_frm2.UnvoicedFrame(
            1, 256. / 44100., probe_frm2.UNVOICED_MASK, 0.))
        stream = io.BytesIO(first + second)
        self.assertEqual(pitch2_reference.read_final_f0(stream, [0, len(first)]), [220., 0.])

    def test_articulation_writer_keeps_layer_coordinate_separate(self):
        skeleton = (b'ARTp' + struct.pack('<III', 0, 0, 1) +
                    struct.pack('<dH', 0., 1) + b'\0' * 20 +
                    struct.pack('<I', 2) + struct.pack('<Q', 0x6c) +
                    stationary.EMPTY_SND + struct.pack('<Q', 0x73) +
                    stationary.EMPTY_EPR)
        ddb = stationary.DdbInfo([0, 32], 64, 100, 44100, 1, 2560)
        output, report = articulation.insert_articulation_at(
            skeleton, 0, ddb, 440., 1, 0., .6, 90.,
            reference_pitch_cents=-1200.)
        self.assertEqual(struct.unpack_from('<ff', output, 26), (0., -1200.))
        self.assertEqual(report['pitch2_cents_relative_to_a4'], -1200.)


if __name__ == '__main__':
    unittest.main()
