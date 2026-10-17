"""Verify a located feature-bit7 producer and its limits for saved V4X fields.

Owned finite Frame inputs exercise the native leaf and preserve every other
feature, including bit17. Reference harmonic arrays stay in memory. The leaf
is a categorization-stage computation, not the original VQM authoring entry.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import struct

from audit_frm2_numeric import F, I, Native, P, Q, SHA256, f32
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_indexed_temporal_features import guarded_feature


THRESHOLDS = [f32(.3), 1., 10.]


def deviation(frequencies, stored_pitch, cents=False, envelope_db=None):
    if stored_pitch <= 0:
        return 1.
    f0 = f32(f32(math.pow(2., f32(stored_pitch / 1200.))) * 440.) if cents else stored_pitch
    expected = f0
    count = 0
    total = power = 0.
    for hz in frequencies[:11]:
        if hz <= 0:
            continue
        distance = abs(f32(expected - hz))
        total = f32(total + f32(distance / f0))
        expected = f32(expected + f0)
        if envelope_db is not None:
            # Owned constant ENV; exp scale is part of the reviewed DRS leaf.
            amplitude = f32(math.exp(envelope_db * .1151292546497) * 32768.)
            power = f32(power + amplitude)
        count += 1
    if not count:
        return 1.
    value = f32(total / count)
    return f32(value / power) if envelope_db is not None else value


def memberships(value, initial):
    result = list(initial)
    index = 0
    while index < 3 and not value < THRESHOLDS[index]:
        index += 1
    if index == 0:
        result[0] = 1.
    elif index == 3:
        result[2] = 1.
    else:
        width = f32(THRESHOLDS[index] - THRESHOLDS[index - 1])
        distance = f32(value - THRESHOLDS[index - 1])
        result[index] = f32(distance / width)
        result[index - 1] = f32(f32(width - distance) / width)
    return result


def invoke(native, frequencies, stored_pitch, cents=False, envelope_db=None, seeded=False):
    frame = native.alloc(0x328)
    native.frame_ctor(frame)
    base, feature = guarded_feature(native)
    P.from_address(frame + 0x128).value = feature
    mask = 8192 | 512 | 2 | ((1 << 31) if cents else 0) | (128 if envelope_db is not None else 0)
    Q.from_address(frame + 0x50).value = mask
    I.from_address(frame + 0x58).value = len(frequencies)
    P.from_address(frame + 0x60).value = native.array(F, frequencies)
    F.from_address(frame + 0xf0).value = stored_pitch
    if envelope_db is not None:
        P.from_address(frame + 0x108).value = native.env(
            [(0., envelope_db), (.5, envelope_db), (1., envelope_db)], 1, 0, (-200., 10., 0.))
    initial = [f32(.125 + k / 8.) for k in range(3)] if seeded else [0.] * 3
    target_base = native.alloc(160)
    c.memset(target_base, 0xa5, 32)
    target = target_base + 32
    c.memset(target + 96, 0x5a, 32)
    c.memmove(target + 0x4c, struct.pack('<3f', *initial), 12)
    before_target = c.string_at(target_base, 160)
    before = c.string_at(base, 0x228 + 64)
    before_frame = c.string_at(frame, 0x328)
    assert native.fn(0x6c3c0, Q, P, P, F)(frame, target, 22050.) == 0
    actual = F.from_address(feature + 0x20).value
    expected = deviation(frequencies, stored_pitch, cents, envelope_db)
    assert struct.pack('<f', actual) == struct.pack('<f', expected), ('deviation', actual, expected)
    expected_members = memberships(expected, initial)
    assert c.string_at(target + 0x4c, 12) == struct.pack('<3f', *expected_members)
    after = c.string_at(base, 0x228 + 64)
    assert all(a == b for k, (a, b) in enumerate(zip(before, after))
               if not 32 + 0x20 <= k < 32 + 0x24)
    after_target = c.string_at(target_base, 160)
    assert all(a == b for k, (a, b) in enumerate(zip(before_target, after_target))
               if not 32 + 0x4c <= k < 32 + 0x58)
    assert c.string_at(frame, 0x328) == before_frame
    return actual


def verify(dll, sequences):
    native = Native(dll)
    cases = [(name, frequencies, pitch, cents) for name, frequencies, pitch, cents in (
        ('zero_pitch', [200., 400.], 0., False),
        ('negative_cents', [220., 440.], -1200., True),
        ('no_slots', [], 200., False),
        ('missing_slots', [0., -1., 0.], 200., False),
        ('harmonic', [200. * k for k in range(1, 15)], 200., False),
        ('ignore_twelfth', [200. * k for k in range(1, 12)] + [10000.], 200., False),
        ('compress_missing', [0., 200., -1., 410., 0., 600.], 200., False),
        ('decoded_cents', [880., 1760., 2660.], 1200., True),
        ('detuned', [210., 420., 630.], 200., False))]
    for value in (.3, .65, 1., 5.5, 10., 20.):
        cases.append((f'membership_{value}', [f32(200. * (1. + value))], 200., False))
    owned = []
    for name, frequencies, pitch, cents in cases:
        for env_db in (None, 0., -6.):
            for seeded in (False, True):
                actual = invoke(native, frequencies, pitch, cents, env_db, seeded)
                owned.append(dict(name=name, slots=len(frequencies), cents=cents,
                    constant_envelope_db=env_db, seeded_membership=seeded,
                    deviation=actual, exact_deviation_and_membership=True,
                    frame_unchanged=True, only_bit7_and_memberships_written=True,
                    bit17_and_guards_preserved=True))
    reference = []
    for index, sequence in enumerate(sequences):
        negatives = matches = 0
        saved_values = []
        for frame in sequence['frames']:
            # Explicit raw-Hz/no-ENV stage inputs: do not claim this constructs
            # the saved frame's encoded/caller protocol or reproduces its maker.
            actual = invoke(native, [f32(hz) for hz, _ in frame['components']], f32(frame['f0']))
            saved = frame['features'][7]
            saved_values.append(saved)
            negatives += saved < 0.
            matches += struct.pack('<f', actual) == struct.pack('<f', saved)
        reference.append(dict(sequence=index, frames=len(sequence['frames']),
            saved_negative_bit7_frames=negatives, raw_hz_no_env_native_matches_saved=matches,
            saved_bit7_unique=len(set(saved_values)), saved_bit7_min=min(saved_values),
            saved_bit7_max=max(saved_values),
            raw_hz_no_env_leaf_exact=True))
    return dict(owned_cases=owned, reference_cases=reference,
        scope='Actual categorization helper 6c3c0, positive raw-Hz component inputs, '
              'explicit stored-pitch/cents gate and optional owned constant E7. '
              'Not PCM-to-harmonics and not original VQM maker. Commercial comparison '
              'changes protocol to raw Hz without ENV deliberately and only rules '
              'out directly identifying saved negative bit7 with this nonnegative '
              'deviation under these valid conditions. No claim about invalid '
              'envelopes, other producers or official feature names.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'output'):
        parser.add_argument('--' + name, required=True, type=Path)
    args = parser.parse_args()
    parser_hash, ddi_hash, sequences = load_sequences(args.manifest, args.voice_root, args.ddb_tools)
    report = verify(args.dll, sequences)
    names = ('verify_vqm_harmonic_deviation.py', 'audit_frm2_numeric.py',
             'verify_vqm_indexed_temporal_features.py', 'verify_vqm_bit16_spectral_power.py',
             'audit_v4x_vqm_fields.py', 'verify_reference_ddi.py')
    report.update(dse_sha256=SHA256, parser_sha256=parser_hash, ddi_sha256=ddi_hash,
        native_rva='0x6c3c0', physical_feature_offset='0x20', serialized_feature_bit=7,
        membership_thresholds=THRESHOLDS,
        source_sha256={name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                       for name in names})
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(owned_cases=len(report['owned_cases']),
        reference_frames=sum(x['frames'] for x in report['reference_cases']), exact=True)))


if __name__ == '__main__':
    main()
