#!/usr/bin/env python3
"""Owned source E7 -> independent fit -> ordinary FRM2 -> DSE reader/consumer.

Bit37 uses source-derived raw-energy controls for the verified default profile.
Disabled band-analysis classification is verified as zero, not assumed absent.
Generated files stay in the caller-specified research output directory.
"""
import argparse
import ctypes as c
import json
import math
import struct
from collections import Counter
from dataclasses import replace
from pathlib import Path

from audit_frm2_numeric import Native, SHA256, F, D, I, P, Q, B, f32, interpolate, stats
from frm2_dse_model import DseModel
from frm2_resonance_optimizer import curve
from probe_frm2 import MainFrame, Envelope, MAIN_MASK, parse_frame, serialize_frame
from verify_frm2_pitch_state import verify
from verify_frm2_source_fit import frame_from_prediction
from verify_frm2_fit_schedule import schedule, snapshot
from verify_frm2_phase_pipeline import PhaseModel
from verify_frm2_pcm_phase_roundtrip import readback


def fitted_frame(native, row):
    frame = frame_from_prediction(native, row)
    schedule(native, frame, 22050., 1000., True)
    _, counts, values = snapshot(frame)
    abc = tuple(values[:3])
    n, m, _ = counts
    primary = tuple(tuple(values[3 + 3*k:6 + 3*k]) for k in range(n))
    at = 3 + 3*n
    secondary = tuple(tuple(values[at + 3*k:at + 3*k + 3]) for k in range(m))
    residual = Envelope(0x00960002, (-100., 10., 0.), tuple(map(f32, values[at + 3*m:])))
    harmonics = row['harmonics']
    return MainFrame(0, row['time'], MAIN_MASK,
                     tuple(f32(x[0]) for x in harmonics), tuple(f32(x[1]) for x in harmonics),
                     tuple(f32(x[2]) for x in harmonics), f32(row['f0']),
                     Envelope(0x01820002, (-20000., 20000., 0.), (0., 0., 1., 0.)),
                     primary, abc, secondary, residual)


