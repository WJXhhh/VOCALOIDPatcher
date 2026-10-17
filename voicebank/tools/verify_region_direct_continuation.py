"""Submit a second owned note through the direct-tree event path."""
import argparse
import ctypes as c
import hashlib
import json
import struct
from pathlib import Path

from audit_frm2_numeric import P, I, F, B, SHA256
from region_commit_material import connected
from verify_region_event_production import run


def continue_note(n, state, ring, items, consume, material, first_slot, first_duration,
                  second_control3, gap, pitch_delta=0, inspect_global=False,
                  inspect_allocation=False):
    second_slot = 1 - first_slot
    assert I.from_address(state + 0x7a8).value == second_slot
    first_note = state + 0x58 + first_slot * 0x3a8
    second_note = state + 0x58 + second_slot * 0x3a8
    first_pitch = F.from_address(first_note + 8).value
    ring_submode_before = I.from_address(ring + 0x7c).value
    def global_descriptor():
        descriptor = n.lib._handle + 0xa971c0
        unit = P.from_address(descriptor + 0x410).value
        part = P.from_address(descriptor + 0x810).value
        snapshot = dict(count=I.from_address(descriptor + 0xc).value,
                    left=c.string_at(descriptor + 0x10, 16).split(b'\0')[0].decode('ascii', 'replace'),
                    right=c.string_at(descriptor + 0x210, 16).split(b'\0')[0].decode('ascii', 'replace'),
                    unit_edge=next((f'{a}→{b}' for (a, b), node in material.get('tree_units', {}).items()
                                    if node == unit), None),
                    part_index=material['parts'].index(part) if part in material['parts'] else -1)
        if inspect_allocation:
            snapshot['allocated_sides'] = [I.from_address(n.lib._handle + offset).value
                                           for offset in (0xa986d0, 0xa986d4)]
        return snapshot
    descriptor_before = global_descriptor() if inspect_global else None
    before = I.from_address(ring + 0x34).value
    assert before == 1
    assert [I.from_address(items + offset).value for offset in (0, 4)] == [0, 200]
    assert n.fn(0x1b0310, B, P)(second_note) == 0

    start = 100 + first_duration + gap
    midi_pitch = int(first_pitch / 100) + 69 + pitch_delta
    assert 0 <= midi_pitch <= 127
    messages = [(99, 0x50), (98, 2), (6, midi_pitch),
                (98, 3), (6, second_control3),
                (98, 4), (6, 0), (38, 100),
                (98, 0x12), (6, 1),
                (98, 0x13), (6, ord('a')), (38, 64),
                (98, 127), (6, 127 if second_slot else 0)]
    payload = c.create_string_buffer(b''.join(
        struct.pack('<IBBBB', 0, 0xb0, cc, value, 0) for cc, value in messages))
    batch = n.alloc(16)
    I.from_address(batch).value = len(messages)
    P.from_address(batch + 8).value = c.addressof(payload)
    consume(state, start, batch)
    count = I.from_address(ring + 0x34).value
    assert 0 <= count <= 16
    result = dict(start=start, gap=gap, second_control3=second_control3,
                second_start=I.from_address(second_note).value,
                second_duration=I.from_address(second_note + 4).value,
                second_pitch=F.from_address(second_note + 8).value,
                second_aux=F.from_address(second_note + 0xc).value,
                work_items_before=before, work_items_after=count,
                note_slot_after=I.from_address(state + 0x7a8).value,
                work_slot_after=I.from_address(state + 0x4058).value,
                ranges=[[I.from_address(items + j*0x140 + offset).value for offset in (0, 4)]
                        for j in range(count)],
                material_indices=[material['parts'].index(P.from_address(items + j*0x140 + 0x20).value)
                                  if P.from_address(items + j*0x140 + 0x20).value in material['parts']
                                  else -1 for j in range(count)],
                tokens=[[I.from_address(items + j*0x140 + offset).value for offset in (0x10, 0x14)]
                        for j in range(count)],
                reverse=[B.from_address(items + j*0x140 + 0x12c).value for j in range(count)],
                pending=B.from_address(state + 0x7b8).value)
    if pitch_delta or ring_submode_before:
        result['pitch_delta'] = pitch_delta
        result['ring_submode_before'] = ring_submode_before
        result['ring_submode_after'] = I.from_address(ring + 0x7c).value
    if inspect_global:
        result['global_descriptor_before'] = descriptor_before
        result['global_descriptor_after'] = global_descriptor()
    return result


