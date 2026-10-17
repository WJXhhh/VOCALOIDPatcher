import struct
import unittest

from probe_drs_frame import parse, serialize, ProbeError


def frame(mask,payload=b''):
    body=struct.pack('<IdQ',1,.25,mask)+payload
    return struct.pack('<4sI',b'FRM2',8+len(body))+body


class DrsLayoutTests(unittest.TestCase):
    def test_scalar_disk_order_and_selection_pitch_flag(self):
        mask=sum(1<<b for b in (9,20,10,18,19,31))
        raw=frame(mask,struct.pack('<3fBI',-1200.,2.25,-6.,10,2049))
        result=parse(raw)
        self.assertEqual([(f.name,f.offset) for f in result.fields],
            [('bit9.scalar',28),('bit20.scalar',32),('bit10.scalar',36),
             ('bit18.window_id',40),('bit19.sample_length',41)])
        self.assertEqual(serialize(result),raw)
        self.assertEqual(serialize(result,0),frame(1<<31))
        self.assertEqual(serialize(result,1<<20),frame((1<<31)|(1<<20),struct.pack('<f',2.25)))

    def test_unnamed_fixed_blocks_have_exact_size(self):
        payload=bytes(range(35))
        raw=frame(1<<14,payload)
        self.assertEqual(parse(raw).fields[0].payload,payload)
        self.assertEqual(serialize(parse(raw)),raw)
        with self.assertRaises(ProbeError):parse(frame(1<<14,payload+b'\x00'))

    def test_sms2_context_controls_middle_abc_float(self):
        raw=frame(1<<22,struct.pack('<3fI',-26.5,73.5,-.125,0))
        self.assertEqual(parse(raw).fields[0].encoding,'3f32')
        with self.assertRaises(ProbeError):parse(raw,sms2_context=False)
        other=frame(1<<22,struct.pack('<2fI',-26.5,-.125,0))
        self.assertEqual(serialize(parse(other,sms2_context=False)),other)
        with self.assertRaises(ProbeError):parse(other)

    def test_all_truncations_rejected_with_frame_local_bounds(self):
        env=struct.pack('<4sIII3f6f',b'ENV ',52,0x1820002,24,-100.,10.,0.,0.,-20.,.5,-15.,1.,-10.)
        raw=frame((1<<6)|(1<<8)|(1<<33),struct.pack('<I2h',2,-20,30)+
            struct.pack('<I4f',2,.25,.5,.75,1.)+bytes(96)+struct.pack('<2f',1.,2.)+env)
        self.assertEqual(serialize(parse(raw)),raw)
        for end in range(len(raw)):
            with self.subTest(end=end),self.assertRaises(ProbeError):parse(raw[:end])
        corrupt=bytearray(raw);struct.pack_into('<I',corrupt,28,0xffffffff)
        with self.assertRaises(ProbeError):parse(bytes(corrupt))
        corrupt=bytearray(raw);struct.pack_into('<I',corrupt,len(raw)-52+12,28)
        with self.assertRaises(ProbeError):parse(bytes(corrupt))

    def test_packed_spectrum_size_and_selection_refusal(self):
        raw=frame((1<<4)|(1<<5)|(1<<29),struct.pack('<I',3)+bytes(range(6)))
        parsed=parse(raw)
        self.assertEqual([len(f.payload) for f in parsed.fields],[4,3,3])
        self.assertEqual(serialize(parsed),raw)
        with self.assertRaises(ProbeError):serialize(parsed,0)

    def test_unknown_bits_and_inner_feature_flags_rejected(self):
        with self.assertRaises(ProbeError):parse(frame(1<<36))
        with self.assertRaises(ProbeError):parse(frame(1<<13,struct.pack('<IB',1<<21,0)))


if __name__=='__main__':unittest.main()
