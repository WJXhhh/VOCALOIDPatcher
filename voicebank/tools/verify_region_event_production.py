"""Owned controller events through the native note/phoneme producer, before commit."""
import argparse
import ctypes as c
import itertools
import json
import math
import struct
from pathlib import Path

from audit_frm2_numeric import Native, P, I, Q, F, B, SHA256, f32


def run(dll, commit=False, unknown_token=False, no_material=False, material_builder=None,
        token_sequences=None, duration_values=(10, 125, 1000), material_modes=(0,),
        lookahead_tokens=None, commit_control3=64, ring_submode=0,
        commit_note_start_offset=0):
    n = Native(dll)
    reset = n.fn(0x1afe40, None, P)
    consume = n.fn(0x1b2ab0, None, P, I, P)
    ready = n.fn(0x1b0310, B, P)
    rows = []
    for slot, mode, pitch, milliseconds, tokens, split, running in itertools.product(
            [0, 1], [0, 1], [48, 69, 84], duration_values,
            token_sequences or [['a'], ['Sil'], ['a#2'], ['k', 'a'], ['Sil', 'k', 'a#2']],
            [False, True], [False, True]):
        state, ring, context = n.alloc(0x6500), n.alloc(0x5b000 if commit else 0x100), n.alloc(0x30000)
        P.from_address(state).value = ring
        P.from_address(state + 8).value = context
        I.from_address(state + 0x7a8).value = slot
        B.from_address(ring + 0x78).value = mode
        I.from_address(ring + 0x7c).value = ring_submode
        F.from_address(context + 0x1d560).value = 44100
        F.from_address(context + 0x1d550).value = 441
        note = state + 0x58 + slot * 0x3a8
        reset(note)
        assert ready(note) == 0
        initial_note = c.string_at(note, 0x3a8)
        events = []

        def cc(controller, value):
            status = 0 if running and events else 0xb0
            events.append(struct.pack('<IBBBB', 0, status, controller, value, 0))

        def parameter(index, high, low=None):
            cc(98, index)
            cc(6, high)
            if low is not None:
                cc(38, low)

        cc(99, 0x50)
        parameter(2, pitch)
        parameter(4, milliseconds >> 7, milliseconds & 127)
        parameter(0x12, len(tokens))
        for token_index, token in enumerate(tokens):
            cc(98, 0x13 + token_index)
            for index, character in enumerate(token.encode('ascii')):
                cc(6, character)
                if index == 0:
                    cc(38, 64 + token_index)
        chunks = [[event] for event in events] if split else [events]
        for chunk in chunks:
            payload = c.create_string_buffer(b''.join(chunk))
            batch = n.alloc(16)
            I.from_address(batch).value = len(chunk)
            P.from_address(batch + 8).value = c.addressof(payload)
            # An empty work ring at time 100 does not invoke trailing silence append.
            consume(state, 100, batch)
        if running:
            # This pre-parsed record API skips status-less records after 0xb0.
            # Do not assume it implements a raw MIDI running-status decoder.
            assert c.string_at(note, 0x3a8) == initial_note
            assert ready(note) == 0
            rows.append(dict(slot=slot, mode=mode, pitch=pitch, milliseconds=milliseconds,
                             tokens=tokens, split=split, running_status=True,
                             produced=False, note_unchanged=True))
            continue
        actual_tokens = [c.string_at(note + 0x255 + 16*j).decode('ascii')
                         for j in range(len(tokens))]
        duration = I.from_address(note + 4).value
        expected_duration = math.floor(f32(f32(f32((2000 if mode else milliseconds) / 1000) * 44100) / 441))
        assert I.from_address(note).value == 100
        assert duration == expected_duration
        assert F.from_address(note + 8).value == (pitch - 69) * 100
        assert B.from_address(note + 0x254).value == len(tokens)
        assert actual_tokens == tokens
        assert [B.from_address(note + 0x355 + j).value for j in range(len(tokens))] == list(range(64, 64 + len(tokens)))
        assert ready(note) == 1
        # The native predicate compares against NaN with !=; it is not a finite check.
        assert math.isnan(F.from_address(note + 0xc).value)
        assert math.isnan(F.from_address(note + 0x250).value)
        rows.append(dict(slot=slot, mode=mode, pitch=pitch, milliseconds=milliseconds,
                         tokens=tokens, split=split, running_status=running,
                         produced=True, start=100, duration=duration, ready=True,
                         unset_float_fields_still_nan=True))
        if commit and (not (unknown_token or no_material) or mode == 0 or
                       (material_builder is not None and mode in material_modes)):
            from verify_region_missing_bank_commit import verify_commit
            if mode and material_builder is not None and commit_note_start_offset:
                I.from_address(note).value += commit_note_start_offset
            rows[-1]['commit'] = verify_commit(n, state, ring, context, consume, slot, mode, duration,
                                              unknown_token, no_material, material_builder,
                                              lookahead_tokens if mode else None, commit_control3)
    return dict(dse_sha256=SHA256, cases=len(rows), rows=rows,
                scope='Actual native controller event producer and readiness predicate; ' +
                      ('final commit included with explicit fixture conditions; no host rendering'
                       if commit else 'no final 0x7f commit, allocator, or host rendering invoked'))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--commit-missing-bank', action='store_true')
    parser.add_argument('--commit-unknown-token', action='store_true')
    parser.add_argument('--commit-empty-material', action='store_true')
    args = parser.parse_args()
    result = run(args.dll, args.commit_missing_bank or args.commit_unknown_token or args.commit_empty_material,
                 args.commit_unknown_token, args.commit_empty_material)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(cases=result['cases'])))
