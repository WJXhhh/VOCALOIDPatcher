#!/usr/bin/env python3
"""Observe reference ART region clocks, per-key layers and pitch2 candidates.

Read-only companion to verify_reference_ddi.py; run the integrity audit first.
Candidate mean agreement is statistical evidence, not a recovered algorithm.
"""

from __future__ import annotations

import argparse
import json
import math
import mmap
import statistics
import struct
import sys
from collections import Counter, defaultdict
from pathlib import Path

from analyze_reference_layers import walk_art
from verify_reference_ddi import AuditError, load_public_parser, pointer, require
import probe_frm2 as frame_tools


def summarize(values: list[float]) -> dict:
    return {'count': len(values), 'min': min(values), 'median': statistics.median(values),
            'max': max(values)} if values else {'count': 0}


def read_frame(data, offset: int) -> tuple[float, float]:
    frame_tools.require_range(offset, 28, len(data), 'FRM2 prefix')
    magic, size = struct.unpack_from('<4sI', data, offset)
    require(magic == b'FRM2' and size >= 32 and offset + size <= len(data), 'invalid FRM2')
    time = struct.unpack_from('<d', data, offset + 12)[0]
    mask = struct.unpack_from('<Q', data, offset + 20)[0]
    position = offset + 28
    if mask == frame_tools.MAIN_MASK:
        count = struct.unpack_from('<I', data, position)[0]
        position += 4 + count * 12
    else:
        require(mask == frame_tools.UNVOICED_MASK, 'unexpected STA/ART frame mask')
    frame_tools.require_range(position, 4, offset + size, 'F0')
    f0 = struct.unpack_from('<f', data, position)[0]
    require(math.isfinite(time) and math.isfinite(f0), 'nonfinite frame value')
    return time, f0


def region_clock_errors(times: list[float], groups: list[dict], rate: int) -> tuple[int, float]:
    count, maximum = 0, 0.0
    previous = 0
    for group in groups:
        start, end = group['start'], group['end']
        require(start == previous and start <= end <= len(times), 'invalid outer partition')
        for index in range(start, end):
            error = abs(times[index] * rate - (index - start) * 256)
            maximum = max(maximum, error)
            count += error > 1e-5
        previous = end
    require(previous == len(times), 'incomplete outer partition')
    return count, maximum


def voiced_mean(f0: list[float], indices) -> float | None:
    values = [1200 * math.log2(f0[i] / 440) for i in indices if f0[i] > 0]
    return statistics.fmean(values) if values else None


