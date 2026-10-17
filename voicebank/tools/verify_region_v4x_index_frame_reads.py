"""Compare selected indexed V4X ART/STA frames with installed DDB."""
import argparse
from collections import Counter
from contextlib import nullcontext
import ctypes as c
import hashlib
import json
import math
import mmap
from pathlib import Path
import struct

from analyze_reference_layers import walk_art
from analyze_reference_regions import read_frame
from audit_frm2_numeric import P, I, Q, F, D, B, SHA256
from probe_region_cross_note_paths import append_table_tokens, commit_following_note
from region_commit_material import connected
from verify_planned_ddi import load_public_parser
from verify_reference_ddi import pointer
from verify_region_breath_commit import probe_full_loop
from verify_region_event_production import run


WINDOW = 2048
HOP = 256


def verify(dll: Path, bank: str, first: str, second: str,
           manifest: Path, voice_root: Path, ddb_tools: Path,
           selector: bool = False, full_loop_limit: int = 0,
           third: str | None = None, fourth: str | None = None,
           pair_only: bool = False,
           target_edge: tuple[str, str] | None = None,
           note_split: int | None = None, note_gap: int = 0,
           allow_missing_terminal_sta: bool = False,
           full_loop_engine_setup=None,
           full_loop_frame_observer=None,
           full_loop_output_observer=None,
           full_loop_min_target_time: int = 0) -> dict:
    full_loop = full_loop_limit > 0
    assert 0 <= full_loop_limit <= 36
    assert not (selector and full_loop)
    source = json.loads(manifest.read_text(encoding='utf-8'))
    identity = next(row for row in source['banks'] if row['bank'] == bank)
    assert hashlib.sha256((ddb_tools / 'utils' / 'ddi_utils.py').read_bytes()).hexdigest() == source['parser_sha256']
    matches = list(voice_root.rglob(f'{bank}.ddi'))
    assert len(matches) == 1
    ddi = matches[0]
    ddb = ddi.with_suffix('.ddb')
    raw = ddi.read_bytes()
    assert hashlib.sha256(raw).hexdigest() == identity['ddi_sha256']
    audit = json.loads(manifest.with_name('105_v4x_regions.json').read_text(encoding='utf-8'))
    bank_audit = next(row['audit'] for row in audit['banks'] if row['audit']['bank'] == bank)
    assert bank_audit['ddi_sha256'] == identity['ddi_sha256']
    assert ddb.stat().st_size == bank_audit['ddb_bytes']
    model = load_public_parser(ddb_tools)(raw)
    model.read()
    assert fourth is None or third is not None and pair_only
    assert not pair_only or third is not None
    sequence = (first, second) + ((third,) if third is not None else ()) + (
        (fourth,) if fourth is not None else ())
    assert note_split is None or 0 < note_split < len(sequence)
    assert note_gap >= 0 and (note_split is not None or note_gap == 0)
    triple_mode = third is not None and third != 'Sil' and not pair_only
    sta_anchor = (sequence[-1] if pair_only and not allow_missing_terminal_sta else
                  third if triple_mode and third in model.ddi_data_dict['sta'] else
                  second if second in model.ddi_data_dict['sta'] else
                  first if first in model.ddi_data_dict['sta'] else 'a')
    assert sta_anchor in model.ddi_data_dict['sta']
    if triple_mode:
        assert third in model.ddi_data_dict['sta']
    elif not pair_only and not allow_missing_terminal_sta:
        assert second in model.ddi_data_dict['sta'] or second == 'Sil'
    conditional_transition = (pair_only or triple_mode or first == second or
                              sequence[-1] == 'Sil' or allow_missing_terminal_sta)
    available = {key: parts for root in model.art_data.values()
                 for key, parts in walk_art(root) if parts}
    pair_keys = tuple(zip(('Sil', *sequence[:-1]), sequence))
    if triple_mode:
        triple_key = (first, '^' + second, third)
        assert triple_key in available and ('Sil', first) in available
        wanted_pairs = tuple(key for key in pair_keys if key in available)
        wanted = (*wanted_pairs, triple_key)
    else:
        triple_key = None
        assert all(key in available for key in pair_keys)
        wanted_pairs = pair_keys
        wanted = pair_keys
    if target_edge is not None:
        assert pair_only and target_edge in wanted_pairs
    else:
        target_edge = wanted[-1]
    found = {key: available[key] for key in wanted}

    layer_specs = {}
    for edge in wanted:
        layers = []
        for part in found[edge]:
            snd, count = pointer(part['snd'], raw, 18)
            core, _ = pointer(part['snd_start'].split('_')[0], raw, 18)
            offsets = [pointer(value, raw)[0] for value in part['epr']]
            layers.append(dict(pitch1=part['pitch1'], pitch2=part['pitch2'],
                               dynamics=part['dynamics'], frames=len(offsets),
                               groups=[tuple(group[field] for field in
                                       ('start', 'end', 'start2', 'end2'))
                                       for group in part['frame_align']],
                               frame_offsets=offsets, snd=snd, core=core,
                               pcm_count=count))
        layer_specs[edge] = layers

    sta_specs = None
    if conditional_transition and (pair_only or triple_mode or sta_anchor == first or
                                   third is not None and sta_anchor == second):
        sta_specs = []
        for part in model.ddi_data_dict['sta'][sta_anchor]:
            core_pointer, count = pointer(part['snd'], raw)
            core = core_pointer - 18
            offsets = [pointer(value, raw)[0] for value in part['epr']]
            assert count == len(offsets) * HOP + WINDOW
            sta_specs.append(dict(pitch1=part['pitch'], frames=len(offsets),
                                  groups=[(0, len(offsets), 0, len(offsets))],
                                  frame_offsets=offsets, snd=core - WINDOW,
                                  core=core, pcm_count=count))

    crt = c.CDLL('ucrtbase.dll')
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    handle = crt._wfopen(str(ddb.resolve()), 'rb')
    if not handle:
        raise OSError(f'Cannot open installed DDB: {bank}')
    observations = []
    full_loop_runs = []
    try:
        with ddb.open('rb') as stream, mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as data:
            for layers in layer_specs.values():
                for layer in layers:
                    assert layer['snd'] + layer['pcm_count'] * 2 <= len(data)
                    assert all(value + 0x20 <= len(data) for value in layer['frame_offsets'])
            if sta_specs:
                for layer in sta_specs:
                    assert layer['snd'] >= 0
                    assert layer['snd'] + layer['pcm_count'] * 2 <= len(data)
                    assert all(value + 0x20 <= len(data)
                               for value in layer['frame_offsets'])

            def build(n, state, context, voice, table, sta, art):
                if note_split is not None:
                    append_table_tokens(n, table, sequence[note_split:])
                material = connected(n, state, context, voice, table, sta, art,
                                     edges=wanted_pairs, tree=False,
                                     edge_layers={key: layer_specs[key]
                                                  for key in wanted_pairs},
                                     stationary_phoneme=sta_anchor,
                                     stationary_layers=sta_specs,
                                     triple_layers={triple_key: layer_specs[triple_key]}
                                     if triple_mode else None)
                stream_obj = n.alloc(0x80)
                P.from_address(stream_obj + 8).value = handle
                I.from_address(stream_obj + 0x2c).value = 0x32534d53
                P.from_address(voice + 0x1a0).value = stream_obj
                parts = {address: (edge, index, layer_specs[edge][index])
                         for edge in wanted_pairs
                         for index, address in enumerate(material['edge_parts'][edge])}
                if triple_mode:
                    parts.update({address: (triple_key, index,
                                            layer_specs[triple_key][index])
                                  for index, address in enumerate(
                                      material['triple_parts'][triple_key])})

                def inspect(n, state, ring, items, material):
                    if note_split is not None:
                        commit_following_note(n, state, table,
                                              sequence[note_split:], note_split,
                                              gap=note_gap)
                    records = P.from_address(ring).value
                    config = P.from_address(context).value
                    assert config
                    read_context = n.alloc(0x2b000)
                    P.from_address(read_context).value = config
                    F.from_address(read_context + 0x1d550).value = HOP
                    F.from_address(read_context + 0x1d564).value = 22050.
                    D.from_address(read_context + 0x1d558).value = .01
                    controls = n.alloc(0x100)
                    P.from_address(read_context + 0x1d568).value = controls
                    analysis = n.alloc(0x100)
                    I.from_address(analysis + 0x8c).value = WINDOW
                    P.from_address(read_context + 0x1fc8).value = analysis
                    destination = n.alloc((WINDOW + 1) * 2 + 32)
                    P.from_address(read_context + 0x29510).value = destination + 16
                    output = n.alloc(0x328)
                    n.fn(0x12a670, P, P)(output)
                    load = n.fn(0x175dd0, None, P, P, I, P, I, I, P)
                    loaded = I()
                    items_read = []
                    expected_at = {}
                    if selector:
                        selector_ctx = n.alloc(0x2b200)
                        P.from_address(selector_ctx).value = config
                        P.from_address(selector_ctx + 0x1d568).value = ring
                        F.from_address(selector_ctx + 0x2208).value = 440.
                        F.from_address(selector_ctx + 0x1d550).value = HOP
                        F.from_address(selector_ctx + 0x1d560).value = 44100.
                        F.from_address(selector_ctx + 0x1d564).value = 22050.
                        D.from_address(selector_ctx + 0x1d558).value = .01
                        I.from_address(selector_ctx + 0x24120).value = 1
                        I.from_address(selector_ctx + 0x24124).value = WINDOW
                        P.from_address(selector_ctx + 0x24118).value = n.alloc((WINDOW + 1) * 2 + 32)
                        selector_pcm = n.alloc((WINDOW + 1) * 2 + 32)
                        P.from_address(selector_ctx + 0x29510).value = selector_pcm + 16
                        selector_analysis = n.alloc(0x330)
                        n.fn(0x147d10, P, P, I, B, F, I, I, F, I)(
                            selector_analysis, HOP, 0, 8., 50, 0, 0., 1)
                        assert I.from_address(selector_analysis + 0x8c).value == WINDOW
                        P.from_address(selector_ctx + 0x1fc8).value = selector_analysis
                        init_candidate = n.fn(0x12a670, P, P)
                        for offset in (0x247b8, 0x247c0):
                            candidate = n.alloc(0x328)
                            init_candidate(candidate)
                            n.fn(0x12ba70, Q, P, I, Q, I)(candidate, 16, 0x30, 0)
                            P.from_address(selector_ctx + offset).value = candidate
                        selector_frame = n.alloc(0x328)
                        init_candidate(selector_frame)
                        clear_frame = n.fn(0x12cf40, Q, P, Q, I, I)
                        select_frame = n.fn(0x16a870, Q, P, I, P, P, B, B)
                    for work_index in range(I.from_address(ring + 0x34).value):
                        work = items + work_index * 0x140
                        part = P.from_address(work + 0x20).value
                        if part not in parts:
                            continue
                        edge, layer_index, layer = parts[part]
                        reverse = B.from_address(work + 0x12c).value
                        assert reverse in (0, 1)
                        begin, end = I.from_address(work).value, I.from_address(work + 4).value
                        positions = [F.from_address(records + time * 0x18 + 8).value
                                     for time in range(begin, end)]
                        assert all(math.isfinite(value) and value >= 0
                                   for value in positions)
                        if not positions:
                            empty_item = dict(edge=list(edge), layer=layer_index,
                                              positions=0, frames_read=0,
                                              nonzero_pcm_windows=0,
                                              frame_range=None, reads_sha256=None)
                            if triple_mode and edge == triple_key:
                                empty_item['stage'] = I.from_address(work + 0x28).value
                            items_read.append(empty_item)
                            continue
                        if full_loop and len(full_loop_runs) < full_loop_limit:
                            for time, position in enumerate(positions, begin):
                                assert time not in expected_at
                                expected_at[time] = (edge, layer, position, reverse)
                        indexes = sorted({min(layer['frames'] - 1, math.ceil(value))
                                          for value in positions})
                        assert indexes and indexes[0] >= 0
                        digest = hashlib.sha256()
                        nonzero = 0
                        for frame_index in indexes:
                            source_offset = layer['frame_offsets'][frame_index]
                            expected_time, _ = read_frame(data, source_offset)
                            c.memset(destination, 0xa5, (WINDOW + 1) * 2 + 32)
                            loaded.value = 0
                            load(read_context, work, frame_index, output, 0, 1,
                                 c.byref(loaded))
                            assert loaded.value == 1
                            assert D.from_address(output + 0x148).value == expected_time
                            start = ((layer['core'] - layer['snd']) // 2 +
                                     frame_index * HOP - WINDOW // 2)
                            first_sample = max(0, start)
                            last_sample = min(layer['pcm_count'], start + WINDOW + 1)
                            front = max(0, -start)
                            tail = max(0, start + WINDOW + 1 - layer['pcm_count'])
                            expected = (b'\0' * (front * 2) +
                                        data[layer['snd'] + first_sample * 2:
                                             layer['snd'] + last_sample * 2] +
                                        b'\0' * (tail * 2))
                            actual = c.string_at(destination + 16, (WINDOW + 1) * 2)
                            assert len(expected) == len(actual) and actual == expected
                            assert c.string_at(destination, 16) == b'\xa5' * 16
                            assert c.string_at(destination + 16 + (WINDOW + 1) * 2, 16) == b'\xa5' * 16
                            nonzero += bool(any(actual))
                            digest.update(struct.pack('<Id', frame_index, expected_time))
                            digest.update(hashlib.sha256(actual).digest())
                        item_result = dict(edge=list(edge), layer=layer_index,
                                           positions=len(positions), frames_read=len(indexes),
                                           nonzero_pcm_windows=nonzero,
                                           frame_range=[indexes[0], indexes[-1]],
                                           reads_sha256=digest.hexdigest())
                        if triple_mode and edge == triple_key:
                            stage = I.from_address(work + 0x28).value
                            assert stage in (0, 1)
                            item_result['stage'] = stage
                        if conditional_transition and full_loop:
                            item_result['reverse'] = reverse
                        if selector:
                            selection_digest = hashlib.sha256()
                            masks = Counter()
                            selector_nonzero = 0
                            original_mode = B.from_address(ring + 0x78).value
                            assert original_mode == 0
                            B.from_address(ring + 0x78).value = 1
                            try:
                                for time, position in enumerate(positions, begin):
                                    frame_index = min(layer['frames'] - 1, math.ceil(position))
                                    clear_frame(selector_frame, 0x6f0fffffff, 1, 0)
                                    c.memset(selector_pcm, 0xa5, (WINDOW + 1) * 2 + 32)
                                    select_frame(selector_ctx, time, selector_frame,
                                                 records + time * 0x18, 1, 1)
                                    start = ((layer['core'] - layer['snd']) // 2 +
                                             frame_index * HOP - WINDOW // 2)
                                    first_sample = max(0, start)
                                    last_sample = min(layer['pcm_count'], start + WINDOW + 1)
                                    front = max(0, -start)
                                    tail = max(0, start + WINDOW + 1 - layer['pcm_count'])
                                    expected = (b'\0' * (front * 2) +
                                                data[layer['snd'] + first_sample * 2:
                                                     layer['snd'] + last_sample * 2] +
                                                b'\0' * (tail * 2))
                                    actual = c.string_at(selector_pcm + 16, (WINDOW + 1) * 2)
                                    assert len(expected) == len(actual) and actual == expected
                                    assert c.string_at(selector_pcm, 16) == b'\xa5' * 16
                                    assert c.string_at(selector_pcm + 16 + (WINDOW + 1) * 2, 16) == b'\xa5' * 16
                                    output_time = D.from_address(selector_frame + 0x148).value
                                    output_mask = Q.from_address(selector_frame + 0x150).value
                                    assert math.isfinite(output_time)
                                    masks[f'{output_mask:016x}'] += 1
                                    selector_nonzero += bool(any(actual))
                                    selection_digest.update(struct.pack(
                                        '<IfIdQ', time, position, frame_index,
                                        output_time, output_mask))
                                    selection_digest.update(hashlib.sha256(actual).digest())
                            finally:
                                B.from_address(ring + 0x78).value = original_mode
                            item_result['selector'] = dict(
                                records=len(positions), nonzero_pcm_windows=selector_nonzero,
                                output_masks=dict(masks), sha256=selection_digest.hexdigest())
                        items_read.append(item_result)
                    if conditional_transition:
                        assert len(items_read) >= 1
                    else:
                        assert len(items_read) == 2
                    selected_edges = {tuple(item['edge']) for item in items_read}
                    if conditional_transition:
                        assert selected_edges.issubset(set(wanted))
                        assert wanted[0] in selected_edges
                    else:
                        assert selected_edges == set(wanted), (
                            [item['edge'] for item in items_read], wanted)
                    if third is None and second == 'Sil' and first not in model.ddi_data_dict['sta']:
                        assert all(P.from_address(items + index * 0x140 + 0x20).value
                                   not in material['stationary_parts']
                                   for index in range(I.from_address(ring + 0x34).value))
                    if (full_loop and len(full_loop_runs) < full_loop_limit and
                            expected_at and max(expected_at) >= full_loop_min_target_time):
                        assert expected_at
                        sta_at = {}
                        if sta_specs:
                            sta_by_part = dict(zip(material['stationary_parts'],
                                                   sta_specs))
                            for work_index in range(I.from_address(ring + 0x34).value):
                                work = items + work_index * 0x140
                                part = P.from_address(work + 0x20).value
                                if part not in sta_by_part:
                                    continue
                                sta_layer = sta_by_part[part]
                                begin = I.from_address(work).value
                                end = I.from_address(work + 4).value
                                offset = I.from_address(work + 0x94).value
                                for time in range(begin, end):
                                    index = offset + time - begin
                                    assert 0 <= index < sta_layer['frames']
                                    assert time not in expected_at and time not in sta_at
                                    sta_at[time] = (sta_layer, index)
                        pcm_checks = [0, 0]
                        sta_pcm_checks = [0, 0]
                        first_differences = []
                        sta_first_differences = []

                        def observe_pcm(time, output_context):
                            if time not in expected_at and time not in sta_at:
                                return
                            stationary_time = time in sta_at
                            if stationary_time:
                                layer_at_time, frame_index = sta_at[time]
                            else:
                                edge_at_time, layer_at_time, position, reverse_at_time = expected_at[time]
                                frame_index = min(layer_at_time['frames'] - 1,
                                                  math.floor(position) if reverse_at_time
                                                  else math.ceil(position))
                            start = ((layer_at_time['core'] - layer_at_time['snd']) // 2 +
                                     frame_index * HOP - WINDOW // 2)
                            first_sample = max(0, start)
                            last_sample = min(layer_at_time['pcm_count'],
                                              start + WINDOW + 1)
                            expected = (b'\0' * (max(0, -start) * 2) +
                                        data[layer_at_time['snd'] + first_sample * 2:
                                             layer_at_time['snd'] + last_sample * 2] +
                                        b'\0' * (max(0, start + WINDOW + 1 -
                                                     layer_at_time['pcm_count']) * 2))
                            assert len(expected) == (WINDOW + 1) * 2
                            pointer_to_pcm = P.from_address(output_context + 0x29510).value
                            assert pointer_to_pcm
                            actual = c.string_at(pointer_to_pcm, (WINDOW + 1) * 2)
                            checks = sta_pcm_checks if stationary_time else pcm_checks
                            checks[0] += 1
                            if actual == expected:
                                checks[1] += 1
                            elif stationary_time and len(sta_first_differences) < 3:
                                sta_first_differences.append(dict(
                                    time=time, selected_frame=frame_index,
                                    expected_sha256=hashlib.sha256(expected).hexdigest(),
                                    observed_sha256=hashlib.sha256(actual).hexdigest()))
                            elif not stationary_time and len(first_differences) < 3:
                                first_differences.append(dict(
                                    time=time, edge=list(edge_at_time), position=position,
                                    selected_frame=frame_index, reverse=reverse_at_time,
                                    expected_sha256=hashlib.sha256(expected).hexdigest(),
                                    observed_sha256=hashlib.sha256(actual).hexdigest()))
                            if full_loop_frame_observer is not None:
                                full_loop_frame_observer(time, output_context, ring)

                        setup = (full_loop_engine_setup(n, voice, model, raw)
                                 if full_loop_engine_setup is not None else nullcontext(None))
                        with setup as engine_prepare:
                            output_run = probe_full_loop(
                                n, context, ring, max(set(expected_at) | set(sta_at)),
                                replay_from_start=True,
                                analysis_mode=2, initialize_output_gate=True,
                                initialize_native_candidate=True, source_event_mode=0,
                                compact_full_loop_trace=True, pcm_observer=observe_pcm,
                                output_block_observer=full_loop_output_observer,
                                engine_prepare=engine_prepare)
                        assert output_run['status'] == 0
                        assert output_run['output_ready'] == 1
                        assert output_run['output_bytes'] == 512
                        assert output_run['guard_intact']
                        assert output_run['effective_spectrum_selection_byte'] == 0
                        assert pcm_checks[0] == len(expected_at)
                        assert sta_pcm_checks[0] == len(sta_at)
                        loop_row = dict(
                            target_time=max(set(expected_at) | set(sta_at)),
                            blocks=output_run['blocks_run'],
                            pcm_observer_checks=pcm_checks[0],
                            pcm_observer_matches=pcm_checks[1],
                            first_pcm_differences=first_differences,
                            nonzero_blocks=output_run['nonzero_blocks'],
                            analysis_nonzero_blocks=output_run['analysis_nonzero_blocks'],
                            first_nonzero_time=output_run['first_nonzero_time'],
                            stream_sha256=output_run['output_stream_sha256'])
                        if sta_specs:
                            loop_row['sta_pcm_checks'] = sta_pcm_checks[0]
                            loop_row['sta_pcm_matches'] = sta_pcm_checks[1]
                            loop_row['sta_first_differences'] = sta_first_differences
                        full_loop_runs.append(loop_row)
                    observations.append(items_read)
                    return dict(items=len(items_read), frames_read=sum(
                        item['frames_read'] for item in items_read))

                material['verify_consumption'] = inspect
                return material

            first_event_tokens = sequence[:note_split] if note_split is not None else sequence
            report = run(dll, True, False, True, build, [list(first_event_tokens)],
                         material_modes=(0,), commit_control3=64)
    finally:
        assert crt.fclose(handle) == 0
    cases = [row for row in report['rows'] if row['mode'] == 0 and 'commit' in row]
    assert len(cases) == len(observations) == 36
    summaries = {}
    for edge in wanted:
        relevant = [item for case in observations for item in case
                    if tuple(item['edge']) == edge]
        if conditional_transition:
            if second == 'Sil' and edge == wanted[0]:
                assert len(relevant) == 36
        else:
            assert len(relevant) == 36
        summaries['->'.join(edge)] = dict(work_items=len(relevant),
                                         positions=sum(item['positions'] for item in relevant),
                                         frame_reads=sum(item['frames_read'] for item in relevant),
                                         nonzero_pcm_windows=sum(item['nonzero_pcm_windows']
                                                                 for item in relevant),
                                         observed_layers=sorted({item['layer'] for item in relevant}),
                                         cases_sha256=hashlib.sha256(json.dumps(
                                             relevant, sort_keys=True).encode()).hexdigest())
        if triple_mode and edge == triple_key:
            summaries['->'.join(edge)]['stage_counts'] = dict(sorted(
                Counter(item['stage'] for item in relevant).items()))
            summaries['->'.join(edge)]['stage_positions'] = dict(sorted(
                Counter({stage: sum(item['positions'] for item in relevant
                                    if item['stage'] == stage)
                         for stage in (0, 1)}).items()))
            summaries['->'.join(edge)]['stage_frame_reads'] = dict(sorted(
                Counter({stage: sum(item['frames_read'] for item in relevant
                                    if item['stage'] == stage)
                         for stage in (0, 1)}).items()))
        if selector:
            summaries['->'.join(edge)]['selector_records'] = sum(
                item['selector']['records'] for item in relevant)
            summaries['->'.join(edge)]['selector_nonzero_pcm_windows'] = sum(
                item['selector']['nonzero_pcm_windows'] for item in relevant)
    result = dict(bank=bank, first=first, second=second,
                ddi_sha256=identity['ddi_sha256'], dse_sha256=SHA256,
                commits=len(cases),
                art_work_items=sum(len(case) for case in observations),
                art_positions=sum(item['positions'] for case in observations for item in case),
                frame_reads=sum(item['frames_read'] for case in observations for item in case),
                nonzero_pcm_windows=sum(item['nonzero_pcm_windows']
                                        for case in observations for item in case),
                by_edge=summaries,
                scope='Installed V4X DDI/DDB read-only. Synthetic indexed two-phoneme '
                      'event selects both ART edges; each selected source frame is read '
                      'through DSE mode-0 file loader and checked against DDB frame time '
                      'and bounded PCM window. No Editor timing or final audio output.')
    if triple_mode:
        result['third'] = third
    if pair_only:
        result['tokens'] = list(sequence)
        result['target_edge'] = list(target_edge)
    if note_split is not None:
        result['note_split'] = note_split
    if note_gap:
        result['note_gap'] = note_gap
    if allow_missing_terminal_sta:
        result['allow_missing_terminal_sta'] = True
    if conditional_transition:
        result['selected_transition_cases'] = sum(
            any(tuple(item['edge']) == target_edge for item in case)
            for case in observations)
        result['scope'] = (
            'Installed V4X DDI/DDB read-only. Synthetic indexed multi-phoneme '
            'event includes available pair ART along a Silence-to-STA path. '
            'Actually selected frame times and bounded PCM windows are '
            'compared with DDB. No Editor timing or listening test.'
            if pair_only else
            'Installed V4X DDI/DDB read-only. Synthetic indexed three-phoneme '
            'event includes the installed triple ART and available pair ART; '
            'actual selected frame times and bounded PCM windows are checked '
            'against DDB. No Editor timing or listening test.'
            if triple_mode else
            'Installed V4X DDI/DDB read-only. Synthetic indexed three-token '
            'event selects available pair ART parts; entry, middle, and '
            'conditional Silence exit positions are checked against installed '
            'DDB time and PCM windows. No Editor timing or listening test.'
            if third is not None else
            'Installed V4X DDI/DDB read-only. Synthetic indexed '
            'same-token event selects actual ART parts; both entry '
            'and repeated-token ART read positions are checked against '
            'installed DDB time and PCM windows. Selection can differ '
            'by event condition. No Editor timing or listening test.'
            if first == second else
            'Installed V4X DDI/DDB read-only. Synthetic indexed '
            'Silence-ending event selects actual ART parts; entry and '
            'conditional exit read positions are checked against installed '
            'DDB time and PCM windows. No Editor timing or listening test.')
    if selector:
        result['selector_records'] = sum(item['selector']['records']
                                         for case in observations for item in case)
        result['selector_nonzero_pcm_windows'] = sum(
            item['selector']['nonzero_pcm_windows']
            for case in observations for item in case)
        result['selector_output_masks'] = dict(sum((Counter(item['selector']['output_masks'])
                                                    for case in observations for item in case),
                                                   Counter()))
        result['scope'] = ('Installed V4X DDI/DDB read-only. Indexed two-phoneme ART '
                           'work items enter native no-layer-blend frame selector after '
                           'temporarily setting event read control byte to 1; every '
                           'selected PCM window is compared against the DDB SND range. '
                           'The event byte is restored after each item. Original-mode '
                           'complete output and Editor timing remain untested.')
    if full_loop:
        assert len(full_loop_runs) == full_loop_limit
        result['full_loop'] = dict(
            events=full_loop_limit,
            blocks=sum(row['blocks'] for row in full_loop_runs),
            pcm_observer_checks=sum(row['pcm_observer_checks'] for row in full_loop_runs),
            pcm_observer_matches=sum(row['pcm_observer_matches'] for row in full_loop_runs),
            nonzero_blocks=sum(row['nonzero_blocks'] for row in full_loop_runs),
            analysis_nonzero_blocks=sum(row['analysis_nonzero_blocks']
                                        for row in full_loop_runs),
            rows=full_loop_runs)
        if sta_specs:
            result['full_loop']['sta_pcm_checks'] = sum(
                row['sta_pcm_checks'] for row in full_loop_runs)
            result['full_loop']['sta_pcm_matches'] = sum(
                row['sta_pcm_matches'] for row in full_loop_runs)
        result['scope'] = (
            'Installed V4X DDI/DDB read-only. Synthetic indexed multi-phoneme '
            'events include installed pair ART along a Silence-to-STA path. '
            'Actually selected ART and final STA input PCM windows in the '
            'native mode-0 output loop are compared with DDB SND bytes. '
            'Editor timing and listening are not tested.' if pair_only else
            'Installed V4X DDI/DDB read-only. Synthetic indexed three-phoneme '
            'events include installed triple ART and available pair ART. '
            'Actually selected ART and STA input PCM windows in the native '
            'mode-0 output loop are compared with DDB SND bytes. Editor timing '
            'and listening are not tested.' if triple_mode else
            'Installed V4X DDI/DDB read-only. Synthetic indexed three-token '
            'events replay original mode-0 records through the native output '
            'loop; actually selected pair ART and available STA input PCM '
            'windows are compared with DDB SND bytes. Editor timing and '
            'listening are not tested.' if third is not None else
            'Installed V4X DDI/DDB read-only. Synthetic indexed '
            'Silence-ending events replay original mode-0 records through the '
            'native output loop; actually selected ART and available STA input '
            'PCM windows are compared with DDB SND bytes. Editor timing and '
            'listening are not tested.' if second == 'Sil' else
            'Installed V4X DDI/DDB read-only. Selected synthetic indexed '
            'two-phoneme events replay original mode-0 records through the native '
            'output loop; actually selected ART-position PCM input windows are '
            'compared with DDB SND bytes. Repeated-token events may select '
            'different ART parts by condition; installed STA input PCM windows '
            'are also compared with DDB. Editor timing and listening are not '
            'tested.' if first == second else
            'Installed V4X DDI/DDB read-only. Selected synthetic indexed '
            'two-phoneme events replay original mode-0 records through the native '
            'output loop; ART-position PCM input windows are compared with DDB '
            'SND bytes. Editor timing and final listening are not tested.')
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--bank', required=True)
    parser.add_argument('--first', required=True)
    parser.add_argument('--second', required=True)
    parser.add_argument('--third')
    parser.add_argument('--fourth')
    parser.add_argument('--pair-only', action='store_true')
    parser.add_argument('--target-left')
    parser.add_argument('--target-right')
    parser.add_argument('--note-split', type=int)
    parser.add_argument('--note-gap', type=int, default=0)
    parser.add_argument('--allow-missing-terminal-sta', action='store_true')
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--selector', action='store_true')
    parser.add_argument('--full-loop-limit', type=int, default=0)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    assert (args.target_left is None) == (args.target_right is None)
    result = verify(args.dll, args.bank, args.first, args.second,
                    args.manifest, args.voice_root, args.ddb_tools,
                    args.selector, args.full_loop_limit, args.third,
                    args.fourth, args.pair_only,
                    (args.target_left, args.target_right)
                    if args.target_left is not None else None,
                    args.note_split, args.note_gap,
                    args.allow_missing_terminal_sta)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                           encoding='utf-8')
    print(json.dumps(dict(commits=result['commits'],
                          art_work_items=result['art_work_items'],
                          frame_reads=result['frame_reads'])))
