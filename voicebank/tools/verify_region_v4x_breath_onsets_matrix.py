"""Check installed V4X Silence-to-breath frames in owned direct-tree events."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys


BREATH_TOKENS = tuple(f'br{index}' for index in range(1, 6))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--output-root', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    args = parser.parse_args()

    identities = json.loads(args.manifest.read_text(encoding='utf-8'))
    script = Path(__file__).with_name('verify_region_v4x_direct_onset_frames.py')
    rows = []
    for identity in identities['banks']:
        bank = identity['bank']
        for token in BREATH_TOKENS:
            reports = []
            for repeat in (1, 2):
                output = args.output_root / f'v4x_breath_onset_{bank}_{token}_400_r{repeat}.json'
                command = [sys.executable, '-B', str(script), '--dll', str(args.dll),
                           '--bank', bank, '--manifest', str(args.manifest),
                           '--voice-root', str(args.voice_root), '--ddb-tools',
                           str(args.ddb_tools), '--offset', '400',
                           '--phoneme', token, '--breath', '--output', str(output)]
                finished = subprocess.run(command, capture_output=True, text=True)
                assert finished.returncode == 0, (bank, token, finished.stderr[-2000:])
                reports.append(output.read_bytes())
            assert reports[0] == reports[1], (bank, token)
            report = json.loads(reports[0])
            assert report['bank'] == bank and report['phoneme'] == token
            assert report['breath'] and report['commits'] == 36
            assert all(item['all_selected_frames_read'] for item in report['by_pitch'].values())
            rows.append(dict(bank=bank, phoneme=token,
                             frame_reads=report['frame_reads'],
                             nonzero_pcm_windows=report['nonzero_pcm_windows'],
                             by_pitch=report['by_pitch'],
                             report_sha256=hashlib.sha256(reports[0]).hexdigest()))
        print(json.dumps(dict(bank=bank, checked=len(BREATH_TOKENS))), flush=True)

    assert len(rows) == 40
    evidence = dict(dse_sha256=report['dse_sha256'],
                    parser_sha256=identities['parser_sha256'],
                    banks=8, scenarios=len(rows), commits=len(rows) * 36,
                    frame_reads=sum(row['frame_reads'] for row in rows),
                    nonzero_pcm_windows=sum(row['nonzero_pcm_windows'] for row in rows),
                    repeat_runs=2, rows=rows,
                    scope='Installed V4X Silence-to-breath ART with actual DDI/DDB frame '
                          'and SND pointers. Synthetic one-token direct-tree event uses '
                          'a fixture-only stationary a node; only its first breath ART '
                          'work item and mode-0 frame reads are checked. Offset is synthetic; '
                          'no Editor event or final audio output.')
    args.evidence.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps(dict(scenarios=evidence['scenarios'],
                          commits=evidence['commits'],
                          frame_reads=evidence['frame_reads'])))


if __name__ == '__main__':
    main()
