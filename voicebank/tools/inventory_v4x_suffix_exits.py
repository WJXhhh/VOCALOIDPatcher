"""List EVEC suffix exits that need a preceding base phoneme."""
import argparse
import hashlib
import json
from pathlib import Path

from analyze_reference_layers import walk_art
from verify_planned_ddi import load_public_parser


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
               for key, parts in walk_art(root) if len(key) == 2 and parts}
        outside = {tuple(key) for row in remaining['rows'] if row['bank'] == bank
                   for key in row['outside_pair_keys']}
        for second, tail in sorted(outside):
            if tail != 'Sil' or ('Sil', second) in art:
                continue
            assert second.endswith(('#2', '#6')), (bank, second)
            first = second[:-2]
            edges = (('Sil', first), (first, second), (second, 'Sil'))
            assert all(edge in art for edge in edges), (bank, edges)
            assert second in model.ddi_data_dict['sta'], (bank, second)
            rows.append(dict(bank=bank, first=first, second=second,
                             layers=[len(art[edge]) for edge in edges],
                             second_sta_layers=len(model.ddi_data_dict['sta'][second])))
    assert len(rows) == 96, len(rows)
    result = dict(manifest_sha256=hashlib.sha256(manifest_bytes).hexdigest(),
                  remaining_sha256=hashlib.sha256(remaining_bytes).hexdigest(),
                  exits=len(rows), rows=rows,
                  scope='Installed V4X DDI structural inventory only. Each suffix exit '
                        'has a nonempty Silence-to-base, base-to-suffix, and '
                        'suffix-to-Silence pair ART path plus suffix STA. '
                        'No event behavior or PCM is inferred.')
    args.evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps(dict(exits=len(rows))))


if __name__ == '__main__':
    main()