def check_file(native, path, frames):
    crt = c.CDLL('ucrtbase')
    fopen, seek, close = crt._wfopen, crt._fseeki64, crt.fclose
    fopen.argtypes, fopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    seek.argtypes, seek.restype = [P, c.c_int64, I], I
    close.argtypes = [P]
    reader = native.fn(0x106e20, Q, P, P)
    consumer = native.fn(0x12da40, Q, P, F, F, P, P, B, P, B, I)
    handle = fopen(str(path.resolve()), 'rb')
    if not handle:raise OSError('Cannot open generated FRM2 stream')
    fields, amplitudes, rows, pitch_errors = [], [], [], []
    offset = 0
    try:
        for frame in frames:
            blob = serialize_frame(frame)
            assert parse_frame(blob) == frame
            obj, stream = native.alloc(0x328), native.alloc(0x80)
            native.fn(0x12a670, P, P)(obj)
            P.from_address(stream + 8).value = handle
            Q.from_address(stream).value = offset
            I.from_address(stream + 0x2c).value = 0x32534d53
            assert seek(handle, offset, 0) == 0 and reader(obj, stream) == 0
            offset += len(blob)
            assert Q.from_address(stream).value == offset
            assert Q.from_address(obj + 0x150).value == MAIN_MASK | (1 << 31)
            assert I.from_address(obj + 0x140).value == frame.kind
            fields.append(abs(D.from_address(obj + 0x148).value - frame.time_seconds))
            cents = f32(math.log(frame.f0_hz / 440.) * 1200. / math.log(2.))
            pitch_errors.append(abs(F.from_address(obj + 0x1d0).value - cents))
            for off, expected in zip((0x270, 0x274, 0x278), frame.secondary_header):
                fields.append(abs(F.from_address(obj + off).value - expected))
            for co, po, expected in ((0x260, 0x268, frame.resonances), (0x27c, 0x280, frame.secondary_resonances)):
                assert I.from_address(obj + co).value == len(expected)
                ptr = P.from_address(obj + po).value
                for k, triple in enumerate(expected):
                    res = P.from_address(ptr + 8*k).value
                    fields.extend(abs(D.from_address(res + off).value - v) for off, v in zip((0x140, 0x148, 0x158), triple))
            assert I.from_address(obj + 0x158).value == len(frame.frequencies_hz)
            for off, expected in ((0x160, frame.frequencies_hz), (0x168, frame.amplitudes), (0x170, frame.phases_radians)):
                ptr = P.from_address(obj + off).value
                fields.extend(abs(F.from_address(ptr + 4*k).value - v) for k, v in enumerate(expected))
            for off, envelope in ((0x218, frame.harmonic_envelope), (0x288, frame.residual_envelope)):
                env = P.from_address(obj + off).value
                assert I.from_address(env + 0x15c).value == len(envelope.values)//2
                ptr = P.from_address(env + 0x178).value
                for k, (x, y) in enumerate(zip(envelope.values[::2], envelope.values[1::2])):
                    fields.extend((abs(D.from_address(ptr + 16*k).value - x), abs(F.from_address(ptr + 16*k + 8).value - y)))
            model = DseModel(native, frame.secondary_header, frame.resonances, frame.secondary_resonances)
            knots = list(zip(frame.residual_envelope.values[::2], frame.residual_envelope.values[1::2]))
            errors = []
            for hz in [float(i*110.25) for i in range(201)] + [x for x in frame.frequencies_hz if x > 0]:
                hz = f32(hz)
                amp, phase, cutoff = F(), F(), F(0.)
                assert consumer(obj, hz, 22050., c.byref(amp), c.byref(phase), 0, c.byref(cutoff), 0, 0) == 0
                total = f32(sum(model.independent_resonance(res, hz) for res in model.resonances))
                db = f32(20*math.log10(total/32768)) if total > 0 else -3.4028234663852886e38
                baseline = f32(frame.secondary_header[0] + curve(frame.secondary_header, hz))
                correction = interpolate(knots, f32(hz/22050), regular=True)
                expected = f32(f32(baseline + correction) + db)
                errors.append(abs(expected - amp.value))
            amplitudes.extend(errors)
            rows.append(dict(time=frame.time_seconds, bytes=len(blob), harmonic_slots=len(frame.frequencies_hz),
                             amplitude_errors=stats(errors)))
    finally:
        close(handle)
    assert max(pitch_errors, default=0.) < 1e-4
    return dict(frames=rows, field_errors=stats(fields), pitch_cents_errors=stats(pitch_errors),
                amplitude_errors=stats(amplitudes), bytes=offset)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--frequencies', nargs='+', type=float, default=[220., 330., 880.])
    parser.add_argument('--all-frames', action='store_true')
    parser.add_argument('--read-existing', type=Path, help='Recheck streams named in an earlier report without refitting')
    args = parser.parse_args()
    cases = [];source_checks=[]
    if args.read_existing:
        previous = json.loads(args.read_existing.read_text(encoding='utf-8'))
        for case in previous['cases']:
            path = args.read_existing.parent / f"fitted_{case['shape']}_{case['frequency']:g}.frm2"
            data, offset, frames = path.read_bytes(), 0, []
            while offset < len(data):
                if offset + 8 > len(data):raise ValueError('Truncated stream header')
                size = struct.unpack_from('<I', data, offset + 4)[0]
                if not 28 <= size <= 1000000 or offset + size > len(data):raise ValueError('Invalid frame extent')
                frames.append(parse_frame(data[offset:offset + size]))
                offset += size
            native = Native(args.dll)
            result = check_file(native, path, frames)
            assert result['field_errors']['max'] == 0 and result['amplitude_errors']['max'] < 1e-4
            cases.append(dict(shape=case['shape'], frequency=case['frequency'],
                              phase=readback(native, path.resolve(), frames), **result))
        args.output.write_text(json.dumps(dict(dll_sha256=SHA256, cases=cases, scope='Existing generated streams: complete readback and amplitude/phase consumers; generation evidence is in the source report.'), indent=2)+'\n', encoding='utf-8')
        print(json.dumps(dict(cases=len(cases), frames=sum(len(x['frames']) for x in cases),
                              pitch_max=max(x['pitch_cents_errors']['max'] for x in cases))))
        return
    for shape in ('steady', 'attack', 'gap'):
        for frequency in args.frequencies:
            inputs, source = [], []
            source_check=verify(args.dll, shape, frequency, True, fit_inputs=inputs, source_outputs=source)
            source_checks.append({k:source_check[k] for k in ('shape','frequency','phase_control_frames','phase_control_max_error')})
            if not inputs:continue
            native = Native(args.dll)
            capacity = max(len(row['harmonics']) for row in source)
            phase_model = PhaseModel(capacity)
            phase_envelopes = {}
            phase_nodes = reordered_frames = 0
            phase_branches=Counter()
            for row in source:
                harmonics = row['harmonics'] + [(0., 10000., 0.)] * (capacity - len(row['harmonics']))
                controls=row['phase_controls']
                prediction = phase_model.advance([x[0] for x in harmonics], [x[1] for x in harmonics],
                                                 [x[2] for x in harmonics], row['f0'], controls['derivative'], controls['classification'], 256)
                if prediction is not None:
                    _, xs, ys, _, _ = prediction
                    phase_branches[str(prediction[3:])]+=1
                    # Native ENV insertion orders coordinates and overwrites
                    # duplicate knots. Track slot order need not be frequency order.
                    knots = sorted({min(1., max(0., f32(x))): f32(y) for x, y in zip(xs, ys)}.items())
                    reordered_frames += any(xs[k] < xs[k-1] for k in range(1, len(xs)))
                    reference = native.env(list(zip(xs, ys)), 3, 0, (-20000, 20000, 0))
                    assert I.from_address(reference + 0x5c).value == len(knots)
                    ptr = P.from_address(reference + 0x78).value
                    for k, (x, y) in enumerate(knots):
                        assert D.from_address(ptr + 16*k).value == x
                        assert F.from_address(ptr + 16*k + 8).value == y
                    phase_nodes += len(knots)
                    phase_envelopes[row['time']] = Envelope(0x01820002, (-20000., 20000., 0.),
                        tuple(v for pair in knots for v in pair))
            indices = range(len(inputs)) if args.all_frames else sorted(set((0, len(inputs)//2, len(inputs)-1)))
            frames = [replace(fitted_frame(native, inputs[i]), harmonic_envelope=phase_envelopes[inputs[i]['time']]) for i in indices]
            path = args.output.parent / f'fitted_{shape}_{frequency:g}.frm2'
            path.write_bytes(b''.join(serialize_frame(frame) for frame in frames))
            result = check_file(native, path, frames)
            phase_result = readback(native, path.resolve(), frames)
            cases.append(dict(shape=shape, frequency=frequency, phase=phase_result,
                              phase_install_nodes=phase_nodes, phase_reordered_frames=reordered_frames,
                              phase_branches=dict(phase_branches), **result))
    report = dict(dll_sha256=SHA256, cases=cases,source_checks=source_checks,
                  scope='Source-predicted E7 and harmonics, independent DRS fit and stateful phase model with verified source-derived raw-energy derivative and default zero classification, hop256; ordinary binary roundtrip and DSE amplitude/phase consumers. Dynamic35/84/85=0 guarded. Other feature profiles, lookup assets and native backend dependencies remain; not commercial preset or audio quality acceptance.')
    args.output.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(cases=len(cases), frames=sum(len(x['frames']) for x in cases),
                          field_max=max(x['field_errors']['max'] for x in cases),
                          amplitude_max=max(x['amplitude_errors']['max'] for x in cases))))
    assert all(x['field_errors']['max'] == 0 and x['amplitude_errors']['max'] < 1e-4 for x in cases)


if __name__ == '__main__':main()
