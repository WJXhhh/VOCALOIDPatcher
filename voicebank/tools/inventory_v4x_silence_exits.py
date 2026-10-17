"""List V4X ART exits whose left token also has a Silence entry."""
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
        art = {key: parts for root in model.art_data.values()
               for key, parts in walk_art(root) if len(key) == 2 and parts}
        sta = set(model.ddi_data_dict['sta'])
        outside = {tuple(key) for item in remaining['rows']
                   if item['bank'] == bank
                   for key in item['outside_pair_keys']}
        for first, last in sorted(key for key in outside if key[1] == 'Sil'):
            if ('Sil', first) not in art:
                continue
            rows.append(dict(bank=bank, first=first,
                             first_has_sta=first in sta,
                             entry_layers=len(art[('Sil', first)]),
                             exit_layers=len(art[(first, last)])))

    assert len(rows) == 352
    evidence = dict(manifest_sha256=hashlib.sha256(manifest_bytes).hexdigest(),
                    remaining_sha256=hashlib.sha256(remaining_bytes).hexdigest(),
                    exits=len(rows), first_has_sta=sum(row['first_has_sta']
                                                        for row in rows),
                    rows=rows,
                    scope='Installed V4X DDI structure. Each listed exit has nonempty '
                          'Silence-to-first and first-to-Silence ART. This is a '
                          'candidate event shape, not proof of event selection.')
    args.evidence.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps(dict(exits=evidence['exits'],
                          first_has_sta=evidence['first_has_sta'])))


if __name__ == '__main__':
    main()
