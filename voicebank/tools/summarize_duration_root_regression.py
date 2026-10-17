"""Check isolated duration-selection regressions and save bounded, pointer-free evidence."""
import argparse
import hashlib
import json
import statistics
import struct
import wave
from pathlib import Path


def read(path):
    if path.stat().st_size > 80_000_000:
        raise ValueError('Unexpectedly large regression report')
    return json.loads(path.read_text(encoding='utf-8-sig'))


def sources(row):
    return sorted({item['Material']['Id'] for item in row['selected']})


def checked_case(path, strict=False):
    report = read(path)
    rows = report['rows']
    assert len(rows) == 12
    assert all(row['allTimingUnchanged'] and row['nativeTransitionsUnchanged'] for row in rows)
    assert all(row['repeatStable'] for row in rows)
    assert rows[0]['hash'] == rows[1]['hash'] == rows[-1]['hash']
    assert report['coldPcm'] == report['baselinePcm']
    assert all(row['inputHash'] == rows[0]['inputHash'] for row in rows)
    assert all(row['events'] == rows[0]['events'] for row in rows)
    assert all(row['pitchComparison']['maximumCents'] == 0 for row in rows)
    plan = report['planOnly']
    assert plan['equal'] and plan['queries'] == dict(Starts=0, Steps=0, Samples=0)
    if strict:
        assert all(row['hash'] == report['baselinePcm'] for row in rows)
        assert all(sources(row) == sources(rows[0]) for row in rows)
    compact = []
    for row in rows:
        material_changed = sources(row) != sources(rows[0])
        audio_changed = row['hash'] != rows[0]['hash']
        assert material_changed == audio_changed
        compact.append(dict(index=row['index'], budget_frames=row['budgetFrames'],
                            boundaries=row['boundaries'],
                            all_boundaries=row.get('allBoundaries'),
                            native_transitions=row['nativeTransitions'],
                            material_ids=sources(row), material_changed=material_changed,
                            pcm_sha256=row['hash'], native_pcm_sha256=row['nativePcmHash'],
                            native_samples=row['nativeSamples'], audio_changed=audio_changed,
                            repeat_stable=row['repeatStable'],
                            reference_pcm_equal=row['pcmMatchesUnhooked'],
                            pitch_comparison=row['pitchComparison'],
                            changed_time_contract_entries=row['contractChanged']))
    return dict(fixture=report['fixture'], report_sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                real_timing_reference=report['baseline'],
                reference_pcm_sha256=report['baselinePcm'],
                selection_a_pcm_sha256=rows[0]['hash'],
                selection_a_equals_reference=rows[0]['hash'] == report['baselinePcm'],
                plan_ms=plan['milliseconds'], query_counters=plan['queries'],
                time_plan_entries=len(plan['actual']),
                preparation_ms=plan.get('PreparationMs'),
                initialization_ms=plan.get('InitializationMs'),
                material_change_cases=sum(row['material_changed'] for row in compact),
                audio_change_cases=sum(row['audio_changed'] for row in compact), rows=compact)


def matrix(directory, strict=False):
    names = sorted(path.name.removesuffix('-plan-only.json')
                   for path in directory.glob('*-plan-only.json'))
    assert len(names) == 10
    return {name: checked_case(directory / (name + '.json'), strict) for name in names}


def pcm_difference(directory, fixture, first, second, budget):
    samples = []
    for index in (first, second):
        with wave.open(str(directory / f'{fixture}-{index}-{budget}.wav'), 'rb') as stream:
            assert (stream.getnchannels(), stream.getsampwidth(), stream.getframerate()) == (1, 2, 44100)
            samples.append(struct.unpack('<' + 'h' * stream.getnframes(), stream.readframes(stream.getnframes())))
    assert len(samples[0]) == len(samples[1])
    delta = [index for index, (a, b) in enumerate(zip(*samples)) if a != b]
    assert delta
    return dict(different_samples=len(delta), first_ms=delta[0] / 44.1, last_ms=delta[-1] / 44.1,
                maximum_integer_delta=max(abs(samples[0][i] - samples[1][i]) for i in delta))


