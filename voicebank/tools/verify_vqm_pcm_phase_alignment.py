"""Owned PCM to phase alignment/ENV and native saved-phase consumption.

All production predictions precede DSE loading. Native origin and circular-math
leaves receive generated numerical fixtures only; the PCM phase entry is never
called, and no analysis frame or pitch is injected or temporarily bypassed.
"""
import argparse
from collections import Counter
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import struct
import subprocess
import sys

from audit_frm2_numeric import Native, F, D, I, P, Q, SHA256, f32, interpolate
from audit_frm2_phase_history import wrap
from frm2_candidate_pcm_source import raw_at
from frm2_mode5_math_backend import module_loaded
from frm2_pcm_phase_alignment import PcmPhaseAlignment
from frm2_work_directory import temporary_directory
from probe_frm2 import MAIN_MASK, MainFrame, Envelope, parse_frame, serialize_frame
from verify_frm2_pcm_phase_roundtrip import readback
from verify_vqm_pcm_harmonic_phase import BASE, PATH_ARGS, HarmonicObserver
from verify_vqm_pcm_origin_owned import guarded, guards
from verify_vqm_time_domain_events import EventProduction


class ConsumerNative(Native):
    def __init__(self, dll, expectations):
        self.phase_expectations = expectations
        self.phase_cursor = 0
        self.phase_byte_exact_values = 0
        super().__init__(dll)

    def fn(self, rva, result, *args):
        original = super().fn(rva, result, *args)
        if rva != 0x16fa70:
            return original
        def check(context, enabled):
            assert enabled == 1 and self.phase_cursor < len(self.phase_expectations)
            expected = self.phase_expectations[self.phase_cursor]
            value = original(context, enabled)
            frame = P.from_address(context + 0x2238).value
            assert I.from_address(frame + 0x158).value == len(expected) // 4
            actual = c.string_at(P.from_address(frame + 0x170).value, len(expected))
            assert actual == expected, (self.phase_cursor, 'saved phase consumer IEEE bytes')
            self.phase_cursor += 1
            self.phase_byte_exact_values += len(expected) // 4
            return value
        return check


def circular_alignment(native, output, pitch, origin, previous, voiced_run):
    wrap = native.fn(0x2af60, D, D)
    difference = native.fn(0x2afd0, D, D, D)
    frequency, amplitude, raw = map(list, zip(*output))
    # The installed instruction sequence uses a double delay and wraps in
    # double before each float32 array store.
    delay = difference(float(origin), float(raw[0])) / math.tau / float(pitch)
    aligned = [f32(wrap((float(k) * (float(pitch) * math.tau)) * delay + float(phase)))
        if hz > 0. else phase for k, (hz, phase) in enumerate(zip(frequency, raw), 1)]
    math_calls = sum(hz > 0. for hz in frequency) + 1
    valid = 0
    if voiced_run > 1 and previous is not None:
        total = 0.
        old_frequency, old_phase = previous
        for k in range(10):
            if frequency[k] > 0. and old_frequency[k] > 0.:
                total = f32(float(total) + difference(float(old_phase[k]), float(aligned[k])) / float(k + 1))
                valid += 1
        if valid:
            shift = f32(float(f32(total / float(valid))) * .9)
            aligned = [f32(wrap(float(phase) + float(k) * float(shift)))
                if hz > 0. else phase for k, (hz, phase) in enumerate(zip(frequency, aligned), 1)]
            math_calls += sum(hz > 0. for hz in frequency)
        math_calls += valid
    return aligned, (frequency, aligned), math_calls, valid


