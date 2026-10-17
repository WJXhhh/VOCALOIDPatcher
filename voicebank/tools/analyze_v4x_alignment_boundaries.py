"""Summarize installed V4X ART alignment and PCM margins by token shape."""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path

from analyze_reference_layers import walk_art
from verify_reference_ddi import load_public_parser, pointer


def token_shape(key: tuple[str, ...]) -> str:
    if any(token.startswith('br') for token in key):
        return 'breath_token'
    if any(token.startswith('*#') for token in key):
        return 'star_suffix_token'
    if any('#' in token for token in key):
        return 'numbered_suffix_token'
    return 'plain_token'


def analyze_bank(path: Path, expected_sha256: str, model_type: type) -> dict:
    raw = path.read_bytes()
    digest = hashlib.sha256(raw).hexdigest()
    assert digest == expected_sha256
    model = model_type(raw)
    model.read()
    groups_by_category = defaultdict(Counter)
    key_count = Counter()
    part_count = Counter()
    layer_hist = defaultdict(Counter)
    trim_by_index = defaultdict(Counter)
    margin_hist = Counter()
    short_margin = []
    frames = 0
    groups_total = 0
    for root in model.art_data.values():
        for key, parts in walk_art(root):
            if not parts:
                continue
            category = ('triple/' if len(key) == 3 else 'pair/') + token_shape(key)
            key_count[category] += 1
            layer_hist[category][len(parts)] += 1
            for layer_index, part in enumerate(parts):
                part_count[category] += 1
                offsets = part['epr']
                alignment = part['frame_align']
                assert len(alignment) == (4 if len(key) == 3 else 2)
                frames += len(offsets)
                groups_total += len(alignment)
                previous_end = 0
                part_narrowed = False
                for group_index, group in enumerate(alignment):
                    start, end, start2, end2 = (group[name] for name in
                                                 ('start', 'end', 'start2', 'end2'))
                    assert start == previous_end
                    assert 0 <= start <= start2 <= end2 <= end <= len(offsets)
                    previous_end = end
                    left_trim, right_trim = start2 - start, end - end2
                    outer_len, inner_len = end - start, end2 - start2
                    category_counts = groups_by_category[category]
                    category_counts['groups'] += 1
                    category_counts['inner_same_as_outer'] += left_trim == right_trim == 0
                    category_counts['inner_narrowed'] += left_trim > 0 or right_trim > 0
                    category_counts['inner_empty'] += inner_len == 0
                    category_counts['outer_empty'] += outer_len == 0
                    category_counts['left_trim_total'] += left_trim
                    category_counts['right_trim_total'] += right_trim
                    category_counts['max_left_trim'] = max(
                        category_counts['max_left_trim'], left_trim)
                    category_counts['max_right_trim'] = max(
                        category_counts['max_right_trim'], right_trim)
                    category_counts['min_outer_length'] = min(
                        category_counts.get('min_outer_length', outer_len), outer_len)
                    category_counts['max_outer_length'] = max(
                        category_counts['max_outer_length'], outer_len)
                    trim_by_index[f'{category}/group{group_index}'][
                        f'{left_trim}/{right_trim}'] += 1
                    part_narrowed |= left_trim > 0 or right_trim > 0
                assert previous_end == len(offsets)
                groups_by_category[category]['parts_with_any_inner_narrowing'] += part_narrowed

                snd, pcm_count = pointer(part['snd'], raw, 18)
                core, _ = pointer(part['snd_start'].split('_')[0], raw, 18)
                margin = pcm_count - len(offsets) * 256
                delta = core - snd
                margin_hist[f'{margin}/{delta}'] += 1
                if margin != 2048 or delta != 2048:
                    short_margin.append(dict(key=list(key), layer=layer_index,
                                             category=category, frames=len(offsets),
                                             pcm_margin_samples=margin,
                                             core_delta_bytes=delta))
    return dict(bank=path.stem, ddi_sha256=digest,
                keys=dict(sorted(key_count.items())),
                parts=dict(sorted(part_count.items())),
                layer_histograms={key: dict(sorted(value.items()))
                                  for key, value in sorted(layer_hist.items())},
                alignment={key: dict(value) for key, value in sorted(groups_by_category.items())},
                trim_by_group={key: dict(sorted(value.items()))
                               for key, value in sorted(trim_by_index.items())},
                margin_histogram=dict(sorted(margin_hist.items())),
                short_margin=short_margin,
                frames=frames, groups=groups_total)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    args = parser.parse_args()
    manifest_bytes = args.manifest.read_bytes()
    manifest = json.loads(manifest_bytes)
    parser_digest = hashlib.sha256((args.ddb_tools / 'utils' / 'ddi_utils.py').read_bytes()).hexdigest()
    assert parser_digest == manifest['parser_sha256']
    model_type = load_public_parser(args.ddb_tools)
    rows = []
    for identity in manifest['banks']:
        matches = list(args.voice_root.rglob(identity['bank'] + '.ddi'))
        assert len(matches) == 1
        rows.append(analyze_bank(matches[0], identity['ddi_sha256'], model_type))
    assert len(rows) == 8
    keys = Counter()
    parts = Counter()
    margins = Counter()
    alignment = defaultdict(Counter)
    for row in rows:
        keys.update(row['keys'])
        parts.update(row['parts'])
        margins.update(row['margin_histogram'])
        for category, metrics in row['alignment'].items():
            for name, value in metrics.items():
                if name.startswith('min_'):
                    alignment[category][name] = min(
                        alignment[category].get(name, value), value)
                elif name.startswith('max_'):
                    alignment[category][name] = max(
                        alignment[category][name], value)
                else:
                    alignment[category][name] += value
    result = dict(manifest_sha256=hashlib.sha256(manifest_bytes).hexdigest(),
                  parser_sha256=parser_digest, banks=len(rows),
                  keys=dict(sorted(keys.items())), parts=dict(sorted(parts.items())),
                  alignment={key: dict(value) for key, value in sorted(alignment.items())},
                  margin_histogram=dict(sorted(margins.items())),
                  short_margin_parts=sum(len(row['short_margin']) for row in rows),
                  frames=sum(row['frames'] for row in rows),
                  groups=sum(row['groups'] for row in rows), rows=rows,
                  scope='Read-only V4X DDI metadata. Full ART outer partitions, '
                        'inner bounds, and serialized PCM/core margins are '
                        'checked across installed banks. Token-shape categories '
                        'are lexical and do not assert recording intent. DDB '
                        'payloads and Editor timing are not analyzed here.')
    assert sum(keys.values()) == 8431
    assert sum(parts.values()) == 23881
    assert result['frames'] == 907928 and result['groups'] == 54294
    assert result['short_margin_parts'] == 8
    args.evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps({key: result[key] for key in
                      ('banks', 'frames', 'groups', 'short_margin_parts')}))


if __name__ == '__main__':
    main()
