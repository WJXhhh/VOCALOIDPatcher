"""Check pair ART targets encountered on installed triple ART paths."""
import argparse
import hashlib
import json
from pathlib import Path


def summarize(inventory: Path, triple_evidence: Path) -> dict:
    inventory_bytes, triple_bytes = inventory.read_bytes(), triple_evidence.read_bytes()
    source, triple = json.loads(inventory_bytes), json.loads(triple_bytes)
    assert source['pair_keys'] == 4680 and triple['triple_keys'] == 994
    routes = [row for row in source['rows'] if row['category'] == 'triple_bridge']
    assert len(routes) == 174
    by_path = {(row['bank'], row['first'], row['second'], row['third']): row
               for row in triple['rows']}
    assert len(by_path) == 994
    rows = []
    for route in routes:
        target_name = route['left'] + '->' + route['right']
        examples = []
        for finish in route['triple_finishes']:
            path = by_path[(route['bank'], route['left'], route['right'], finish)]
            edge = path['by_edge'].get(target_name, {})
            examples.append(dict(third=finish, note_split=path['note_split'],
                                 work_items=edge.get('work_items', 0),
                                 positions=edge.get('positions', 0),
                                 frame_reads=edge.get('frame_reads', 0)))
        rows.append(dict(bank=route['bank'], left=route['left'],
                         right=route['right'], selected=any(
                             example['positions'] > 0 for example in examples),
                         target_positions=sum(example['positions']
                                              for example in examples),
                         examples=examples))
    return dict(inventory_sha256=hashlib.sha256(inventory_bytes).hexdigest(),
                triple_evidence_sha256=hashlib.sha256(triple_bytes).hexdigest(),
                pair_keys=len(rows), selected_pair_keys=sum(row['selected'] for row in rows),
                target_positions=sum(row['target_positions'] for row in rows),
                rows=rows,
                scope='Pair ART targets that have a listed triple ART path to STA. '
                      'Selected target positions are counted from the independently '
                      'verified adjacent-note triple event reports, whose selected '
                      'frame times and input PCM windows match installed DDB. '
                      'Editor timing and listening are not tested.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--inventory', type=Path, required=True)
    parser.add_argument('--triple-evidence', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    args = parser.parse_args()
    result = summarize(args.inventory, args.triple_evidence)
    args.evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps({key: result[key] for key in
                      ('pair_keys', 'selected_pair_keys', 'target_positions')}))


if __name__ == '__main__':
    main()
