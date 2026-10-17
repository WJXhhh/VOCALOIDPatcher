"""Observe owned V4X phoneme transitions across two adjacent note events."""
import argparse
from collections import Counter
import ctypes as c
import json
from pathlib import Path
import struct

from audit_frm2_numeric import B, F, I, P, SHA256
from region_commit_material import connected
from verify_region_event_production import run


TRIPLE_LAYER = dict(pitch1=0., pitch2=0., dynamics=.6, frames=40,
                    groups=[(0, 4, 0, 4), (4, 8, 4, 8),
                            (8, 12, 8, 12), (12, 40, 12, 40)])


def append_table_tokens(n, table, tokens):
    """Add following-note names to the owned phoneme table before index build."""
    count = I.from_address(table + 0x144).value
    records = [P.from_address(P.from_address(table + 0x148).value + j * 8).value
               for j in range(count)]
    known = {c.string_at(record).decode('ascii') for record in records}
    for token in tokens:
        if token not in known:
            record = n.alloc(0x40)
            raw = token.encode('ascii') + b'\0'
            c.memmove(record, raw, len(raw))
            records.append(record)
            known.add(token)
    I.from_address(table + 0x144).value = len(records)
    P.from_address(table + 0x148).value = n.array(P, records)
    classes = n.alloc(0x158)
    n.fn(0x1278b0, P, P, P)(classes, table)
    P.from_address(table + 0x158).value = classes


def commit_following_note(n, state, table, tokens, first_count, control3=64,
                          gap=0):
    """Submit an adjacent owned note after the first note's completed commit."""
    next_slot = I.from_address(state + 0x7a8).value
    first_note = state + 0x58 + (1 - next_slot) * 0x3a8
    first_end = I.from_address(first_note).value + I.from_address(first_note + 4).value
    assert gap >= 0
    pitch = round(F.from_address(first_note + 8).value / 100) + 69
    assert B.from_address(first_note + 0x254).value == first_count

    next_work_slot = (I.from_address(state + 0x4058).value + 1) % 2
    next_bank = state + 0x23f0 + next_work_slot * 0x1c50
    P.from_address(next_bank).value = table
    I.from_address(next_bank + 8).value = 0
    I.from_address(next_bank + 12).value = 0

    events = []

    def cc(controller, value):
        events.append(struct.pack('<IBBBB', 0, 0xb0, controller, value, 0))

    def parameter(index, high, low=None):
        cc(98, index)
        cc(6, high)
        if low is not None:
            cc(38, low)

    cc(99, 0x50)
    parameter(2, pitch)
    parameter(3, control3)
    parameter(4, 0, 100)
    parameter(0x12, len(tokens))
    for token_index, token in enumerate(tokens):
        cc(98, 0x13 + token_index)
        for index, character in enumerate(token.encode('ascii')):
            cc(6, character)
            if index == 0:
                cc(38, 64 + token_index)
    cc(98, 127)
    cc(6, 127 if next_slot else 0)
    payload = c.create_string_buffer(b''.join(events))
    batch = n.alloc(16)
    I.from_address(batch).value = len(events)
    P.from_address(batch + 8).value = c.addressof(payload)
    n.fn(0x1b2ab0, None, P, I, P)(state, first_end + gap, batch)


def probe(dll: Path, first_tokens: list[str], second_tokens: list[str],
          target: tuple[str, str] | None = None,
          triple: tuple[str, str, str] | None = None,
          duration_values: tuple[int, ...] = (10, 125, 1000),
          control3: int = 64) -> dict:
    tokens = first_tokens + second_tokens
    assert first_tokens and second_tokens and len(tokens) <= 4
    assert all(0 < len(token) < 16 and token.isascii() for token in tokens)
    assert target is not None or triple is not None
    edges = list(zip(['Sil'] + tokens[:-1], tokens))
    observations = []

    def build(n, state, context, voice, table, sta, art):
        append_table_tokens(n, table, second_tokens)
        material = connected(n, state, context, voice, table, sta, art,
                             edges=edges, stationary_phoneme=tokens[-1],
                             triple_layers={triple: [TRIPLE_LAYER]} if triple else None)
        pair_by_part = {part: edge for edge, parts in material['edge_parts'].items()
                        for part in parts}
        triple_parts = set(material['triple_parts'].get(triple, []))

        def inspect(n, state, ring, items, current):
            commit_following_note(n, state, table, second_tokens,
                                  len(first_tokens), control3)

            observed = []
            for index in range(I.from_address(ring + 0x34).value):
                item = items + index * 0x140
                part = P.from_address(item + 0x20).value
                edge = pair_by_part.get(part)
                kind = ('triple' if part in triple_parts else
                        'pair' if edge else 'other')
                observed.append(dict(kind=kind, edge=list(edge) if edge else None,
                                     begin=I.from_address(item).value,
                                     end=I.from_address(item + 4).value,
                                     stage=I.from_address(item + 0x28).value))
            observations.append(observed)
            return dict(second_note_ready=True, work_items=len(observed))

        material['verify_consumption'] = inspect
        return material

    report = run(dll, True, False, True, build,
                 token_sequences=[first_tokens], material_modes=(0,),
                 duration_values=duration_values, commit_control3=control3)
    cases = [row for row in report['rows'] if row['mode'] == 0 and 'commit' in row]
    assert len(cases) == len(observations) == 12 * len(duration_values)
    target_name = list(target) if target else None
    target_cases = sum(any(item['edge'] == target_name for item in items)
                       for items in observations) if target else 0
    triple_cases = sum(any(item['kind'] == 'triple' for item in items)
                       for items in observations) if triple else 0
    triple_stages = Counter(item['stage'] for items in observations for item in items
                            if item['kind'] == 'triple')
    counts = Counter(f"{item['edge'][0]}->{item['edge'][1]}" for items in observations
                     for item in items if item['edge'])
    return dict(dse_sha256=SHA256, first_tokens=first_tokens,
                second_tokens=second_tokens, target=list(target) if target else None,
                triple=list(triple) if triple else None,
                cases=len(cases), target_selected_cases=target_cases,
                triple_selected_cases=triple_cases,
                triple_stage_counts=dict(sorted(triple_stages.items())),
                pair_work_items=dict(sorted(counts.items())),
                representative=observations[0],
                scope='Owned adjacent note events with synthetic ART and STA '
                      'material. Records describe selected phoneme work items; '
                      'installed voicebank frame reads and Editor timing are outside this probe.')


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--first', nargs='+', required=True)
    parser.add_argument('--second', nargs='+', required=True)
    parser.add_argument('--target', nargs=2)
    parser.add_argument('--triple', nargs=3)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = probe(args.dll, args.first, args.second,
                   tuple(args.target) if args.target else None,
                   tuple(args.triple) if args.triple else None)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                           encoding='utf-8')
    print(json.dumps({key: result[key] for key in
                      ('cases', 'target_selected_cases', 'triple_selected_cases',
                       'triple_stage_counts')}))


if __name__ == '__main__':
    main()
