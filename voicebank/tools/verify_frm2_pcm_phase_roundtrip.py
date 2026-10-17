#!/usr/bin/env python3
"""Owned PCM -> measured harmonics -> independent phase model -> FRM2 -> DSE.

The two analysis passes deliberately use different configurations. This verifies
their explicit handoff and the saved phase consumer, not a recovered commercial
authoring preset. Native F0/features are inputs to the independent phase model.
All PCM and serialized payloads are generated here and kept in temporary files.
"""
from __future__ import annotations

import argparse
import ctypes as c
from dataclasses import replace
from itertools import product
import json
import math
import os
from pathlib import Path
import re
import struct
import subprocess
import sys
import wave

from audit_frm2_numeric import Native, SHA256, F, D, I, P, Q, B, f32, stats, interpolate
from probe_frm2 import MAIN_MASK, MainFrame, Envelope, parse_frame, serialize_frame
from verify_frm2_phase_pipeline import run_case
from frm2_work_directory import temporary_directory


def readback(native, path, frames):
    crt = c.CDLL('ucrtbase')
    fopen, seek, close = crt._wfopen, crt._fseeki64, crt.fclose
    fopen.argtypes, fopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    seek.argtypes, seek.restype = [P, c.c_int64, I], I
    close.argtypes = [P]
    reader = native.fn(0x106e20, Q, P, P)
    consume = native.fn(0x16fa70, None, P, B)
    context, destination = native.alloc(0x2b000), native.alloc(0x328)
    P.from_address(context + 0x2238).value = destination
    F.from_address(context + 0x1d564).value = 22050
    native.dse_env([(0, 0), (1, 1)], mode=1, at=context + 0x247f0)
    handle = fopen(str(path), 'rb')
    if not handle:
        raise OSError('Cannot open owned FRM2 data')
    point_errors, consumer_errors, mismatches = [], [], []
    offset = nonzero_points = 0
    try:
        for frame in frames:
            obj, stream = native.alloc(0x328), native.alloc(0x80)
            native.fn(0x12a670, P, P)(obj)
            P.from_address(stream + 8).value = handle
            Q.from_address(stream).value = offset
            I.from_address(stream + 0x2c).value = 0x32534d53
            blob = serialize_frame(frame)
            if parse_frame(blob) != frame:
                raise AssertionError('Independent serialization roundtrip differs')
            if seek(handle, offset, 0) or reader(obj, stream):
                raise AssertionError('DSE reader failed')
            offset += len(blob)
            if (Q.from_address(stream).value != offset
                    or Q.from_address(obj + 0x150).value != MAIN_MASK | (1 << 31)):
                raise AssertionError('Native cursor or mask differs')
            for field, values in ((0x160, frame.frequencies_hz), (0x168, frame.amplitudes),
                                   (0x170, frame.phases_radians)):
                ptr = P.from_address(obj + field).value
                if list((F * len(values)).from_address(ptr)) != list(values):
                    raise AssertionError('Native harmonic readback differs')
            env = P.from_address(obj + 0x218).value
            values = frame.harmonic_envelope.values
            knots = list(zip(values[::2], values[1::2]))
            if I.from_address(env + 0x15c).value != len(knots):
                raise AssertionError('Native phase knot count differs')
            ptr = P.from_address(env + 0x178).value
            for k, (x, y) in enumerate(knots):
                point_errors.extend((abs(x - D.from_address(ptr + k * 16).value),
                                     abs(y - F.from_address(ptr + k * 16 + 8).value)))
                nonzero_points += y != 0
            P.from_address(context + 0x247b8).value = obj
            I.from_address(destination + 0x158).value = len(frame.frequencies_hz)
            P.from_address(destination + 0x160).value = P.from_address(obj + 0x160).value
            phases = native.array(F, frame.phases_radians)
            P.from_address(destination + 0x170).value = phases
            consume(context, 1)
            for k, (hz, phase) in enumerate(zip(frame.frequencies_hz, frame.phases_radians)):
                expected = phase
                if hz > 0:
                    x = f32(hz * f32(1 / 22050))
                    value = interpolate(knots, x, cubic=True)
                    wrapped = value - (math.trunc(value / math.tau) * math.tau
                                       - (math.tau if value < 0 else 0))
                    expected = f32(phase + f32(wrapped))
                actual = F.from_address(phases + 4 * k).value
                consumer_errors.append(abs(expected - actual))
                if abs(expected - actual) >= 1e-4:
                    mismatches.append(dict(time=frame.time_seconds, index=k, hz=hz, expected=expected, actual=actual))
    finally:
        close(handle)
    if not nonzero_points or max(point_errors + consumer_errors) >= 1e-4:
        raise AssertionError(dict(points=stats(point_errors), consumer=stats(consumer_errors), mismatches=mismatches[:10]))
    return dict(frames=len(frames), nonzero_phase_points=nonzero_points,
                readback_coordinates=stats(point_errors), phase_consumer=stats(consumer_errors))