def analyze(ddi_path: Path, model_type: type) -> dict:
    require(ddi_path.stat().st_size <= 256 * 1024 * 1024, 'DDI exceeds size budget')
    raw = ddi_path.read_bytes()
    model = model_type(raw)
    model.read()
    voiced_phonemes = set(model.phdc_data['phoneme']['voiced'])
    layers = defaultdict(Counter)
    sta_rank_pitches = defaultdict(list)
    groups_by_arity = Counter()
    clock_checks = Counter()
    max_clock_error = 0.0
    layer_keys = Counter()
    empty_keys = []
    middle_lengths = Counter()
    candidate_errors = defaultdict(list)
    candidate_missing = Counter()
    finite_samples = Counter()
    no_pitch_samples = Counter()
    margin_examples = []
    worst_mean_examples = []
    units = []
    for unit in model.sta_data.values():
        key = (unit['phoneme'],)
        parts = list(unit['stap'].values())
        units.append(('STA', key, parts))
        suffix = key[0].split('#', 1)[1] if '#' in key[0] else 'plain'
        for rank, part in enumerate(sorted(parts, key=lambda p: p['pitch1'])):
            if part['pitch1'] > -3e38:
                sta_rank_pitches[f'{suffix}/layers{len(parts)}/rank{rank}'].append(part['pitch1'])
    for root in model.art_data.values():
        units.extend(('ART', key, parts) for key, parts in walk_art(root))
    with ddi_path.with_suffix('.ddb').open('rb') as stream:
        with mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as data:
            for category, key, parts in units:
                label = f'{category}/arity{len(key)}'
                layers[label][len(parts)] += 1
                layer_keys[label] += 1
                if not parts:
                    empty_keys.append(key)
                for part in parts:
                    values = [read_frame(data, pointer(e, raw)[0]) for e in part['epr']]
                    require(bool(values), 'sample has no frames')
                    times, f0 = map(list, zip(*values))
                    n = len(times)
                    if category == 'ART':
                        groups = part['frame_align']
                        groups_by_arity[f'{len(key)}/{len(groups)}'] += 1
                        if len(groups) == 4:
                            middle_lengths[f"{groups[1]['end']-groups[1]['start']}/{groups[2]['end']-groups[2]['start']}"] += 1
                        bad, maximum = region_clock_errors(times, groups, part['fs'])
                        clock_checks['ART_frames'] += n
                        clock_checks['ART_groups'] += len(groups)
                        clock_checks['ART_mismatching_frames'] += bad
                        max_clock_error = max(max_clock_error, maximum)
                        start, pcm = pointer(part['snd'], raw, 18)
                        core, _ = pointer(part['snd_start'].split('_')[0], raw, 18)
                        if pcm - n * 256 != 2048 or core - start != 2048:
                            margin_examples.append({'key': key, 'snd_offset': hex(start),
                                'frames': n, 'pcm_margin': pcm - n * 256,
                                'core_delta_bytes': core - start, 'pitch1': part['pitch1'],
                                'pitch2': part['pitch2'], 'groups': groups,
                                'clock_mismatches': bad})
                    else:
                        groups = [{'start': 0, 'end': n, 'start2': 0, 'end2': n}]
                    pitch = part['pitch2']
                    require(math.isfinite(pitch), 'nonfinite pitch2')
                    if pitch <= -3e38:
                        no_pitch_samples[label] += 1
                        continue
                    finite_samples[label] += 1
                    selections = {
                        'all_positive_f0': range(n),
                        'inner_positive_f0': [i for g in groups for i in range(g['start2'], g['end2'])],
                        'first_outer_positive_f0': range(groups[0]['start'], groups[0]['end']),
                        'last_outer_positive_f0': range(groups[-1]['start'], groups[-1]['end']),
                    }
                    if category == 'ART' and len(key) == len(groups):
                        selections['phdc_voiced_outer_positive_f0'] = [
                            i for phoneme, group in zip(key, groups) if phoneme in voiced_phonemes
                            for i in range(group['start'], group['end'])]
                    for name, indices in selections.items():
                        mean = voiced_mean(f0, indices)
                        if mean is None:
                            candidate_missing[f'{label}/{name}'] += 1
                        else:
                            error = abs(pitch - mean)
                            candidate_errors[f'{label}/{name}'].append(error)
                            if name == 'all_positive_f0' and error > 1:
                                worst_mean_examples.append({'key': key, 'error_cents': error,
                                    'pitch2': pitch, 'mean': mean})
    return {
        'bank': ddi_path.stem, 'key_counts': dict(layer_keys),
        'empty_art_keys': empty_keys,
        'layer_count_by_key_arity': dict(layers),
        'sta_pitch1_by_suffix_layer_count_rank': {k: summarize(v) for k, v in sta_rank_pitches.items()},
        'art_key_arity_alignment_groups': dict(groups_by_arity),
        'four_group_middle_lengths': dict(middle_lengths),
        'region_clocks': dict(clock_checks), 'max_clock_error_samples': max_clock_error,
        'short_margin_samples': margin_examples,
        'finite_pitch2_samples': dict(finite_samples), 'no_pitch_samples': dict(no_pitch_samples),
        'pitch2_candidate_abs_error_cents': {k: {**summarize(v),
            'within_0_01_cent': sum(e <= 0.01 for e in v)} for k, v in candidate_errors.items()},
        'pitch2_candidates_without_positive_f0': dict(candidate_missing),
        'largest_all_f0_mean_errors_limit_8': sorted(worst_mean_examples,
            key=lambda x: x['error_cents'], reverse=True)[:8],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('ddi', type=Path)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    args = parser.parse_args()
    try:
        result = analyze(args.ddi, load_public_parser(args.ddb_tools))
    except (AuditError, frame_tools.ProbeError, OSError, ValueError, AssertionError,
            KeyError, TypeError, struct.error) as error:
        print(f'analysis failed: {type(error).__name__}: {error}', file=sys.stderr)
        return 2
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