def ablations(directory):
    result = {}
    for name, fixture in [('without-timing-contract', 'ling-multi'),
                          ('without-timbre-scope', 'ling-multi'),
                          ('without-layer-scope', 'miku-ka-up')]:
        folder = directory / name
        path = folder / (fixture + '.json')
        data = read(path)
        rows = data['rows']
        drift = sum(not row['allTimingUnchanged'] or not row['nativeTransitionsUnchanged'] for row in rows)
        repeat = sum(not row['repeatStable'] for row in rows)
        if name == 'without-timing-contract':
            assert drift > 0 and repeat == 0
        else:
            assert drift == 0 and repeat == 1
        item = dict(fixture=fixture, report_sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                    exit_code=int((folder / 'exit.txt').read_text(encoding='utf-8-sig')),
                    renders=len(rows), timing_drift_cases=drift, repeat_failures=repeat,
                    cold_pcm_sha256=data['coldPcm'], reference_pcm_sha256=data['baselinePcm'],
                    final_pcm_sha256=rows[-1]['hash'])
        if name == 'without-layer-scope':
            item['pcm_difference'] = pcm_difference(folder, fixture, 6, 9, 64)
            assert item['pcm_difference']['different_samples'] == 13
            assert item['pcm_difference']['maximum_integer_delta'] == 1
        if name == 'without-timing-contract':
            assert item['exit_code'] == 0
        else:
            assert item['exit_code'] != 0
        result[name] = item
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ['forward', 'reverse', 'strict', 'ablations', 'output']:
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--final-smoke', type=Path)
    args = parser.parse_args()
    forward, reverse, strict = matrix(args.forward), matrix(args.reverse), matrix(args.strict, True)
    assert forward.keys() == reverse.keys() == strict.keys()
    for name in forward:
        assert [row['pcm_sha256'] for row in forward[name]['rows']] == [row['pcm_sha256'] for row in reverse[name]['rows']]
    final_smoke = {}
    if args.final_smoke:
        for name in ['miku-ka-up', 'ling-multi']:
            final_smoke[name] = checked_case(args.final_smoke / name / (name + '.json'))
            assert [row['pcm_sha256'] for row in final_smoke[name]['rows']] == [row['pcm_sha256'] for row in forward[name]['rows']]
    latency = [case['plan_ms'] for group in (forward, reverse, strict) for case in group.values()]
    evidence = dict(schema=1, date='2026-10-17',
                    dse_version='6.13.1.1', dse_image_base='0x180000000',
                    dse_sha256='7A4965BD377DBC2B08710E807AE1345B67D9FFF6579CA0C01A769F744366F5B5',
                    scope='Serialized isolated process; no Editor, deployment, production ABI or audio postprocessing.',
                    experimental_selection=dict(pitch_window_cents=1200, duration_weight=128),
                    strict_selection=dict(pitch_window='native', duration_weight=1),
                    summary=dict(fixtures=10, experimental_renders=240, strict_renders=120,
                                 timing_mismatches=0, repeat_or_restore_mismatches=0,
                                 cross_process_pcm_mismatches=0,
                                 experimental_material_changes=sum(case['material_change_cases'] for group in (forward, reverse) for case in group.values()),
                                 plan_only_queries=len(latency),
                                 plan_ms=dict(minimum=min(latency), median=statistics.median(latency), maximum=max(latency))),
                    roots=dict(real_time_contract=['0x19D650', '0x1B3D30', '0x1A7310'],
                               timbre_history=dict(lookup='0x182220', updater='0x1C44F0', unvoiced_consumer='0x1C5220', constructor='0x1C4240'),
                               layer_history=dict(consumer='0x1722C0', shared_bss_rva='0xA20050')),
                    ablations=ablations(args.ablations), forward=forward, reverse=reverse, strict=strict,
                    final_direct_harness_smoke=final_smoke,
                    limitations=['Experimental pitch-window enlargement can change selection A relative to native selection; two fixtures do so.',
                                 'Common valid target-pitch fields match numerically, but some tail validity masks differ.',
                                 'Preparation and cold engine initialization are excluded from plan-only latency.',
                                 'No host concurrency, multi-Part lifecycle, voice-quality listening or general pitch-curve coverage is claimed.'])
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(evidence['summary'], ensure_ascii=False))


if __name__ == '__main__':
    main()
