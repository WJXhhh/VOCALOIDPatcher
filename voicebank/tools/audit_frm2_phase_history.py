#!/usr/bin/env python3
"""Read-only audit of the DRS phase-history rule against consecutive V4X frames.

Frame origins and the predecessor state are observed inputs, not a recovered
independent bit37 producer. Only statistics and frame identifiers are exported.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

from audit_frm2_numeric import SHA256, f32, read_frame
from probe_frm2 import MainFrame
from verify_reference_ddi import load_public_parser, pointer, samples


def wrap(value):
    if value < 0:
        return value - (int(value / math.tau) * math.tau - math.tau)
    if value >= math.tau:
        return value - int(value / math.tau) * math.tau
    return value


def difference(value, reference):
    if value > reference + math.pi:
        value -= math.tau
    elif value < reference - math.pi:
        value += math.tau
    return value - reference


def harmonic_slots(frame):
    slots = [round(x * 22050 / frame.f0_hz) - 1
             for x in frame.harmonic_envelope.values[2:-2:2]]
    if any(slot < 0 or slot >= len(frame.phases_radians)
           or frame.frequencies_hz[slot] <= 0 for slot in slots):
        raise ValueError('Phase ENV knot does not map to a valid harmonic slot')
    return slots


def observe(frame):
    values = frame.harmonic_envelope.values[1::2]
    origin = values[1] - frame.phases_radians[0]
    phases = [f32(wrap(phase + k * origin)) for k, phase in enumerate(frame.phases_radians, 1)]
    deltas, states = {}, {}
    for index, slot in enumerate(harmonic_slots(frame), 1):
        delta = f32(difference(phases[slot], f32(wrap(values[index - 1]))))
        deltas[slot] = delta
        states[slot] = values[index] - values[index - 1] - delta
    return phases, deltas, states


def predict(frame, previous, threshold=5.183628082275391, decay=.2):
    phases, _, _ = observe(frame)
    _, old_deltas, old_states = previous
    saved = frame.harmonic_envelope.values[1::2]
    values = [saved[0]]  # Phase origin remains an explicit observed input.
    for slot in harmonic_slots(frame):
        delta = f32(difference(phases[slot], f32(wrap(values[-1]))))
        state = old_states.get(slot, 0.0)
        if frame.frequencies_hz[slot] > 3500:
            state = 0.0
        else:
            old_delta = old_deltas.get(slot, 0.0)
            if abs(f32(delta - old_delta)) > threshold:
                state = f32(state + f32(old_delta - delta))
            state = math.copysign(max(0.0, f32(abs(state) - f32(decay))), state)
        values.append(f32(f32(delta + state) + values[-1]))
    values.append(values[-1])
    return values


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--parser-project', type=Path, required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    data = json.loads(args.manifest.read_text(encoding='utf-8'))
    manifest = data['manifest'] if isinstance(data, dict) else data
    if not isinstance(manifest, list) or not 1 <= len(manifest) <= 1000:
        parser.error('Require 1..1000 frame identifiers')
    paths = {p.stem: p for p in args.voice_root.rglob('*V4X*.ddi')}
    model_type = load_public_parser(args.parser_project)
    configurations = [(t, d) for t in (5.183628082275391, 5.654866695404053, 5.9690260887146)
                       for d in (.2, .4, f32(math.pi), f32(math.tau))]
    counts = [dict(frames=0, matches=0, points=0, point_matches=0, max_error=0.0) for _ in configurations]
    exceptions, banks, seeds, ordinary = [], [], 0, 0
    best_matches, best_max_error = 0, 0.0
    endpoint_mismatches, sparse_frames = 0, 0
    for bank in sorted({row['bank'] for row in manifest}):
        path = paths[bank]
        raw = path.read_bytes()
        model = model_type(raw)
        model.read()
        wanted = {(row['category'], row['part']) for row in manifest if row['bank'] == bank}
        indices = {'STA': 0, 'ART': 0}
        bank_frames, bank_matches, bank_points = 0, 0, 0
        for category, part in samples(model):
            if category not in indices:
                continue
            index = indices[category]
            indices[category] += 1
            if (category, index) not in wanted:
                continue
            previous = None
            for frame_index, ep in enumerate(part['epr']):
                offset = pointer(ep, raw)[0]
                frame = read_frame(path.with_suffix('.ddb'), offset)
                if not isinstance(frame, MainFrame):
                    previous = None
                    continue
                ordinary += 1
                saved_values = frame.harmonic_envelope.values[1::2]
                endpoint_mismatches += saved_values[0] != saved_values[1] or saved_values[-1] != saved_values[-2]
                sparse_frames += any(slot != i for i, slot in enumerate(harmonic_slots(frame)))
                current = observe(frame)
                if previous is None:
                    seeds += 1
                    previous = current
                    continue
                saved = frame.harmonic_envelope.values[1::2]
                errors = []
                for config, count in zip(configurations, counts):
                    predicted = predict(frame, previous, *config)
                    diff = [abs(a - b) for a, b in zip(saved[1:-1], predicted[1:-1])]
                    error = max(diff)
                    errors.append(error)
                    count['frames'] += 1
                    count['matches'] += error < 1e-4
                    count['points'] += len(diff)
                    count['point_matches'] += sum(v < 1e-4 for v in diff)
                    count['max_error'] = max(count['max_error'], error)
                bank_frames += 1
                bank_matches += errors[0] < 1e-4
                bank_points += len(saved) - 2
                best_matches += min(errors) < 1e-4
                best_max_error = max(best_max_error, min(errors))
                if errors[0] >= 1e-4 and len(exceptions) < 100:
                    best = min(range(len(errors)), key=errors.__getitem__)
                    exceptions.append(dict(bank=bank, category=category, part=index, index=frame_index,
                                           offset=hex(offset), default_error=errors[0],
                                           best_configuration=configurations[best], best_error=errors[best]))
                previous = current
        banks.append(dict(bank=bank, transitions=bank_frames, default_matches=bank_matches, points=bank_points))
        print(json.dumps(banks[-1]), flush=True)
    report = dict(dll_sha256=SHA256,
                  status='conditional transition audit; current origins and predecessor states observed',
                  ordinary_frames=ordinary, initial_state_frames=seeds, banks=banks,
                  endpoint_mismatches=endpoint_mismatches, sparse_harmonic_frames=sparse_frames,
                  best_of_native_branches=dict(matches=best_matches, max_error=best_max_error,
                                               caveat='branch selected using saved output; selection not independently recovered'),
                  configurations=[dict(threshold=t, decay=d, **count)
                                  for (t, d), count in zip(configurations, counts)], exceptions=exceptions)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report['configurations'], indent=2))


if __name__ == '__main__':
    main()
