"""List installed V4X two-phoneme paths with both ART edges and a final STA."""
import argparse
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
    parser.add_argument('--previous', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    args = parser.parse_args()

    manifest_bytes = args.manifest.read_bytes()
    manifest = json.loads(manifest_bytes)
    previous_bytes = args.previous.read_bytes()
    previous = json.loads(previous_bytes)
    prior_paths = {(row['bank'], row['first'], row['following'])
                   for row in previous['rows']}
    assert len(prior_paths) == previous['scenarios'] == 232
    assert hashlib.sha256((args.ddb_tools / 'utils' / 'ddi_utils.py').read_bytes()).hexdigest() == manifest['parser_sha256']
    parser_type = load_public_parser(args.ddb_tools)
    rows = []
    banks = []
    for identity in manifest['banks']:
        bank = identity['bank']
        matches = list(args.voice_root.rglob(f'{bank}.ddi'))
        assert len(matches) == 1, (bank, len(matches))
        raw = matches[0].read_bytes()
        assert hashlib.sha256(raw).hexdigest() == identity['ddi_sha256']
        model = parser_type(raw)
        model.read()
        sta = set(model.ddi_data_dict['sta'])
        art = {key: parts for root in model.art_data.values()
               for key, parts in walk_art(root) if len(key) == 2 and parts}
        firsts = sorted(right for left, right in art if left == 'Sil')
        current = []
        for first in firsts:
            for second in sorted(sta):
                if (first, second) not in art:
                    continue
                row = dict(bank=bank, first=first, following=second,
                           first_has_sta=first in sta,
                           same_token=first == second,
                           previously_tested=(bank, first, second) in prior_paths,
                           entry_layers=len(art[('Sil', first)]),
                           transition_layers=len(art[(first, second)]))
                current.append(row)
                rows.append(row)
        banks.append(dict(bank=bank, ddi_sha256=identity['ddi_sha256'],
                          entry_tokens=len(firsts), paths=len(current),
                          first_has_sta=sum(row['first_has_sta'] for row in current),
                          same_token=sum(row['same_token'] for row in current),
                          previously_tested=sum(row['previously_tested'] for row in current)))

    assert all(path in {(row['bank'], row['first'], row['following']) for row in rows}
               for path in prior_paths)
    evidence = dict(manifest_sha256=hashlib.sha256(manifest_bytes).hexdigest(),
                    previous_sha256=hashlib.sha256(previous_bytes).hexdigest(),
                    paths=len(rows), previously_tested=len(prior_paths),
                    new_paths=len(rows) - len(prior_paths),
                    first_has_sta=sum(row['first_has_sta'] for row in rows),
                    same_token=sum(row['same_token'] for row in rows),
                    banks=banks, rows=rows,
                    scope='Installed V4X DDI structure only. Each path has nonempty '
                          'Sil-to-first and first-to-second ART and a second-phoneme '
                          'STA. Structural availability does not prove event selection '
                          'or audio output.')
    args.evidence.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps({key: evidence[key] for key in
                      ('paths', 'previously_tested', 'new_paths',
                       'first_has_sta', 'same_token')}))


if __name__ == '__main__':
    main()
