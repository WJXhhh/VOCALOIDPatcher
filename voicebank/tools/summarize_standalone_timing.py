"""Validate standalone timing results and retain numerical evidence, without WAVs or DLLs."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import statistics


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def latency(values):
    values = sorted(values)
    return {'min': values[0], 'median': statistics.median(values),
            'p95': values[math.ceil(len(values) * .95) - 1], 'max': values[-1]}


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('matrix', 'benchmark', 'editor', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    matrix, benchmark = load(args.matrix), load(args.benchmark)
    rows = matrix['results']
    basic = {'alone', 'three', 'previous', 'following', 'five', 'gap', 'short', 'evec',
             'shift_tempo', 'rin', 'repeat', 'chain100', 'release', 'controllers'}
    expected = {(name, v) for name in basic for v in (0, 32, 64, 96, 127)}
    expected |= {(f'fuzz_{n}', v) for n in range(24) for v in (0, 64, 127)}
    if len(rows) != 142 or {(r['fixture'], r['velocity']) for r in rows} != expected:
        raise ValueError('Missing or duplicate full-boundary comparison')
    for row in rows:
        prediction, cached = row['prediction'], row['cached']
        if not (row['equal'] and row['sourceUnchanged'] and prediction['Boundaries'] == row['actual']
                and cached['Boundaries'] == row['actual'] and cached['CacheHit'] and cached['PlannerRuns'] == 0):
            raise ValueError('Boundary, source isolation or cache failure')
        if row['dse']['Steps'] or row['dse']['Samples']:
            raise ValueError('Candidate synthesized audio')
        if not (row['referenceDse']['Steps'] > 0 and row['referenceDse']['Samples'] > 0):
            raise ValueError('Missing PCM positive control')
        if not (1 <= prediction['ContextNotes'] <= 33 and 1 <= prediction['PlannerRuns'] <= 5):
            raise ValueError('Unbounded context or planning')
    golden_path = root / 'VOCALOIDPatcher.McpTests/Fixtures/consonant-timing-response.json'
    golden = load(golden_path)
    golden_map = {(case['phonemes'], case['legato'], v): start
                  for case in golden['cases'] for v, start in enumerate(case['starts'])}
    measured = benchmark['goldenResults']
    if len(measured) != 512 or len(golden_map) != 512 or benchmark['goldenComparisons'] != 512:
        raise ValueError('Incomplete VEL sweep')
    if {(r['phones'], r['legato'], r['velocity']): r['onset'] for r in measured} != golden_map:
        raise ValueError('VEL sweep differs from independently rendered golden')
    if benchmark['dse']['Steps'] or benchmark['dse']['Samples'] or any(benchmark['preCanceled'].values()):
        raise ValueError('Benchmark or pre-canceled query entered synthesis')
    if not benchmark['resultArrayIsolation'] or benchmark['disposedSessions'] < 12:
        raise ValueError('Missing cache or lifecycle checks')
    if (len(benchmark['invalidations']) != 9 or len(benchmark['longResults']) != 3
            or [r['sourceNotes'] for r in benchmark['longResults']] != [100, 1000, 10000]):
        raise ValueError('Missing invalidation or long-Part probes')
    for row in benchmark['longResults']:
        if row['queryCount'] != 20 or row['maxContext'] > 33 or row['maxRuns'] > 5:
            raise ValueError('Invalid long-Part probe')
    images = {name: sha256(args.editor / name) for name in ('DSE.dll', 'VSM.dll', 'VOCALOID6.dll')}
    if images['DSE.dll'] != golden['dse_sha256']:
        raise ValueError('Golden and installed DSE differ')
    evidence = {
        'schema': 1, 'capturedAtUtc': datetime.now(timezone.utc).isoformat(),
        'scope': 'Private research process; no Editor launch, deployment or drag integration.',
        'images': images,
        'backendSourceSha256': {p.name: sha256(p) for p in sorted(
            (root / 'VOCALOIDPatcher/VOCALOIDPatcher/ConsonantTiming/Backend').glob('*.cs'))},
        'rawResultSha256': {'matrix': sha256(args.matrix), 'benchmark': sha256(args.benchmark),
                            'golden': sha256(golden_path)},
        'fullBoundaryComparison': {
            'cases': len(rows), 'allEqual': True, 'maxErrorTicks': 0,
            'emptyNativeResults': sum(not r['actual'] for r in rows),
            'allSourcesUnchanged': True, 'candidatePcmSteps': 0, 'candidatePcmSamples': 0,
            'everyOracleProducedPcm': True, 'initializationMs': matrix['initializationMs'],
            'uncachedLatencyMs': latency([r['prediction']['Milliseconds'] for r in rows]),
            'cachedLatencyMs': latency([r['cached']['Milliseconds'] for r in rows]),
            'maxContextNotes': max(r['prediction']['ContextNotes'] for r in rows),
            'maxPlannerRuns': max(r['prediction']['PlannerRuns'] for r in rows),
            'results': [dict(fixture=r['fixture'], velocity=r['velocity'], predicted=r['prediction']['Boundaries'],
                             actual=r['actual'], contextNotes=r['prediction']['ContextNotes'],
                             plannerRuns=r['prediction']['PlannerRuns'], milliseconds=r['prediction']['Milliseconds'],
                             referencePcmSteps=r['referenceDse']['Steps'], referencePcmSamples=r['referenceDse']['Samples'])
                        for r in rows],
        },
        'velocitySweep': {'cases': 512, 'curves': 4, 'allOnsetsEqual': True,
                          'latencyMs': benchmark['goldenLatency']},
        'performance': {key: value for key, value in benchmark.items()
                        if key not in ('goldenResults', 'goldenComparisons', 'goldenLatency')},
        'limitations': [
            'Only the exact native image hashes, Windows x64 and 44100 Hz are enabled.',
            'MIKU/RIN sampled banks measured; AI, secondary bank, overlapping notes, DVQM and manual phoneme offsets rejected.',
            'At most 33 context notes, 22 seconds, 256 tempo entries, 60 phoneme characters per note and 4096 score frames.',
            'Twice-stable expanded windows are empirical evidence, not a universal context proof.',
            'Caller must keep source stable, clear cache after other Part edits, and retain manager lifetimes.',
            'Long-Part probes measure performance only; they do not full-render 1000/10000-note references.',
            'A native planning mutex and busy rejection exist; concurrent Editor rendering and UI request scheduling remain unvalidated.',
            'One before/after memory observation cannot prove absence of leaks.',
        ],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'fullBoundaryCases': len(rows), 'goldenOnsetCases': 512,
                      'pcmSteps': 0, 'output': str(args.output)}, ensure_ascii=False))


if __name__ == '__main__':
    main()
