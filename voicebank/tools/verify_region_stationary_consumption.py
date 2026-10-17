"""Feed real produced STA work records into the native file-frame consumer.

Owned marker frames identify every source index independently by F0/time.
Consumer context uses the no-layer-blending file path; no Editor is launched.
"""
import argparse
import ctypes as c
import json
import math
from pathlib import Path
from audit_frm2_numeric import Native, P, I, Q, F, D, B, SHA256, f32, interpolate
from probe_frm2 import MainFrame, Envelope, MAIN_MASK, UnvoicedFrame, UNVOICED_MASK, serialize_frame
from region_commit_material import connected
from verify_region_event_production import run
from verify_region_stationary_random import crt_for, prime
from verify_region_frame_blend import cents


def full_marker(index, pitch):
    return MainFrame(0, float(1000+index), MAIN_MASK,
        tuple(f32(pitch*k) for k in [1, 2, 3]),
        tuple(f32(.1+index*.001+k*.02) for k in range(3)),
        tuple(f32(index*.01+k*.1) for k in range(3)), pitch,
        Envelope(0x01820002, (-20000., 20000., 0.), (0., f32(index*.01), 1., f32(index*.02))),
        tuple((500.+index+k*200, 1.5+index*.125, 100.+index) for k in range(1+index%2)),
        (f32(-20.+index*.1), f32(.25+index*.001), f32(-.0001-index*.000001)),
        # CSMSFrame's native constructor reserves one secondary-resonance slot.
        tuple((1200.+index+k*300, .5+index*.0625, 80.+index) for k in range(index%2)),
        Envelope(0x00960002, (-100., 10., 0.), (0., f32(-10.+index*.01), 1., f32(-20.+index*.02))))


def check_fields(obj, frame, harmonics):
    for offset, expected in zip([0x270, 0x274, 0x278], frame.secondary_header):
        assert F.from_address(obj+offset).value == expected
    for count, pointer, expected in [(0x260, 0x268, frame.resonances),
                                     (0x27c, 0x280, frame.secondary_resonances)]:
        assert I.from_address(obj+count).value == len(expected)
        data = P.from_address(obj+pointer).value
        for k, triple in enumerate(expected):
            res = P.from_address(data+k*8).value
            for offset, value in zip([0x140, 0x148, 0x158], triple):
                assert D.from_address(res+offset).value == value, (offset, value)
    envelopes = [(0x288, frame.residual_envelope)]
    if harmonics:
        assert I.from_address(obj+0x158).value == 3
        for offset, values in [(0x168, frame.amplitudes), (0x170, frame.phases_radians)]:
            data = P.from_address(obj+offset).value
            assert [F.from_address(data+k*4).value for k in range(3)] == list(values)
        data = P.from_address(obj+0x160).value
        for k, value in enumerate(frame.frequencies_hz):
            assert abs(F.from_address(data+k*4).value-value) < .001
        envelopes.append((0x218, frame.harmonic_envelope))
    else:
        # The consumer's copy mask intentionally excludes harmonics and bit37.
        assert I.from_address(obj+0x158).value == 0
        assert Q.from_address(obj+0x150).value & (7 | (1 << 37)) == 0
    for offset, envelope in envelopes:
        env = P.from_address(obj+offset).value
        assert I.from_address(env+0x15c).value == len(envelope.values)//2
        nodes = P.from_address(env+0x178).value
        for k, (x, y) in enumerate(zip(envelope.values[::2], envelope.values[1::2])):
            assert D.from_address(nodes+16*k).value == x
            assert F.from_address(nodes+16*k+8).value == y


