"""Read installed V4X Silence-onset frames from direct-tree ART work items."""
import argparse
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
from region_commit_material import connected
from verify_planned_ddi import load_public_parser
from verify_reference_ddi import pointer
from verify_region_event_production import run


WINDOW = 2048
HOP = 256


def verify(dll: Path, bank: str, manifest: Path, voice_root: Path,
           ddb_tools: Path, offset: int, phoneme: str = 'a',
           following: str | None = None, breath: bool = False) -> dict:
    source = json.loads(manifest.read_text(encoding='utf-8'))
    assert hashlib.sha256((ddb_tools / 'utils' / 'ddi_utils.py').read_bytes()).hexdigest() == source['parser_sha256']
    identity = next(row for row in source['banks'] if row['bank'] == bank)
    matches = list(voice_root.rglob(f'{bank}.ddi'))
    assert len(matches) == 1
    ddi = matches[0]
    ddb = ddi.with_suffix('.ddb')
    raw = ddi.read_bytes()
    assert hashlib.sha256(raw).hexdigest() == identity['ddi_sha256']
    region_audit = json.loads(manifest.with_name('105_v4x_regions.json').read_text(encoding='utf-8'))
    audited = next(row['audit'] for row in region_audit['banks'] if row['audit']['bank'] == bank)
    assert audited['ddi_sha256'] == identity['ddi_sha256']
    assert ddb.stat().st_size == audited['ddb_bytes']
    model = load_public_parser(ddb_tools)(raw)
    model.read()
    assert not breath or (phoneme.startswith('br') and following is None)
    stationary_phoneme = following or ('a' if breath else phoneme)
    assert stationary_phoneme in model.ddi_data_dict['sta']
    if following:
        assert phoneme not in model.ddi_data_dict['sta']
    parts = [layers for root in model.art_data.values()
             for key, layers in walk_art(root) if key == ('Sil', phoneme)]
    assert len(parts) == 1 and parts[0]
    next_parts = None
    if following:
        matches = [layers for root in model.art_data.values()
                   for key, layers in walk_art(root) if key == (phoneme, following)]
        assert len(matches) == 1 and matches[0]
        next_parts = matches[0]
    layer_specs = []
    for part in parts[0]:
        snd, count = pointer(part['snd'], raw, 18)
        core, _ = pointer(part['snd_start'].split('_')[0], raw, 18)
        frame_offsets = [pointer(value, raw)[0] for value in part['epr']]
        layer_specs.append(dict(pitch1=part['pitch1'], pitch2=part['pitch2'],
                                dynamics=part['dynamics'], frames=len(frame_offsets),
                                groups=[tuple(group[field] for field in
                                    ('start', 'end', 'start2', 'end2'))
                                        for group in part['frame_align']],
                                frame_offsets=frame_offsets, snd=snd, core=core,
                                pcm_count=count))
    next_specs = []
    if next_parts:
        for part in next_parts:
            next_specs.append(dict(pitch1=part['pitch1'], pitch2=part['pitch2'],
                                   dynamics=part['dynamics'], frames=len(part['epr']),
                                   groups=[tuple(group[field] for field in
                                           ('start', 'end', 'start2', 'end2'))
                                           for group in part['frame_align']]))
    crt = c.CDLL('ucrtbase.dll')
    crt._wfopen.argtypes, crt._wfopen.restype = [c.c_wchar_p, c.c_wchar_p], P
    crt.fclose.argtypes, crt.fclose.restype = [P], I
    handle = crt._wfopen(str(ddb.resolve()), 'rb')
    if not handle:
        raise OSError(f'Cannot open installed DDB: {bank}')
    observations = []
    try:
        with ddb.open('rb') as stream, mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as data:
            for layer in layer_specs:
                assert layer['snd'] + layer['pcm_count'] * 2 <= len(data)
                assert all(value + 0x20 <= len(data) for value in layer['frame_offsets'])

            def build(n, state, context, voice, table, sta, art):
                edges = (('Sil', phoneme),)
                edge_layers = {edges[0]: layer_specs}
                if following:
                    edges += ((phoneme, following),)
                    edge_layers[edges[1]] = next_specs
                material = connected(n, state, context, voice, table, sta, art,
                                     edges=edges, tree=True, edge_layers=edge_layers,
                                     stationary_phoneme=stationary_phoneme)
                stream_obj = n.alloc(0x80)
                P.from_address(stream_obj + 8).value = handle
                I.from_address(stream_obj + 0x2c).value = 0x32534d53
                P.from_address(voice + 0x1a0).value = stream_obj

                def inspect(n, state, ring, items, material):
                    assert I.from_address(ring + 0x34).value == 2
                    work = items
                    selected = P.from_address(work + 0x20).value
                    layer_index = material['parts'].index(selected) - 1
                    assert 0 <= layer_index < len(layer_specs)
                    layer = layer_specs[layer_index]
                    begin, end = I.from_address(work).value, I.from_address(work + 4).value
                    assert [begin, end] == [0, offset]
                    records = P.from_address(ring).value
                    frame_indexes = sorted({min(layer['frames'] - 1, math.ceil(
                        F.from_address(records + t * 0x18 + 8).value))
                        for t in range(begin, end)})
                    assert frame_indexes and frame_indexes[0] >= 0
                    if offset >= 100:
                        assert frame_indexes == list(range(layer['frames']))

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
                    digest = hashlib.sha256()
                    nonzero = 0
                    for index in frame_indexes:
                        source_offset = layer['frame_offsets'][index]
                        expected_time, _ = read_frame(data, source_offset)
                        c.memset(destination, 0xa5, (WINDOW + 1) * 2 + 32)
                        loaded.value = 0
                        load(read_context, work, index, output, 0, 1, c.byref(loaded))
                        assert loaded.value == 1
                        assert D.from_address(output + 0x148).value == expected_time
                        start = (layer['core'] - layer['snd']) // 2 + index * HOP - WINDOW // 2
                        first, last = max(0, start), min(layer['pcm_count'], start + WINDOW + 1)
                        front, tail = max(0, -start), max(0, start + WINDOW + 1 - layer['pcm_count'])
                        expected = (b'\0' * (front * 2) +
                                    data[layer['snd'] + first * 2:layer['snd'] + last * 2] +
                                    b'\0' * (tail * 2))
                        actual = c.string_at(destination + 16, (WINDOW + 1) * 2)
                        assert actual == expected
                        assert c.string_at(destination, 16) == b'\xa5' * 16
                        assert c.string_at(destination + 16 + (WINDOW + 1) * 2, 16) == b'\xa5' * 16
                        nonzero += bool(any(actual))
                        digest.update(struct.pack('<Id', index, expected_time))
                        digest.update(hashlib.sha256(actual).digest())
                    midi_pitch = int(F.from_address(
                        state + 0x58 + (1 - I.from_address(state + 0x7a8).value) * 0x3a8 + 8).value
                        / 100) + 69
                    nearest = min(range(len(layer_specs)), key=lambda candidate:
                                  abs(layer_specs[candidate]['pitch1'] - (midi_pitch - 69) * 100))
                    assert layer_index == nearest
                    result = dict(midi_pitch=midi_pitch, selected_layer=layer_index,
                        selected_layer_frames=layer['frames'],
                        all_selected_frames_read=frame_indexes == list(range(layer['frames'])),
                        frame_reads=len(frame_indexes), nonzero_pcm_windows=nonzero,
                        frame_index_range=[frame_indexes[0], frame_indexes[-1]],
                        reads_sha256=digest.hexdigest())
                    observations.append(result)
                    return result

                material['post_direct_commit'] = inspect
                return material

            tokens = [phoneme, following] if following else [phoneme]
            report = run(dll, True, False, True, build, [tokens],
                         material_modes=(1,), commit_control3=64,
                         commit_note_start_offset=offset)
    finally:
        assert crt.fclose(handle) == 0
    assert len(observations) == 36
    grouped = {}
    for row in observations:
        key = str(row['midi_pitch'])
        grouped.setdefault(key, []).append(row)
    summaries = {}
    for key, rows in grouped.items():
        assert len(rows) == 12
        assert len({json.dumps(row, sort_keys=True) for row in rows}) == 1
        summaries[key] = rows[0]
    result = dict(bank=bank, ddi_sha256=identity['ddi_sha256'],
                ddb_bytes=ddb.stat().st_size, dse_sha256=SHA256,
                note_start_offset=offset, commits=len(observations),
                frame_reads=sum(row['frame_reads'] for row in observations),
                nonzero_pcm_windows=sum(row['nonzero_pcm_windows'] for row in observations),
                by_pitch=summaries,
                scope='Installed DDI/DDB read-only. Native direct-tree event built the ART '
                      'work item; its selected source frames were read through native DSE '
                      'mode-0 loader and checked against DDB SND windows. No Editor or full audio output.')
    if phoneme != 'a':
        result['phoneme'] = phoneme
    if following:
        result['following'] = following
    if breath:
        result['breath'] = True
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--bank', required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--offset', type=int, default=20)
    parser.add_argument('--phoneme', default='a')
    parser.add_argument('--following')
    parser.add_argument('--breath', action='store_true')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    assert 0 < args.offset <= 400
    result = verify(args.dll, args.bank, args.manifest, args.voice_root,
                    args.ddb_tools, args.offset, args.phoneme, args.following,
                    args.breath)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                           encoding='utf-8')
    print(json.dumps(dict(commits=result['commits'], frame_reads=result['frame_reads'])))
