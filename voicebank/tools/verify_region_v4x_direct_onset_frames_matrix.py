"""Repeat the installed V4X onset-frame checks in separate DSE processes."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys


OFFSETS = (1, 20, 100, 400)


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
    for bank_row in identities['banks']:
        bank = bank_row['bank']
        for offset in OFFSETS:
            reports = []
            for repeat in (1, 2):
                output = args.output_root / f'v4x_direct_frames_{bank}_{offset}_r{repeat}.json'
                command = [sys.executable, str(script), '--dll', str(args.dll),
                           '--bank', bank, '--manifest', str(args.manifest),
                           '--voice-root', str(args.voice_root), '--ddb-tools',
                           str(args.ddb_tools), '--offset', str(offset),
                           '--output', str(output)]
                subprocess.run(command, check=True, capture_output=True, text=True)
                reports.append(output.read_bytes())
            assert reports[0] == reports[1], (bank, offset)
            raw = json.loads(reports[0])
            assert raw['bank'] == bank and raw['note_start_offset'] == offset
            assert raw['commits'] == 36
            assert raw['nonzero_pcm_windows'] == raw['frame_reads']
            assert set(raw['by_pitch']) == {'48', '69', '84'}
            if offset >= 100:
                assert all(item['all_selected_frames_read']
                           for item in raw['by_pitch'].values())
            rows.append(dict(bank=bank, offset=offset,
                             commits=raw['commits'], frame_reads=raw['frame_reads'],
                             nonzero_pcm_windows=raw['nonzero_pcm_windows'],
                             by_pitch=raw['by_pitch'],
                             report_sha256=hashlib.sha256(reports[0]).hexdigest()))

    evidence = dict(dse_sha256=raw['dse_sha256'],
                    parser_sha256=identities['parser_sha256'],
                    banks=len(identities['banks']), offsets=list(OFFSETS),
                    scenarios=len(rows), commits=sum(row['commits'] for row in rows),
                    frame_reads=sum(row['frame_reads'] for row in rows),
                    nonzero_pcm_windows=sum(row['nonzero_pcm_windows'] for row in rows),
                    repeat_runs=2, rows=rows,
                    scope='Installed V4X DDI/DDB read-only. Synthetic direct-tree event '
                          'uses actual Sil-to-a layer metadata and frame/SND pointers; '
                          'native mode-0 frame reads checked against DDB time and PCM '
                          'sample windows. Offset is synthetic. No Editor or full audio output.')
    args.evidence.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps(dict(scenarios=evidence['scenarios'],
                          commits=evidence['commits'],
                          frame_reads=evidence['frame_reads'])))


if __name__ == '__main__':
    main()
