"""Classify pair ART keys outside anchored paths by available event routes."""
import argparse
from collections import Counter, deque
import hashlib
import json
from pathlib import Path

from analyze_reference_layers import walk_art
from verify_planned_ddi import load_public_parser


def shortest_paths(starts, adjacency):
    paths = {name: (name,) for name in sorted(starts)}
    pending = deque(sorted(starts))
    while pending:
        name = pending.popleft()
        for following in sorted(adjacency.get(name, ())):
            if following not in paths:
                paths[following] = (*paths[name], following)
                pending.append(following)
    return paths


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--remaining', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    args = parser.parse_args()

    manifest_bytes = args.manifest.read_bytes()
    manifest = json.loads(manifest_bytes)
    remaining_bytes = args.remaining.read_bytes()
    remaining = json.loads(remaining_bytes)
    assert remaining['pair_keys_outside_paths'] == 5168
    assert hashlib.sha256((args.ddb_tools / 'utils' / 'ddi_utils.py').read_bytes()).hexdigest() == manifest['parser_sha256']
    parse_ddi = load_public_parser(args.ddb_tools)
    rows = []
    for identity in manifest['banks']:
        bank = identity['bank']
        matches = list(args.voice_root.rglob(f'{bank}.ddi'))
        assert len(matches) == 1, (bank, len(matches))
        raw = matches[0].read_bytes()
        assert hashlib.sha256(raw).hexdigest() == identity['ddi_sha256']
        model = parse_ddi(raw)
        model.read()
        art = {key: parts for root in model.art_data.values()
               for key, parts in walk_art(root) if parts}
        outgoing = {}
        incoming = {}
        for left, right in (key for key in art if len(key) == 2):
            outgoing.setdefault(left, set()).add(right)
            incoming.setdefault(right, set()).add(left)
        from_sil = shortest_paths(('Sil',), outgoing)
        to_sta_reverse = shortest_paths(model.ddi_data_dict['sta'], incoming)
        outside = next(row['outside_pair_keys'] for row in remaining['rows']
                       if row['bank'] == bank)
        for left, right in map(tuple, outside):
            if left == 'Sil' or right == 'Sil':
                continue
            assert (left, right) in art and left in from_sil
            entry = from_sil[left]
            reverse = to_sta_reverse.get(right)
            pair_finish = tuple(reversed(reverse)) if reverse else None
            triple_finishes = sorted(third for third in model.ddi_data_dict['sta']
                                    if (left, '^' + right, third) in art)
            category = ('pair_route' if pair_finish else
                        'triple_bridge' if triple_finishes else 'no_listed_route')
            rows.append(dict(bank=bank, left=left, right=right,
                             entry_path=list(entry), pair_finish=list(pair_finish)
                             if pair_finish else None,
                             triple_finishes=triple_finishes,
                             category=category, layers=len(art[(left, right)])))
    assert len(rows) == 4680, len(rows)
    categories = Counter(row['category'] for row in rows)
    assert categories == {'pair_route': 4410, 'triple_bridge': 174,
                          'no_listed_route': 96}
    result = dict(manifest_sha256=hashlib.sha256(manifest_bytes).hexdigest(),
                  remaining_sha256=hashlib.sha256(remaining_bytes).hexdigest(),
                  pair_keys=len(rows), categories=dict(sorted(categories.items())),
                  rows=rows,
                  scope='Read-only installed V4X DDI route inventory for pair ART '
                        'outside evidence 253–257, excluding Silence starts and '
                        'ends already covered elsewhere. Routes use nonempty pair '
                        'ART keys; triple bridges require exact contextual triple '
                        'ART. Lack of a listed route is not evidence of unusable '
                        'material or runtime silence.')
    args.evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps(dict(pair_keys=len(rows), categories=result['categories'])))


if __name__ == '__main__':
    main()
