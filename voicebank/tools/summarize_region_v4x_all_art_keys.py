"""Reconcile installed V4X ART keys with positive DDB frame evidence."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path


FILES = {
    'paths': '253_region_v4x_two_phoneme_path_inventory.json',
    'universe': '258_region_v4x_remaining_art_inventory.json',
    'initial': '252_region_v4x_index_full_output.json',
    'extended': '254_region_v4x_extended_paths_output.json',
    'same_token': '257_region_v4x_same_token_full_output.json',
    'silence_exit': '260_region_v4x_silence_exit_output.json',
    'suffix_exit': '262_region_v4x_suffix_exit_output.json',
    'breath_entry': '247_region_v4x_breath_onsets.json',
    'pair_20': '266_region_v4x_pair_route_20_output.json',
    'pair_11': '273_region_v4x_pair_route_11_cross_note_output.json',
    'pair_21': '276_region_v4x_pair_route_21_cross_note_output.json',
    'triple_bridge': '274_region_v4x_triple_bridge_gap_output.json',
    'terminal': '275_region_v4x_terminal_pair_output.json',
    'triple_inventory': '263_region_v4x_triple_art_inventory.json',
    'triple_stage': '272_region_v4x_triple_stage_frame_reads.json',
}


def summarize(evidence_dir: Path) -> dict:
    raw = {name: (evidence_dir / filename).read_bytes()
           for name, filename in FILES.items()}
    reports = {name: json.loads(value) for name, value in raw.items()}
    universe = reports['universe']
    paths = reports['paths']
    assert universe['pair_keys'] == 7437
    assert universe['pair_keys_in_paths'] == 2269
    assert universe['pair_keys_outside_paths'] == 5168
    assert paths['paths'] == len(paths['rows']) == 1941
    inside = set()
    for row in paths['rows']:
        bank, first, following = (row[name] for name in
                                  ('bank', 'first', 'following'))
        inside.update(((bank, 'Sil', first), (bank, first, following)))
    assert len(inside) == 2269
    outside = {(row['bank'], left, right)
               for row in universe['rows']
               for left, right in row['outside_pair_keys']}
    assert len(outside) == 5168 and inside.isdisjoint(outside)
    assert len(inside | outside) == 7437

    seen = {}

    def add(bank, left, right, source, positions):
        identity = bank, left, right
        assert identity in inside | outside and positions > 0
        seen.setdefault(identity, set()).add(source)

    for source in ('initial', 'extended', 'same_token'):
        for row in reports[source]['rows']:
            bank = row['bank']
            first = row['token'] if source == 'same_token' else row['first']
            following = first if source == 'same_token' else row['following']
            for left, right in (('Sil', first), (first, following)):
                edge = row['by_edge'][left + '->' + right]
                add(bank, left, right, source, edge['positions'])
    assert inside <= set(seen)

    for row in reports['silence_exit']['rows']:
        add(row['bank'], row['first'], 'Sil', 'silence_exit',
            row['by_edge'][row['first'] + '->Sil']['positions'])
    for row in reports['suffix_exit']['rows']:
        add(row['bank'], row['second'], 'Sil', 'suffix_exit',
            row['by_edge'][row['second'] + '->Sil']['positions'])
    for row in reports['breath_entry']['rows']:
        assert row['frame_reads'] > 0
        add(row['bank'], 'Sil', row['phoneme'], 'breath_entry',
            row['frame_reads'])
    for source in ('pair_20', 'pair_11', 'pair_21', 'triple_bridge', 'terminal'):
        for row in reports[source]['rows']:
            assert row['status'] == 'passed'
            add(row['bank'], row['left'], row['right'], source,
                row['target_positions'])
    assert outside <= set(seen) and set(seen) == inside | outside
    assert all(len(seen[identity]) == 1 for identity in outside)
    outside_sources = Counter(next(iter(seen[identity])) for identity in outside)
    assert outside_sources == Counter(silence_exit=352, suffix_exit=96,
                                      breath_entry=40, pair_20=848,
                                      pair_11=1736, pair_21=1826,
                                      triple_bridge=174, terminal=96)

    triple_inventory = reports['triple_inventory']
    triple_stage = reports['triple_stage']
    expected_triples = {(row['bank'], row['first'], row['second'], row['third'])
                        for row in triple_inventory['rows']}
    observed_triples = {(row['bank'], row['first'], row['second'], row['third'])
                        for row in triple_stage['rows']}
    assert triple_inventory['triple_keys'] == len(expected_triples) == 994
    assert triple_stage['passed'] == len(observed_triples) == 994
    assert expected_triples == observed_triples
    assert all(row['status'] == 'passed' and
               row['stage_positions']['0'] > 0 and
               row['stage_positions']['1'] > 0 and
               row['stage_frame_reads']['0'] > 0 and
               row['stage_frame_reads']['1'] > 0
               for row in triple_stage['rows'])
    return dict(source_sha256={name: hashlib.sha256(value).hexdigest()
                               for name, value in sorted(raw.items())},
                installed_banks=8, pair_keys=len(seen),
                pair_keys_in_prior_paths=len(inside),
                pair_keys_outside_prior_paths=len(outside),
                outside_pair_sources=dict(sorted(outside_sources.items())),
                triple_keys=len(observed_triples),
                total_art_keys=len(seen) + len(observed_triples),
                scope='Every nonempty installed V4X pair ART key has positive '
                      'selected source positions or, for Silence-to-breath '
                      'entries, direct DDB frame reads in the cited owned checks. '
                      'All triple ART keys have positive stage-0 and stage-1 '
                      'positions and DDB frame reads. This reconciles research '
                      'evidence, not Editor natural note timing or listening.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence-dir', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    args = parser.parse_args()
    result = summarize(args.evidence_dir)
    args.evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps({key: result[key] for key in
                      ('pair_keys', 'triple_keys', 'total_art_keys')}))


if __name__ == '__main__':
    main()
