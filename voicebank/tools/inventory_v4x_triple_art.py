"""List installed V4X triple ART paths and their pair-ART context."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path

from analyze_reference_layers import walk_art
from verify_planned_ddi import load_public_parser


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    args = parser.parse_args()

    manifest_bytes = args.manifest.read_bytes()
    manifest = json.loads(manifest_bytes)
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
        for key, layers in sorted(art.items()):
            if len(key) != 3:
                continue
            first, middle, third = key
            assert middle.startswith('^') and middle[1:]
            second = middle[1:]
            pair_keys = (('Sil', first), (first, second), (second, third))
            available = [pair in art for pair in pair_keys]
            assert available[0] and third in model.ddi_data_dict['sta']
            assert all(len(layer['frame_align']) == 4 for layer in layers)
            rows.append(dict(bank=bank, first=first, second=second,
                             third=third, triple_key=list(key),
                             pair_edges_available=available,
                             triple_layers=len(layers),
                             triple_frames=[len(layer['epr']) for layer in layers],
                             triple_pitch1=[layer['pitch1'] for layer in layers],
                             triple_pitch2=[layer['pitch2'] for layer in layers],
                             third_sta_layers=len(model.ddi_data_dict['sta'][third])))
    assert len(rows) == 994, len(rows)
    shapes = Counter(''.join('1' if value else '0'
                             for value in row['pair_edges_available']) for row in rows)
    result = dict(manifest_sha256=hashlib.sha256(manifest_bytes).hexdigest(),
                  triple_keys=len(rows), pair_shapes=dict(sorted(shapes.items())),
                  rows=rows,
                  scope='Read-only installed V4X DDI inventory. Normalized middle '
                        'phonemes remove the ART ^ prefix for event tokens; '
                        'triple ART keeps its original three-name key. Pair '
                        'availability, layer pitches, and frame counts are '
                        'structural metadata, not runtime selection evidence.')
    args.evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps(dict(triple_keys=len(rows), pair_shapes=result['pair_shapes'])))


if __name__ == '__main__':
    main()
