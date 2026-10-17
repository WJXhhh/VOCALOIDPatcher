"""Compare complete and missing ART edges through actual native event commits."""
import argparse
import json
from pathlib import Path
from audit_frm2_numeric import SHA256
from region_commit_material import connected
from verify_region_event_production import run


def verify(dll):
    variants = [(), (('Sil', 'a'),), (('a', 'Sil'),), (('Sil', 'a'), ('a', 'Sil'))]
    reports, paired = [], {}
    for edges in variants:
        report = run(dll, True, False, True,
                     lambda *args: connected(*args, edges=edges), [['a']])
        for row in report['rows']:
            if 'commit' not in row:
                continue
            actual = row['commit']
            has_onset = ('Sil', 'a') in edges
            assert actual['pending'] == int(not has_onset)
            assert actual['anchor'] == 1
            assert actual['new_slot'] == 1-row['slot']
            ranges = actual['item_ranges']
            if has_onset:
                assert actual['work_items'] == 3
                assert actual['material_indices'] == [-1, 1, 2 if len(edges) == 2 else 1]
                assert actual['reverse'] == [0, 0, 0 if len(edges) == 2 else 1]
                assert actual['tokens'] == [[-1, -1], [0, 1], [1, 0]]
                assert all(a < b for a, b in ranges)
                assert ranges[0][0] == 0
                assert all(ranges[j][1] == ranges[j+1][0] for j in [0, 1])
            else:
                assert ranges == [[0, 100 + actual['duration']]]
                assert actual['material_indices'] == [-1]
            key = (row['slot'], row['pitch'], row['milliseconds'], row['split'])
            if len(edges) == 1 and has_onset:
                paired[key] = actual
            if len(edges) == 2:
                # Replacing the missing release with a real release of equal
                # alignment preserves timing, while changing source and direction.
                assert actual['item_ranges'] == paired[key]['item_ranges']
                assert actual['left_lengths'] == paired[key]['left_lengths']
            reports.append(dict(edges=edges, slot=row['slot'], pitch=row['pitch'],
                                milliseconds=row['milliseconds'], split=row['split'], result=actual))
    assert len(reports) == 144
    return dict(dse_sha256=SHA256, commits=len(reports), cases=reports,
                scope='Single owned a note, stationary anchor, zero/one/two ART edges; '
                      'native events, lookup, selection and allocation; no acoustic rendering')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = verify(args.dll)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(commits=result['commits'])))
