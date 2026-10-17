#!/usr/bin/env python3
"""Read-only DDI material coverage; run verify_reference_ddi first.

Exact phoneme tokens are preserved. Graph reachability is a structural property,
not evidence that the engine can concatenate or synthesize a route.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from collections import Counter
from pathlib import Path

from analyze_reference_layers import walk_art
from verify_reference_ddi import load_public_parser, require


def pitch_inventory(parts: list[dict]) -> dict:
    values = [float(p['pitch1']) for p in parts]
    require(all(math.isfinite(v) for v in values), 'nonfinite pitch1')
    finite = sorted(v for v in values if v > -3e38)
    return {'parts': len(parts), 'pitched_parts': len(finite),
            'sentinel_parts': len(values) - len(finite),
            'distinct_exact_pitch1': len(set(finite)),
            'pitch1_cents': finite}


def reachable(edges: set[tuple[str, str]], start: str, reverse: bool = False) -> set[str]:
    adjacency = {}
    for a, b in edges:
        if reverse:
            a, b = b, a
        adjacency.setdefault(a, set()).add(b)
    seen, pending = {start}, [start]
    while pending:
        for token in adjacency.get(pending.pop(), ()):
            if token not in seen:
                seen.add(token)
                pending.append(token)
    return seen


def coverage(sta: list[tuple[tuple[str, ...], list[dict]]],
             art: list[tuple[tuple[str, ...], list[dict]]]) -> dict:
    rows = []
    seen = set()
    for category, units in [('STA', sta), ('ART', art)]:
        for key, parts in units:
            require((category, key) not in seen, 'duplicate material key')
            require(len(key) == 1 if category == 'STA' else len(key) in (2, 3),
                    'unsupported material key arity')
            seen.add((category, key))
            rows.append({'category': category, 'key': list(key), **pitch_inventory(parts)})
    pairs = {tuple(r['key']) for r in rows
             if r['category'] == 'ART' and len(r['key']) == 2 and r['parts']}
    sta_tokens = {r['key'][0] for r in rows if r['category'] == 'STA' and r['parts']}
    nodes = {t for edge in pairs for t in edge}
    forward, backward = reachable(pairs, 'Sil'), reachable(pairs, 'Sil', True)
    by_kind = {}
    for category, arity in [('STA', 1), ('ART', 2), ('ART', 3)]:
        subset = [r for r in rows if r['category'] == category and len(r['key']) == arity]
        by_kind[f'{category}{arity}'] = {
            'directory_keys': len(subset), 'material_keys': sum(r['parts'] > 0 for r in subset),
            'parts': sum(r['parts'] for r in subset),
            'part_count_histogram': dict(sorted(Counter(r['parts'] for r in subset).items())),
            'keys_with_repeated_exact_pitch1': sum(
                r['pitched_parts'] > r['distinct_exact_pitch1'] for r in subset),
            'sentinel_only_keys': sum(r['parts'] > 0 and r['pitched_parts'] == 0 for r in subset)}
    return {'by_kind': by_kind,
            'empty_art_keys': [r['key'] for r in rows if r['category'] == 'ART' and not r['parts']],
            'pair_graph': {'nodes': len(nodes), 'edges': len(pairs),
                'sil_present': 'Sil' in nodes,
                'nodes_unreachable_from_sil': sorted(nodes - forward),
                'nodes_without_route_to_sil': sorted(nodes - backward),
                'sta_without_direct_sil_attack': sorted(t for t in sta_tokens if ('Sil', t) not in pairs),
                'sta_without_direct_sil_release': sorted(t for t in sta_tokens if (t, 'Sil') not in pairs),
                'sta_without_any_incoming_pair': sorted(sta_tokens - {b for a, b in pairs}),
                'sta_without_any_outgoing_pair': sorted(sta_tokens - {a for a, b in pairs})},
            'material_inventory': rows}


def analyze(path: Path, model_type: type) -> dict:
    require(path.stat().st_size <= 256 * 1024 * 1024, 'DDI exceeds size budget')
    raw = path.read_bytes()
    model = model_type(raw)
    model.read()
    sta = [((u['phoneme'],), list(u['stap'].values())) for u in model.sta_data.values()]
    art = [entry for root in model.art_data.values() for entry in walk_art(root)]
    return {'bank': path.stem, 'ddi_sha256': hashlib.sha256(raw).hexdigest(),
            **coverage(sta, art)}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('ddi', type=Path)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--include-inventory', action='store_true',
                        help='include per-key pitch coordinates; default prints aggregates')
    args = parser.parse_args()
    result = analyze(args.ddi, load_public_parser(args.ddb_tools))
    if not args.include_inventory:
        result.pop('material_inventory')
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
