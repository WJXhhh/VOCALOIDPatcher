"""Table-driven STA variation through full owned note commits, without hooks.

The pinned DLL's static table is input data; it is read in memory, never exported.
Independent arithmetic/index and segmentation models predict native results.
"""
import argparse
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
from audit_frm2_numeric import Native, P, I, F, D, B, SHA256, f32
from region_commit_material import connected
from verify_region_event_production import run
from verify_region_continuation_timing import predict

HEAD_AMPLITUDE = f32(.2)-2**-26
TAIL_BASE = f32(.15)-2**-25
TAIL_AMPLITUDE = f32(.1)-5*2**-27


class TableVariation:
    def __init__(self, table, index, last_key=0, enabled=True):
        self.table, self.index, self.last_key, self.enabled = table, index, last_key, enabled

    def value(self, base, amplitude, phase):
        if not self.enabled:
            return f32(base)
        key = int((phase-math.floor(f32(phase)))*1024)
        assert 0 <= key < 1024, 'Model inputs must stay within the verified phase domain'
        self.index = self.index+1 if key == self.last_key else key
        if self.index >= 1024:
            self.index = 0
        self.last_key = key
        return f32(f32(base)+f32(f32(amplitude)*self.table[self.index]))


def chunks(length, variation, part_length=100, limit=12):
    def rounded(x):
        return int(f32(x+(.5 if x >= 0 else -.5)))
    remaining, result = length, []
    while remaining:
        head = rounded(f32(variation.value(.25, HEAD_AMPLITUDE, 64.)*part_length))
        tail = rounded(f32(variation.value(TAIL_BASE, TAIL_AMPLITUDE, 64.)*part_length))
        begin = 0
        if remaining > part_length:
            if not result:
                end = rounded(f32(remaining*.5)) if remaining-part_length < limit else part_length
            else:
                begin, end = head, part_length-tail
                if end-begin <= limit <= part_length:
                    extra = limit-(end-begin)
                    begin = max(0, begin-rounded(f32(extra*.5)))
                    end = min(part_length, end+extra-head+begin)
                leftover = remaining-(end-begin)
                if leftover < limit:
                    new_begin = max(0, begin-rounded(f32(leftover*.5)))
                    end = min(part_length, end+leftover-(begin-new_begin))
                    begin = new_begin
        else:
            if result:
                begin = min(head, part_length-remaining)
            end = begin+remaining
        size = end-begin
        assert 0 < size <= remaining
        result.append(dict(source_offset=begin, length=size))
        remaining -= size
    return result


def verify(dll):
    n = Native(dll)
    raw = c.string_at(n.lib._handle+0xa12990, 4096)
    table = list((F*1024).from_buffer_copy(raw))
    native = n.fn(0x1cd270, F, P, F, F, D)
    # Actual caller constants differ by an ULP from rounded decimal literals.
    head_amplitude = F.from_address(n.lib._handle+0x5bf098).value
    tail_base = F.from_address(n.lib._handle+0x5bf07c).value
    tail_amplitude = F.from_address(n.lib._handle+0x249450).value
    assert head_amplitude == HEAD_AMPLITUDE
    assert tail_base == TAIL_BASE
    assert tail_amplitude == TAIL_AMPLITUDE
    scalar_count = 0
    for enabled in [False, True]:
        state = n.alloc(16)
        B.from_address(state).value = int(enabled)
        B.from_address(state+1).value = 1
        model = TableVariation(table, 0, enabled=enabled)
        phases = [64.]*1030 + [0., .25, .25, .5, .999, 1., 1.25, 1.25]
        for phase in phases:
            for base, amplitude in [(.25, head_amplitude), (tail_base, tail_amplitude)]:
                expected = model.value(base, amplitude, phase)
                actual = native(state, base, amplitude, phase)
                assert actual == expected
                assert [I.from_address(state+o).value for o in [4, 8]] == [model.last_key, model.index]
                scalar_count += 1
    rows = []
    milliseconds = [1010, 1810, 1900, 1910, 1920, 2490, 2500, 2510, 3990, 4000]
    for initial_index in [0, 255, 511, 1022, 1023]:
        for self_edge in [False, True]:
            for gap in [0, 12, 13, 63]:
                edges = [('Sil', 'a'), ('a', 'Sil')] + ([('a', 'a')] if self_edge else [])
                def build(native, state, *args):
                    material = connected(native, state, *args, edges=edges)
                    B.from_address(state+0x40a8).value = 1
                    B.from_address(state+0x40a9).value = 1
                    I.from_address(state+0x40b0).value = initial_index
                    material['next_gap'] = gap
                    return material
                report = run(dll, True, False, True, build, [['a']], milliseconds)
                for row in report['rows']:
                    if 'commit' not in row:
                        continue
                    duration = row['duration']
                    model = TableVariation(table, initial_index)
                    expected_chunks = chunks(duration-80, model)
                    first, ranges, lengths, sources = predict(duration, gap, self_edge, expected_chunks)
                    commit, after = row['commit'], row['commit']['continuation']
                    assert commit['item_ranges'] == first
                    for key, expected in [('ranges', ranges), ('left_lengths', lengths),
                                          ('source_indices', sources)]:
                        assert after[key] == expected, (initial_index, duration, gap, key, after[key], expected)
                    sta_indices = [j for j, source in enumerate(sources) if source == 0]
                    expected_sta = [dict(item=j, source_offset=chunk['source_offset'],
                                         requested_length=duration-80, direct=1)
                                    for j, chunk in zip(sta_indices, expected_chunks)]
                    assert len(sta_indices) == len(expected_chunks)
                    assert after['stationary_items'] == expected_sta
                    assert after['variation_state'] == [model.last_key, model.index]
                    rows.append(dict(initial_index=initial_index, self_edge=self_edge, gap=gap,
                                     milliseconds=row['milliseconds'], slot=row['slot'], pitch=row['pitch'],
                                     split=row['split'], stationary_chunks=expected_chunks,
                                     ranges=ranges, final_variation_state=after['variation_state']))
    return dict(dse_sha256=SHA256, table_sha256=hashlib.sha256(raw).hexdigest(),
                scalar_checks=scalar_count, sequences=len(rows), native_commits=2*len(rows), cases=rows,
                scope='Table mode index/state and full owned two-note timing with varied STA offsets; '
                      'runtime static table is input data, not exported. Random mode and '
                      'full acoustic consumption are not verified.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = verify(args.dll)
    args.output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({key: result[key] for key in ['scalar_checks', 'sequences', 'native_commits']}))
