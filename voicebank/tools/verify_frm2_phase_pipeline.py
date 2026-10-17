#!/usr/bin/env python3
"""Verify the DRS phase pipeline with owned injected harmonic sequences.

The native PCM/F0/feature analysis supplies input features, not reference phases.
A private object's virtual step is observed; DLL code/global vtables are unchanged.
This is NOT verification of commercial PCM analysis or final bit37 serialization.
Each case runs in a disposable child process, with a timeout and bounded steps.
"""
from __future__ import annotations

import argparse
from collections import Counter
import ctypes as c
import json
from itertools import product
import math
import os
from pathlib import Path
import subprocess
import sys
import traceback

from audit_frm2_numeric import Native, SHA256, F, D, I, P, Q, f32, stats
from audit_frm2_phase_history import difference, wrap
from verify_frm2_phase_origin import search_origin, smooth_origin, update_lock


class PhaseModel:
    def __init__(self, count, initial_delta=0.):
        self.history = [0.] * 15
        self.f0_history = [0.] * 20
        self.origin = 0.
        self.locked = False
        self.previous = None
        self.previous_frequencies = None
        self.correction = [0.] * count
        self.delta = [f32(initial_delta)] * count
        self.delta_valid = set()
        self.processed = 0
        self.voiced = 0

    def advance(self, frequencies, amplitudes, raw, f0, derivative, classification, hop):
        self.f0_history = self.f0_history[1:] + [f0]
        if f0 <= 0:
            self.voiced = 0
            self.processed += 1
            return None
        if self.voiced == 0:
            self.correction = [0.] * len(raw)
        self.voiced += 1
        candidate = search_origin(frequencies, raw, f0, self.origin)
        self.history = self.history[1:] + [candidate]
        self.locked = update_lock(self.history, self.f0_history, self.processed, self.locked)
        continuous = self.processed > 1 and self.f0_history[-3] > 0 and self.f0_history[-4] > 0
        proposed = smooth_origin(candidate, self.origin, self.locked, continuous)
        # Native low-band scan terminates at the first frequency above 1600 Hz.
        other = -10000.
        for hz, amp in zip(frequencies[1:10], amplitudes[1:10]):
            if hz > 1600:
                break
            if hz > 0:
                other = max(other, amp)
        if f32(amplitudes[0] - 20) <= other:
            self.origin = proposed
        delay = (difference(self.origin, raw[0]) / math.tau) / f0
        aligned = [f32(wrap(k * (f0 * math.tau) * delay + phase)) if hz > 0 else phase
                   for k, (hz, phase) in enumerate(zip(frequencies, raw), 1)]
        if self.voiced > 1 and self.previous is not None:
            total, valid = 0., 0
            for k in range(10):
                if frequencies[k] > 0 and self.previous_frequencies[k] > 0:
                    total = f32(total + difference(self.previous[k], aligned[k]) / (k + 1))
                    valid += 1
            if valid:
                shift = f32(f32(total / valid) * .9)
                aligned = [f32(wrap(phase + k * shift)) if hz > 0 else phase
                           for k, (hz, phase) in enumerate(zip(frequencies, aligned), 1)]
        self.previous = aligned[:]
        self.previous_frequencies = frequencies[:]
        # The first feature above -100 selects the threshold; abs(value)<2 uses
        # the largest threshold, abs(value)>2 retains the default.
        threshold = (5.183628082275391 if classification is None or abs(classification) > 2
                     else 5.654866695404053 if abs(classification) == 2 else 5.9690260887146)
        limit = 1.5
        if f0 < 100:
            limit = f32(f32(2 * min(1., f32(f32(100 - f0) / 50.))) + 1.5)
        decay = f32(math.pi) if abs(derivative) > limit else f32(.2)
        if hop == 512:
            decay = f32(decay * 2)
        values, xs = [aligned[0]], [0.]
        tail_value, tail_x = aligned[0], 0.
        inserted = {0.: aligned[0], 1.: 0.}
        self.processed += 1
        for k, (hz, phase) in enumerate(zip(frequencies, aligned)):
            if hz <= 0:
                continue
            delta = f32(difference(phase, f32(wrap(tail_value))))
            if hz <= 3500 and self.voiced > 5:
                if abs(f32(delta - self.delta[k])) > threshold:
                    self.correction[k] = f32(self.correction[k] + f32(self.delta[k] - delta))
                self.correction[k] = math.copysign(
                    max(0., f32(abs(self.correction[k]) - decay)), self.correction[k])
            else:
                self.correction[k] = 0.
            self.delta[k] = delta
            self.delta_valid.add(k)
            value = f32(f32(delta + self.correction[k]) + tail_value)
            x = f32(hz / 22050)
            values.append(value)
            xs.append(x)
            # Native insertion sorts each node immediately. The next slot
            # reads the highest interior knot; the x=1 endpoint is excluded.
            key = min(1., max(0., x))
            inserted[key] = value
            if key < 1. and key >= tail_x:
                tail_x, tail_value = key, value
        ordered = sorted(inserted)
        values[0] = inserted[ordered[1]]
        inserted[0.] = values[0]
        values.append(inserted[ordered[-2]])
        xs.append(1.)
        return aligned, xs, values, threshold, decay