def verify(dll: Path, edges, second_control3: int, gap: int, ring_submode: int = 0,
           pitch_delta: int = 0, inspect_global: bool = False,
           inspect_allocation: bool = False) -> dict:
    def build(*args):
        material = connected(*args, edges=edges, tree=True)
        material['direct_followup'] = (
            lambda n, state, ring, items, consume, current, slot, duration:
            continue_note(n, state, ring, items, consume, current, slot, duration,
                          second_control3, gap, pitch_delta, inspect_global,
                          inspect_allocation))
        return material

    report = run(dll, True, False, True, build, [['a']],
                 material_modes=(1,), commit_control3=64, ring_submode=ring_submode)
    cases = [row for row in report['rows'] if row['mode'] == 1 and 'commit' in row]
    assert len(cases) == 36
    return dict(dse_sha256=SHA256, edges=edges, second_control3=second_control3,
                ring_submode=ring_submode, pitch_delta=pitch_delta,
                gap=gap, sequences=len(cases), cases=cases,
                scope='Two owned notes, retained native work state, direct-tree event commits; '
                      'no Editor host or acoustic rendering.')


def verify_matrix(dll: Path) -> dict:
    gaps = [0, 1, 12, 13, 19, 20, 21, 22, 50, 100, 200]
    results = []
    for gap in gaps:
        for second_control3 in (0, 64):
            pair = []
            for self_edge in (False, True):
                edges = [('Sil', 'a'), ('a', 'Sil')]
                if self_edge:
                    edges.append(('a', 'a'))
                report = verify(dll, edges, second_control3, gap)
                for row in report['cases']:
                    first = row['commit']
                    second = first['followup']
                    assert first['material_indices'] == [0]
                    assert first['item_ranges'] == [[0, 200]]
                    assert second['work_items_before'] == 1
                    assert second['start'] == 300 + gap
                    assert second['second_start'] == second['start']
                    assert second['second_pitch'] == first['note_pitch']
                    assert second['note_slot_after'] == row['slot']
                    assert second['pending'] == 0
                    assert all(index == 0 for index in second['material_indices'])
                    assert all(a <= b for a, b in second['ranges'])
                    assert second['second_aux'] == second_control3 / 128
                    if second_control3 == 0:
                        assert second['work_items_after'] == 1
                        assert second['second_duration'] == 0
                        assert second['ranges'] == [[0, 200]]
                    else:
                        assert second['work_items_after'] >= 2
                        assert second['second_duration'] >= 200
                shapes = {json.dumps((row['commit']['followup']['ranges'],
                                      row['commit']['followup']['second_duration']))
                          for row in report['cases']}
                assert len(shapes) == 1, (gap, second_control3, self_edge, shapes)
                digest = hashlib.sha256(json.dumps(report['cases'], sort_keys=True).encode()).hexdigest()
                summary = dict(gap=gap, second_control3=second_control3, self_edge=self_edge,
                               sequences=report['sequences'],
                               work_items_after=report['cases'][0]['commit']['followup']['work_items_after'],
                               second_duration_after=report['cases'][0]['commit']['followup']['second_duration'],
                               representative_ranges=report['cases'][0]['commit']['followup']['ranges'],
                               result_sha256=digest)
                results.append(summary)
                pair.append(report['cases'])
            assert json.dumps(pair[0], sort_keys=True) == json.dumps(pair[1], sort_keys=True), (
                gap, second_control3)
    return dict(dse_sha256=SHA256, scenarios=len(results),
                sequences=sum(row['sequences'] for row in results),
                native_commits=2*sum(row['sequences'] for row in results), results=results,
                scope='Two-note direct-tree fixture with native events and retained work state; '
                      'self ART edge explicitly linked in the tree and index. All observed '
                      'work items in this matrix still use the stationary part. Host sequence '
                      'assembly, arbitrary material and acoustic output remain unverified.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--self-edge', action='store_true')
    parser.add_argument('--no-onset-edge', action='store_true')
    parser.add_argument('--second-control3', type=int, choices=range(128))
    parser.add_argument('--gap', type=int, default=0)
    parser.add_argument('--ring-submode', type=int, choices=(0, 1), default=0)
    parser.add_argument('--pitch-delta', type=int, default=0)
    parser.add_argument('--inspect-global', action='store_true')
    parser.add_argument('--inspect-allocation', action='store_true')
    parser.add_argument('--matrix', action='store_true')
    args = parser.parse_args()
    if args.matrix:
        assert args.second_control3 is None and not args.self_edge
        result = verify_matrix(args.dll)
    else:
        assert args.second_control3 is not None
        edges = [('a', 'Sil')]
        if not args.no_onset_edge:
            edges.insert(0, ('Sil', 'a'))
        if args.self_edge:
            edges.append(('a', 'a'))
        result = verify(args.dll, edges, args.second_control3, args.gap,
                        args.ring_submode, args.pitch_delta, args.inspect_global,
                        args.inspect_allocation)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(sequences=result['sequences'], scenarios=result.get('scenarios'))))
