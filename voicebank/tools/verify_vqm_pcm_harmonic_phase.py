"""PCM to natural harmonic phase, coupled to the established candidate pipeline.

Reference pitch, harmonic indices and phases are comparisons only. Commercial
inputs and predictions stay in memory; no additional native leaf receives them.
"""
import argparse
from collections import Counter
import ctypes as c
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import struct
import subprocess
import sys
import traceback

import verify_vqm_peak_phase as previous
from audit_frm2_numeric import F, I, P, Q, D, SHA256, f32
from frm2_candidate_geometry import controls
from frm2_candidate_pcm_source import raw_at, band_ratio
from frm2_candidate_pipeline import CandidatePipeline
from frm2_mode5_math_backend import module_loaded
from frm2_pcm_harmonic_phase import HarmonicPhasePipeline


BASE = previous.BASE
PATH_ARGS = previous.PATH_ARGS


class HarmonicObserver(previous.PhaseObserver):
    def __init__(self, args, pcm, frequency, complete):
        bypass = args.mode == 1 and BASE.PROFILES[args.profile].get(20, 1) == 0
        super().__init__(args.dll, pcm, bypass)
        assert not module_loaded('DSE.dll')
        self.tracking_counts, self.tracking_branches = Counter(), Counter()
        configured = controls(frequency, BASE.PROFILES[args.profile])
        if args.mode == 1 and not bypass:
            candidate = CandidatePipeline(1, configured)
            complete, previous_energy = [], None
            for index, source in enumerate(self.inputs):
                records = [struct.unpack_from('<fffiI', source['records'], i*20)
                    for i in range(source['count'])]
                old_records = [(hz, db, span, bin_index) for hz, db, phase, span, bin_index in records]
                raw = raw_at(pcm, index, configured['curves'][23] > 0)
                values = raw['feature4'], raw['feature8'], raw['feature12'], band_ratio(old_records)
                complete.append(candidate.process(dict(power=source['power'], records=old_records, values=values), previous_energy))
                previous_energy = values[2]
        self.frame_predictions = []
        pipeline = HarmonicPhasePipeline(frequency)
        self.harmonic_controls = pipeline.controls
        for index, source in enumerate(self.inputs[:-1]):
            coarse_pitch = frequency if bypass else complete[index+1]['history'][-3][0]
            records = [struct.unpack_from('<fffiI', source['records'], i*20)
                for i in range(source['count'])]
            self.frame_predictions.append(pipeline.process(records, coarse_pitch, index))
        assert not module_loaded('DSE.dll')

    def install(self, native, context):
        super().install(native, context)
        tracker = P.from_address(context+0xc98).value
        config = P.from_address(context+0xad0).value
        assert tracker and I.from_address(tracker+0x18).value == 0
        assert I.from_address(context+0xc18).value == 3
        candidate = P.from_address(context+0xc88).value
        assert I.from_address(candidate+0x5bc).value == 1
        query = native.fn(0x2b050, F, P, I, F)
        for key, value in self.harmonic_controls.items():
            assert query(config, key, 0.) == query(config, key, 1.) == value, (key, 'harmonic config')
        assert I.from_address(config+0x5d0+35*0xa0+0x50).value == 1
        assert query(config, 36, 0.) == 0.
        assert query(config, 0x70, 0.) == 1. and query(config, 0x7e, 0.) == 0.
        table = P.from_address(context).value
        original = c.CFUNCTYPE(Q, P)(P.from_address(table+8).value)
        private = native.alloc(0x100)
        c.memmove(private, table, 0x100)
        def step(ctx):
            try:
                queue = P.from_address(ctx+0xc50).value
                descriptor = P.from_address(queue).value
                target = P.from_address(descriptor+0x28).value
                result = original(ctx)
                output = P.from_address(ctx+0xcc8).value
                if not output:
                    return result
                assert output == target
                at = round(D.from_address(output+0x48).value*44100/256.)
                assert at == self.tracking_counts['calls'], (at, 'tracking schedule')
                prediction = self.frame_predictions[at]
                assert c.string_at(tracker+0x21bd8, 4) == struct.pack('<f', prediction['history']), (at, 'tracking pitch')
                assert bool(I.from_address(tracker+0x21b30).value) == prediction['initialized'], (at, 'initialized')
                assert I.from_address(tracker+0x1c).value == len(prediction['tracks']), (at, 'track count')
                for slot, fields in enumerate(prediction['tracks']):
                    for offset, data in fields:
                        actual = c.string_at(tracker+0x20+slot*0x54+offset, len(data))
                        assert actual == data, (at, slot, hex(offset), 'track state')
                        self.tracking_counts['track_state_values_byte_exact'] += len(data)//4
                count = I.from_address(output+0x58).value
                assert count == len(prediction['output']), (at, 'output slots', count, len(prediction['output']))
                phases = P.from_address(output+0x70).value
                actual = c.string_at(phases, count*4) if count else b''
                assert actual == prediction['phase_bytes'], (at, 'harmonic phases')
                feature = P.from_address(output+0x128).value
                self.tracking_counts.update(calls=1, weighted_pitch_byte_exact=1,
                    harmonic_phase_values_byte_exact=count,
                    harmonic_phase_nonzero=sum(row[2] != 0. for row in prediction['output']),
                    assigned_harmonics=sum(i >= 0 for i in prediction['selected']),
                    missing_harmonics=prediction['missing'],
                    observed_bit17_nonzero=F.from_address(feature+0x21c).value != 0.)
                self.tracking_branches.update(initialization=prediction['initialized_now'],
                    reset=prediction['reset_now'], collisions=prediction['collisions'],
                    unvoiced=prediction['pitch'] <= 0.)
                return result
            except BaseException:
                traceback.print_exc()
                os._exit(2)
        callback = c.CFUNCTYPE(Q, P)(step)
        native.keep.extend((original, callback))
        P.from_address(private+8).value = c.cast(callback, P).value
        P.from_address(context).value = private

    def tracking_report(self, frames):
        assert self.tracking_counts['calls'] == frames == len(self.frame_predictions)
        return dict(complete_predictions_before_dse_load=True, native_pitch_or_selection_or_phase_as_model_inputs=False,
            counts=dict(sorted(self.tracking_counts.items())), branches=dict(sorted(self.tracking_branches.items())),
            controls={str(key):value for key, value in self.harmonic_controls.items()},
            next_candidate_history_offset=-3, queue_index=0, queue_entries=3,
            repair_durations_zero=True, frequency_post_correction_zero=True)