def child(args):
    assert not module_loaded('DSE.dll') and args.sample is None
    pcm = [f32(value) for value in BASE.owned_signal(args.shape, args.frequency)]
    event = EventProduction(args, pcm, args.frequency) if args.mode == 2 else None
    harmonic = HarmonicObserver(args, pcm, args.frequency, event.complete if event else None)
    producer = PcmPhaseAlignment()
    predictions, frames = [], []
    counts = Counter()
    energy_enabled = BASE.PROFILES[args.profile].get(23, 1.) > 0.
    for index, frame in enumerate(harmonic.frame_predictions):
        energy = raw_at(pcm, index, energy_enabled)['feature12']
        row = producer.advance(frame, energy, energy_enabled)
        predictions.append(row)
        counts.update(frames=1, unvoiced=row['aligned'] is None,
            locked_frames=row['locked'], amplitude_gate_frames=row['origin']['amplitude_gate'],
            nonzero_origin_state_frames=row['origin']['origin'] != 0.,
            nonzero_origin_returns=row['origin']['called'] and row['origin']['origin'] != 0.,
            nonzero_correction_values=sum(value != 0. for value in row['correction']),
            nonzero_derivatives=row['derivative'] != 0.)
        if row['serializable_phase']:
            output = frame['output']
            frequency, amplitude, raw = map(tuple, zip(*output))
            env = Envelope(0x01820002, (-20000., 20000., 0.),
                tuple(value for pair in row['knots'] for value in pair))
            # Owned phase-field format fixture: spectral fitting is deliberately
            # absent. It is never offered as a playable voicebank or an original
            # maker output. The raw phase array and ENV handoff follow builder100.
            saved = MainFrame(0, index * 256 / 44100., MAIN_MASK,
                frequency, amplitude, raw, f32(frame['pitch']), env,
                (), (0., 0., 0.), (), Envelope(0x00960002, (-100., 10., 0.), (0., 0., 1., 0.)))
            blob = serialize_frame(saved)
            assert parse_frame(blob) == saved
            frames.append(saved)
            counts['produced_phase_frames'] += 1
            counts['phase_knots'] += len(row['knots'])
        elif row['aligned'] is not None:
            counts['unsupported_phase_envelopes_not_emitted'] += 1
    assert not module_loaded('DSE.dll')
    phase_expectations = []
    for frame in frames:
        knots = list(zip(frame.harmonic_envelope.values[::2], frame.harmonic_envelope.values[1::2]))
        values = []
        for hz, phase in zip(frame.frequencies_hz, frame.phases_radians):
            value = phase
            if hz > 0.:
                x = f32(hz * f32(1 / 22050.))
                value = f32(phase + f32(wrap(interpolate(knots, x, cubic=True))))
            values.append(value)
        phase_expectations.append(struct.pack('<110f', *values))
    assert not module_loaded('DSE.dll')
    native = ConsumerNative(args.dll, phase_expectations)
    context = guarded(native, bytes(0xc00))
    history, pitch_history = guarded(native, bytes(60)), guarded(native, bytes(80))
    I.from_address(context + 0xa80).value = 15
    P.from_address(context + 0xa88).value = history
    I.from_address(context + 0x428).value = 20
    P.from_address(context + 0x420).value = pitch_history
    frame = guarded(native, bytes(4096))
    native.frame_ctor(frame)
    Q.from_address(frame + 0x50).value = 7
    arrays = [guarded(native, bytes(440)) for _ in range(3)]
    for offset, pointer in zip((0x60, 0x68, 0x70), arrays):
        P.from_address(frame + offset).value = pointer
    origin_leaf = native.fn(0xda730, F, P, F, F, P)
    native_origin, previous, voiced_run = 0., None, 0
    for index, row in enumerate(predictions):
        I.from_address(context + 0xa90).value = index
        c.memmove(pitch_history, row['pitch_history'], 80)
        output = row['frame']['output']
        payloads = [struct.pack('<' + str(len(output)) + 'f', *(v[column] for v in output))
            for column in range(3)]
        I.from_address(frame + 0x58).value = len(output)
        for pointer, payload in zip(arrays, payloads):
            c.memmove(pointer, payload, len(payload))
        before = c.string_at(frame, 4096)
        if row['aligned'] is not None:
            native_origin = origin_leaf(context, native_origin, row['frame']['pitch'], frame)
            assert struct.pack('<f', native_origin) == struct.pack('<f', row['origin']['origin']), (index, 'origin')
            voiced_run += 1
            actual, previous, math_calls, valid = circular_alignment(native, output,
                row['frame']['pitch'], native_origin, previous, voiced_run)
            assert struct.pack('<110f', *actual) == struct.pack('<110f', *row['aligned']), (index, 'aligned phase',
                next((i for i, (a, b) in enumerate(zip(actual, row['aligned'])) if a != b), None))
            counts.update(native_origin_calls=1, aligned_phase_values_byte_exact=110,
                circular_math_leaf_calls=math_calls, temporal_shared_low_band_slots=valid)
        else:
            voiced_run = 0
            counts['unvoiced_no_native_origin_call'] += 1
        assert c.string_at(history, 60) == row['history']
        assert bool(I.from_address(context + 0xa94).value) == row['locked']
        assert before == c.string_at(frame, 4096)
        for pointer, payload in zip(arrays, payloads):
            assert c.string_at(pointer, len(payload)) == payload
            guards(pointer, 440)
        for pointer, size in ((context, 0xc00), (history, 60), (pitch_history, 80), (frame, 4096)):
            guards(pointer, size)
    with temporary_directory('460-pcm-phase-') as directory:
        path = Path(directory) / 'owned-phase.frm2'
        if frames:
            path.write_bytes(b''.join(serialize_frame(value) for value in frames))
            saved = readback(native, path.resolve(), frames)
            assert saved['readback_coordinates']['max'] == 0.
            counts['native_phase_consumer_frames'] += saved['frames']
            counts['native_phase_consumer_values'] += saved['phase_consumer']['count']
        else:
            saved = dict(frames=0, no_supported_voiced_phase=True)
    assert native.phase_cursor == len(frames)
    counts['native_phase_consumer_values_byte_exact'] = native.phase_byte_exact_values
    return dict(shape=args.shape, frequency=args.frequency, mode=args.mode, profile=args.profile,
        counts=dict(sorted(counts.items())), phase_readback=saved,
        predictions_before_dse_load=True, reference_pitch_harmonics_origin_or_phase_as_model_inputs=False,
        owned_native_leaf_inputs_and_guards_preserved=True,
        native_pcm_phase_entry=False, analysis_frame_injection=False, temporary_pitch_bypass=False,
        classification_value=0., independent_delta_initialization=0., scratch_removed=True)


