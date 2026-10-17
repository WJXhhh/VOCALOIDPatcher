"""Check every installed V4X ordinary part against the independent pitch2 rule."""

import argparse
from collections import Counter
import hashlib
import json
import math
import mmap
from pathlib import Path
import struct

from analyze_reference_layers import walk_art
from analyze_reference_regions import read_frame
from pitch2_reference import NO_PITCH, reference_pitch_cents
from verify_reference_ddi import load_public_parser, pointer


TOLERANCE_CENT = 0.0001


def audit_bank(path, expected_sha256, model_type):
    raw = path.read_bytes()
    ddi_sha256 = hashlib.sha256(raw).hexdigest()
    assert ddi_sha256 == expected_sha256, path
    model = model_type(raw)
    model.read()
    voiced = set(model.phdc_data['phoneme']['voiced'])
    units = [('STA', (unit['phoneme'],), list(unit['stap'].values()))
             for unit in model.sta_data.values()]
    units.extend(('ART', key, parts)
                 for root in model.art_data.values() for key, parts in walk_art(root))
    totals = Counter()
    max_errors = {}
    mismatches = []
    with path.with_suffix('.ddb').open('rb') as stream:
        with mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as data:
            for kind, key, parts in units:
                for part in parts:
                    f0 = [read_frame(data, pointer(epr, raw)[0])[1]
                          for epr in part['epr']]
                    category = ('STA' if kind == 'STA' else
                                'ART2' if len(key) == 2 else 'ART3')
                    if category == 'ART2':
                        alignments = tuple((group['start'], group['end'],
                                            group['start2'], group['end2'])
                                           for group in part['frame_align'])
                        predicted = reference_pitch_cents(f0, alignments, key, voiced)
                    else:
                        predicted = reference_pitch_cents(f0)
                    stored = float(part['pitch2'])
                    totals[category + '_parts'] += 1
                    totals[category + '_frames'] += len(f0)
                    if predicted == NO_PITCH:
                        totals[category + '_sentinel'] += 1
                        valid = stored == NO_PITCH
                        error = None
                    else:
                        totals[category + '_finite'] += 1
                        error = abs(predicted - stored)
                        max_errors[category] = max(max_errors.get(category, 0.), error)
                        if struct.pack('<f', predicted) == struct.pack('<f', stored):
                            totals[category + '_exact_float32'] += 1
                        valid = math.isfinite(stored) and error <= TOLERANCE_CENT
                    if not valid:
                        totals['mismatches'] += 1
                        if len(mismatches) < 12:
                            mismatches.append(dict(kind=category, key=list(key),
                                                   stored=stored, predicted=predicted,
                                                   error_cent=error))
    return dict(bank=path.stem, ddi_sha256=ddi_sha256,
                counts=dict(sorted(totals.items())),
                max_error_cent=max_errors, mismatch_examples=mismatches)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--voice-root', type=Path, required=True)
    parser.add_argument('--ddb-tools', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    args = parser.parse_args()
    manifest_bytes = args.manifest.read_bytes()
    manifest = json.loads(manifest_bytes)
    parser_sha256 = hashlib.sha256(
        (args.ddb_tools / 'utils' / 'ddi_utils.py').read_bytes()).hexdigest()
    assert parser_sha256 == manifest['parser_sha256']
    model_type = load_public_parser(args.ddb_tools)
    banks = []
    for row in manifest['banks']:
        matches = list(args.voice_root.rglob(row['bank'] + '.ddi'))
        assert len(matches) == 1
        banks.append(audit_bank(matches[0], row['ddi_sha256'], model_type))
    totals = Counter()
    for bank in banks:
        totals.update(bank['counts'])
    assert len(banks) == 8 and totals['STA_parts'] == 688
    assert totals['ART2_parts'] == 20615 and totals['ART3_parts'] == 3266
    assert all(totals[category + '_exact_float32'] == totals[category + '_finite']
               for category in ('STA', 'ART2', 'ART3'))
    result = dict(manifest_sha256=hashlib.sha256(manifest_bytes).hexdigest(),
                  parser_sha256=parser_sha256,
                  tolerance_cent=TOLERANCE_CENT, totals=dict(sorted(totals.items())),
                  banks=banks,
                  scope='All installed V4X ordinary STA and ART parts, including '
                        'sentinel cases. PHDC voiced outer regions select pair ART '
                        'frames; STA and triple ART use all positive-F0 frames. '
                        'This checks the saved values, not the original authoring caller.')
    args.evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n',
                             encoding='utf-8')
    print(json.dumps(result['totals']))
    assert totals['mismatches'] == 0, 'pitch2 rule has mismatches'


if __name__ == '__main__':
    main()
