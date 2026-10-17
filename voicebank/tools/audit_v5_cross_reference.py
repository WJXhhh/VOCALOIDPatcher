"""Read-only V5/V4X cross-check using the established DDI/FRM2 contracts.

Retains aggregate statistics and hashes only. Never saves commercial frames or
PCM. Existing evidence and installed resources are not changed. The complete
physical/reference audit is performed for V5; V4X uses pinned prior coverage
plus a fresh metadata, TDB and VQM examination.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
import mmap
from pathlib import Path
import statistics
import struct

from analyze_reference_coverage import coverage
from analyze_reference_layers import walk_art
from analyze_reference_regions import read_frame, region_clock_errors
from pitch2_reference import reference_pitch_cents, NO_PITCH
import probe_frm2 as frm
from verify_reference_ddi import audit, load_public_parser, pointer, samples
from verify_vqm_timbre_selector_scope import TdbCursor


def sha(path):
    with path.open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


def stats(values):
    values = list(values)
    finite = [v for v in values if math.isfinite(v)]
    return dict(count=len(values), nonfinite=len(values)-len(finite),
                nonzero=sum(v != 0 for v in finite),
                minimum=min(finite) if finite else None,
                maximum=max(finite) if finite else None)


def tdb(raw, model):
    start, end = model.offset_map['tdb']
    cursor = TdbCursor(raw, start, end)
    cursor.header(b'TDB ')
    count = cursor.integer()
    assert count == len(model.tdb_data) and count <= 1024
    observed = {}
    for _ in range(count):
        cursor.header(b'TMM ')
        index, axes = cursor.integer(), cursor.integer()
        assert axes == 3 and index not in observed
        for name in ('pitch', 'dynamics', 'opening'):
            cursor.header(b'ARR ')
            assert cursor.integer() == 0, 'Nonempty templates require a separate leaf audit'
            assert cursor.string() == name
        observed[index] = cursor.string()
    assert cursor.string() == 'timbre' and cursor.position == end
    assert observed == model.tdb_data
    return dict(models=count, empty_axis_groups=3*count, template_frame_leaves=0,
                independent_cursor_match=True)


def feature_fields(block):
    position, fields = 0, {}
    for bit in range(21):
        if not block.flags & (1 << bit):
            continue
        count = 40 if bit in (10, 11) else 20 if bit in (12, 13) else 1
        if bit == 20:
            count = 0
        fields[bit] = struct.unpack_from('<' + 'f'*count, block.payload, position)
        position += count*4
    assert position == len(block.payload)
    return fields


def vqm(raw, model, data):
    rows = []
    for sample in (model.vqm_data or {}).values():
        scalar = defaultdict(list)
        flags, masks, b18, b19, trailers, bins = (Counter() for _ in range(6))
        frame_hash = hashlib.sha256()
        components, times, f0 = [], [], []
        envs = {'bit7': Counter(), 'bit17': Counter()}
        for value in sample['epr']:
            offset, _ = pointer(value, raw)
            size = struct.unpack_from('<I', data, offset+4)[0]
            payload = data[offset:offset+size]
            frame = frm.parse_frame(payload)
            assert isinstance(frame, frm.VqmFrame)
            assert frm.serialize_frame(frame) == payload
            frame_hash.update(payload)
            masks[hex(frame.mask)] += 1
            flags[hex(frame.feature_block.flags)] += 1
            b18[frame.bit18_value] += 1
            b19[frame.bit19_value] += 1
            trailers[frame.feature_trailer] += 1
            bins[len(frame.spectrum_amplitudes)] += 1
            components.append(len(frame.component_frequencies_hz))
            times.append(frame.time_seconds)
            f0.append(frame.f0_hz)
            for bit, values in feature_fields(frame.feature_block).items():
                scalar[str(bit)].extend(values)
            for name, env in [('bit7', frame.bit7_envelope), ('bit17', frame.bit17_envelope)]:
                envs[name][f'{env.flags:#x}/{len(env.values)}'] += 1
        snd, count = pointer(sample['snd'], raw)
        magic, size, rate, channels, actual_count = struct.unpack_from('<4sIIHI', data, snd)
        assert magic == b'SND ' and actual_count == count and size == 18+2*count
        pcm_hash = hashlib.sha256(data[snd+18:snd+size]).hexdigest()
        mean = statistics.fmean(1200*math.log2(v/440) for v in f0 if v > 0)
        rows.append(dict(frame_count=len(times), frame_sha256=frame_hash.hexdigest(),
            pcm_sha256=pcm_hash, pcm_count=count, rate=rate, channels=channels,
            pcm_minus_frames_times_256=count-256*len(times),
            pitch1=sample['pitch1'], pitch2=sample['pitch2'],
            mean_positive_f0_cents=mean, pitch2_minus_mean_cents=sample['pitch2']-mean,
            f0_hz=stats(f0), component_counts=stats(components), masks=dict(masks),
            spectrum_bin_histogram=dict(bins), feature_flags=dict(flags),
            feature_field_statistics={k:stats(v) for k,v in scalar.items()},
            bit18_histogram=dict(b18), bit19_histogram=dict(b19),
            feature_trailer_histogram=dict(trailers), envelope_layouts=envs,
            all_frames_python_byte_roundtrip=True,
            max_frame_clock_error_samples=max(abs(t*rate-i*256) for i,t in enumerate(times))))
    return rows


def ordinary(raw, model, data, sta, art):
    errors = defaultdict(list)
    sentinel, clocks, align = Counter(), Counter(), Counter()
    max_clock = 0.
    examples = []
    for kind, units in [('STA', sta), ('ART', art)]:
        for tokens, parts in units:
            for part in parts:
                offsets = [pointer(s, raw)[0] for s in part['epr']]
                values = [read_frame(data, off) for off in offsets]
                times, pitches = map(list, zip(*values))
                groups = part.get('frame_align') if kind == 'ART' else None
                if groups is not None:
                    align[len(groups)] += 1
                    bad, maximum = region_clock_errors(times, groups, part['fs'])
                    clocks['ART_frames'] += len(values)
                    clocks['ART_mismatches'] += bad
                    max_clock = max(max_clock, maximum)
                else:
                    maximum = max(abs(t*part['fs']-i*256) for i,t in enumerate(times))
                    clocks['STA_frames'] += len(values)
                    clocks['STA_parts_clock_mismatch'] += maximum > 1e-5
                    max_clock = max(max_clock, maximum)
                intervals = tuple((g['start'],g['end'],g['start2'],g['end2']) for g in groups) if groups else None
                predicted = reference_pitch_cents(pitches, intervals,
                    tokens if groups else None, set(model.phdc_data['phoneme']['voiced']))
                actual = part['pitch2']
                if predicted == NO_PITCH or actual <= -3e38:
                    sentinel[f'{kind}/predicted={predicted == NO_PITCH}/saved={actual <= -3e38}'] += 1
                else:
                    error = abs(actual-predicted)
                    errors[kind].append(error)
                    if error > .01 and len(examples) < 8:
                        examples.append(dict(kind=kind,key=tokens,frames=len(values),
                                             saved=actual,predicted=predicted,error_cents=error))
    # Bounded ordinary-frame numeric inspection, spaced through the physical DDB.
    representatives = []
    last = -1
    for _, parts in sta + art:
        for part in parts:
            for encoded in part['epr']:
                off, _ = pointer(encoded, raw)
                if off-last < len(data)//16:
                    continue
                size = struct.unpack_from('<I',data,off+4)[0]
                payload = data[off:off+size]
                frame = frm.parse_frame(payload)
                if not isinstance(frame, frm.MainFrame):
                    continue
                assert frm.serialize_frame(frame) == payload
                representatives.append(dict(offset=hex(off),bytes=size,
                    mask=hex(frame.mask),harmonics=len(frame.frequencies_hz),
                    primary_resonances=len(frame.resonances),
                    secondary_resonances=len(frame.secondary_resonances),
                    secondary_header=list(frame.secondary_header),
                    amplitude=stats(frame.amplitudes),
                    byte_roundtrip=True))
                last = off
    return dict(pitch2_abs_error_cents={k:{**stats(v),'within_0_01':sum(x<=.01 for x in v)}
                for k,v in errors.items()}, sentinel_cases=dict(sentinel),
                clocks=dict(clocks),maximum_clock_error_samples=max_clock,
                alignment_group_counts=dict(align),pitch2_exceptions_limit8=examples,
                ordinary_representatives=representatives)


def bank(path, model_type, full):
    raw = path.read_bytes()
    model = model_type(raw)
    model.read()
    sta = [((u['phoneme'],),list(u['stap'].values())) for u in model.sta_data.values()]
    art = [entry for root in model.art_data.values() for entry in walk_art(root)]
    all_parts = list(samples(model))
    phdc = model.phdc_data
    result = dict(bank=path.stem,component=path.parent.name,ddi_sha256=hashlib.sha256(raw).hexdigest(),
        ddi_bytes=len(raw),ddb_bytes=path.with_suffix('.ddb').stat().st_size,
        parts_by_kind=dict(Counter(c for c,p in all_parts)),
        metadata_float32_bits={name:dict(Counter(struct.pack('<f',p[name]).hex() for c,p in all_parts))
                              for name in ('unknown2','dynamics','tempo')},
        phdc_counts={k:len(v) for k,v in phdc['phoneme'].items()},
        phdc_phoneme_sha256=hashlib.sha256(json.dumps(phdc['phoneme'],sort_keys=True).encode()).hexdigest(),
        extension_tokens=[t for values in phdc['phoneme'].values() for t in values if '#' in t],
        epr_guide_entries=len(phdc['epr_guide']),tdb=tdb(raw,model))
    if full:
        result['full_reference_audit'] = audit(path,path.with_suffix('.ddb'),model_type)
        graph = coverage(sta,art)
        result['coverage'] = {k:v for k,v in graph.items() if k != 'material_inventory'}
    with path.with_suffix('.ddb').open('rb') as source, mmap.mmap(source.fileno(),0,access=mmap.ACCESS_READ) as data:
        result['vqm'] = vqm(raw,model,data)
        if full:
            result['ordinary'] = ordinary(raw,model,data,sta,art)
    return result


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--v5-root',required=True,type=Path)
    p.add_argument('--v4-root',required=True,type=Path)
    p.add_argument('--v4-manifest',required=True,type=Path)
    p.add_argument('--ddb-tools',required=True,type=Path)
    p.add_argument('--work-root',required=True,type=Path)
    a = p.parse_args()
    a.work_root.mkdir(parents=True,exist_ok=False)
    prior = Path(__file__).resolve().parents[2]/'voicebank_doc/evidence'
    (a.work_root/'prior_evidence_sha256.json').write_text(json.dumps(
        {s.name:sha(s) for s in sorted(prior.glob('*.json'))},indent=2)+'\n',encoding='utf-8')
    manifest = json.loads(a.v4_manifest.read_text(encoding='utf-8'))
    assert sha(a.ddb_tools/'utils/ddi_utils.py') == manifest['parser_sha256']
    model_type = load_public_parser(a.ddb_tools)
    result = dict(parser_sha256=manifest['parser_sha256'],
        v4_manifest_sha256=sha(a.v4_manifest),v5=[],v4x=[],
        scope='Seven installed Chinese V5 products versus eight pinned Japanese V4X products. '
              'Product/language differences do not establish generation-exclusive features. '
              'No synthesis, maker provenance, licensing changes, or resource writes.')
    paths = sorted(a.v5_root.rglob('*.ddi'))
    assert len(paths) == 7, 'Review changed installed V5 sample set'
    for path in paths:
        row = bank(path,model_type,True)
        result['v5'].append(row)
        (a.work_root/(path.stem+'.json')).write_text(json.dumps(row,indent=2)+'\n',encoding='utf-8')
        print(json.dumps(dict(bank=path.stem,frames=row['full_reference_audit']['frame_count'],
            parts=sum(row['parts_by_kind'].values()),vqm_frames=sum(v['frame_count'] for v in row['vqm']))),flush=True)
    for pinned in manifest['banks']:
        paths = list(a.v4_root.rglob(pinned['bank']+'.ddi'))
        assert len(paths) == 1 and sha(paths[0]) == pinned['ddi_sha256']
        result['v4x'].append(bank(paths[0],model_type,False))
    result['tool_sha256'] = sha(Path(__file__))
    (a.work_root/'results.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(v5_banks=len(result['v5']),v4x_banks=len(result['v4x']),complete=True)),flush=True)


if __name__ == '__main__':
    main()
