"""List ART keys outside the two-phoneme paths checked by evidence 253–257."""
import argparse
import hashlib
import json
from pathlib import Path

from analyze_reference_layers import walk_art
from verify_planned_ddi import load_public_parser


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--path-inventory', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    args = parser.parse_args()

    manifest_bytes = args.manifest.read_bytes()
    manifest = json.loads(manifest_bytes)
    inventory_bytes = args.path_inventory.read_bytes()
    inventory = json.loads(inventory_bytes)
    assert inventory['paths'] == len(inventory['rows']) == 1941
    assert hashlib.sha256((args.ddb_tools / 'utils' / 'ddi_utils.py').read_bytes()).hexdigest() == manifest['parser_sha256']
    parser_type = load_public_parser(args.ddb_tools)
    rows = []
    for identity in manifest['banks']:
        bank = identity['bank']
        matches = list(args.voice_root.rglob(f'{bank}.ddi'))
        assert len(matches) == 1, (bank, len(matches))
        raw = matches[0].read_bytes()
        assert hashlib.sha256(raw).hexdigest() == identity['ddi_sha256']
        model = parser_type(raw)
        model.read()
        keys = {key for root in model.art_data.values()
                for key, parts in walk_art(root) if parts}
        pairs = {key for key in keys if len(key) == 2}
        triples = {key for key in keys if len(key) == 3}
        paths = [item for item in inventory['rows'] if item['bank'] == bank]
        covered = {edge for item in paths
                   for edge in (('Sil', item['first']),
                                (item['first'], item['following']))}
        assert covered <= pairs
        outside = pairs - covered
        rows.append(dict(bank=bank, ddi_sha256=identity['ddi_sha256'],
                         two_phoneme_paths=len(paths),
                         pair_keys=len(pairs), pair_keys_in_paths=len(covered),
                         pair_keys_outside_paths=len(outside),
                         triple_keys=len(triples),
                         outside_pair_keys=[list(key) for key in sorted(outside)],
                         triple_key_names=[list(key) for key in sorted(triples)]))

    evidence = dict(manifest_sha256=hashlib.sha256(manifest_bytes).hexdigest(),
                    path_inventory_sha256=hashlib.sha256(inventory_bytes).hexdigest(),
                    banks=len(rows), two_phoneme_paths=sum(row['two_phoneme_paths']
                                                           for row in rows),
                    pair_keys=sum(row['pair_keys'] for row in rows),
                    pair_keys_in_paths=sum(row['pair_keys_in_paths'] for row in rows),
                    pair_keys_outside_paths=sum(row['pair_keys_outside_paths']
                                                for row in rows),
                    triple_keys=sum(row['triple_keys'] for row in rows),
                    rows=rows,
                    scope='Installed V4X DDI structure. Coverage here refers only to '
                          'the 253–257 two-phoneme path matrix; keys outside this '
                          'matrix may have other research evidence. Names only; no '
                          'frame or audio payload.')
    args.evidence.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps({key: evidence[key] for key in
                      ('two_phoneme_paths', 'pair_keys', 'pair_keys_in_paths',
                       'pair_keys_outside_paths', 'triple_keys')}))


if __name__ == '__main__':
    main()
