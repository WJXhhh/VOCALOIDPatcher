"""Replay V4X breath ART events through DSE with installed frame/PCM data.

The installed DDI/DDB files are read-only. Output PCM remains in memory; the
report records only counts and stream hashes, never frame or audio payloads.
"""

import argparse
from collections import Counter, defaultdict
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import struct

from analyze_reference_layers import walk_art
from audit_frm2_numeric import Native, P, I, Q, F, B, SHA256
from region_commit_material import stationary
from verify_planned_ddi import load_public_parser
from verify_reference_ddi import pointer
from verify_region_breath_commit import probe_full_loop
from verify_region_event_production import run


def verify(dll: Path, report: Path, voice_root: Path, ddb_tools: Path,
           full_loop_limit: int, whole_event: bool = False,
           record_frame_state: bool = False,
           selector_only: bool = False,
           audit_full_loop_pcm: bool = False) -> dict:
    assert selector_only or (0 < full_loop_limit <= 432 if whole_event else
            0 < full_loop_limit <= 576 and full_loop_limit % 8 == 0)
    assert not selector_only or not whole_event
    assert not audit_full_loop_pcm or (whole_event and not selector_only)
    source = report.read_bytes()
    banks = json.loads(source)['banks']
    parser = load_public_parser(ddb_tools)
    durations = (10, 125, 500, 1000, 2000, 5000)
    rows = []
    budgets = defaultdict(int)
    event_budget = [full_loop_limit]
    bank_sources = []
    for bank in banks:
        shorts = {tuple(sample['key']): sample
                  for sample in bank['regions']['short_margin_samples']}
        if not shorts:
            continue
        bank_name = bank['audit']['bank']
        matches = list(voice_root.rglob(f'{bank_name}.ddi'))
        assert len(matches) == 1, (bank_name, len(matches))
        ddi = matches[0]
        ddb = ddi.with_suffix('.ddb')
        raw = ddi.read_bytes()
        assert hashlib.sha256(raw).hexdigest() == bank['ddi_sha256']
        assert ddb.stat().st_size == bank['audit']['ddb_bytes']
        with ddb.open('rb') as stream:
            ddb_sha256 = hashlib.file_digest(stream, 'sha256').hexdigest()
        model = parser(raw)
        model.read()
        art_parts = {}
        for root in model.art_data.values():
            for key, parts in walk_art(root):
                if len(key) == 2 and 'Sil' in key and any(
                        name.startswith('br') for name in key):
                    assert len(parts) == 1, (bank_name, key)
                    art_parts[key] = parts[0]
        for key, shape in shorts.items():
            part = art_parts[key]
            assert len(part['epr']) == shape['frames']
            assert part['frame_align'] == shape['groups']
        bank_sources.append(dict(bank=bank_name, ddi_sha256=bank['ddi_sha256'],
                                 ddb_bytes=ddb.stat().st_size,
                                 ddb_sha256=ddb_sha256))

        for token in sorted({name for key in shorts for name in key
                             if name.startswith('br')}):
            edges = [art_parts[('Sil', token)], art_parts[(token, 'Sil')]]
            crt = c.CDLL('ucrtbase.dll')
            crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
            crt.fclose.argtypes, crt.fclose.restype = [P], I
            handle = crt._wfopen(str(ddb.resolve()), 'rb')
            if not handle:
                raise OSError(f'Cannot open installed DDB: {bank_name}')
            source_stream = ddb.open('rb')

            def material(n: Native, state: int, context: int, voice: int,
                         table: int, sta: int, art: int) -> dict:
                result = stationary(n, state, context, voice, table, sta, art)
                parents = {}
                callback = c.CFUNCTYPE(P, P)(lambda node: parents.get(node, 0))
                n.keep.append(callback)
                vtable = n.alloc(0x68)
                P.from_address(vtable + 0x60).value = c.cast(callback, P).value
                stream_obj = n.alloc(0x80)
                P.from_address(stream_obj + 8).value = handle
                I.from_address(stream_obj + 0x2c).value = 0x32534d53
                P.from_address(voice + 0x1a0).value = stream_obj
                groups_by_part = {}
                short_by_part = {}
                for key, part_data in zip((('Sil', token), (token, 'Sil')), edges):
                    previous = art
                    for name in key:
                        node = n.alloc(0x180)
                        P.from_address(node).value = vtable
                        encoded = name.encode('ascii') + b'\0'
                        c.memmove(node + 0x18, encoded, len(encoded))
                        parents[node] = previous
                        previous = node
                    part = n.alloc(0x280)
                    P.from_address(part).value = vtable
                    parents[part] = node
                    F.from_address(part + 0x160).value = part_data['pitch1']
                    F.from_address(part + 0x164).value = part_data['pitch2']
                    F.from_address(part + 0x16c).value = .6
                    frame_offsets = [pointer(value, raw)[0]
                                     for value in part_data['epr']]
                    frame_times = []
                    for offset in frame_offsets:
                        source_stream.seek(offset + 12)
                        frame_times.append(struct.unpack('<d', source_stream.read(8))[0])
                    P.from_address(part + 0x1a8).value = n.array(Q, frame_offsets)
                    I.from_address(part + 0x1b0).value = len(frame_offsets)
                    snd, count = pointer(part_data['snd'], raw, 18)
                    core, _ = pointer(part_data['snd_start'].split('_')[0], raw, 18)
                    Q.from_address(part + 0x1b8).value = snd
                    Q.from_address(part + 0x1c0).value = core
                    I.from_address(part + 0x1d0).value = count
                    groups = part_data['frame_align']
                    alignment = n.array(I, [group[field] for group in groups
                        for field in ('start', 'end', 'start2', 'end2')])
                    P.from_address(part + 0x1d8).value = alignment
                    P.from_address(part + 0x1e0).value = alignment + 32
                    P.from_address(node + 0x148).value = n.array(P, [part])
                    I.from_address(node + 0x150).value = 1
                    n.fn(0x18d790, None, P, P)(voice + 0x2e0, node)
                    result['parts'].append(part)
                    groups_by_part[part] = [tuple(group[field] for field in
                                                  ('start', 'end', 'start2', 'end2'))
                                            for group in groups]
                    if key in shorts:
                        shape = shorts[key]
                        assert len(frame_offsets) == shape['frames']
                        assert count == len(frame_offsets) * 256 + shape['pcm_margin']
                        assert core - snd == shape['core_delta_bytes']
                        short_by_part[part] = dict(key=key, frames=len(frame_offsets),
                                                   pcm_count=count, snd=snd,
                                                   core=core, frame_times=frame_times)
                result['groups_by_part'] = groups_by_part

                def consume(native, producer, ring, items, source_material):
                    records = P.from_address(ring).value
                    original_mode = B.from_address(ring + 0x78).value
                    assert original_mode == 0
                    found = []
                    short_items = []
                    selector_ctx = None
                    if selector_only:
                        # Isolated selector requires the no-layer-blend branch;
                        # the whole-event replay above covers original mode 0.
                        B.from_address(ring + 0x78).value = 1
                        selector_ctx = native.alloc(0x2b200)
                        P.from_address(selector_ctx).value = P.from_address(context).value
                        P.from_address(selector_ctx + 0x1d568).value = ring
                        F.from_address(selector_ctx + 0x2208).value = 440.
                        F.from_address(selector_ctx + 0x1d550).value = 256.
                        F.from_address(selector_ctx + 0x1d560).value = 44100.
                        F.from_address(selector_ctx + 0x1d564).value = 22050.
                        c.c_double.from_address(selector_ctx + 0x1d558).value = .01
                        I.from_address(selector_ctx + 0x24120).value = 1
                        I.from_address(selector_ctx + 0x24124).value = 2048
                        P.from_address(selector_ctx + 0x24118).value = native.alloc(2049 * 2 + 32)
                        selector_pcm = native.alloc(2049 * 2 + 32)
                        P.from_address(selector_ctx + 0x29510).value = selector_pcm + 16
                        analysis = native.alloc(0x330)
                        native.fn(0x147d10, P, P, I, B, F, I, I, F, I)(
                            analysis, 256, 0, 8., 50, 0, 0., 1)
                        assert I.from_address(analysis + 0x8c).value == 2048
                        P.from_address(selector_ctx + 0x1fc8).value = analysis
                        init_selector_frame = native.fn(0x12a670, P, P)
                        for offset in (0x247b8, 0x247c0):
                            candidate = native.alloc(0x328)
                            init_selector_frame(candidate)
                            native.fn(0x12ba70, Q, P, I, Q, I)(
                                candidate, 16, 0x30, 0)
                            P.from_address(selector_ctx + offset).value = candidate
                        selector_frame = native.alloc(0x328)
                        init_selector_frame(selector_frame)
                        clear_frame = native.fn(0x12cf40, Q, P, Q, I, I)
                        select_frame = native.fn(0x16a870, Q, P, I, P, P, B, B)
                    for j in range(I.from_address(ring + 0x34).value):
                        item = items + j * 0x140
                        part = P.from_address(item + 0x20).value
                        shape = short_by_part.get(part)
                        if shape is None:
                            continue
                        edge_id = (bank_name, *shape['key'])
                        begin, end = I.from_address(item).value, I.from_address(item + 4).value
                        valid = [(t, F.from_address(records + t * 0x18 + 8).value)
                                 for t in range(begin, end)
                                 if F.from_address(records + t * 0x18 + 8).value >= 0]
                        assert valid and all(math.isfinite(position)
                                             for _, position in valid)
                        short_items.append(dict(edge_id=edge_id, shape=shape,
                                                valid=valid))
                        if selector_only:
                            digest = hashlib.sha256()
                            time_model_mismatches = 0
                            first_time_model_mismatch = None
                            output_time_zero_count = 0
                            output_mask_counts = Counter()
                            zero_time_by_mask = Counter()
                            time_model_mismatches_by_mask = Counter()
                            for t, position in valid:
                                frame_index = min(shape['frames'] - 1,
                                                  math.ceil(position))
                                clear_frame(selector_frame, 0x6f0fffffff, 1, 0)
                                c.memset(selector_pcm, 0xa5, 2049 * 2 + 32)
                                select_frame(selector_ctx, t, selector_frame,
                                             records + t * 0x18, 1, 1)
                                observed_time = c.c_double.from_address(
                                    selector_frame + 0x148).value
                                output_time_zero_count += observed_time == 0.
                                output_mask = Q.from_address(selector_frame + 0x150).value
                                mask_label = f'{output_mask:016x}'
                                output_mask_counts[mask_label] += 1
                                if observed_time == 0.:
                                    zero_time_by_mask[mask_label] += 1
                                left_index = max(0, min(shape['frames'] - 1,
                                                        math.floor(position)))
                                right_index = frame_index
                                fraction = position - math.floor(position)
                                expected_time = (shape['frame_times'][left_index] *
                                                 (1 - fraction) +
                                                 shape['frame_times'][right_index] * fraction)
                                assert math.isfinite(observed_time)
                                if output_mask & 0x200:
                                    assert abs(observed_time - expected_time) < 1e-3
                                if abs(observed_time - expected_time) >= 1e-3:
                                    time_model_mismatches += 1
                                    time_model_mismatches_by_mask[mask_label] += 1
                                    if first_time_model_mismatch is None:
                                        first_time_model_mismatch = dict(
                                            record=t, position=position,
                                            output_mask=mask_label,
                                            observed=observed_time,
                                            linear_model=expected_time)
                                start = ((shape['core'] - shape['snd']) // 2 +
                                         frame_index * 256 - 1024)
                                first = max(0, start)
                                last = min(shape['pcm_count'], start + 2049)
                                front = max(0, -start)
                                tail = max(0, start + 2049 - shape['pcm_count'])
                                source_stream.seek(shape['snd'] + first * 2)
                                expected_pcm = (b'\0' * (front * 2) +
                                                source_stream.read((last - first) * 2) +
                                                b'\0' * (tail * 2))
                                assert len(expected_pcm) == 4098
                                actual_pcm = c.string_at(selector_pcm + 16, 4098)
                                assert actual_pcm == expected_pcm, (edge_id, t)
                                assert c.string_at(selector_pcm, 16) == b'\xa5' * 16
                                assert c.string_at(selector_pcm + 16 + 4098, 16) == b'\xa5' * 16
                                digest.update(struct.pack('<IfIdQ', t, position,
                                                          frame_index, observed_time,
                                                          output_mask))
                                digest.update(hashlib.sha256(actual_pcm).digest())
                            found.append(dict(bank=bank_name, key=shape['key'],
                                              selected_records=len(valid),
                                              output_time_zero_count=output_time_zero_count,
                                              output_mask_counts=dict(output_mask_counts),
                                              zero_time_by_mask=dict(zero_time_by_mask),
                                              time_model_mismatches=time_model_mismatches,
                                              time_model_mismatches_by_mask=dict(
                                                  time_model_mismatches_by_mask),
                                              first_time_model_mismatch=first_time_model_mismatch,
                                              selection_sha256=digest.hexdigest()))
                            continue
                        if whole_event or budgets[edge_id] >= full_loop_limit // 8:
                            continue
                        safe = min(shape['frames'] - 1,
                                   (shape['pcm_count'] - 2049) // 256)
                        beyond = [t for t, position in valid
                                  if min(shape['frames'] - 1,
                                         math.ceil(position)) > safe]
                        target = beyond[0] if beyond else valid[-1][0]
                        output = probe_full_loop(native, context, ring, target,
                                                 replay_from_start=True,
                                                 analysis_mode=2,
                                                 initialize_output_gate=True,
                                                 initialize_native_candidate=True,
                                                 source_event_mode=original_mode,
                                                 compact_full_loop_trace=not record_frame_state)
                        assert output['status'] == 0
                        assert output['output_ready'] == 1
                        assert output['output_bytes'] == 512
                        assert output['guard_intact']
                        assert output['effective_spectrum_selection_byte'] == 0
                        budgets[edge_id] += 1
                        row = dict(bank=bank_name, key=shape['key'],
                                          event_slot=I.from_address(state + 0x7a8).value,
                                          target_time=target,
                                          target_above_complete_cache_capacity=bool(beyond),
                                          blocks=output['blocks_run'],
                                          nonzero_blocks=output['nonzero_blocks'],
                                          analysis_nonzero_blocks=output['analysis_nonzero_blocks'],
                                          first_nonzero_time=output['first_nonzero_time'],
                                          stream_sha256=output['output_stream_sha256'],
                                          final_frame_flags=output['final_frame_flags'],
                                          final_frame_harmonics=output['final_frame_harmonics'])
                        if record_frame_state:
                            row['frame_state_trace'] = output['frame_state_trace']
                        found.append(row)
                    if whole_event and event_budget[0] and short_items:
                        target = max(entry['valid'][-1][0] for entry in short_items)
                        expected_at = {t: (entry['shape'], position)
                                       for entry in short_items
                                       for t, position in entry['valid']}
                        pcm_checks = [0, 0]
                        first_pcm_difference = []

                        def observe_pcm(time, output_context):
                            if time not in expected_at:
                                return
                            shape, position = expected_at[time]
                            frame_index = min(shape['frames'] - 1,
                                              math.ceil(position))
                            start = ((shape['core'] - shape['snd']) // 2 +
                                     frame_index * 256 - 1024)
                            first = max(0, start)
                            last = min(shape['pcm_count'], start + 2049)
                            source_stream.seek(shape['snd'] + first * 2)
                            expected_pcm = (b'\0' * (max(0, -start) * 2) +
                                            source_stream.read((last - first) * 2) +
                                            b'\0' * (max(0, start + 2049 -
                                                         shape['pcm_count']) * 2))
                            assert len(expected_pcm) == 4098
                            pointer_to_pcm = P.from_address(output_context + 0x29510).value
                            assert pointer_to_pcm
                            actual_pcm = c.string_at(pointer_to_pcm, 4098)
                            pcm_checks[0] += 1
                            if actual_pcm == expected_pcm:
                                pcm_checks[1] += 1
                            elif len(first_pcm_difference) < 3:
                                first_pcm_difference.append(dict(
                                    time=time, key=shape['key'], position=position,
                                    selected_frame=frame_index,
                                    expected_sha256=hashlib.sha256(expected_pcm).hexdigest(),
                                    observed_sha256=hashlib.sha256(actual_pcm).hexdigest()))

                        output = probe_full_loop(native, context, ring, target,
                                                 replay_from_start=True,
                                                 analysis_mode=2,
                                                 initialize_output_gate=True,
                                                 initialize_native_candidate=True,
                                                 source_event_mode=original_mode,
                                                 compact_full_loop_trace=not record_frame_state,
                                                 pcm_observer=(observe_pcm if audit_full_loop_pcm
                                                               else None))
                        assert output['status'] == 0
                        assert output['output_ready'] == 1
                        assert output['output_bytes'] == 512
                        assert output['guard_intact']
                        assert output['effective_spectrum_selection_byte'] == 0
                        event_budget[0] -= 1
                        row = dict(bank=bank_name, token=token,
                                          short_items=len(short_items),
                                          short_records=sum(len(entry['valid'])
                                                            for entry in short_items),
                                          short_edges=[entry['shape']['key']
                                                       for entry in short_items],
                                          target_time=target,
                                          blocks=output['blocks_run'],
                                          nonzero_blocks=output['nonzero_blocks'],
                                          analysis_nonzero_blocks=output['analysis_nonzero_blocks'],
                                          first_nonzero_time=output['first_nonzero_time'],
                                          stream_sha256=output['output_stream_sha256'])
                        if record_frame_state:
                            row['frame_state_trace'] = output['frame_state_trace']
                        if audit_full_loop_pcm:
                            assert pcm_checks[0] == row['short_records']
                            row['pcm_observer_checks'] = pcm_checks[0]
                            row['pcm_observer_matches'] = pcm_checks[1]
                            row['first_pcm_differences'] = first_pcm_difference
                        found.append(row)
                    if selector_only:
                        B.from_address(ring + 0x78).value = original_mode
                    return found

                result['verify_consumption'] = consume
                return result

            try:
                cases = run(dll, True, False, True, material, [[token]], durations)
            finally:
                source_stream.close()
                assert crt.fclose(handle) == 0
            for case in cases['rows']:
                if 'commit' in case:
                    rows.extend(case['commit'].get('consumption', []))

    assert len(rows) == (576 if selector_only else full_loop_limit)
    if selector_only:
        assert sum(row['selected_records'] for row in rows) == 56616
    if whole_event and full_loop_limit == 432:
        assert sum(row['short_items'] for row in rows) == 576
        assert sum(row['short_records'] for row in rows) == 56616
    if not whole_event and not selector_only:
        assert len(budgets) == 8
    result = dict(dll_sha256=SHA256,
                input_sha256=hashlib.sha256(source).hexdigest(),
                sources=bank_sources,
                rows=rows,
                scope='Read-only installed V4X DDI/DDB with native DSE event commit and mode-0 '
                      'output replay. Only output hashes/counts recorded; no audio/frame payload '
                      'export, Editor playback, or host configuration observation.')
    if selector_only:
        result['selector_only'] = True
        result['selected_work_items'] = len(rows)
        result['selected_records'] = sum(row['selected_records'] for row in rows)
        result['scope'] = ('Read-only installed V4X material; all short-margin ART record '
                           'positions pass the DSE isolated frame selector with temporary '
                           'no-layer-blend flag 1. SND-bounded PCM windows are independently '
                           'checked; output time is compared with a simple interpolation '
                           'model and asserted only when the output retains bit 0x200. '
                           'Original mode-0 full-event output is covered separately. '
                           'No Editor host configuration or audio export.')
    else:
        result['total_output_blocks'] = sum(row['blocks'] for row in rows)
        result['nonzero_output_blocks'] = sum(row['nonzero_blocks'] for row in rows)
        result['items_with_nonzero_output'] = sum(row['nonzero_blocks'] > 0 for row in rows)
    if whole_event:
        result['whole_event'] = True
        result['selected_events'] = len(rows)
        result['short_items_covered'] = sum(row['short_items'] for row in rows)
        result['short_records_covered'] = sum(row['short_records'] for row in rows)
        if audit_full_loop_pcm:
            result['pcm_observer_checks'] = sum(row['pcm_observer_checks'] for row in rows)
            result['pcm_observer_matches'] = sum(row['pcm_observer_matches'] for row in rows)
            result['scope'] += (' Whole-loop input PCM snapshots are compared with the '
                                'simple selected-frame SND model; differences are recorded '
                                'without treating them as failures.')
    elif not selector_only:
        result['selected_work_items'] = len(rows)
        result['selected_targets_above_mode_1_2_complete_cache_window'] = sum(
            row['target_above_complete_cache_capacity'] for row in rows)
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--full-loop-limit', type=int, default=16)
    parser.add_argument('--whole-event', action='store_true')
    parser.add_argument('--record-frame-state', action='store_true')
    parser.add_argument('--selector-only', action='store_true')
    parser.add_argument('--audit-full-loop-pcm', action='store_true')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = verify(args.dll, args.report, args.voice_root, args.ddb_tools,
                    args.full_loop_limit, args.whole_event,
                    args.record_frame_state, args.selector_only,
                    args.audit_full_loop_pcm)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                           encoding='utf-8')
    fields = (('selected_work_items', 'selected_records') if args.selector_only else
              ('selected_events', 'short_items_covered', 'short_records_covered')
              if args.whole_event else ('selected_work_items',))
    if not args.selector_only:
        fields += ('total_output_blocks', 'nonzero_output_blocks',
                   'items_with_nonzero_output')
    if args.audit_full_loop_pcm:
        fields += ('pcm_observer_checks', 'pcm_observer_matches')
    print(json.dumps({key: result[key] for key in fields}))
