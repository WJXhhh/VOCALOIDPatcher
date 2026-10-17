"""Combine adjacent-note triple ART checks by available pair-path shape."""
import argparse
import hashlib
import json
from pathlib import Path


FIELDS = ('selected_triple_cases', 'triple_positions', 'art_work_items',
          'art_positions', 'frame_reads', 'nonzero_pcm_windows',
          'full_loop_blocks', 'full_loop_art_pcm_matches',
          'full_loop_sta_pcm_matches', 'nonzero_events')


def identity(row):
    return tuple(row[key] for key in ('bank', 'first', 'second', 'third'))


def summarize(inventory: Path, split2: Path, split1_101: Path) -> dict:
    inventory_bytes = inventory.read_bytes()
    split2_bytes, split1_bytes = split2.read_bytes(), split1_101.read_bytes()
    source = json.loads(inventory_bytes)
    first, second = json.loads(split2_bytes), json.loads(split1_bytes)
    assert source['triple_keys'] == len(source['rows']) == 994
    assert first['candidates'] == 994 and first['note_split'] == 2
    assert second['candidates'] == 13 and second['note_split'] == 1
    assert second['pair_shape'] == '101'
    by_split = [{identity(row): row for row in report['rows']}
                for report in (first, second)]
    assert len(by_split[0]) == 994 and len(by_split[1]) == 13
    selected = []
    for original in source['rows']:
        shape = ''.join('1' if present else '0'
                        for present in original['pair_edges_available'])
        split = 1 if shape == '101' else 2
        row = by_split[1 if split == 1 else 0][identity(original)]
        assert row['status'] == 'passed', (identity(original), row)
        assert row['triple_stage_counts'].get('1', 0) > 0
        selected.append(dict(note_split=split, **row))
    result = dict(inventory_sha256=hashlib.sha256(inventory_bytes).hexdigest(),
                  split2_sha256=hashlib.sha256(split2_bytes).hexdigest(),
                  split1_101_sha256=hashlib.sha256(split1_bytes).hexdigest(),
                  triple_keys=len(selected),
                  stage0_selected_keys=sum(row['triple_stage_counts'].get('0', 0) > 0
                                           for row in selected),
                  stage1_selected_keys=sum(row['triple_stage_counts'].get('1', 0) > 0
                                           for row in selected),
                  stage_counts={str(stage): sum(
                      row['triple_stage_counts'].get(str(stage), 0)
                      for row in selected) for stage in (0, 1)},
                  note_split_counts={str(split): sum(row['note_split'] == split
                                                     for row in selected)
                                     for split in (1, 2)},
                  repeat_runs_per_key=2,
                  rows=selected,
                  scope='Installed V4X triple ART in owned adjacent-note events. '
                        'Split at the first phoneme for paths with missing middle '
                        'pair ART, otherwise at the second phoneme. Selected '
                        'frame times and ART/STA PCM windows are checked against '
                        'installed DDB during native mode-0 output. Editor timing '
                        'and listening are not tested.')
    result.update({field: sum(row[field] for row in selected) for field in FIELDS})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--inventory', type=Path, required=True)
    parser.add_argument('--split2', type=Path, required=True)
    parser.add_argument('--split1-101', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    args = parser.parse_args()
    result = summarize(args.inventory, args.split2, args.split1_101)
    args.evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps({key: result[key] for key in
                      ('triple_keys', 'stage0_selected_keys', 'stage1_selected_keys',
                       'stage_counts')}))


if __name__ == '__main__':
    main()
