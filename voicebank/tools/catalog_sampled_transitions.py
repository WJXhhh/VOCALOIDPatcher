"""Inventory every ART key/stage and PHDC class in pinned sampled voice banks.

Read DDI metadata only. Emit names, counts, hashes and layout observations;
never copy frame payloads, PCM, installed paths or native source code.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
from collections import Counter, defaultdict
from pathlib import Path

from analyze_reference_layers import walk_art
from verify_planned_ddi import load_public_parser


def sha(data):
    return hashlib.sha256(data).hexdigest()


def family(tokens):
    if any(token.startswith('*#') for token in tokens):
        return 'star_suffix'
    if any(token.startswith('br') for token in tokens):
        return 'breath_name'
    if any('#' in token for token in tokens):
        return 'numbered_suffix'
    return 'plain'


def catalog(reference, roots, parser_root, csv_path):
    reference_bytes = reference.read_bytes()
    pinned = json.loads(reference_bytes)['cross_reference']
    parser_hash = sha((parser_root / 'utils/ddi_utils.py').read_bytes())
    if parser_hash != pinned['parser_sha256']:
        raise ValueError('Reference parser hash differs')
    model_type = load_public_parser(parser_root)
    found = defaultdict(set)
    for root in roots:
        if not root.is_dir():
            raise ValueError('Voice root is not a directory')
        for path in root.rglob('*.ddi'):
            found[path.stem].add(path.resolve())
    banks = []
    totals, class_names = Counter(), set()
    fields = ['bank', 'arity', 'path', 'parts', 'stage', 'limit', 'source', 'target',
              'source_groups', 'target_groups', 'source_unvoiced', 'target_unvoiced',
              'boundary', 'lexical_family', 'alignment_group_counts', 'available']
    with csv_path.open('w', encoding='utf-8', newline='') as output:
        writer = csv.DictWriter(output, fieldnames=fields)
        writer.writeheader()
        for generation in ['v5', 'v4x']:
            for identity in pinned[generation]:
                bank = identity['bank']
                matches = found[bank]
                if len(matches) != 1:
                    raise ValueError(f'Expected exactly one pinned DDI for {bank}')
                path = next(iter(matches))
                if path.stat().st_size > 256 * 1024 * 1024:
                    raise ValueError('DDI exceeds metadata size budget')
                raw = path.read_bytes()
                digest = sha(raw)
                if digest != identity['ddi_sha256']:
                    raise ValueError(f'Pinned DDI hash differs for {bank}')
                model = model_type(raw)
                model.read()
                memberships = defaultdict(set)
                groups = {}
                for name, members in model.phdc_data['phg2'].items():
                    groups[name] = sorted(members.values())
                    class_names.add(name)
                    for token in members.values():
                        memberships[token].add(name)
                phonemes = model.phdc_data['phoneme']
                all_tokens = set(phonemes['voiced']) | set(phonemes['unvoiced'])
                unvoiced = set(phonemes['unvoiced'])
                silence = set(groups.get('Silence', []))
                stationary = set(model.ddi_data_dict['sta'])
                counts, lexical, routes, layouts = (Counter() for _ in range(4))
                incoming = defaultdict(lambda: defaultdict(Counter))
                empty, alignment_errors, examples = [], [], {}
                art_tokens = set()
                for root_node in model.art_data.values():
                    for key, parts in walk_art(root_node):
                        tokens = tuple(token[1:] if token.startswith('^') else token
                                       for token in key)
                        arity = len(key)
                        if arity < 2:
                            raise ValueError('ART path has fewer than two tokens')
                        art_tokens.update(tokens)
                        counts['directory_keys'] += 1
                        counts['parts'] += len(parts)
                        if not parts:
                            counts['empty_keys'] += 1
                            empty.append(list(key))
                        else:
                            counts[f'arity_{arity}_keys'] += 1
                            counts[f'arity_{arity}_parts'] += len(parts)
                            lexical[f'{arity}:{family(tokens)}'] += 1
                        group_counts = Counter(len(part['frame_align']) for part in parts)
                        for number, count in group_counts.items():
                            layouts[f'{arity}:{number}'] += count
                        for part in parts:
                            if len(part['frame_align']) != 2 * (arity - 1):
                                alignment_errors.append(list(key))
                            for group in part['frame_align']:
                                if not isinstance(group, dict) or not (
                                        group['start'] <= group['start2'] <
                                        group['end2'] <= group['end']):
                                    alignment_errors.append(list(key))
                        for stage, (left, right) in enumerate(zip(tokens, tokens[1:])):
                            if left in silence and right in silence:
                                boundary = 'silence_to_silence'
                            elif left in silence:
                                boundary = 'silence_entry'
                            elif right in silence:
                                boundary = 'silence_exit'
                            elif left == right:
                                boundary = 'same_token'
                            else:
                                boundary = 'internal'
                            left_groups = '/'.join(sorted(memberships[left])) or 'Unclassified'
                            right_groups = '/'.join(sorted(memberships[right])) or 'Unclassified'
                            writer.writerow(dict(bank=bank, arity=arity, path=' '.join(key),
                                parts=len(parts), stage=stage, limit=arity - 1,
                                source=left, target=right, source_groups=left_groups,
                                target_groups=right_groups, source_unvoiced=left in unvoiced,
                                target_unvoiced=right in unvoiced, boundary=boundary,
                                lexical_family=family(tokens),
                                alignment_group_counts='/'.join(map(str, sorted(group_counts))),
                                available=bool(parts)))
                            counts['csv_rows'] += 1
                            if parts:
                                counts['nonempty_stage_rows'] += 1
                                counts[boundary] += 1
                                routes[f'{left_groups} -> {right_groups}'] += 1
                                incoming[right][left][f'arity_{arity}_stage_{stage}'] += len(parts)
                                if arity == 2 and (left, right) in [('u', 'l'), ('7', 'l'), ('e', '4'), ('M', '4')]:
                                    examples[' '.join(key)] = len(parts)
                row = dict(bank=bank, product_group=generation, ddi_sha256=digest,
                           phonemes=len(all_tokens), counts=dict(counts),
                           class_members=groups,
                           phonemes_without_group=sorted(token for token in all_tokens
                                                          if not memberships.get(token)),
                           overlapping_groups={token: sorted(names) for token, names in sorted(memberships.items()) if len(names) > 1},
                           phonemes_without_art=sorted(all_tokens - art_tokens),
                           art_tokens_not_in_phdc=sorted(art_tokens - all_tokens),
                           stationary_tokens=sorted(stationary),
                           empty_keys=empty, alignment_errors=alignment_errors,
                           lexical_key_counts=dict(sorted(lexical.items())),
                           alignment_layout_counts=dict(sorted(layouts.items())),
                           stage_class_matrix=dict(sorted(routes.items())),
                           incoming_sources={right: {left: dict(sorted(kinds.items()))
                               for left, kinds in sorted(lefts.items())}
                               for right, lefts in sorted(incoming.items())},
                           illustrative_boundary_material_counts=examples)
                expected_art = identity.get('parts_by_kind', {}).get('ART')
                if expected_art is not None and counts['parts'] != expected_art:
                    raise ValueError(f'ART count differs from reference for {bank}')
                banks.append(row)
                totals.update(counts)
                print(json.dumps(dict(bank=bank, keys=counts['directory_keys'],
                                      empty=counts['empty_keys'], parts=counts['parts'])), flush=True)
    csv_bytes = csv_path.read_bytes()
    return dict(schema_version=1, date='2026-10-15',
                tool_sha256=sha(Path(__file__).read_bytes()),
                reference_sha256=sha(reference_bytes), parser_sha256=parser_hash,
                bank_count=len(banks), totals=dict(totals),
                class_names=sorted(class_names), csv_sha256=sha(csv_bytes),
                banks=banks,
                scope='Exhaustive hash-pinned ART directory/stage and PHDC inventory '
                      'for the supplied 15-bank reference. Incoming sources are '
                      'structural candidates, not validated donor compatibility. '
                      'No DDB, PCM, synthesis, editor or licensing operations.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--reference', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, action='append', required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--csv', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = catalog(args.reference, args.voice_root, args.ddb_tools, args.csv)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(banks=report['bank_count'], totals=report['totals'],
                          alignment_errors=sum(len(b['alignment_errors']) for b in report['banks']))))
