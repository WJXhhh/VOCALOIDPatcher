"""Missing-bank, unknown-token and empty-material native commit fixtures."""
import ctypes as c
import struct
from audit_frm2_numeric import P, I, Q, F, D, B


def verify_commit(n, state, ring, context, consume, slot, mode, duration, unknown_token=False, no_material=False,
                  material_builder=None, lookahead_tokens=None, commit_control3=64):
    config, provider, vtable = n.alloc(0x100), n.alloc(8), n.alloc(0x40)
    calls = []

    def missing(unused, first, second):
        calls.append([I.from_address(first).value, I.from_address(second).value])
        return None

    callback = c.CFUNCTYPE(P, P, P, P)(missing)
    n.keep.append(callback)
    P.from_address(vtable + 0x30).value = c.cast(callback, P).value
    P.from_address(provider).value = vtable
    P.from_address(config).value = provider
    P.from_address(context).value = config
    if unknown_token or no_material:
        assert mode == 0 or material_builder is not None
        registry, voice, table, record = (n.alloc(size) for size in [0x20, 0x320, 0x180, 0x40])
        P.from_address(config + 0x10).value = registry
        I.from_address(registry + 8).value = 1
        P.from_address(registry + 0x10).value = n.array(P, [voice])
        P.from_address(registry + 0x18).value = n.array(P, [n.alloc(8)])
        P.from_address(voice + 0x170).value = table
        n.fn(0x124b80, P, P)(table)
        c.memmove(record, b'z\0', 2)
        records = [record]
        note_address = state + 0x58 + slot * 0x3a8
        if B.from_address(note_address + 0x254).value > 1:
            known = n.alloc(0x40)
            c.memmove(known, b'a\0', 2)
            records.append(known)
        I.from_address(table + 0x144).value = len(records)
        P.from_address(table + 0x148).value = n.array(P, records)
        if no_material:
            names = ['Sil', 'a', 'k', 'a#2']
            if material_builder is not None:
                note_address = state + 0x58 + slot * 0x3a8
                names.extend(c.string_at(note_address + 0x255 + j * 16).decode('ascii')
                             for j in range(B.from_address(note_address + 0x254).value))
                names = list(dict.fromkeys(names))
            records = []
            for name in names:
                record = n.alloc(0x40)
                raw = name.encode() + b'\0'
                c.memmove(record, raw, len(raw))
                records.append(record)
            I.from_address(table + 0x144).value = len(records)
            P.from_address(table + 0x148).value = n.array(P, records)
            classes = n.alloc(0x158)
            n.fn(0x1278b0, P, P, P)(classes, table)
            P.from_address(table + 0x158).value = classes
            root, sta, art = (n.alloc(0x180) for _ in range(3))
            for node, name in [(root, b'voice\0'), (sta, b'stationary\0'), (art, b'articulation\0')]:
                n.fn(0x1067f0, P, P, I)(node, 0)
                c.memmove(node + 0x18, name, len(name))
            P.from_address(voice + 0x148).value = n.array(P, [root])
            I.from_address(voice + 0x150).value = 1
            P.from_address(root + 0x148).value = n.array(P, [sta, art])
            I.from_address(root + 0x150).value = 2
            sentinel = n.alloc(0xa8)
            P.from_address(voice + 0x2e0).value = sentinel
            for offset in [0, 8, 16]:
                P.from_address(sentinel + offset).value = sentinel
            B.from_address(sentinel + 0x18).value = 1
            B.from_address(sentinel + 0x19).value = 1
            generator = n.alloc(0x100)
            P.from_address(state + 0x4060).value = generator
            D.from_address(generator).value = .01
            P.from_address(generator + 0x10).value = ring
            for offset in [0x20, 0x28, 0x38, 0x40]:
                P.from_address(generator + offset).value = n.alloc(0x140)
            for offset in [0x68, 0x70]:
                P.from_address(generator + offset).value = n.alloc(0x3a8)
            n.fn(0x123280, Q, P)(state + 0x40e8)
    D.from_address(context + 0x1d558).value = .01
    if material_builder is not None:
        assert no_material
        for work_slot in [0, 1]:
            n.fn(0x1afc30, None, P)(state + 0x7b8 + work_slot * 0x1c50)
            assert all(F.from_address(state + 0x7b8 + work_slot*0x1c50 + 0x1610 + k*4).value == 1.
                       for k in range(64))
        material = material_builder(n, state, context, voice, table, sta, art)
        # Index construction enumerates available banks; keep those lookups
        # distinct from metadata requests made by the actual note commit.
        material['index_metadata_queries'] = list(calls)
        calls.clear()
    for control in [3, 4, 8, 23, 24]:
        base = ring + 0x5a380 + control * 0x48
        points = n.alloc(16 * 8)
        for offset in [0, 0x18, 0x30]:
            P.from_address(base + offset).value = points
        for offset in [8, 0x20, 0x38]:
            I.from_address(base + offset).value = 16
        for offset in [0x24, 0x3c]:
            I.from_address(base + offset).value = 15
    if material_builder is not None:
        base, records = ring + 0x5a338, n.alloc(16 * 0x250)
        for offset in [0, 0x18, 0x30]:
            P.from_address(base + offset).value = records
        for offset in [8, 0x20, 0x38]:
            I.from_address(base + offset).value = 16
        for offset in [0x24, 0x3c]:
            I.from_address(base + offset).value = 15
    if lookahead_tokens is not None:
        assert mode and material_builder is not None
        next_slot = 1 - slot
        next_note = state + 0x58 + next_slot * 0x3a8
        n.fn(0x1afe40, None, P)(next_note)
        I.from_address(state + 0x7a8).value = next_slot
        note_pitch = int(F.from_address(state + 0x58 + slot * 0x3a8 + 8).value / 100) + 69
        events = []

        def cc(controller, value):
            events.append(struct.pack('<IBBBB', 0, 0xb0, controller, value, 0))

        def parameter(index, high, low=None):
            cc(98, index)
            cc(6, high)
            if low is not None:
                cc(38, low)

        cc(99, 0x50)
        parameter(2, note_pitch)
        parameter(4, 0, 100)
        parameter(0x12, len(lookahead_tokens))
        for token_index, token in enumerate(lookahead_tokens):
            cc(98, 0x13 + token_index)
            for index, character in enumerate(token.encode('ascii')):
                cc(6, character)
                if index == 0:
                    cc(38, 64 + token_index)
        payload = c.create_string_buffer(b''.join(events))
        batch = n.alloc(16)
        I.from_address(batch).value = len(events)
        P.from_address(batch + 8).value = c.addressof(payload)
        consume(state, 100 + duration, batch)
        assert n.fn(0x1b0310, B, P)(next_note) == 1
        I.from_address(state + 0x7a8).value = slot
        # This owned fixture has no preceding bank-selection event for the
        # lookahead work slot. Associate the already registered bank explicitly.
        next_work_slot = (I.from_address(state + 0x4058).value + 1) % 2
        next_bank = state + 0x23f0 + next_work_slot * 0x1c50
        P.from_address(next_bank).value = table
        I.from_address(next_bank + 8).value = 0
        I.from_address(next_bank + 12).value = 0
    frames, items = n.alloc(1024 * 0x18), n.alloc(16 * 0x140)
    P.from_address(ring).value = frames
    P.from_address(ring + 0x10).value = n.alloc(1024 * 0x2c)
    I.from_address(ring + 0x18).value = 1024
    I.from_address(ring + 8).value = 1024
    for offset in [0x20, 0x38, 0x50]:
        P.from_address(ring + offset).value = items
    for offset in [0x28, 0x2c, 0x40, 0x58]:
        I.from_address(ring + offset).value = 16
    for offset in [0x44, 0x5c]:
        I.from_address(ring + offset).value = 15
    note = state + 0x58 + slot * 0x3a8
    other_note = state + 0x58 + (1-slot)*0x3a8
    lookahead_before_commit = (dict(pitch=F.from_address(other_note + 8).value,
                                    start=I.from_address(other_note).value,
                                    bank_ids=[I.from_address(state + 0x23f8 +
                                                             ((I.from_address(state + 0x4058).value + 1) % 2)*0x1c50 + k*4).value
                                              for k in range(2)]) if lookahead_tokens else None)
    original = c.string_at(note, 0x3a8)
    value = 127 if slot else 0
    events = [(98, 127), (6, value)]
    if material_builder is not None:
        F.from_address(ring + 0x5aa90).value = .6
        events = [(98, 3), (6, commit_control3)] + events
    payload = c.create_string_buffer(b''.join(struct.pack('<IBBBB', 0, 0xb0, cc, val, 0)
                                             for cc, val in events))
    batch = n.alloc(16)
    I.from_address(batch).value = len(events)
    P.from_address(batch + 8).value = c.addressof(payload)
    consume(state, 100, batch)
    assert calls == ([] if unknown_token or no_material else [[0, 0]]), calls
    if unknown_token or no_material:
        assert P.from_address(state + 0x23f0).value == table
    count = I.from_address(ring + 0x34).value
    new_slot = I.from_address(state + 0x7a8).value
    if no_material:
        assert all(c.string_at(note + 0x255 + j*16).decode('ascii') in names
                   for j in range(B.from_address(note + 0x254).value))
        ranges = [[I.from_address(items+j*0x140+o).value for o in [0,4]] for j in range(count)]
        if material_builder is not None:
            assert 0 <= count <= 16
            if mode:
                result = dict(mode=mode, duration=duration, work_items=count, new_slot=new_slot,
                            lookahead_tokens=lookahead_tokens,
                            lookahead_before_commit=lookahead_before_commit,
                            commit_control3=commit_control3,
                            note_aux=F.from_address(note + 0xc).value,
                            note_pitch=F.from_address(note + 8).value,
                            other_pitch=F.from_address(other_note + 8).value,
                            other_start=I.from_address(other_note).value,
                            item_ranges=ranges, pending=B.from_address(state+0x7b8).value,
                            selected_parts=[P.from_address(items+j*0x140+0x20).value in material['parts']
                                            for j in range(count)],
                            material_indices=[material['parts'].index(P.from_address(items+j*0x140+0x20).value)
                                              if P.from_address(items+j*0x140+0x20).value in material['parts'] else -1
                                              for j in range(count)],
                            reverse=[B.from_address(items+j*0x140+0x12c).value for j in range(count)],
                            tokens=[[I.from_address(items+j*0x140+o).value for o in [0x10,0x14]]
                                    for j in range(count)],
                            left_lengths=[I.from_address(items+j*0x140+8).value for j in range(count)],
                            anchor=B.from_address(state+0x7c0).value,
                            candidate_count=I.from_address(state+0x7c4).value)
                if 'direct_followup' in material:
                    result['followup'] = material['direct_followup'](
                        n, state, ring, items, consume, material, slot, duration)
                if 'post_direct_commit' in material:
                    result['diagnostics'] = material['post_direct_commit'](
                        n, state, ring, items, material)
                return result
            from verify_region_output_mapping import predict
            position_errors = []
            for j, (begin, end) in enumerate(ranges):
                item = items + j*0x140
                part = P.from_address(item + 0x20).value
                if (part in material.get('stationary_parts', [material['part']]) or
                        part not in material['parts']):
                    continue
                left = I.from_address(item + 8).value
                reverse = B.from_address(item + 0x12c).value
                source_groups = material.get('groups_by_part', {}).get(
                    part, [(0, 50, 5, 45), (50, 100, 55, 95)])
                stage = I.from_address(item + 0x28).value
                groups = source_groups[2 * stage:2 * stage + 2][::(-1 if reverse else 1)]
                expected = predict(groups[0], left, 0, reverse) + predict(groups[1], end-begin-left, 1, reverse)
                actual = [F.from_address(frames + t*0x18 + 8).value for t in range(begin, end)]
                alignment = P.from_address(part + 0x1d8).value
                current_alignment = [I.from_address(alignment + k*4).value for k in range(8)]
                error = max(abs(a-b) for a, b in zip(actual, expected))
                # Native work reset initializes the tested coefficients to one.
                # The default right-side mapping can exceed the outer interval;
                # compare the full numeric model instead of imposing a false bound.
                assert error < 1e-5, (j, error, actual[:8], actual[-8:], expected[:8], expected[-8:])
                position_errors.append(dict(item=j, frames=len(actual), unit_model_max_error=error,
                                            alignment=current_alignment,
                                            min_position=min(actual), max_position=max(actual),
                                            first=actual[:8], last=actual[-8:]))
            result = dict(mode=mode, duration=duration, work_items=count, new_slot=new_slot,
                        index_metadata_queries=material['index_metadata_queries'],
                        position_checks=position_errors,
                        item_ranges=ranges, pending=B.from_address(state+0x7b8).value,
                        selected_parts=[P.from_address(items+j*0x140+0x20).value == material['part'] for j in range(count)],
                        material_indices=[material['parts'].index(P.from_address(items+j*0x140+0x20).value)
                                          if P.from_address(items+j*0x140+0x20).value in material['parts'] else -1
                                          for j in range(count)],
                        reverse=[B.from_address(items+j*0x140+0x12c).value for j in range(count)],
                        tokens=[[I.from_address(items+j*0x140+o).value for o in [0x10,0x14]] for j in range(count)],
                        left_lengths=[I.from_address(items+j*0x140+8).value for j in range(count)],
                        anchor=B.from_address(state+0x7c0).value,
                        allocation_coefficients=[F.from_address(state+0x7b8+0x1610+k*4).value for k in range(4)],
                        candidate_count=I.from_address(state+0x7c4).value)
            if 'next_gap' in material:
                from verify_region_note_continuation import continue_note
                result['continuation'] = continue_note(n, state, ring, items, consume, material,
                                                       slot, 100+duration+material['next_gap'])
            if 'verify_consumption' in material:
                result['consumption'] = material['verify_consumption'](n, state, ring, items, material)
            return result
        assert ranges == [[0, 100+duration]], ranges
        assert new_slot == 1-slot
        assert B.from_address(state+0x7b8).value == 1
        assert Q.from_address(items+0x10).value == 0xffffffffffffffff
        assert B.from_address(items+0x90).value == 1
        return dict(mode=mode, duration=duration, work_items=count, new_slot=new_slot,
                    item_ranges=ranges, pending=True, metadata_lookups=calls,
                    loaded_bank=True, all_tokens_present=True, stationary_parts=0,
                    articulation_entries=0)
    assert count == (0 if mode else 1), (mode, count)
    assert new_slot == (slot if mode else 1-slot)
    expected_note = bytearray(original)
    tokens_before, tokens_after = [], []
    for j in range(original[0x254]):
        begin = 0x255 + j * 16
        token = original[begin:begin+16].split(b'\0', 1)[0]
        tokens_before.append(token.decode('ascii'))
        if unknown_token and original[0x254] > 1 and token == b'a#2':
            # strcpy("a") leaves bytes beyond the first NUL unchanged.
            expected_note[begin:begin+2] = b'a\0'
        tokens_after.append(c.string_at(note + begin).decode('ascii'))
    assert c.string_at(note, 0x3a8) == bytes(expected_note)
    if not mode:
        assert [I.from_address(items + o).value for o in [0, 4]] == [0, 100 + duration]
        assert Q.from_address(items + 0x10).value == 0xffffffffffffffff
        assert B.from_address(items + 0x90).value == 1
    assert B.from_address(state + 0x7b8).value == 1
    return dict(metadata_lookups=calls, unknown_token=unknown_token,
                tokens_before=tokens_before, tokens_after=tokens_after,
                commit_value=value, work_items=count,
                note_slot_before=slot, note_slot_after=new_slot,
                pending_flag=True, silence_end=100+duration if not mode else None)
