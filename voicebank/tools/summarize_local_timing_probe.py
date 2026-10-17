"""Validate isolated timing probe outputs and save compact, non-proprietary evidence."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import statistics


def digest(rows):
    return hashlib.sha256('\n'.join(rows).encode('ascii')).hexdigest()


def distribution(rows, key):
    values = [row[key] for row in rows]
    return {name: round(value, 6) for name, value in {
        'min': min(values), 'median': statistics.median(values), 'max': max(values)
    }.items()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('commit', 'metadata', 'direct', 'editor', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    commit = json.loads(args.commit.read_text(encoding='utf-8-sig'))
    metadata = json.loads(args.metadata.read_text(encoding='utf-8-sig'))
    direct = json.loads(args.direct.read_text(encoding='utf-8-sig'))
    expected_fixtures = {'three', 'alone', 'previous', 'following', 'five', 'five_crop_three',
                         'gap', 'short', 'evec', 'shift_tempo', 'rin', 'tail_1000', 'long_empty_tail'}
    velocities = {32, 64, 96}
    direct_keys = {(r['fixture'], r['velocity']) for r in direct}
    metadata_keys = {(r['fixture'], r['velocity']) for r in metadata}
    expected_direct = {(name, velocity) for name in expected_fixtures for velocity in velocities}
    required_metadata = {(name, velocity) for name in expected_fixtures - {
        'shift_tempo', 'rin', 'tail_1000', 'long_empty_tail'} for velocity in velocities}
    if (direct_keys != expected_direct or len(direct_keys) != len(direct)
            or not required_metadata <= metadata_keys <= expected_direct
            or len(metadata_keys) != len(metadata) or len(commit['results']) != 3):
        raise ValueError('Incomplete fixture matrix')
    for row in metadata + direct:
        if not (row['equal'] and row['earlyEqual'] and row['inputsEqual'] and row['status'] == -1):
            raise ValueError('Timing comparison failed')
        if row['activeReference'] != row['activeEarly']:
            raise ValueError('Early schedule differs')
        if row['dse']['Steps'] != 0 or row['dse']['Samples'] != 0 or row['dse']['Starts'] != 1:
            raise ValueError('Unexpected candidate synthesis or lifecycle')
        if row['referenceDse']['Steps'] <= 0 or row['referenceDse']['Samples'] <= 0:
            raise ValueError('Missing synthesis positive control')
    for row in direct:
        if row['hostVelocity'] != 64 or row['nativeFrameAfter'] != 0 or row['planningCalls'] <= 0:
            raise ValueError('Direct planner entered frame advancement or used candidate host velocity')
    for row in commit['results']:
        if row['after'] != row['before'] or row['withoutAudioEqual'] or any(row['dse'].values()):
            raise ValueError('Unexpected local commit result')
        if row['mode'] != 'commit' and row['editingBefore'] != row['editing']:
            raise ValueError('Editing timing geometry changed')
    primary = [r for r in direct if r['fixture'] not in ('tail_1000', 'long_empty_tail')]
    overhead = [r for r in direct if r not in primary]
    by_case = {(r['fixture'], r['velocity']): r for r in direct}
    crops = []
    for velocity in (64, 32, 96):
        full = by_case['five', velocity]['boundaries']
        cropped = by_case['five_crop_three', velocity]['boundaries']
        if full != cropped:
            raise ValueError('Five-to-three fixture crop differs')
        crops.append({'velocity': velocity, 'full': full, 'cropped': cropped})
    fields = ('fixture', 'notes', 'velocity', 'bank', 'PartStart', 'TempoChangeTick',
              'TailNotes', 'CandidateDuration', 'hostVelocity', 'planningCalls',
              'nativeFrameAfter', 'inputFrames', 'dse', 'referenceDse',
              'referenceWallMs', 'computeMs', 'startMs', 'wallMs', 'status')
    compact = []
    for row in direct:
        item = {key: row[key] for key in fields}
        item.update(referenceTickBoundaries=row['boundaries'],
                    independentlyPredictedTickBoundaries=None,
                    scheduleRecordCount=len(row['activeReference']),
                    referenceScheduleSha256=digest(row['activeReference']),
                    candidateScheduleSha256=digest(row['activeEarly']),
                    inputMatchesFreshVsm=row['inputsEqual'], scheduleEqual=row['earlyEqual'])
        compact.append(item)
    result = {
        'schema': 1, 'capturedAtUtc': datetime.now(timezone.utc).isoformat(),
        'scope': 'Isolated research process; no Editor launch, deployment or runtime drag integration.',
        'images': {name: hashlib.sha256((args.editor / name).read_bytes()).hexdigest()
                   for name in ('DSE.dll', 'VSM.dll', 'VOCALOID6.dll')},
        'metadataReplay': {
            'cases': len(metadata), 'fullScheduleEqual': True, 'earlyActiveScheduleEqual': True,
            'pcmSteps': 0, 'pcmSamples': 0,
            'fullAdvanceMs': distribution(metadata, 'computeMs'),
            'earlySnapshotMs': distribution(metadata, 'earlyMs'),
        },
        'directPlanning': {
            'correctnessCases': len(primary), 'overheadCases': len(overhead),
            'allActiveSchedulesEqual': True, 'pcmSteps': 0, 'pcmSamples': 0,
            'nativeFrameAfter': 0,
            'planningAndReadMs': distribution(primary, 'computeMs'),
            'engineStartMs': distribution(primary, 'startMs'),
            'vsmQueryEnvelopeMs': distribution(primary, 'wallMs'),
            'note': 'Observed min/median/max across fixtures, not a repeated p95 latency benchmark.',
        },
        'localCommit': commit, 'fiveToThreeCrop': crops, 'cases': compact,
        'limitations': [
            'Baseline VEL64 input was captured during an oracle render of each short fixture. A standalone local input builder is not implemented.',
            'Only NRPN target VEL is changed in that baseline input; fresh candidate VSM input is an independent oracle. Candidate host Part VEL stays 64.',
            'Exact active native schedule tuples are compared; standalone reconstruction of GetPhonemePositions tick boundaries is not implemented.',
            'Candidate engine sessions still start and cancel through VSM. Timing excludes fixture creation, insertion, commit and input copying.',
            'Tail benchmarks use cached short-context input and compare with the short oracle, not a full render of the 1003-note Part.',
            'One five-to-three-note crop matching does not establish a universal context radius.',
            'Memory, sustained gesture scheduling, engine concurrency and additional engine versions remain unvalidated.',
        ],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(f'validated: metadata={len(metadata)}, direct={len(primary)}, overhead={len(overhead)}, commit=3')


if __name__ == '__main__':
    main()
