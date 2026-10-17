#!/usr/bin/env python3
"""Read-only DDI/DDB reference audit. Emit counts, never PCM or frame payloads.

Uses an explicitly supplied external ddb-tools parser for the DDI tree and an
independent bounded DDB walk. Structural errors fail; unproven geometry rules
are reported as observations rather than silently imposed on every voicebank.
"""

from __future__ import annotations

import argparse
import bisect
import hashlib
import json
import mmap
import re
import struct
import sys
from collections import Counter, defaultdict
from pathlib import Path

from analyze_reference_layers import walk_art
from verify_planned_ddi import load_public_parser
import probe_frm2


class AuditError(Exception):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AuditError(message)


def pointer(value: str, ddi: bytes, adjustment: int = 0) -> tuple[int, int | None]:
    match = re.fullmatch(r"([0-9a-fA-F]+)=([0-9a-fA-F]+)(?:_([0-9a-fA-F]+))?", value)
    require(match is not None, "invalid parser pointer encoding")
    position, offset = int(match[1], 16), int(match[2], 16)
    require(position + 8 <= len(ddi), "DDI pointer field outside file")
    require(struct.unpack_from("<Q", ddi, position)[0] == offset + adjustment,
            "parser pointer differs from original DDI field")
    count = int(match[3], 16) if match[3] else None
    if count is not None:
        require(position >= 4 and struct.unpack_from("<I", ddi, position - 4)[0] == count,
                "parser sample count differs from original DDI field")
    return offset, count


def index_ddb(data) -> tuple[dict, dict]:
    frames, sounds = {}, {}
    position = 0
    while position < len(data):
        probe_frm2.require_range(position, 8, len(data), "DDB header")
        magic, size = struct.unpack_from("<4sI", data, position)
        require(size >= 8 and position + size <= len(data), "invalid DDB chunk extent")
        if magic == b"FRM2":
            _, kind, mask = probe_frm2.validate_frame_at(data, position, len(data))
            frames[position] = (size, kind, mask)
        elif magic == b"SND ":
            require(size >= 18, "truncated SND header")
            rate, channels, count = struct.unpack_from("<IHI", data, position + 8)
            require(rate > 0 and channels > 0 and size == 18 + count * 2,
                    "invalid SND dimensions")
            sounds[position] = (size, rate, channels, count)
        else:
            raise AuditError(f"unknown DDB chunk at 0x{position:x}")
        position += size
    return frames, sounds


def samples(model):
    for unit in model.sta_data.values():
        for sample in unit['stap'].values():
            yield 'STA', sample
    for root in model.art_data.values():
        for _, parts in walk_art(root):
            for sample in parts:
                yield 'ART', sample
    for sample in (model.vqm_data or {}).values():
        yield 'VQM', sample