def child(args):
    frequency = args.frequency
    if args.sample is None:
        pcm = BASE.owned_signal(args.shape, frequency)
    else:
        _, _, sequences = BASE.load_sequences(args.manifest, args.voice_root, args.ddb_tools)
        sequences += BASE.auxiliary_sequences(args.aux_vdb)
        sequence = sequences[args.sample]
        pcm = sequence['pcm']
        frequency = sequence.get('pitch1', statistics.median(row['f0'] for row in sequence['frames']))
    pcm = [f32(value) for value in pcm]
    event_module = previous.previous
    original_event = event_module.EventProduction
    event = original_event(args, pcm, frequency) if args.mode == 2 else None
    observer = HarmonicObserver(args, pcm, frequency, event.complete if event else None)
    original_observer = previous.PhaseObserver
    def event_factory(call_args, call_pcm, call_frequency):
        assert call_args is args
        assert list(call_pcm) == pcm, 'PCM content differs'
        assert call_frequency == frequency, 'Frequency configuration differs'
        return event
    def observer_factory(dll, call_pcm, bypass):
        assert dll == args.dll and call_pcm == pcm and bypass == observer.manual_bypass
        return observer
    event_module.EventProduction, previous.PhaseObserver = event_factory, observer_factory
    try:
        row = previous.child(args)
    finally:
        event_module.EventProduction, previous.PhaseObserver = original_event, original_observer
    row['harmonic_phase'] = observer.tracking_report(row['frames'])
    return row


