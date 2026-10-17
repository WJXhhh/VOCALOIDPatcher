"""Compare reviewed special-PCM mode4 construction against ordinary protocol.

Uses existing evidence389 as the matched baseline. Commercial PCM and produced
frames stay in child-process memory; only scalar comparisons are saved.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256
from verify_vqm_estimator_producer_variants import VARIANTS


def run(args):
    baseline = json.loads(args.baseline.read_text(encoding='utf-8'))
    assert baseline['dse_sha256'] == SHA256
    auxiliary_hash = hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest()
    assert baseline['auxiliary_sha256'] == auxiliary_hash
    rows = []
    for sample in range(5):
        for variant in VARIANTS:
            command = [sys.executable, '-B', str(Path(__file__).with_name(
                'verify_vqm_estimator_producer_variants.py').resolve()),
                '--dll', str(args.dll), '--manifest', str(args.manifest),
                '--voice-root', str(args.voice_root), '--ddb-tools', str(args.ddb_tools),
                '--aux-vdb', str(args.aux_vdb), '--child', '--sample', str(sample),
                '--variant', str(variant), '--automatic', '1', '--special-pcm', '1']
            process = subprocess.run(command, capture_output=True, text=True, timeout=90)
            if process.returncode:
                raise RuntimeError(f'{sample}/{variant}: {process.stderr[-1800:]}')
            row = json.loads(process.stdout)
            assert row['contract']['special_pcm'] == 1
            assert row['contract']['window_kind'] == 15
            assert row['contract']['queue_index'] == 0
            peer = next(x for x in baseline['cases'] if x['case'] == row['case']
                        and x['variant'] == variant and x['automatic'] == 1)
            assert row['parser_sha256'] == peer['parser_sha256']
            assert row['ddi_sha256'] == peer['ddi_sha256']
            assert row['frames'] == peer['frames']
            row['ordinary_protocol_comparisons'] = peer['comparisons']
            rows.append(row)
            print(json.dumps(dict(case=row['case'], variant=variant,
                                  bit17_nonzero=row['comparisons']['17']['produced_nonzero'])),
                  flush=True)
    return dict(dse_sha256=SHA256,
                auxiliary_sha256=auxiliary_hash,
                baseline_sha256=hashlib.sha256(args.baseline.read_bytes()).hexdigest(),
                cases=rows, frame_count=sum(row['frames'] for row in rows),
                scope='Config ID134/+5c8 is set before the native constructor. '
                      'Five reviewed mode4 variants, automatic pitch, five installed '
                      'sequences. Assert kind15, queue index zero and corresponding '
                      'window geometry. No scalar/score injection or history replacement; '
                      'this does not identify the commercial authoring protocol.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb', 'baseline', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    report = run(args)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
