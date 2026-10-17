"""Reconcile every remaining installed V4X pair key with selected frame evidence."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path


def key(row):
    return row['bank'], row['left'], row['right']


def summarize(inventory: Path, sources: dict[str, Path]) -> dict:
    inventory_bytes = inventory.read_bytes()
    original = json.loads(inventory_bytes)
    assert original['pair_keys'] == len(original['rows']) == 4680
    inventory_keys = {key(row): row for row in original['rows']}
    assert len(inventory_keys) == 4680
    gathered = {}
    source_hashes = {}
    source_counts = {}
    for name, path in sources.items():
        raw = path.read_bytes()
        report = json.loads(raw)
        source_hashes[name] = hashlib.sha256(raw).hexdigest()
        source_counts[name] = len(report['rows'])
        for row in report['rows']:
            identity = key(row)
            assert identity in inventory_keys and identity not in gathered
            assert row['status'] == 'passed' and row['target_positions'] > 0
            gathered[identity] = dict(source=name,
                                      target_positions=row['target_positions'])
    assert set(gathered) == set(inventory_keys), (
        len(gathered), len(inventory_keys), len(set(inventory_keys) - set(gathered)))
    by_category = Counter(row['category'] for row in original['rows'])
    by_source = Counter(row['source'] for row in gathered.values())
    assert by_source == Counter(pair_20=848, pair_11=1736, pair_21=1826,
                                triple_bridge=174, terminal=96)
    return dict(inventory_sha256=hashlib.sha256(inventory_bytes).hexdigest(),
                source_sha256=source_hashes, source_counts=source_counts,
                pair_keys=len(gathered), positive_target_keys=sum(
                    row['target_positions'] > 0 for row in gathered.values()),
                categories=dict(sorted(by_category.items())),
                by_source=dict(sorted(by_source.items())),
                total_target_positions=sum(row['target_positions']
                                           for row in gathered.values()),
                min_target_positions=min(row['target_positions']
                                         for row in gathered.values()),
                scope='Reconciles the 4680 installed V4X pair ART keys outside '
                      'the prior path matrix and Silence/breath groups. Each '
                      'key has positive selected source positions in an owned '
                      'event and a source report with installed DDB frame and '
                      'mode-0 output input-window checks. Does not prove Editor '
                      'natural timing or listening quality.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--inventory', type=Path, required=True)
    parser.add_argument('--pair-20', type=Path, required=True)
    parser.add_argument('--pair-11', type=Path, required=True)
    parser.add_argument('--pair-21', type=Path, required=True)
    parser.add_argument('--triple-bridge', type=Path, required=True)
    parser.add_argument('--terminal', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    args = parser.parse_args()
    result = summarize(args.inventory, dict(
        pair_20=args.pair_20, pair_11=args.pair_11, pair_21=args.pair_21,
        triple_bridge=args.triple_bridge, terminal=args.terminal))
    args.evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps({key: result[key] for key in
                      ('pair_keys', 'positive_target_keys', 'total_target_positions',
                       'min_target_positions')}))


if __name__ == '__main__':
    main()
