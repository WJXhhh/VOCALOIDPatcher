"""Check V4X Silence-to-consonant frames in two-phoneme owned events."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from analyze_reference_layers import walk_art
from verify_planned_ddi import load_public_parser


VOWELS = ('a', 'i', 'e', 'o')


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--output-root', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--bank')
    args = parser.parse_args()

    identities = json.loads(args.manifest.read_text(encoding='utf-8'))
    assert hashlib.sha256((args.ddb_tools / 'utils' / 'ddi_utils.py').read_bytes()).hexdigest() == identities['parser_sha256']
    ddi_parser = load_public_parser(args.ddb_tools)
    script = Path(__file__).with_name('verify_region_v4x_direct_onset_frames.py')
    rows, skipped = [], []
    for identity in identities['banks']:
        bank = identity['bank']
        if args.bank and bank != args.bank:
            continue
        matches = list(args.voice_root.rglob(f'{bank}.ddi'))
        assert len(matches) == 1
        raw = matches[0].read_bytes()
        assert hashlib.sha256(raw).hexdigest() == identity['ddi_sha256']
        model = ddi_parser(raw)
        model.read()
        sta = set(model.ddi_data_dict['sta'])
        edges = {key: parts for root in model.art_data.values()
                 for key, parts in walk_art(root) if len(key) == 2 and parts}
        firsts = sorted(key[1] for key in edges
                        if key[0] == 'Sil' and key[1] not in sta)
        for first in firsts:
            choices = [vowel for vowel in VOWELS if (first, vowel) in edges]
            if not choices:
                skipped.append(dict(bank=bank, phoneme=first,
                                    reason='No matching vowel continuation in this matrix'))
                continue
            following = choices[0]
            reports = []
            for repeat in (1, 2):
                key = first.encode('ascii').hex()
                output = args.output_root / f'v4x_consonant_{bank}_{key}_400_r{repeat}.json'
                command = [sys.executable, '-B', str(script), '--dll', str(args.dll),
                           '--bank', bank, '--manifest', str(args.manifest),
                           '--voice-root', str(args.voice_root), '--ddb-tools',
                           str(args.ddb_tools), '--offset', '400',
                           '--phoneme', first, '--following', following,
                           '--output', str(output)]
                finished = subprocess.run(command, capture_output=True, text=True)
                assert finished.returncode == 0, (bank, first, following, finished.stderr[-2000:])
                reports.append(output.read_bytes())
            assert reports[0] == reports[1], (bank, first, following)
            result = json.loads(reports[0])
            assert result['bank'] == bank and result['phoneme'] == first
            assert result['following'] == following and result['commits'] == 36
            assert 0 <= result['nonzero_pcm_windows'] <= result['frame_reads']
            assert all(item['all_selected_frames_read'] for item in result['by_pitch'].values())
            rows.append(dict(bank=bank, first=first, following=following,
                             frame_reads=result['frame_reads'],
                             nonzero_pcm_windows=result['nonzero_pcm_windows'],
                             zero_pcm_windows=result['frame_reads'] - result['nonzero_pcm_windows'],
                             by_pitch=result['by_pitch'],
                             report_sha256=hashlib.sha256(reports[0]).hexdigest()))
        print(json.dumps(dict(bank=bank, checked=sum(row['bank'] == bank for row in rows),
                              no_matching_vowel=sum(row['bank'] == bank for row in skipped))), flush=True)

    assert rows
    evidence = dict(dse_sha256=result['dse_sha256'],
                    parser_sha256=identities['parser_sha256'],
                    banks=len({row['bank'] for row in rows}),
                    scenarios=len(rows), commits=len(rows) * 36,
                    frame_reads=sum(row['frame_reads'] for row in rows),
                    nonzero_pcm_windows=sum(row['nonzero_pcm_windows'] for row in rows),
                    zero_pcm_windows=sum(row['zero_pcm_windows'] for row in rows),
                    repeat_runs=2, skipped=skipped, rows=rows,
                    scope='Installed DDI/DDB Silence-to-consonant edge in a synthetic '
                          'two-phoneme event followed by an available vowel. Native '
                          'direct-tree first work item and mode-0 frame loader checked '
                          'against DDB frame time and PCM window; all selected-layer '
                          'frames covered. No Editor or full audio output.')
    args.evidence.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps(dict(scenarios=evidence['scenarios'],
                          commits=evidence['commits'],
                          frame_reads=evidence['frame_reads'],
                          skipped=len(skipped))))


if __name__ == '__main__':
    main()