def verify(dll, work, full_frames=False):
    work.mkdir(parents=True, exist_ok=True)
    offsets, blob = [], bytearray()
    pitches = [220.+index*3 for index in range(100)]
    frames = [full_marker(index, pitch) if full_frames else
              UnvoicedFrame(0, float(1000+index), UNVOICED_MASK, pitch)
              for index, pitch in enumerate(pitches)]
    for index, frame in enumerate(frames):
        offsets.append(len(blob))
        blob.extend(serialize_frame(frame))
    path = work/'owned_sta_markers.bin'
    with path.open('xb') as file:
        file.write(blob)
    probe = Native(dll)
    frequencies = [110., 440., 1000., 4000., 12000.]
    expected_amplitudes = []
    if full_frames:
        from frm2_dse_model import DseModel
        from frm2_resonance_optimizer import curve
        for frame in frames:
            model = DseModel(probe, frame.secondary_header, frame.resonances, frame.secondary_resonances)
            knots = list(zip(frame.residual_envelope.values[::2], frame.residual_envelope.values[1::2]))
            values = []
            for hz in frequencies:
                total = f32(sum(model.independent_resonance(res, hz) for res in model.resonances))
                level = f32(20*math.log10(total/32768)) if total > 0 else f32(-3.4028234663852886e38)
                baseline = f32(frame.secondary_header[0]+curve(frame.secondary_header, hz))
                correction = interpolate(knots, f32(hz/22050), regular=True)
                values.append(f32(f32(baseline+correction)+level))
            expected_amplitudes.append(values)
    crt = crt_for(probe)
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    handle = crt._wfopen(str(path.resolve()), 'rb')
    if not handle:
        raise OSError('Cannot open owned STA markers')
    rows = []
    try:
        for mode in ['disabled', 'table', 'random']:
            for gap in [0, 13, 63]:
                def build(n, state, context, voice, table, sta, art):
                    material = connected(n, state, context, voice, table, sta, art,
                                         edges=(('Sil','a'), ('a','Sil'), ('a','a')))
                    stream = n.alloc(0x80)
                    P.from_address(stream+8).value = handle
                    I.from_address(stream+0x2c).value = 0x32534d53
                    P.from_address(voice+0x1a0).value = stream
                    P.from_address(material['part']+0x1a8).value = n.array(Q, offsets)
                    if mode != 'disabled':
                        B.from_address(state+0x40a8).value = 1
                        B.from_address(state+0x40a9).value = mode == 'table'
                        if mode == 'table':
                            I.from_address(state+0x40b0).value = 1022
                        else:
                            prime(n)
                            crt.srand(42)
                    material['next_gap'] = gap

                    def consume(native, producer, ring, items, source):
                        ctx, controls = native.alloc(0x2b200), native.alloc(0x5ab00)
                        P.from_address(ctx).value = P.from_address(context).value
                        P.from_address(ctx+0x1d568).value = controls
                        B.from_address(controls+0x78).value = 1
                        P.from_address(controls).value = native.alloc(0x18)
                        I.from_address(controls+8).value = 1
                        P.from_address(controls+0x10).value = native.alloc(0x58)
                        I.from_address(controls+0x18).value = 2
                        F.from_address(ctx+0x2208).value = 440.
                        F.from_address(ctx+0x1d564).value = 22050.
                        D.from_address(ctx+0x1d558).value = .01
                        first, second, output = [native.alloc(0x328) for _ in range(3)]
                        for frame in [first, second, output]:
                            native.fn(0x12a670, P, P)(frame)
                        P.from_address(ctx+0x247b8).value = first
                        P.from_address(ctx+0x247c0).value = second
                        call = native.fn(0x16a870, Q, P, I, P, P, B, B)
                        clear = native.fn(0x12cf40, Q, P, Q, I, I)
                        amplitude = native.fn(0x12da40, Q, P, F, F, P, P, B, P, B, I)
                        records = P.from_address(ring).value
                        count, checked, maximum, spectral_error = 0, 0, 0., 0.
                        indices = set()
                        for j in range(I.from_address(ring+0x34).value):
                            item = items+j*0x140
                            if P.from_address(item+0x20).value != source['part']:
                                continue
                            begin, end = [I.from_address(item+o).value for o in [0, 4]]
                            offset = I.from_address(item+0x94).value
                            assert I.from_address(item+0x14).value == -1
                            assert B.from_address(item+0x90).value == 1
                            count += 1
                            for t in range(begin, end):
                                record = records+(t % 1024)*0x18
                                assert P.from_address(record).value == item
                                index = offset+t-begin
                                assert 0 <= index < 100
                                clear(output, 0x6f0fffffff, 1, 0)
                                call(ctx, t, output, record, 0, 0)
                                error = abs(F.from_address(output+0x1d0).value-cents(pitches[index]))
                                assert error < 2e-4, (j, t, index, error)
                                assert D.from_address(output+0x148).value == 1000+index
                                expected_mask = 0x80e00200 if full_frames else 0x80000200
                                assert Q.from_address(output+0x150).value == expected_mask
                                if full_frames:
                                    check_fields(first, frames[index], True)
                                    check_fields(output, frames[index], False)
                                    for hz, expected in zip(frequencies, expected_amplitudes[index]):
                                        value, phase, cutoff = F(), F(), F()
                                        assert amplitude(output, hz, 22050., c.byref(value), c.byref(phase),
                                                         0, c.byref(cutoff), 0, 0) == 0
                                        difference = abs(value.value-expected)
                                        assert difference < 1e-3, (index, hz, value.value, expected)
                                        spectral_error = max(spectral_error, difference)
                                assert F.from_address(ctx+0x2415c).value == 0.
                                maximum = max(maximum, error)
                                indices.add(index)
                                checked += 1
                        assert count and checked
                        return dict(stationary_items=count, frames=checked, max_pitch_error=maximum,
                                    spectral_queries=checked*len(frequencies) if full_frames else 0,
                                    max_spectral_error=spectral_error,
                                    min_source=min(indices), max_source=max(indices))
                    material['verify_consumption'] = consume
                    return material
                report = run(dll, True, False, True, build, [['a']], [1010, 1810, 2500, 4000])
                for row in report['rows']:
                    if 'commit' in row:
                        rows.append(dict(mode=mode, gap=gap, milliseconds=row['milliseconds'],
                                         slot=row['slot'], pitch=row['pitch'], split=row['split'],
                                         **row['commit']['consumption']))
    finally:
        assert crt.fclose(handle) == 0
    return dict(dse_sha256=SHA256, sequences=len(rows), native_commits=2*len(rows),
                frame_reads=sum(r['frames'] for r in rows), cases=rows,
                full_frames=full_frames,
                scope='Actual produced STA records consumed through 0x16a870 and native file loader; '
                      'owned markers and optional full ordinary fields/spectral queries; '
                      'file path with blending/PCM analysis disabled. '
                      'Not full acoustic synthesis or host rendering.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--work', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--full-frames', action='store_true')
    args = parser.parse_args()
    result = verify(args.dll, args.work, args.full_frames)
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({key: result[key] for key in ['sequences', 'native_commits', 'frame_reads']}))