def audit(ddi_path: Path, ddb_path: Path, model_type: type) -> dict:
    require(ddi_path.stat().st_size <= 256 * 1024 * 1024, "DDI exceeds audit size budget")
    ddi = ddi_path.read_bytes()
    model = model_type(ddi)
    model.read()
    with ddb_path.open('rb') as stream:
        require(ddb_path.stat().st_size > 0, "empty DDB")
        with mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as data:
            frames, sounds = index_ddb(data)
    sound_starts = sorted(sounds)
    frame_refs, sound_refs = Counter(), Counter()
    masks = defaultdict(Counter)
    kinds, margins, pointer_deltas, core_deltas = (defaultdict(Counter) for _ in range(4))
    alignment_counts = Counter()
    alignment_anomalies = Counter()
    geometry_examples = []
    for category, sample in samples(model):
        offsets = [pointer(item, ddi)[0] for item in sample['epr']]
        require(bool(offsets), f"{category} sample has no frames")
        snd_pointer, pcm_count = pointer(sample['snd'], ddi, 18 if category == 'ART' else 0)
        if category == 'STA':
            index = bisect.bisect_right(sound_starts, snd_pointer) - 1
            require(index >= 0, "STA pointer precedes all SND chunks")
            start = sound_starts[index]
        else:
            start = snd_pointer
        require(start in sounds, f"{category} pointer does not identify a SND")
        size, rate, channels, count = sounds[start]
        require(start <= snd_pointer < start + size, "SND pointer outside chunk")
        require(count == pcm_count, "DDI/SND PCM count mismatch")
        require(sample['fs'] == rate, "DDI/SND sample rate mismatch")
        for offset in offsets:
            require(offset in frames, f"{category} EpR does not identify a FRM2")
            _, kind, mask = frames[offset]
            frame_refs[offset] += 1
            masks[category][f'0x{mask:016x}'] += 1
            kinds[category][kind] += 1
        sound_refs[start] += 1
        margins[category][count - len(offsets) * 256] += 1
        pointer_deltas[category][snd_pointer - start] += 1
        if category == 'ART':
            # The second field is another u64 pointer, not preceded by a PCM count.
            core_encoded = sample['snd_start'].split('_', 1)[0]
            core, _ = pointer(core_encoded, ddi, 18)
            require(start + 18 <= core + 18 < start + size, "ART core pointer outside PCM")
            core_deltas[category][core - snd_pointer] += 1
            if (count - len(offsets) * 256 != 2048 or core - snd_pointer != 2048) and len(geometry_examples) < 16:
                geometry_examples.append({
                    'snd_offset': hex(start), 'frames': len(offsets), 'pcm_count': count,
                    'pcm_margin': count - len(offsets) * 256,
                    'core_delta_bytes': core - snd_pointer,
                })
            groups = sample['frame_align']
            alignment_counts[len(groups)] += 1
            previous = 0
            for group in groups:
                require(isinstance(group, dict), "unsupported ART alignment representation")
                a, b, c, d = (group[key] for key in ('start', 'end', 'start2', 'end2'))
                if not 0 <= a <= c <= d <= b <= len(offsets):
                    alignment_anomalies['inner_or_outer_bounds'] += 1
                if a != previous:
                    alignment_anomalies['outer_not_contiguous'] += 1
                previous = b
            if previous != len(offsets):
                alignment_anomalies['outer_end_not_frame_count'] += 1
    require(set(frame_refs) == set(frames), "DDI EpR set does not cover exactly all FRM2")
    require(set(sound_refs) == set(sounds), "DDI sample set does not cover exactly all SND")
    return {
        'bank': ddi_path.stem,
        'ddi_sha256': hashlib.sha256(ddi).hexdigest(),
        'ddb_bytes': ddb_path.stat().st_size,
        'frame_count': len(frames), 'snd_count': len(sounds),
        'frame_reference_multiplicity': dict(Counter(frame_refs.values())),
        'snd_reference_multiplicity': dict(Counter(sound_refs.values())),
        'masks_by_category': dict(masks), 'frame_kinds_by_category': dict(kinds),
        'pcm_minus_frames_times_256': dict(margins),
        'snd_pointer_delta': dict(pointer_deltas), 'art_core_delta': dict(core_deltas),
        'art_alignment_group_counts': dict(alignment_counts),
        'art_alignment_observations': dict(alignment_anomalies),
        'art_geometry_examples_limit_16': geometry_examples,
        'reference_sets_complete': True,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('ddi', type=Path)
    parser.add_argument('--ddb', type=Path, help='defaults to same stem as DDI')
    parser.add_argument('--ddb-tools', type=Path, required=True)
    args = parser.parse_args()
    try:
        result = audit(args.ddi, args.ddb or args.ddi.with_suffix('.ddb'),
                       load_public_parser(args.ddb_tools))
    except (AuditError, probe_frm2.ProbeError, OSError, ValueError,
            AssertionError, KeyError, TypeError, struct.error) as error:
        print(f'audit failed: {type(error).__name__}: {error}', file=sys.stderr)
        return 2
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
