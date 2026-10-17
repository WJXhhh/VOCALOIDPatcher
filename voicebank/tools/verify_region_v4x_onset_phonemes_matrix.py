"""Check every shared V4X Silence-onset phoneme using installed frame data."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from analyze_reference_layers import walk_art
from verify_planned_ddi import load_public_parser


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
    assert hashlib.sha256((args.ddb_tools / 'utils' / 'ddi_utils.py').read_bytes()).hexdigest() == identities['parser_sha256']
    ddi_parser = load_public_parser(args.ddb_tools)
    per_bank = {}
    edge_counts = {}
    for identity in identities['banks']:
        bank = identity['bank']
        matches = list(args.voice_root.rglob(f'{bank}.ddi'))
        assert len(matches) == 1
        raw = matches[0].read_bytes()
        assert hashlib.sha256(raw).hexdigest() == identity['ddi_sha256']
        model = ddi_parser(raw)
        model.read()
        sta = set(model.ddi_data_dict['sta'])
        entries = [(key, parts) for root in model.art_data.values()
                   for key, parts in walk_art(root)
                   if len(key) == 2 and key[0] == 'Sil']
        per_bank[bank] = {key[1] for key, parts in entries
                          if parts and key[1] in sta}
        edge_counts[bank] = dict(all_edges=len(entries),
                                 populated=sum(bool(parts) for _, parts in entries),
                                 with_stationary=len(per_bank[bank]),
                                 without_stationary=sum(bool(parts) and key[1] not in sta
                                                        for key, parts in entries))

    common = sorted(set.intersection(*per_bank.values()))
    assert common and all(phoneme.isascii() and len(phoneme) <= 15 for phoneme in common)
    script = Path(__file__).with_name('verify_region_v4x_direct_onset_frames.py')
    rows = []
    for identity in identities['banks']:
        bank = identity['bank']
        for phoneme in common:
            reports = []
            for repeat in (1, 2):
                key = phoneme.encode('ascii').hex()
                output = args.output_root / f'v4x_onset_{bank}_{key}_400_r{repeat}.json'
                command = [sys.executable, '-B', str(script), '--dll', str(args.dll),
                           '--bank', bank, '--manifest', str(args.manifest),
                           '--voice-root', str(args.voice_root), '--ddb-tools',
                           str(args.ddb_tools), '--offset', '400',
                           '--phoneme', phoneme, '--output', str(output)]
                finished = subprocess.run(command, capture_output=True, text=True)
                assert finished.returncode == 0, (bank, phoneme, finished.stderr[-2000:])
                reports.append(output.read_bytes())
            assert reports[0] == reports[1], (bank, phoneme)
            raw = json.loads(reports[0])
            assert raw['bank'] == bank and raw['commits'] == 36
            assert raw.get('phoneme', 'a') == phoneme
            assert raw['nonzero_pcm_windows'] == raw['frame_reads']
            assert all(item['all_selected_frames_read'] for item in raw['by_pitch'].values())
            rows.append(dict(bank=bank, phoneme=phoneme,
                             frame_reads=raw['frame_reads'],
                             by_pitch=raw['by_pitch'],
                             report_sha256=hashlib.sha256(reports[0]).hexdigest()))
        print(json.dumps(dict(bank=bank, checked=len(common))), flush=True)

    evidence = dict(dse_sha256=raw['dse_sha256'],
                    parser_sha256=identities['parser_sha256'],
                    banks=len(identities['banks']), common_phonemes=common,
                    silence_edge_counts=edge_counts,
                    scenarios=len(rows), commits=len(rows) * 36,
                    frame_reads=sum(row['frame_reads'] for row in rows),
                    repeat_runs=2, rows=rows,
                    scope='Installed DDI/DDB Silence-onset edges shared by all eight V4X banks. '
                          'Synthetic 400-frame start offset; native direct-tree item and mode-0 '
                          'frame loader checked against DDB frame time and PCM sample window. '
                          'All frames of each selected layer covered. No Editor or full audio output.')
    args.evidence.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps(dict(scenarios=evidence['scenarios'],
                          commits=evidence['commits'],
                          frame_reads=evidence['frame_reads'])))


if __name__ == '__main__':
    main()
