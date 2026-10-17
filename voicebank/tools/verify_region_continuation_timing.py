"""Independent timing model for the owned, equal-region two-a-note fixture.

Expected ranges depend only on input durations, gap, and available edges.
They never read native output ranges or work-state allocation lengths.
This is not a timing model for arbitrary voicebanks or EVEC sequences.
"""
import argparse
import json
import math
from pathlib import Path
from audit_frm2_numeric import f32
from verify_region_duration_allocation import predict as allocate
from verify_region_note_continuation import verify as native_sequences


def predict(duration, gap, self_edge, stationary_chunks=None):
    # Fixture: 10 ms hop; each ART region outer=50, inner=40;
    # PHDC byte 0x38=0. DSE's rounded 0.1161/hop limit is 12.
    limit = int(0.1160999983549118 / .01 + .5)
    right, left = (allocate([40, 40], [50, 50], [0, 0], duration)
                   if duration <= 100 else [40, 40])
    ranges = [[0, 50]]
    lengths = [0]
    sources = [-1]

    def append(left_length, right_length, source):
        size = left_length + right_length
        if size:
            begin = ranges[-1][1]
            ranges.append([begin, begin+size])
            lengths.append(left_length)
            sources.append(source)

    append(50, right, 1)
    remaining = duration-right-left
    first_chunk = True
    chunk_index = 0
    while remaining:
        # Disabled stationary variation returns base offsets 25 and 15.
        # First chunk uses the whole 100-frame part unless that leaves
        # fewer than 12 frames, in which case it halves the remainder.
        if stationary_chunks is not None:
            size = stationary_chunks[chunk_index]['length']
            chunk_index += 1
        else:
            size = remaining
            if remaining > 100:
                size = (int(remaining*.5+.5) if remaining-100 < limit else 100) if first_chunk else 60
        append(0, size, 0)
        remaining -= size
        first_chunk = False
    pivot = ranges[-1][1]
    append(left, limit, 2)
    first = [r[:] for r in ranges]

    if gap <= limit:
        # Old tail remains in the ring with a zero-length range. Rebuild
        # the preceding work list with either a->a or a->Sil->a.
        ranges[-1][1] = pivot
        count = 3 if self_edge else 5
        pieces = allocate([40]*count, [50]*count, [0]*count, duration+gap)
        append(pieces[0], pieces[1], 1)
        if not self_edge:
            append(pieces[2], pieces[3], 2)
        append(pieces[-1], 50, 3 if self_edge else 1)
    else:
        # The old tail is re-emitted unchanged. Its 12-frame margin and
        # the cursor at the previous note end are distinct quantities.
        start = 100 + duration + gap
        onset_left = min(50, gap)
        silence = max(0, start-onset_left-ranges[-1][1])
        append(0, silence, -1)
        append(onset_left, 50, 1)
    append(50, limit, 2)
    return first, ranges, lengths, sources


def verify(dll, long_notes=False):
    gaps = list(range(16)) + list(range(49, 64)) + [100]
    report = native_sequences(dll, gaps)
    extra_ms = [20, 30, 390, 400, 790, 800, 810, 990]
    extra = native_sequences(dll, [0, 12, 13, 63], extra_ms)
    report['cases'].extend(extra['cases'])
    report['sequences'] += extra['sequences']
    report['native_commits'] += extra['native_commits']
    long_ms = [1010, 1190, 1200, 1210, 1490, 1500, 1510, 1790, 1800, 1810,
               1900, 1910, 1920, 2490, 2500, 2510, 3990, 4000]
    if long_notes:
        extra = native_sequences(dll, [0, 12, 13, 63], long_ms)
        report['cases'].extend(extra['cases'])
        report['sequences'] += extra['sequences']
        report['native_commits'] += extra['native_commits']
    for row in report['cases']:
        duration = math.floor(f32(f32(f32(row['milliseconds']/1000)*44100)/441))
        assert duration == row['duration']
        first, ranges, lengths, sources = predict(duration, row['gap'], row['self_edge'])
        actual = row['after']
        assert row['first_ranges'] == first, row
        for key, expected in [('ranges', ranges), ('left_lengths', lengths),
                              ('source_indices', sources)]:
            assert actual[key] == expected, (row['self_edge'], row['gap'], duration,
                                             key, actual[key], expected)
        remaining = max(0, duration-80) if duration > 100 else 0
        stationary = []
        for index, source in enumerate(sources):
            if source != 0:
                continue
            offset = 0 if not stationary else (25 if remaining > 100 else min(25, 100-remaining))
            stationary.append(dict(item=index, source_offset=offset,
                                   requested_length=duration-80, direct=1))
            remaining -= ranges[index][1]-ranges[index][0]
        assert remaining == 0
        assert actual['stationary_items'] == stationary, (row, stationary)
        row['independent_timing_verified'] = True
    report['scope'] = ('Complete item ranges, left lengths, and sources predicted from inputs '
                       'for this equal-region, unit-coefficient two-a-note fixture; '
                       'not arbitrary material, EVEC, or acoustic rendering')
    report['gaps'] = gaps
    report['additional_duration_matrix'] = dict(milliseconds=extra_ms, gaps=[0, 12, 13, 63])
    if long_notes:
        report['long_duration_matrix'] = dict(milliseconds=long_ms, gaps=[0, 12, 13, 63])
        report['resolved_fixture_boundary'] = ('RVA 0x1a9f46 dereferenced the absent state+0x4098 '
            'stationary index. Native 0x1a74a0 now builds it from owned material. '
            'Stationary variation remains disabled; STA acoustic consumption is not verified.')
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--long-notes', action='store_true')
    args = parser.parse_args()
    result = verify(args.dll, args.long_notes)
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({key: result[key] for key in ['sequences', 'native_commits']}))
