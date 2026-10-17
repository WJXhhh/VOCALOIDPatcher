"""Verify bounded DRS collection statistics with entirely owned frame records.

Reuses the independently verified float32 regression model, not its native kernel.
Does not infer a DDI producer from collection statistic outputs.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path

from audit_frm2_numeric import D, F, I, Native, P, Q, SHA256, f32
from verify_pitch2_native_mean import SENTINEL
from verify_vqm_bit7_spectral_slope import regression, same


def cents(value, encoded):
    value = f32(value)
    if encoded:
        return value
    return f32(math.log(value / 440.) * 1200. / math.log(2.)) if value > 0 else SENTINEL


def run(dll):
    native = Native(dll)
    invoke = native.fn(0xC86A0, None, P, D, I, I, I)
    rows, numeric_examples = [], []
    for count in (1, 2, 3, 4, 5, 8, 9):
        for shape in ('regular', 'sparse', 'no_features', 'no_pitch', 'constant_time', 'missing_value'):
            for flags in ('active', 'inactive', 'outer_only'):
                frames, source_regions = [], []
                times, pitches, levels = [], [], []
                for k in range(count):
                    frame = native.alloc(0x148)
                    time = .125 if shape == 'constant_time' else .123 + k * .027 + (k % 3) * .003
                    encoded = k % 2 == 0
                    pitch = (-900. + k * 125.) if encoded else (220. if k % 4 == 1 else 880.)
                    if shape == 'no_pitch' or (shape == 'sparse' and k % 3 == 0):
                        pitch, encoded = (SENTINEL, True) if k % 2 else (0., False)
                    level = f32(-2.25 + k * .153 + (k % 3) * .041)
                    if shape == 'missing_value' and k % 3 == 0:
                        level = 10000.
                    present = shape != 'no_features' and not (shape == 'sparse' and k % 3 == 1)
                    D.from_address(frame + 0x48).value = time
                    Q.from_address(frame + 0x50).value = ((1 << 31) if encoded else 0) | ((1 << 13) if flags != 'inactive' else 0)
                    F.from_address(frame + 0xF0).value = pitch
                    if present:
                        feature = native.alloc(0x228)
                        I.from_address(feature).value = (1 << 5) if flags == 'active' else 0
                        F.from_address(feature + 0x18).value = level
                        P.from_address(frame + 0x128).value = feature
                        source_regions.append((feature, 0x228))
                    frames.append(frame)
                    source_regions.append((frame, 0x148))
                    times.append(f32(time))
                    pitches.append(cents(pitch, encoded))
                    levels.append(level if present else None)
                collection = native.alloc(0x280)
                P.from_address(collection + 0x20).value = native.array(P, frames)
                output = native.alloc(32 + 24 + 32)
                P.from_address(collection + 0x268).value = output + 32
                sources_before = [c.string_at(at, size) for at, size in source_regions]
                intervals = [(0, count - 1)]
                if count >= 4:
                    intervals.append((1, count - 2))
                for start, end in intervals:
                    selected = [k for k in range(start, end + 1) if pitches[k] > SENTINEL]
                    pitch_x, pitch_y = [times[k] for k in selected], [pitches[k] for k in selected]
                    feature_indices = [k for k in range(start, end + 1) if levels[k] is not None]
                    level_x = [times[k] for k in feature_indices]
                    level_y = [levels[k] for k in feature_indices]
                    total = 0.
                    for value in pitch_y:
                        total = f32(total + value)
                    expected = {
                        0x5D: f32(total / len(pitch_y)) if pitch_y else 0.,
                        0x5E: f32(pitch_y[-1] - pitch_y[0]) if pitch_y else 0.,
                        0x5F: f32((D.from_address(frames[end] + 0x48).value - D.from_address(frames[start] + 0x48).value) + .01),
                        0x61: regression(pitch_x, pitch_y)[1],
                        0x62: regression(level_x, level_y)[1] if level_y else 0.,
                    }
                    for stat_id, value in expected.items():
                        c.memset(output, 0xA5, 88)
                        before = c.string_at(output, 88)
                        invoke(collection, .01, start, end, stat_id)
                        slot = {0x5D: 0, 0x5E: 4, 0x5F: 8, 0x61: 16, 0x62: 20}[stat_id]
                        actual = F.from_address(output + 32 + slot).value
                        assert same(actual, value), (count, shape, flags, start, end, hex(stat_id), actual, value)
                        if count == 5 and flags == 'active' and start == 0:
                            numeric_examples.append(dict(shape=shape, statistic=hex(stat_id),
                                actual=actual if math.isfinite(actual) else str(actual),
                                independently_expected=value if math.isfinite(value) else str(value)))
                        after = c.string_at(output, 88)
                        assert before[:32 + slot] == after[:32 + slot]
                        assert before[36 + slot:] == after[36 + slot:]
                        rows.append(dict(count=count, shape=shape, flags=flags, start=start, end=end,
                                         statistic=hex(stat_id), finite_exact=math.isfinite(actual),
                                         nonfinite_class_match=not math.isfinite(actual),
                                         only_expected_slot_written=True))
                    c.memset(output, 0xA5, 88)
                    before = c.string_at(output, 88)
                    invoke(collection, .01, start, end, 0x60)
                    assert c.string_at(output, 88) == before
                assert sources_before == [c.string_at(at, size) for at, size in source_regions]
    return dict(dse_sha256=SHA256, consumer_rva='0xc86a0', caller_rva='0xc84f0',
                reused_independent_model='verify_vqm_bit7_spectral_slope.regression',
                cases=rows, numeric_examples=numeric_examples, summary=dict(comparisons=len(rows),
                    finite_float32_exact=sum(row['finite_exact'] for row in rows),
                    nonfinite_class_matches=sum(row['nonfinite_class_match'] for row in rows),
                    source_records_and_output_guards_unchanged=True),
                limits=['Owned records exercise the DRS collection ABI, not V4X original maker inputs.',
                        'No DDI pitch1/pitch2 assignment or statistic persistence is established.',
                        'Nonfinite results are compared by class, not NaN payload bits.',
                        'Counts and closed intervals are valid; malformed indices are out of scope.'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll)
    result['script_sha256'] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result['summary']))


if __name__ == '__main__':
    main()