def review_harmonic(dll, decoder_dir):
    sys.path.insert(0, str(decoder_dir))
    import capstone as cs
    from audit_vqm_v4_module_leads import PE
    from audit_vqm_feature_calls_cfg import runtime_groups
    data = dll.read_bytes()
    assert hashlib.sha256(data).hexdigest() == SHA256
    pe = PE(data)
    groups, _ = runtime_groups(data, pe)
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    decoder.detail = True
    def decode(root):
        ops = {}
        for start, end, _ in groups[root]:
            ops.update({op.address-pe.image_base:op for op in decoder.disasm(
                data[pe.offset(start):pe.offset(start)+end-start], pe.image_base+start)})
        return ops
    caller, candidate, copy = decode(0xd2cc0), decode(0xe60a0), decode(0xf0cf0)
    for site, target in ((0xd580b, 0xe60a0), (0xd693e, 0xef9f0),
            (0xd6958, 0xeff80), (0xd760d, 0xda730)):
        assert caller[site].operands[0].imm-pe.image_base == target
    for site in (0xe623d, 0xe62d6):
        assert candidate[site].operands[0].mem.disp == 0xf0
    assert candidate[0xe6235].operands[1].mem.disp == 0x18
    assert candidate[0xe6222].operands[1].mem.disp == -8
    assert candidate[0xe62cf].operands[1].mem.disp == 0
    assert copy[0xf0dec].operands[1].mem.disp == 0x70
    assert caller[0xd7615].operands[0].mem.disp == 0xa98
    return dict(main_rva='0xd2cc0', history_dispatch_rva='0xe60a0',
        history_hz_store_rvas=['0xe623d', '0xe62d6'],
        history_delay=1, output_delay=1, queue_entries=3, tracking_queue_index=0,
        next_pipeline_history_index=-3, weighted_pitch_range=['0xd66b2', '0xd68ce'],
        tracking_rva='0xef9f0', tracking_call_rva='0xd693e', phase_copy_rva='0xf0cf0',
        phase_array_load_rva='0xf0dec', phase_peak_field_offset=8,
        optional_frequency_correction_rva='0xeff80', correction_call_rva='0xd6958',
        ordinary_origin_rva='0xda730', origin_call_rva='0xd760d',
        origin_state_store_rva='0xd7615', origin_state_offset='0xa98',
        internal_bit17_offset='0x21c', origin_fixture_frame_mask=7,
        scope='Hash-pinned actual calls and history/output source offsets. '
            'Natural batches leave phase mode off. Ordinary origin is checked '
            'separately on generated PCM-derived numerical fixtures.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in PATH_ARGS:
        parser.add_argument('--'+name, type=Path, required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--child', action='store_true')
    parser.add_argument('--sample', type=int, choices=range(5))
    parser.add_argument('--shape', default='steady')
    parser.add_argument('--frequency', type=float, default=220.)
    parser.add_argument('--mode', type=int, choices=(1, 2), default=1)
    parser.add_argument('--profile', choices=BASE.PROFILES, default='default')
    args = parser.parse_args()
    args.owned_length = None
    if args.child:
        print(json.dumps(child(args)))
        return
    assert args.output
    baseline = json.loads(Path('voicebank_doc/evidence/457_vqm_peak_phase.json').read_text(encoding='utf-8'))
    contract = review_harmonic(args.dll, args.decoder_dir)
    from verify_vqm_pcm_origin_owned import run as owned_origin_checks
    owned = owned_origin_checks(args)
    def repeat(rows):
        output = []
        for old in rows:
            command = [sys.executable, '-B', str(Path(__file__).resolve()), '--child']
            for name in PATH_ARGS:
                command += ['--'+name, str(getattr(args, name.replace('-', '_')))]
            for name in ('shape', 'frequency', 'mode', 'profile', 'sample'):
                if old[name] is not None:
                    command += ['--'+name, str(old[name])]
            process = subprocess.run(command, capture_output=True, text=True, timeout=180)
            assert process.returncode == 0, (old['sample'], old['shape'], old['mode'], old['profile'], process.stderr[-1700:])
            row = json.loads(process.stdout)
            assert all(row[key] == value for key, value in old.items()), 'Established outputs changed'
            output.append(row)
            print(json.dumps(dict(sample=row['sample'], shape=row['shape'], mode=row['mode'], profile=row['profile'],
                harmonic_phase_values=row['harmonic_phase']['counts']['harmonic_phase_values_byte_exact'])), flush=True)
        return output
    rows = repeat(baseline['cases'])
    boundary = repeat(baseline['owned_geometry_checks']['cases'])
    counts, branches = Counter(), Counter()
    for row in rows+boundary:
        counts.update(row['harmonic_phase']['counts']); branches.update(row['harmonic_phase']['branches'])
    sources = tuple(baseline['source_sha256'])+('frm2_pcm_harmonic_phase.py',
        'verify_vqm_pcm_harmonic_phase.py', 'frm2_peak_selection.py', 'probe_frm2_weighted_pitch.py',
        'frm2_harmonic_origin.py', 'verify_vqm_pcm_origin_owned.py',
        'verify_frm2_phase_origin.py', 'audit_frm2_phase_history.py', 'verify_vqm_compact_origin_stage.py')
    report = dict(baseline)
    report.update(source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        cases=rows, owned_geometry_checks=dict(baseline['owned_geometry_checks'], cases=boundary),
        harmonic_phase_contract=contract, owned_pcm_origin_checks=owned,
        harmonic_phase_summary=dict(cases=len(rows+boundary), counts=dict(sorted(counts.items())), branches=dict(sorted(branches.items()))),
        scope='PCM plus explicit configuration to candidate history, weighted pitch, harmonic selection/state and natural harmonic phases before DSE loads',
        limits=list(baseline['limits'])+[
            'Harmonic mode1,110 slots,static default feedback,disabled repair and frequency correction only',
            'Origin alignment and original saved bit17 remain separate production gaps'])
    args.output.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report['harmonic_phase_summary']))


if __name__ == '__main__':
    main()
