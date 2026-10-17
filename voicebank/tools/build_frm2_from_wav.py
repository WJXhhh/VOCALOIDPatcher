"""Build verified voiced ordinary FRM2 frames from owned mono 44.1-kHz PCM16 WAV.

Independent phase and fitting; pinned DSE/MKL source-analysis dependencies remain.
No native phase path, injected harmonic frames, or temporary F0 bypass is used.
Output is a research FRM2 stream and timeline, not an installable DDI/DDB voicebank.
"""
import argparse
from dataclasses import replace
import hashlib
import json
import math
import os
from pathlib import Path
import struct
import subprocess
import sys
import uuid
import wave

from audit_frm2_numeric import Native, SHA256, f32
from probe_frm2 import Envelope, serialize_frame
from verify_frm2_pitch_state import verify
from verify_frm2_phase_pipeline import PhaseModel
from verify_frm2_fitted_roundtrip import fitted_frame, check_file, readback


def read_pcm(path):
    with wave.open(str(path), 'rb') as wav:
        if (wav.getnchannels(), wav.getsampwidth(), wav.getframerate(), wav.getcomptype()) != (1, 2, 44100, 'NONE'):
            raise ValueError('Require mono, 44100 Hz, uncompressed PCM16 WAV')
        count = wav.getnframes()
        if not 2048 <= count <= 5*44100:
            raise ValueError('Research build accepts 2048 samples to 5 seconds; split longer recordings explicitly')
        payload = wav.readframes(count)
        if len(payload) != count*2:
            raise ValueError('Truncated WAV')
    return list(struct.unpack('<' + 'h'*count, payload))


def file_hash(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024*1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def build(dll, wav, destination, seed):
    pcm = read_pcm(wav)
    source, inputs = [], []
    checks = verify(dll, 'wav', seed, True, fit_inputs=inputs, source_outputs=source, pcm_override=pcm)
    if not inputs:
        raise ValueError('No supported voiced E7 frames; no output published')
    capacity = max(10, max(len(row['harmonics']) for row in source))
    model = PhaseModel(capacity)
    envelopes, timeline = {}, []
    for row in source:
        harmonics = row['harmonics'] + [(0., 10000., 0.)]*(capacity-len(row['harmonics']))
        controls = row['phase_controls']
        prediction = model.advance([h[0] for h in harmonics], [h[1] for h in harmonics],
                                   [h[2] for h in harmonics], row['f0'],
                                   controls['derivative'], controls['classification'], 256)
        timeline.append(dict(time=row['time'], f0=row['f0'], emitted=False))
        if prediction is not None:
            _, xs, ys, _, _ = prediction
            points = sorted({min(1., max(0., f32(x))): f32(y) for x, y in zip(xs, ys)}.items())
            if len(points) < 3:
                raise ValueError('Two-node phase ENV is not content-defined in this DSE; no output published')
            if not all(math.isfinite(x) and math.isfinite(y) and -20000 <= y <= 20000 for x, y in points):
                raise ValueError('Phase ENV outside verified finite domain')
            envelopes[row['time']] = Envelope(0x01820002, (-20000., 20000., 0.),
                tuple(value for pair in points for value in pair))
    native = Native(dll)
    frames = []
    for row in inputs:
        frames.append(replace(fitted_frame(native, row), harmonic_envelope=envelopes[row['time']]))
    path = destination/'frames.frm2'
    path.write_bytes(b''.join(serialize_frame(frame) for frame in frames))
    numeric = check_file(native, path, frames)
    phase = readback(native, path.resolve(), frames)
    if numeric['field_errors']['max'] != 0 or numeric['amplitude_errors']['max'] >= 1e-4:
        raise AssertionError('Numeric consumer verification failed')
    frame_index = {frame.time_seconds: i for i, frame in enumerate(frames)}
    for row in timeline:
        row['emitted'] = row['time'] in frame_index
        row['frame_index'] = frame_index.get(row['time'])
    (destination/'timeline.json').write_text(json.dumps(timeline, indent=2)+'\n', encoding='utf-8')
    report = dict(dll_sha256=SHA256, input_sha256=file_hash(wav),
        stream_sha256=file_hash(path), samples=len(pcm),
        source_frames=len(source), emitted_frames=len(frames), omitted_frames=len(source)-len(frames),
        source_checks=checks, numeric=numeric, phase=phase,
        contract=dict(native_phase_entry=False, injected_harmonics=False, pitch_rewrite_bypass=False,
                      independent_phase_initial_delta=0, sample_rate=44100, hop=256,
                      source_mode=4, automatic_pitch=True, classification_features=False),
        dependencies=['Pinned DSE native source scheduling', 'DSE/MKL FFT', 'DLL-owned FIR and runtime lookup assets'],
        limitations=['Voiced ordinary frames only; omitted/unvoiced times are explicit in timeline.json',
                     'No DDI/DDB assembly or commercial voicebank/host audio acceptance',
                     'Unverified source conditions fail closed; research input limit is 5 seconds'])
    (destination/'report.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--wav', type=Path, required=True)
    parser.add_argument('--output-dir', type=Path, required=True)
    parser.add_argument('--pitch-seed', type=float, default=220.)
    parser.add_argument('--worker', action='store_true', help=argparse.SUPPRESS)
    args = parser.parse_args()
    if not math.isfinite(args.pitch_seed) or not 40 <= args.pitch_seed <= 2000:
        parser.error('Pitch seed must be 40..2000 Hz')
    if args.worker:
        build(args.dll, args.wav, args.output_dir, args.pitch_seed)
        return
    read_pcm(args.wav)  # Reject invalid files before starting native work.
    output = args.output_dir.resolve()
    if output.exists():
        raise FileExistsError('Output directory already exists; choose a new directory')
    output.parent.mkdir(parents=True, exist_ok=True)
    staging = output.parent/('.frm2-build-'+uuid.uuid4().hex)
    staging.mkdir()
    try:
        result = subprocess.run([sys.executable, __file__, '--worker', '--dll', str(args.dll.resolve()),
            '--wav', str(args.wav.resolve()), '--output-dir', str(staging), '--pitch-seed', str(args.pitch_seed)],
            capture_output=True, text=True, timeout=1800)
    except subprocess.TimeoutExpired:
        (staging/'failure.txt').write_text('Native worker exceeded 1800 seconds; output not published.\n', encoding='utf-8')
        raise RuntimeError(f'Build timed out; diagnostics: {staging}') from None
    if result.returncode:
        (staging/'failure.txt').write_text(result.stderr, encoding='utf-8')
        raise RuntimeError(f'Build failed; output not published. Diagnostics: {staging}')
    if output.exists():
        raise FileExistsError('Output appeared during build; verified staging retained')
    os.rename(staging, output)
    report = json.loads((output/'report.json').read_text(encoding='utf-8'))
    print(json.dumps(dict(output=str(output), source_frames=report['source_frames'],
                          emitted_frames=report['emitted_frames'], verified=True)))


if __name__ == '__main__':
    main()