def run_case(dll, variant, hop, features, seed_f0, *, source_frames=None, phase_sink=None,
             pcm_override=None, source_controls=False, step_observer=None, source_pitch=None,
             initial_delta=0.):
    initial_delta = f32(initial_delta)
    if not math.isfinite(initial_delta):
        raise ValueError('Initial delta must be finite')
    if source_pitch is not None and (source_frames is None or len(source_pitch)!=len(source_frames)):
        raise ValueError("Replay pitch and harmonics must have equal frame counts")
    native = Native(dll)
    config = native.alloc(0x35f0)
    native.fn(0x90de0, P, P)(config)
    F.from_address(config + 0x18).value = 44100 / hop
    for parameter, value in ((0x14, 0), (0x0d, seed_f0), (4, 350), (0x2a, 4)):
        for x in (0., 1.):
            native.add(config + 0x5d0 + parameter * 0xa0, x, value)
    I.from_address(config + 0x5a8).value = 1
    I.from_address(config + 0x54c).value = features
    collection = native.alloc(0x68)
    native.fn(0x938c0, P, P)(collection)
    context = native.alloc(0xce0)
    native.fn(0xcdde0, P, P, I, P)(context, 44100, config)
    # The constructor allocates 700 delta slots without initializing them.
    # Give injected late-active slots a defined history in this private harness.
    delta_memory = P.from_address(context + 0xa78).value
    for k in range(700):
        F.from_address(delta_memory + 4 * k).value = initial_delta
    pcm = [6000 * sum(math.sin(math.tau * seed_f0 * k * i / 44100 + k * .37) / k
                     for k in range(1, 9)) for i in range(22050)]
    if variant in ('silence_gap', 'stable_gap'):
        pcm[6615:13230] = [0.] * 6615
    if pcm_override is not None:
        pcm = pcm_override
    raw_observer = None
    classification_observer = None
    if source_controls:
        if features:
            from frm2_live_classification import ClassificationObserver
            classification_observer = ClassificationObserver([f32(v) for v in pcm])
        from frm2_raw_feature_observer import RawFeatureObserver
        raw_observer = RawFeatureObserver([f32(v) for v in pcm], phase_controls=True, phase_stage=True, classification_observer=classification_observer)
        for key in (0x35, 0x84, 0x85):
            if native.fn(0x2b050, F, P, I, F)(config, key, 0.) != 0:
                raise AssertionError(("Unsupported feature profile", key))
    descriptor = native.alloc(16)
    P.from_address(descriptor).value = native.array(F, pcm)
    I.from_address(descriptor + 8).value = len(pcm)
    I.from_address(descriptor + 12).value = 44100
    count = len(source_frames[0].frequencies_hz) if source_frames else 30
    if source_frames and any(len(frame.frequencies_hz) != count for frame in source_frames):
        raise ValueError('Source harmonic capacity must remain constant')
    if not 10 <= count <= 700:
        raise ValueError('Unexpected phase input capacity')
    frame_descriptor = native.alloc(0x40)
    Q.from_address(frame_descriptor).value = 7
    I.from_address(frame_descriptor + 8).value = count
    allocate = native.fn(0x7f1a0, I, P, P, I)
    vtable = P.from_address(context).value
    original_step = c.CFUNCTYPE(Q, P)(P.from_address(vtable + 8).value)
    private_vtable = native.alloc(0x100)
    c.memmove(private_vtable, vtable, 0x100)
    model = PhaseModel(count, initial_delta)
    errors = {key: [] for key in ('origin', 'aligned', 'env_x', 'env_y', 'correction', 'delta')}
    branches = Counter()
    classifications = Counter()
    runtime_profile = {}
    nonzero_corrections = locked_frames = steps = unvoiced_frames = 0

    def step(ctx):
        nonlocal nonzero_corrections, locked_frames, steps, unvoiced_frames
        try:
            steps += 1
            if not runtime_profile:
                runtime_profile["injected_delta_initialization"] = dict(slots=700, value=initial_delta, scope="private harness boundary")
                runtime_profile["step_rva"] = hex(P.from_address(vtable + 8).value-native.lib._handle)
                runtime_profile.update({hex(key):native.fn(0x2b050,F,P,I,F)(P.from_address(ctx+0xad0).value,key,0.) for key in (0x17,0x35,0x7d,0x84,0x85)})
            if steps > 10000:
                raise RuntimeError('Native step budget exceeded')
            before = I.from_address(ctx + 0xa90).value
            frequencies = [f32(seed_f0 * k) for k in range(1, count + 1)]
            raw = [f32(math.sin(k + (0 if variant.startswith('stable') else before * .31)) * 2.9)
                   for k in range(count)]
            amplitudes = [-20.] * count
            if variant == 'sparse':
                frequencies[3 + before % 4] = 0.
                frequencies[22] = 0.
            if variant == 'amplitude_gate':
                amplitudes[0] = 10.
            if source_frames is not None:
                source = source_frames[min(before, len(source_frames) - 1)]
                frequencies = list(source.frequencies_hz)
                amplitudes = list(source.amplitudes)
                raw = list(source.phases_radians)
            if source_pitch is not None:
                # Replay already-verified F0: skip the optional queue pitch rewrite
                # at d464b..d4aca only in this private experimental instance.
                replay_pitch_context=P.from_address(ctx+0xc88).value
                P.from_address(ctx+0xc88).value=0
                replay_pitch=source_pitch[min(before,len(source_pitch)-1)]
                for x in (0.,1.):native.add(config+0x5d0+0x0d*0xa0,x,replay_pitch["f0"])
            queue = P.from_address(ctx + 0xc20).value
            queue_count = I.from_address(ctx + 0xc18).value
            if not 1 <= queue_count <= 64:
                raise ValueError('Unexpected queue size')
            # All queued inputs receive this step's owned harmonics. The private
            # previous-aligned frame at +aa0 retains the native cross-frame state.
            for slot in range(queue_count):
                link = P.from_address(queue + slot * 8).value
                frame = P.from_address(link + 0x28).value
                if allocate(frame, frame_descriptor, 1) != 0:
                    raise RuntimeError('Native harmonic allocation failed')
                if source_pitch is not None:
                    F.from_address(frame+0xf0).value=replay_pitch["f0"]

                for offset, values in ((0x60, frequencies), (0x68, amplitudes), (0x70, raw)):
                    dest = P.from_address(frame + offset).value
                    for k, value in enumerate(values):
                        F.from_address(dest + 4 * k).value = value
            classification_token = classification_observer.before(ctx) if classification_observer else None
            observer_token = step_observer.before(ctx) if step_observer else None
            token = raw_observer.before(ctx) if raw_observer else None
            result = original_step(ctx)
            if source_pitch is not None:P.from_address(ctx+0xc88).value=replay_pitch_context
            if classification_observer:
                classification_observer.after(ctx, classification_token)
            if step_observer:
                step_observer.after(ctx, observer_token)
            if raw_observer:
                raw_observer.after(ctx, token)
            after = I.from_address(ctx + 0xa90).value
            if after == before:
                return result
            if after != before + 1:
                raise AssertionError('Unexpected native frame advancement')
            f0_history = P.from_address(ctx + 0x420).value
            f0 = F.from_address(f0_history + 19 * 4).value
            output = P.from_address(ctx + 0xcc8).value
            if source_pitch is not None:
                if before>=len(source_pitch):raise AssertionError("Replay produced extra frames")
                expected=source_pitch[before]
                if f0!=f32(expected["f0"]):raise AssertionError(("Replay F0 differs",before,expected["f0"],f0,native.fn(0x2b050,F,P,I,F)(P.from_address(ctx+0xad0).value,0xd,0.)))
                if abs(D.from_address(output+0x48).value-expected["time"])>1e-12:raise AssertionError("Replay frame time differs")
                f0=f32(expected["f0"])
            feature = P.from_address(output + 0x128).value
            derivative = F.from_address(feature + 0x28).value
            classification = next((F.from_address(feature + 0x2c + 4 * k).value
                                   for k in range(I.from_address(ctx + 0xa5c).value)
                                   if F.from_address(feature + 0x2c + 4 * k).value > -100), None)
            classifications[str(classification)] += 1
            if raw_observer:
                controls = raw_observer.frames[output]["phase_controls"]
                derivative = controls["derivative"]
                classification = controls["classification"]
            prediction = model.advance(
                frequencies, amplitudes, raw, f0, derivative, classification, hop)
            errors['origin'].append(abs(model.origin - F.from_address(ctx + 0xa98).value))
            if model.locked != bool(I.from_address(ctx + 0xa94).value):
                raise AssertionError('Automatic lock diverged')
            if model.voiced != I.from_address(ctx + 0x418).value:
                raise AssertionError('Voiced counter/reset diverged')
            locked_frames += model.locked
            if prediction is None:
                unvoiced_frames += 1
                return result
            aligned, xs, ys, threshold, decay = prediction
            branches[f'{threshold}/{decay}'] += 1
            old_frame = P.from_address(ctx + 0xaa0).value
            for key, ptr, expected in (
                    ('aligned', P.from_address(old_frame + 0x70).value, aligned),
                    ('correction', P.from_address(ctx + 0xa70).value, model.correction),
                    ('delta', P.from_address(ctx + 0xa78).value, model.delta)):
                errors[key].extend(abs(value - F.from_address(ptr + k * 4).value)
                                   for k, value in enumerate(expected)
                                   if key != 'delta' or k in model.delta_valid)
            nonzero_corrections += sum(value != 0 for value in model.correction)
            # Native ENV insertion sorts and overwrites duplicate coordinates.
            knots=sorted({min(1.,max(0.,f32(x))):f32(y) for x,y in zip(xs,ys)}.items())
            xs=[x for x,y in knots];ys=[y for x,y in knots]
            env = P.from_address(ctx + 0xab0).value
            points = P.from_address(env + 0x78).value
            if I.from_address(env + 0x5c).value != len(ys):
                raise AssertionError('Phase knot count diverged')
            errors['env_x'].extend(abs(value - D.from_address(points + k * 16).value)
                                   for k, value in enumerate(xs))
            errors['env_y'].extend(abs(value - F.from_address(points + k * 16 + 8).value)
                                   for k, value in enumerate(ys))
            if phase_sink is not None:
                phase_sink(before, f0, xs, ys)
            return result
        except BaseException:
            # ctypes swallows callback exceptions: a normal raise could let the
            # native loop continue with an undefined return. Terminate this child.
            traceback.print_exc()
            sys.stderr.flush()
            os._exit(2)

    callback = c.CFUNCTYPE(Q, P)(step)
    P.from_address(private_vtable + 8).value = c.cast(callback, P).value
    P.from_address(context).value = private_vtable
    result = native.fn(0xd9b10, Q, P, P, P, Q, P)(context, 0, collection, 0, descriptor)
    if result != 0 or model.processed == 0:
        raise AssertionError('Native batch did not produce frames')
    if source_pitch is not None and model.processed!=len(source_pitch):
        raise AssertionError("Replay frame count differs")
    summary = {key: stats(values) for key, values in errors.items()}
    if any(not math.isfinite(value) or value >= 1e-4
           for values in errors.values() for value in values):
        raise AssertionError(json.dumps(summary))
    return dict(replayed_frames=model.processed if source_pitch is not None else 0,
                replay_pitch_rewrite_bypassed=source_pitch is not None,variant=variant, hop=hop, features=features, seed_f0=seed_f0, frames=model.processed,
                unvoiced_frames=unvoiced_frames,
                locked_frames=locked_frames, nonzero_corrections=nonzero_corrections,
                branches=dict(branches), classifications=dict(classifications), runtime_profile=runtime_profile, errors=summary,
                classification_errors=stats(classification_observer.errors) if classification_observer else None,
                classification_geometry_errors=stats(classification_observer.geometry_errors) if classification_observer else None,
                classification_power_errors=stats(classification_observer.power_errors) if classification_observer else None,
                classification_profile=classification_observer.profile if classification_observer else None,
                source_control_frames=len(raw_observer.comparisons) if raw_observer else 0,
                source_control_max_error=max((x["phase_control_error"] for x in raw_observer.comparisons), default=0.) if raw_observer else None)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--source-controls', action='store_true')
    parser.add_argument('--case', choices=('stable', 'varying', 'sparse', 'amplitude_gate', 'silence_gap', 'stable_gap'))
    parser.add_argument('--hop', type=int, choices=(256, 512), default=256)
    parser.add_argument('--features', type=int, choices=(0, 1), default=0)
    parser.add_argument('--seed-f0', type=int, choices=(80, 220), default=220)
    args = parser.parse_args()
    if args.case:
        print(json.dumps(run_case(args.dll, args.case, args.hop, args.features, args.seed_f0, source_controls=args.source_controls)))
        return
    if args.output is None:
        parser.error('--output is required for the full matrix')
    cases = []
    variants = ('stable', 'varying', 'sparse', 'amplitude_gate', 'silence_gap', 'stable_gap')
    # Enabled profile uses independently computed bands on explicit native spectra.
    feature_modes = (0, 1)
    for variant, hop, features, seed_f0 in product(variants, (256, 512), feature_modes, (80, 220)):
        result = subprocess.run([sys.executable, __file__, '--dll', str(args.dll),
                                 '--case', variant, '--hop', str(hop), '--seed-f0', str(seed_f0),
                                 '--features', str(features)] + (['--source-controls'] if args.source_controls else []), capture_output=True, text=True, timeout=60)
        if result.returncode:
            raise RuntimeError(f'{variant}/{hop}/{features}/{seed_f0}: {result.stderr[-6000:]}')
        case = json.loads(result.stdout)
        cases.append(case)
        print(json.dumps({key: case[key] for key in ('variant', 'hop', 'features', 'seed_f0', 'frames')}), flush=True)
    report = dict(dll_sha256=SHA256, cases=cases, frames=sum(case['frames'] for case in cases),
                  scope=('Owned injected harmonics and native F0; source-derived raw-energy phase controls; enabled classification uses owned PCM with legacy no-FFT power preparation and independent bands; analytic window/boundary geometry; independent phase state from zero' if args.source_controls else 'Owned injected harmonics; native F0/feature inputs; independent phase state from zero'),
                  exclusions=['Commercial pre-roll', 'PCM to harmonic extraction', 'Final bit37 serialization',
                              'Unvoiced spectrum generation', 'Low-F0 feature threshold boundary',
                              'Other classification profiles' if args.source_controls else 'Source-derived feature controls'])
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
