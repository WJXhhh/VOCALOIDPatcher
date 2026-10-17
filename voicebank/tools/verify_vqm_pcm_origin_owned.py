"""Owned PCM through independent harmonics and the ordinary native origin leaf.

The leaf receives only generated PCM-derived harmonic inputs in a numerical
fixture. It is not an enabled native PCM/phase batch or a maker bit17 writer.
"""
import argparse
from collections import Counter
import ctypes as c
import json
from pathlib import Path
import struct
import subprocess
import sys

from audit_frm2_numeric import Native, F, I, P, Q
from frm2_harmonic_origin import HarmonicOrigin
from frm2_mode5_math_backend import module_loaded
from verify_vqm_pcm_harmonic_phase import BASE, PATH_ARGS, HarmonicObserver
import verify_vqm_time_domain_events as events


def guarded(native, payload):
    pointer = native.alloc(len(payload)+64)
    c.memset(pointer, 0x5a, 32)
    c.memmove(pointer+32, payload, len(payload))
    c.memset(pointer+32+len(payload), 0xa5, 32)
    return pointer+32


def guards(pointer, size):
    assert c.string_at(pointer-32, 32) == b'\x5a'*32
    assert c.string_at(pointer+size, 32) == b'\xa5'*32


def child(args):
    assert args.sample is None and not module_loaded('DSE.dll')
    pcm = BASE.owned_signal(args.shape, args.frequency)
    event = events.EventProduction(args, pcm, args.frequency) if args.mode == 2 else None
    source = HarmonicObserver(args, pcm, args.frequency, event.complete if event else None)
    model = HarmonicOrigin()
    predictions = []
    for frame in source.frame_predictions:
        row = model.advance(frame['output'], frame['pitch'])
        predictions.append(dict(row, pitch_history=struct.pack('<20f', *model.pitch_history),
            history=struct.pack('<15f', *model.history), processed=model.processed-1,
            frame=frame))
    assert not module_loaded('DSE.dll')
    native = Native(args.dll)
    context = guarded(native, bytes(0xc00))
    history, pitch_history = guarded(native, bytes(60)), guarded(native, bytes(80))
    I.from_address(context+0xa80).value = 15
    P.from_address(context+0xa88).value = history
    I.from_address(context+0x428).value = 20
    P.from_address(context+0x420).value = pitch_history
    frame = guarded(native, bytes(4096))
    native.frame_ctor(frame)
    Q.from_address(frame+0x50).value = 7
    arrays = [guarded(native, bytes(110*4)) for _ in range(3)]
    for offset, pointer in zip((0x60, 0x68, 0x70), arrays):
        P.from_address(frame+offset).value = pointer
    origin = native.fn(0xda730, F, P, F, F, P)
    native_previous = 0.
    counts = Counter()
    for at, prediction in enumerate(predictions):
        I.from_address(context+0xa90).value = prediction['processed']
        c.memmove(pitch_history, prediction['pitch_history'], 80)
        output = prediction['frame']['output']
        payloads = [struct.pack('<'+str(len(output))+'f', *(row[column] for row in output))
            for column in range(3)]
        I.from_address(frame+0x58).value = len(output)
        for pointer, payload in zip(arrays, payloads):
            c.memmove(pointer, payload, len(payload))
        before = c.string_at(frame, 4096)
        if prediction['called']:
            assert output and len(output) == 110
            actual = origin(context, native_previous, prediction['frame']['pitch'], frame)
            assert struct.pack('<f', actual) == struct.pack('<f', prediction['origin']), (at, 'owned origin',
                actual, prediction['origin'], native_previous, prediction['candidate'],
                F.from_address(history+56).value, prediction['amplitude_gate'], prediction['continuous'],
                prediction['frame']['pitch'], prediction['frame']['output'][:3])
            native_previous = actual
            counts['native_origin_calls'] += 1
            counts['nonzero_origin_returns'] += actual != 0.
            counts['nonzero_search_candidates'] += prediction['candidate'] != 0.
        else:
            assert not output
            counts['unvoiced_no_origin_call'] += 1
        assert c.string_at(history, 60) == prediction['history'], (at, 'candidate history')
        assert bool(I.from_address(context+0xa94).value) == prediction['locked'], (at, 'lock')
        assert struct.pack('<f', native_previous) == struct.pack('<f', prediction['origin'])
        assert before == c.string_at(frame, 4096)
        for pointer, payload in zip(arrays, payloads):
            assert c.string_at(pointer, len(payload)) == payload
            guards(pointer, 440)
        for pointer, size in ((context, 0xc00), (history, 60), (pitch_history, 80), (frame, 4096)):
            guards(pointer, size)
        counts.update(frames=1, history_values_byte_exact=15, locked_frames=prediction['locked'],
            amplitude_gate_frames=prediction['amplitude_gate'], continuous_frames=prediction['continuous'])
    return dict(shape=args.shape, frequency=args.frequency, mode=args.mode, profile=args.profile,
        controls=dict(sorted(counts.items())), predictions_before_dse_load=True,
        reference_pitch_harmonics_or_origin_as_model_input=False, owned_inputs_and_guards_preserved=True)


def run(args):
    specs = [dict(shape=shape, mode=mode) for shape in ('steady', 'gap', 'attack',
        'noise', 'silence', 'dc_tone', 'impulse', 'pitch_up', 'pitch_down', 'pitch_octave') for mode in (1, 2)]
    specs += [dict(shape=shape, profile='manual_pitch', mode=mode)
        for shape in ('gap', 'noise') for mode in (1, 2)]
    specs += [dict(shape='steady', frequency=hz, mode=2) for hz in (10., 40., 1100., 1600.)]
    rows, counts = [], Counter()
    for spec in specs:
        command = [sys.executable, '-B', str(Path(__file__).resolve()), '--child']
        for name in PATH_ARGS:
            command += ['--'+name, str(getattr(args, name.replace('-', '_')))]
        for name, value in spec.items():
            command += ['--'+name, str(value)]
        process = subprocess.run(command, capture_output=True, text=True, timeout=180)
        assert process.returncode == 0, (spec, process.stderr[-1300:])
        row = json.loads(process.stdout)
        rows.append(row); counts.update(row['controls'])
        print(json.dumps(dict(spec=spec, native_calls=row['controls'].get('native_origin_calls', 0))), flush=True)
    assert counts['nonzero_origin_returns'] and counts['locked_frames'] and counts['amplitude_gate_frames']
    return dict(cases=rows, summary=dict(sorted(counts.items())), native_leaf_owned_inputs_only=True,
        scope='28 generated PCM cases and the isolated ordinary da730 origin leaf. '
            'Full pitch/harmonic predictions precede DSE loading; no native PCM phase toggle '
            'or original maker bit17 assignment is claimed.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in PATH_ARGS:
        parser.add_argument('--'+name, type=Path, required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--child', action='store_true')
    parser.add_argument('--shape', default='steady')
    parser.add_argument('--frequency', type=float, default=220.)
    parser.add_argument('--mode', type=int, choices=(1, 2), default=1)
    parser.add_argument('--profile', choices=BASE.PROFILES, default='default')
    args = parser.parse_args()
    args.sample = args.owned_length = None
    if args.child:
        print(json.dumps(child(args)))
    else:
        assert args.output
        result = run(args)
        args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
