"""Submit another owned note without resetting the existing native work state."""
import ctypes as c
import struct
import argparse
import json
from pathlib import Path
from audit_frm2_numeric import P, I, B, F


def continue_note(n, state, ring, items, consume, material, first_slot, start):
    assert I.from_address(state+0x7a8).value == 1-first_slot
    old = []
    for j in range(I.from_address(ring+0x34).value):
        item = items+j*0x140
        old.append((I.from_address(item).value, I.from_address(item+4).value,
                    P.from_address(item+0x20).value, I.from_address(item+8).value,
                    B.from_address(item+0x12c).value))
    messages = [(99, 0x50), (98, 2), (6, 69), (98, 3), (6, 64),
                (98, 4), (6, 7), (38, 104), # 1000 ms
                (98, 0x12), (6, 1), (98, 0x13), (6, ord('a')), (38, 64),
                (98, 127), (6, 127)]
    payload = c.create_string_buffer(b''.join(struct.pack('<IBBBB', 0, 0xb0, cc, value, 0)
                                             for cc, value in messages))
    batch = n.alloc(16)
    I.from_address(batch).value = len(messages)
    P.from_address(batch+8).value = c.addressof(payload)
    consume(state, start, batch)
    count = I.from_address(ring+0x34).value
    assert 0 < count < 16
    ranges = [[I.from_address(items+j*0x140+o).value for o in [0, 4]] for j in range(count)]
    assert all(a <= b for a, b in ranges), ranges
    assert all(ranges[j][1] == ranges[j+1][0] for j in range(count-1))
    assert I.from_address(state+0x7a8).value == first_slot
    from verify_region_output_mapping import predict
    position_checks = []
    frames = P.from_address(ring).value
    for j, (begin, end) in enumerate(ranges):
        item = items+j*0x140
        part = P.from_address(item+0x20).value
        if part not in material['parts'][1:] or begin == end:
            continue
        left = I.from_address(item+8).value
        reverse = B.from_address(item+0x12c).value
        source_end = end
        retained = j < len(old) and old[j][0] == begin and old[j][2] == part and end <= old[j][1]
        if retained:
            _, source_end, _, left, reverse = old[j]
        groups = [(0, 50, 5, 45), (50, 100, 55, 95)][::(-1 if reverse else 1)]
        expected = (predict(groups[0], left, 0, reverse) +
                    predict(groups[1], source_end-begin-left, 1, reverse))[:end-begin]
        actual = [F.from_address(frames+(t % 1024)*0x18+8).value for t in range(begin, end)]
        assert len(expected) == len(actual)
        error = max(abs(a-b) for a, b in zip(expected, actual))
        assert error < 1e-5, (j, begin, end, left, retained, error, actual[:8], expected[:8])
        position_checks.append(dict(item=j, retained=retained, frames=len(actual), max_error=error))
    return dict(start=start, duration=100, ranges=ranges,
                variation_state=[I.from_address(state+0x40a8+offset).value for offset in [4, 8]],
                stationary_items=[dict(item=j, source_offset=I.from_address(items+j*0x140+0x94).value,
                                       requested_length=I.from_address(items+j*0x140+0x118).value,
                                       direct=B.from_address(items+j*0x140+0x98).value)
                                  for j in range(count)
                                  if P.from_address(items+j*0x140+0x20).value == material['parts'][0]],
                left_lengths=[I.from_address(items+j*0x140+8).value for j in range(count)],
                position_checks=position_checks,
                source_indices=[material['parts'].index(P.from_address(items+j*0x140+0x20).value)
                                if P.from_address(items+j*0x140+0x20).value in material['parts'] else -1
                                for j in range(count)],
                tokens=[[I.from_address(items+j*0x140+o).value for o in [0x10,0x14]] for j in range(count)],
                reverse=[B.from_address(items+j*0x140+0x12c).value for j in range(count)],
                final_slot=first_slot)


def verify(dll, gaps=(0, 20, 100), milliseconds=(10, 125, 1000)):
    from audit_frm2_numeric import SHA256
    from region_commit_material import connected
    from verify_region_event_production import run
    rows = []
    for self_edge in [False, True]:
        for gap in gaps:
            edges = [('Sil', 'a'), ('a', 'Sil')] + ([('a', 'a')] if self_edge else [])
            def build(*args):
                material = connected(*args, edges=edges)
                material['next_gap'] = gap
                return material
            report = run(dll, True, False, True, build, [['a']], milliseconds)
            for row in report['rows']:
                if 'commit' not in row:
                    continue
                after = row['commit']['continuation']
                used_self = 3 in after['source_indices']
                assert not used_self or self_edge
                for index, tokens in zip(after['source_indices'], after['tokens']):
                    if index == 3:
                        assert tokens == [1, 1]
                if self_edge and gap == 0 and row['milliseconds'] == 1000:
                    assert used_self
                rows.append(dict(self_edge=self_edge, gap=gap, slot=row['slot'],
                                 pitch=row['pitch'], milliseconds=row['milliseconds'], duration=row['duration'],
                                 split=row['split'], first_ranges=row['commit']['item_ranges'],
                                 after=after))
    assert len(rows) == 24*len(milliseconds)*len(gaps)
    return dict(dse_sha256=SHA256, sequences=len(rows), native_commits=2*len(rows), cases=rows,
                scope='Two native notes on retained state; both-note frame positions verified; '
                      'retained positions use original allocation before truncation; '
                      'native item timing is observed, '
                      'not a complete independent timing model or acoustic render')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = verify(args.dll)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: result[key] for key in ['sequences', 'native_commits']}))