def review_phase_contract(dll, decoder_dir):
    sys.path.insert(0, str(decoder_dir.resolve()))
    import capstone as cs
    from audit_vqm_v4_module_leads import PE
    from audit_vqm_feature_calls_cfg import runtime_groups
    data = dll.read_bytes()
    assert hashlib.sha256(data).hexdigest() == SHA256
    pe = PE(data)
    groups, _ = runtime_groups(data, pe)
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    decoder.detail = True
    ops = {}
    for start, end, _ in groups[0xd2cc0]:
        ops.update({op.address - pe.image_base: op for op in decoder.disasm(
            data[pe.offset(start):pe.offset(start) + end - start], pe.image_base + start)})
    for site, value in ((0xd690f, 0x7d), (0xd73c7, 0x2a), (0xd73f8, 0x7d)):
        assert ops[site].mnemonic == 'mov' and ops[site].operands[1].imm == value
    for site, target in ((0xd693e, 0xef9f0), (0xd760d, 0xda730),
            (0xd76d3, 0x2af60), (0xd776d, 0x2afd0), (0xd7818, 0x2af60)):
        assert ops[site].operands[0].imm - pe.image_base == target
    assert ops[0xd6926].mnemonic == 'jne' and ops[0xd6926].operands[0].imm - pe.image_base == 0xd695d
    assert ops[0xd740f].mnemonic == 'je' and ops[0xd740f].operands[0].imm - pe.image_base == 0xd81e7
    assert ops[0xd7615].operands[0].mem.disp == 0xa98
    assert ops[0xd76e3].operands[0].mem.index and ops[0xd76df].operands[1].mem.disp == 0x70
    native = Native(dll)
    curve_rows = []
    for enabled in (0, 1):
        config = native.alloc(0x35f0)
        native.fn(0x90de0, P, P)(config)
        I.from_address(config + 0x5a8).value = enabled
        query = native.fn(0x2b050, F, P, I, F)
        values = {hex(key): query(config, key, 0.) for key in (0x2a, 0x7d)}
        assert values['0x7d'] == enabled and values['0x2a'] > 0.
        curve_rows.append(dict(phase_analysis=enabled, curve_values=values))
    return dict(ordinary_step_rva='0xd2cc0', phase_configuration_rows=curve_rows,
        harmonic_gate_curve='0x7d', harmonic_skipped_when_nonzero_branch_rva='0xd6926',
        phase_gate_curve='0x7d', phase_skipped_when_zero_branch_rva='0xd740f',
        additional_phase_enable_curve='0x2a', origin_state_store_rva='0xd7615',
        origin_state_offset='0xa98', phase_array_offset='0x70',
        circular_alignment_rvas=['0xd7629', '0xd76e3'],
        previous_frame_alignment_rvas=['0xd770b', '0xd7828'],
        bit17_offset='0x21c',
        scope='Pinned direct conditions and stores; enabling phase skips harmonic generation. This explains the observed empty arrays but does not restore the unknown native incoming-harmonic protocol.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in PATH_ARGS:
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--child', action='store_true')
    parser.add_argument('--shape', default='steady')
    parser.add_argument('--frequency', type=float, default=220.)
    parser.add_argument('--mode', type=int, choices=(1, 2), default=1)
    parser.add_argument('--profile', choices=BASE.PROFILES, default='default')
    args = parser.parse_args()
    args.sample = args.owned_length = None
    if args.child:
        print(json.dumps(child(args)))
        return
    assert args.output
    baseline = json.loads(Path('voicebank_doc/evidence/458_vqm_pcm_harmonic_phase.json').read_text(encoding='utf-8'))
    contract = review_phase_contract(args.dll, args.decoder_dir)
    rows, counts = [], Counter()
    for old in baseline['owned_pcm_origin_checks']['cases']:
        command = [sys.executable, '-B', str(Path(__file__).resolve()), '--child']
        for name in PATH_ARGS:
            command += ['--' + name, str(getattr(args, name.replace('-', '_')))]
        for name in ('shape', 'frequency', 'mode', 'profile'):
            command += ['--' + name, str(old[name])]
        process = subprocess.run(command, capture_output=True, text=True, timeout=180)
        assert process.returncode == 0, (old['shape'], old['mode'], process.stderr[-1800:])
        row = json.loads(process.stdout)
        assert row['counts']['frames'] == old['controls']['frames']
        assert row['counts'].get('native_origin_calls', 0) == old['controls'].get('native_origin_calls', 0)
        assert row['counts'].get('nonzero_origin_returns', 0) == old['controls'].get('nonzero_origin_returns', 0)
        rows.append(row)
        counts.update(row['counts'])
        print(json.dumps(dict(shape=row['shape'], mode=row['mode'], frequency=row['frequency'], counts=row['counts'])), flush=True)
    sources = set(baseline['source_sha256']) | {
        'frm2_pcm_phase_alignment.py', 'verify_vqm_pcm_phase_alignment.py',
        'verify_frm2_phase_pipeline.py', 'frm2_phase_controls.py',
        'verify_frm2_pcm_phase_roundtrip.py', 'frm2_work_directory.py'}
    report = dict(schema=1, dse_sha256=SHA256,
        source_sha256={name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sorted(sources)},
        phase_contract=contract, cases=rows, summary=dict(sorted(counts.items())),
        scope='Owned PCM plus configured pitch to independent candidates/harmonics/origin/alignment/phase ENV and saved-phase consumption',
        limits=['Default 44.1kHz/hop256, mode1/2 candidate production, harmonic mode1 and110 slots only',
                'Band classification is explicitly zero; independent delta history starts at zero',
                'Native origin/circular leaves validate owned numerical fixtures, not a complete native PCM phase batch',
                'ENV temporal correction reuses the previously verified PhaseModel; no new claim of original maker initialization',
                'Saved frames are phase-format fixtures with no spectral fitting or voicebank assembly',
                'Unsupported or unvoiced phase envelopes are recorded and not emitted',
                'No native analysis-frame injection, temporary pitch bypass, commercial leaf inputs or original bit17 assignment',
                'Original maker scores, BRWSP input/configuration, zero arrays and VQM/Growl completion remain open'])
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__ == '__main__':
    main()