def verify(dll, harness, frequency, shape, features):
    native = Native(dll)
    with temporary_directory(prefix='pcm-phase-') as directory:
        directory = Path(directory)
        wav, source_path, saved_path = (directory / name for name in ('owned.wav', 'analysis.sms2', 'phase.frm2'))
        pcm = [round(4500 * sum(math.sin(math.tau * frequency * k * i / 44100 +
                                       (k * .37 if shape == 'shifted' else math.sin(k) * 2)) / k
                                for k in range(1, 9))) for i in range(22050)]
        if shape == 'attack':
            pcm = [round(v * min(1, i / 4410)) for i, v in enumerate(pcm)]
        with wave.open(str(wav), 'wb') as output:
            output.setparams((1, 2, 44100, 0, 'NONE', 'not compressed'))
            output.writeframes(struct.pack(f'<{len(pcm)}h', *pcm))
        env = {key: value for key, value in os.environ.items() if not key.startswith('DRS_HARNESS_')}
        env['DRS_HARNESS_BUILD_MAIN_FIELDS'] = '1'
        result = subprocess.run(['dotnet', str(harness), str(dll), str(source_path), '.5',
                                 str(frequency), 'external', str(frequency), str(wav)],
                                env=env, capture_output=True, text=True, timeout=60)
        if (result.returncode or 'failed=0' not in result.stdout
                or re.search(r'\bfailed=[1-9]', result.stdout) or 'readback.result=0' not in result.stdout):
            raise RuntimeError(result.stdout[-4000:] + result.stderr[-2000:])
        data, cursor, sources = source_path.read_bytes(), 0, []
        while (cursor := data.find(b'FRM2', cursor)) >= 0:
            size = struct.unpack_from('<I', data, cursor + 4)[0]
            source = parse_frame(data[cursor:cursor + size])
            if not isinstance(source, MainFrame) or source.mask != MAIN_MASK:
                raise AssertionError('Expected an ordinary owned frame')
            # The builder retains a ceil(Nyquist/F0) slot above Nyquist. Exclude
            # it explicitly from this bounded phase experiment and mark absent.
            active = [0 < hz <= 22050 for hz in source.frequencies_hz]
            sources.append(replace(source,
                frequencies_hz=tuple(hz if ok else 0. for hz, ok in zip(source.frequencies_hz, active)),
                amplitudes=tuple(v if ok else 10000. for v, ok in zip(source.amplitudes, active)),
                phases_radians=tuple(v if ok else 0. for v, ok in zip(source.phases_radians, active))))
            cursor += size
        if len(sources) != 87:
            raise AssertionError(f'Unexpected owned analysis frame count: {len(sources)}')
        frames, source_indices = [], []

        def save(index, f0, xs, ys):
            if (index >= len(sources) or source_indices and index <= source_indices[-1]):
                raise AssertionError('Phase handoff frame index differs')
            source_indices.append(index)
            values = tuple(f32(v) for pair in zip(xs, ys) for v in pair)
            frames.append(replace(sources[index], f0_hz=f0,
                harmonic_envelope=Envelope(0x01820002, (-20000., 20000., 0.), values)))

        phase = run_case(dll, 'stable', 256, features, frequency,
                         source_frames=sources, phase_sink=save, pcm_override=pcm)
        if len(frames) + phase['unvoiced_frames'] != len(sources):
            raise AssertionError('Phase processing did not cover all source frames')
        saved_path.write_bytes(b''.join(serialize_frame(frame) for frame in frames))
        return dict(frequency=frequency, shape=shape, features=features, phase=phase,
                    skipped_unvoiced=phase['unvoiced_frames'],
                    saved=readback(native, saved_path, frames))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', required=True, type=Path)
    parser.add_argument('--harness', required=True, type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--frequency', type=int, choices=(80, 110, 220, 440))
    parser.add_argument('--shape', choices=('shifted', 'attack'), default='shifted')
    parser.add_argument('--features', type=int, choices=(0, 1), default=0)
    args = parser.parse_args()
    if args.frequency:
        print(json.dumps(verify(args.dll.resolve(), args.harness.resolve(), args.frequency, args.shape, args.features)))
        return
    if args.output is None:
        parser.error('--output is required for the complete matrix')
    cases = []
    for frequency, shape, features in product((80, 110, 220, 440), ('shifted', 'attack'), (0, 1)):
        result = subprocess.run([sys.executable, __file__, '--dll', str(args.dll), '--harness', str(args.harness),
                                 '--frequency', str(frequency), '--shape', shape, '--features', str(features)],
                                capture_output=True, text=True, timeout=60)
        if result.returncode:
            raise RuntimeError(f'{frequency}/{shape}/{features}: {result.stderr[-6000:]}')
        case = json.loads(result.stdout)
        cases.append(case)
        print(json.dumps(dict(frequency=frequency, shape=shape, features=features, frames=case['saved']['frames'])), flush=True)
    report = dict(dll_sha256=SHA256, cases=cases, scope=__doc__,
                  frames=sum(case['saved']['frames'] for case in cases),
                  exclusions=['Commercial production preset/pre-roll', 'Fully independent PCM analysis',
                              'Unvoiced frame production', 'Host synthesis/audio quality'])
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
