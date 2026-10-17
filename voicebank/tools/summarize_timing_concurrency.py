"""Validate native timing coexistence, cancellation and lifetime evidence from disposable probes."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import statistics


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def distribution(values):
    values = sorted(values)
    return {'count': len(values), 'min': values[0], 'median': statistics.median(values),
            'p95': values[math.ceil(len(values) * .95) - 1], 'max': values[-1]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('concurrency', 'core', 'blocked-before', 'starved-before', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    full = load(args.concurrency)
    if not full['complete'] or full['error'] or full['stage'] != 'completed':
        raise ValueError('Concurrency run did not complete')
    rows = {row['name']: row for row in full['results']}
    required = {'memory', 'lifecycle', 'source_switch', 'two_queries', 'mutex', 'reference_0',
                'reference_1', 'render', 'same_part', 'other_bank', 'paced', 'close'}
    if set(rows) != required or len(full['results']) != len(required):
        raise ValueError('Missing or duplicate lifecycle/concurrency cases')
    reference = rows['reference_0']
    for name in ('reference_0', 'reference_1', 'render', 'same_part', 'other_bank', 'paced', 'close'):
        row = rows[name]
        if row['pcm'] != reference['pcm'] or row['boundaries'] != reference['boundaries']:
            raise ValueError('Concurrent PCM or boundaries differ from serial reference')
        if len(row['boundaries']) != 48 or row['dse']['Steps'] != reference['dse']['Steps'] or row['dse']['Steps'] <= 0:
            raise ValueError('Different reference coverage or PCM step count')
    modes = []
    for name in ('render', 'same_part', 'other_bank', 'paced', 'close'):
        row = rows[name]
        successful = [q for q in row['queries'] if not q['busy']]
        if not successful or row['overlapping'] <= 0:
            raise ValueError('No actual successful query/PCM overlap')
        modes.append({'name': name, 'attempts': len(row['queries']), 'successful': len(successful),
                      'busy': len(row['queries']) - len(successful), 'actualOverlaps': row['overlapping'],
                      'renderWallMs': row['wallMs'], 'queryLatencyMs': distribution([q['milliseconds'] for q in successful]),
                      'maxContextNotes': max(q['ContextNotes'] for q in successful),
                      'maxPlannerRuns': max(q['PlannerRuns'] for q in successful),
                      'pcmEqual': True, 'all48BoundaryListsEqual': True,
                      'maxRequestRateHz': row['maxRequestRateHz'], 'closes': row['closes']})
    if rows['paced']['maxRequestRateHz'] != 60 or rows['close']['closedWhileRendering'] != 3:
        raise ValueError('Missing pacing or active close coverage')
    if not any(c['renderPcmStepsDuringClose'] > 0 for c in rows['close']['closes']):
        raise ValueError('Close did not overlap PCM rendering')
    life = rows['lifecycle']
    if not all(life[key] for key in ('canceled', 'rejected', 'disposed', 'replacementEqual')):
        raise ValueError('Cancellation/close contract failed')
    if life['busyMs'] > 100 or life['cancellationMs'] > 200 or rows['source_switch']['closedSources'] != 30:
        raise ValueError('Missing bounded cancellation or source switching')
    dual = rows['two_queries']
    if min(dual['successesA'], dual['successesB']) < 20 or any(dual['dse'].values()):
        raise ValueError('Concurrent private sessions synthesized/restarted or lost progress')
    mutex = rows['mutex']
    if not mutex['sameMutex'] or mutex['outcome'] != 'TimeoutException' or mutex['waitMs'] > 200:
        raise ValueError('Contended query did not fail promptly')
    memory = rows['memory']
    snapshots = memory['snapshots']
    if [s['queries'] for s in snapshots] != [0, 1000, 2000, 4000, 8000] or any(memory['dse'].values()):
        raise ValueError('Missing sustained memory or no-Start/PCM evidence')
    if (snapshots[-1]['handles'] != snapshots[1]['handles']
            or snapshots[-1]['threads'] != snapshots[1]['threads']):
        raise ValueError('Sustained native handles/threads increased')
    core = load(args.core)
    current_hashes = {p.name: sha256(p) for p in sorted(
        (root / 'VOCALOIDPatcher/VOCALOIDPatcher/ConsonantTiming/Backend').glob('*.cs'))}
    if core['backendSourceSha256'] != current_hashes or core['fullBoundaryComparison']['cases'] != 142 or core['velocitySweep']['cases'] != 512:
        raise ValueError('Core correctness evidence is missing or stale')
    blocked = load(args.blocked_before)['results'][0]
    starved = {r['name']: r for r in load(args.starved_before)['results']}
    if blocked['waitMs'] < 1000 or starved['render']['wallMs'] < 10000:
        raise ValueError('Missing regression witnesses')
    evidence = {
        'schema': 1, 'capturedAtUtc': datetime.now(timezone.utc).isoformat(),
        'scope': 'Same-process native VSM renderer and private timing sessions in an isolated harness. No Editor process, deployment or drag UI.',
        'images': core['images'], 'backendSourceSha256': current_hashes,
        'rawResultSha256': {key: sha256(getattr(args, key)) for key in ('concurrency', 'core', 'blocked_before', 'starved_before')},
        'regressions': {
            'startLockBeforeMs': blocked['waitMs'], 'contendedWarmQueryMs': mutex['waitMs'],
            'startInsideLeaseSerialRenderMs': starved['reference_1']['wallMs'],
            'startInsideLeaseHammerRenderMs': starved['render']['wallMs'],
            'fix': 'Start once at private-session construction; reset only schedule/parser under a cancellable 5 ms slice / 50 ms budget lease.',
        },
        'serialReferences': [{'wallMs': rows[name]['wallMs'], 'pcmSha256': rows[name]['pcm'],
                              'pcmSteps': rows[name]['dse']['Steps']} for name in ('reference_0', 'reference_1')],
        'renderModes': modes, 'lifecycle': life, 'sourceSwitch': rows['source_switch'],
        'concurrentQueries': dual, 'contendedMutex': mutex,
        'memory': {**memory, 'privateBytesGrowthAfter1000': snapshots[-1]['privateBytes'] - snapshots[1]['privateBytes'],
                   'managedBytesGrowthAfter1000': snapshots[-1]['managedBytes'] - snapshots[1]['managedBytes']},
        'correctnessRevalidated': {'fullBoundaryCases': 142, 'onsetCases': 512, 'pcmSteps': 0,
                                  'maxErrorTicks': 0, 'evidence': args.core.name},
        'remaining': ['Real Editor/WPF Dispatcher and normal feature patches have not been exercised.',
                      'No current Editor MCP instance was available; no deployment or Editor launch was performed.',
                      'Future UI must coalesce/latest-only requests and preserve source lifetime; unpaced requests still consume CPU.',
                      'Cold session construction/teardown is not covered by the 50 ms planning-lock budget.',
                      'Memory plateau over 8000 queries does not prove absence of all leaks.'],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'renderModes': len(modes), 'allPcmEqual': True, 'queryWorkers': [dual['successesA'], dual['successesB']],
                      'memoryQueries': 8000, 'output': str(args.output)}, ensure_ascii=False))


if __name__ == '__main__':
    main()
