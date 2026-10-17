"""Check indexed triple-ART stage choices under owned event controls."""
import argparse
from collections import Counter
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import I, P, SHA256
from region_commit_material import connected
from verify_region_event_production import run


KEY = ('4', '^4#2', 'M')
TOKENS = ['4', '4#2', 'M']
TRIPLE_LAYER = dict(pitch1=0., pitch2=0., dynamics=.6, frames=40,
                    groups=[(0, 4, 0, 4), (4, 8, 4, 8),
                            (8, 12, 8, 12), (12, 40, 12, 40)])


def single(dll: Path, control3: int, submode: int) -> dict:
    observed = []

    def build(n, state, context, voice, table, sta, art):
        material = connected(n, state, context, voice, table, sta, art,
                             edges=(('Sil', '4'), ('4', '4#2')),
                             stationary_phoneme='M',
                             triple_layers={KEY: [TRIPLE_LAYER]})
        target = material['triple_parts'][KEY][0]

        def inspect(n, state, ring, items, current):
            stages = []
            for index in range(I.from_address(ring + 0x34).value):
                work = items + index * 0x140
                if P.from_address(work + 0x20).value == target:
                    stage = I.from_address(work + 0x28).value
                    assert stage in (0, 1)
                    stages.append(stage)
            observed.append(stages)
            return stages

        material['verify_consumption'] = inspect
        return material

    report = run(dll, True, False, True, build, token_sequences=[TOKENS],
                 material_modes=(0,), commit_control3=control3,
                 ring_submode=submode)
    cases = [row for row in report['rows'] if row['mode'] == 0 and 'commit' in row]
    assert len(cases) == len(observed) == 36
    counts = Counter(stage for stages in observed for stage in stages)
    return dict(control3=control3, ring_submode=submode,
                events=len(cases), triple_selected_events=sum(bool(x) for x in observed),
                stage_counts={str(stage): counts[stage] for stage in (0, 1)},
                work_item_counts=dict(sorted(Counter(
                    row['commit']['work_items'] for row in cases).items())))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--control3', type=int)
    parser.add_argument('--submode', type=int)
    args = parser.parse_args()
    if args.control3 is not None:
        assert args.submode in (0, 1) and 0 <= args.control3 <= 127
        result = single(args.dll, args.control3, args.submode)
    else:
        rows = []
        for submode in (0, 1):
            for control3 in (0, 32, 64, 96, 127):
                output = args.evidence.with_name(
                    f'{args.evidence.stem}_{control3}_{submode}.json')
                command = [sys.executable, '-B', str(Path(__file__)),
                           '--dll', str(args.dll), '--evidence', str(output),
                           '--control3', str(control3), '--submode', str(submode)]
                finished = subprocess.run(command, capture_output=True, text=True)
                if finished.returncode:
                    rows.append(dict(control3=control3, ring_submode=submode,
                                     status='incomplete',
                                     message=(finished.stderr.strip().splitlines() or
                                              ['no error message'])[-1]))
                else:
                    rows.append(dict(status='passed', **json.loads(
                        output.read_text(encoding='utf-8'))))
        result = dict(dse_sha256=SHA256, conditions=len(rows), rows=rows,
                      scope='Owned indexed three-phoneme event with a synthetic '
                            'four-region triple ART part. Event control3 and '
                            'ring submode vary; no installed frame input or '
                            'Editor timing is tested.')
    args.evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps({key: result[key] for key in
                      ('conditions',) if key in result} or result))


if __name__ == '__main__':
    main()
