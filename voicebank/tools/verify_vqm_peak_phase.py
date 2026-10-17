"""Independent PCM to complete raw peaks, alongside the established full pipeline.

All complex FFT and phase predictions precede DSE loading. Native outputs are
observed, never rewritten; evidence contains only hashes, counts and controls.
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

import verify_vqm_time_domain_events as previous
from audit_frm2_numeric import F, I, P, Q, D, SHA256, f32
from frm2_candidate_pcm_source import HOP
from frm2_mode5_math_backend import PublicRealFft, module_loaded, SystemFloatLog
from frm2_peak_phase import CandidatePcmPhaseSource, SystemFloatAtan2
from verify_vqm_peak_phase_owned import owned_phase_checks


BASE = previous.previous.previous.base
PATH_ARGS = ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb', 'decoder-dir')


class PhaseObserver:
    def __init__(self, dll, pcm, manual_bypass):
        assert not module_loaded('DSE.dll')
        self.inputs, self.counts, self.branches = [], Counter(), Counter()
        self.manual_bypass = manual_bypass
        atan2 = SystemFloatAtan2()
        self.math_runtime = SystemFloatLog.metadata(atan2)
        with PublicRealFft(dll.parent/'DSE_DFT.dll') as transform:
            source = CandidatePcmPhaseSource(transform, atan2)
            for index in range(math.ceil(len(pcm)/HOP)+1):
                prediction = source.spectrum_at(pcm, index)
                power = bytes((F*2048)(*prediction['power']))
                bins = b''.join(struct.pack('<ff', value.real, value.imag) for value in prediction['bins'])
                peaks = b''.join(struct.pack('<fffiI', *row) for row in prediction['records'])
                self.inputs.append(dict(power=power, bins=bins, records=peaks,
                    count=len(prediction['records']), branches=prediction['branches'],
                    nonzero_phase=sum(row[2] != 0. for row in prediction['records'])))
            self.fft_calls, self.configuration = transform.calls, transform.configuration
            assert transform.guards_checked == transform.calls == len(self.inputs)
            assert not module_loaded('DSE.dll')
        assert not transform.handle and not module_loaded('DSE.dll')

    def install(self, native, context):
        obj = P.from_address(context+0xc88).value
        assert obj
        delay = I.from_address(obj+0x5b8).value
        assert delay == 1
        table = P.from_address(context).value
        original = c.CFUNCTYPE(Q, P)(P.from_address(table+8).value)
        private = native.alloc(0x100)
        c.memmove(private, table, 0x100)

        def step(ctx):
            try:
                slots = P.from_address(ctx+0xc30).value
                descriptor = P.from_address(slots+delay*8).value
                target = P.from_address(descriptor+0x28).value
                old_time = D.from_address(obj+0x5b0).value
                result = original(ctx)
                if D.from_address(obj+0x5b0).value == old_time:
                    return result
                at = round(D.from_address(target+0x48).value*44100/HOP)
                predicted = self.inputs[at]
                assert I.from_address(descriptor).value == 2048
                assert c.string_at(P.from_address(descriptor+8).value, 8192) == predicted['power'], (at, 'power')
                assert c.string_at(P.from_address(descriptor+16).value, 16392) == predicted['bins'], (at, 'complex bins')
                count = I.from_address(descriptor+0x18).value
                assert count == predicted['count'], (at, 'record count')
                actual = c.string_at(P.from_address(descriptor+0x20).value, count*20) if count else b''
                assert actual == predicted['records'], (at, 'complete peak',
                    next((i//20 for i, (a, b) in enumerate(zip(actual, predicted['records'])) if a != b), None))
                feature = P.from_address(target+0x128).value
                assert feature
                self.counts.update(calls=1, power_values_byte_exact=2048,
                    complex_float_values_byte_exact=4098, complete_peak_records_byte_exact=count,
                    phase_values_byte_exact=count, nonzero_source_phase_values=predicted['nonzero_phase'],
                    observed_internal_bit17_values=1,
                    observed_internal_bit17_nonzero=F.from_address(feature+0x21c).value != 0.,
                    empty_calls=count == 0)
                self.branches.update(predicted['branches'])
                return result
            except BaseException:
                traceback.print_exc()
                os._exit(2)
        callback = c.CFUNCTYPE(Q, P)(step)
        native.keep.extend((original, callback))
        P.from_address(private+8).value = c.cast(callback, P).value
        P.from_address(context).value = private

    def report(self, expected_calls):
        assert self.counts['calls'] == expected_calls
        assert bool(expected_calls) != self.manual_bypass
        return dict(predictions_before_dse_load=True, reference_fft_or_phase_as_model_input=False,
            self_owned_fft_freed_before_dse_load=True, precomputed_spectrum_windows=len(self.inputs),
            fft_calls=self.fft_calls, fft_configuration=self.configuration,
            system_math_runtime=self.math_runtime, manual_mode1_dispatch_bypass=self.manual_bypass,
            counts=dict(sorted(self.counts.items())), branches=dict(sorted(self.branches.items())))


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
    assert len(pcm)%128 != 1, 'This established matrix contains no omitted isolated sample'
    bypass = args.mode == 1 and BASE.PROFILES[args.profile].get(20, 1) == 0
    observer = PhaseObserver(args.dll, pcm, bypass)
    original_batch = BASE.configured_batch

    def configured_batch(profile):
        batch = original_batch(profile)
        def observed(*arguments, **options):
            old_setup = options.get('setup_observer')
            if old_setup is not None:
                def setup(native, context):
                    old_setup(native, context)
                    observer.install(native, context)
                options['setup_observer'] = setup
            return batch(*arguments, **options)
        return observed

    # A child-local Python binding changes the observer factory, not files,
    # native analysis inputs, native code or a global DLL table.
    BASE.configured_batch = configured_batch
    try:
        row = previous.child(args)
    finally:
        BASE.configured_batch = original_batch
    row['peak_phase'] = observer.report(row['controls']['dispatcher_calls'])
    return row


def review_phase(dll, decoder_dir):
    sys.path.insert(0, str(decoder_dir))
    import capstone as cs
    from audit_vqm_v4_module_leads import PE
    from audit_vqm_feature_calls_cfg import runtime_groups, import_slots
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
    detector, caller, batch = decode(0x51e60), decode(0xd2cc0), decode(0xd9b10)
    for site in (0x522bf, 0x522d0):
        assert detector[site].operands[0].imm-pe.image_base == 0x217df0
    thunk = decode(0x217df0) if 0x217df0 in groups else {
        op.address-pe.image_base:op for op in decoder.disasm(data[pe.offset(0x217df0):pe.offset(0x217df0)+6], pe.image_base+0x217df0)}
    jump = thunk[0x217df0]
    assert jump.mnemonic == 'jmp' and jump.operands[0].type == cs.x86.X86_OP_MEM
    slot = jump.address+jump.size+jump.operands[0].mem.disp-pe.image_base
    imports = import_slots(data, pe)
    assert imports[slot]['symbol'] == 'atan2f'
    for site, name in ((0x523b2, 'subss'), (0x523d1, 'mulss'), (0x523d7, 'addss'),
            (0x523ef, 'addss'), (0x5241c, 'cvtpd2ps'), (0x5242e, 'cvttsd2si'),
            (0x5243f, 'cvtpd2ps'), (0x52457, 'jbe')):
        assert detector[site].mnemonic == name, hex(site)
    assert caller[0xd332e].operands[1].mem.disp == 0xc10
    assert caller[0xd33ab].operands[0].mem.disp == -0x68
    assert caller[0xd33c2].operands[0].mem.disp == -0x68
    assert caller[0xd4f7b].operands[0].mem.disp == 0x20
    assert caller[0xd4f8c].operands[0].imm-pe.image_base == 0xd04c0
    for site, offset in ((0xd9b5f, 0x13c), (0xd9b66, 0x140)):
        assert batch[site].mnemonic == 'mov'
        assert batch[site].operands[1].mem.disp == offset
        assert batch[site].reg_name(batch[site].operands[1].mem.base) == 'rdx'
    assert batch[0xd9dfe].operands[0].imm-pe.image_base == 0x48250
    return dict(detector_rva='0x51e60', wrapper_rva='0xd04c0', atan2_thunk_rva='0x217df0',
        atan2_import=imports[slot], atan2_call_rvas=['0x522bf', '0x522d0'],
        phase_record_offset=8, stride=20, phase_origin_correction_time=0.,
        batch_second_argument=dict(sample_count_offset='0x13c', sample_rate_offset='0x140',
            reader_rva='0x48250', reader_call_rva='0xd9dfe',
            role='Waveform samples, not a preceding harmonic-frame track'),
        rounding_order_sites=[hex(site) for site in (0x523b2, 0x523d1, 0x523d7, 0x523ef, 0x5241c, 0x5242e, 0x5243f)],
        scope='Known normal1025/kind11, centered4096 FFT and unchanged main clock. '
            'Nonzero correction time is separately checked on owned spectra. '
            'The phase record is not feature+0x21c or the later origin state.')


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
    baseline = json.loads(Path('voicebank_doc/evidence/456_vqm_time_domain_events.json').read_text(encoding='utf-8'))
    contract = review_phase(args.dll, args.decoder_dir)
    owned = owned_phase_checks(args.dll)
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
            assert process.returncode == 0, (old['sample'], old['shape'], old['mode'], old['profile'], process.stderr[-1600:])
            row = json.loads(process.stdout)
            assert all(row[key] == value for key, value in old.items()), 'Established pipeline outputs changed'
            output.append(row)
            print(json.dumps(dict(sample=row['sample'], shape=row['shape'], mode=row['mode'], profile=row['profile'],
                phase_calls=row['peak_phase']['counts'].get('calls', 0))), flush=True)
        return output
    rows = repeat(baseline['cases'])
    boundary = repeat(baseline['owned_geometry_checks']['cases'])
    counts, branches = Counter(), Counter()
    for row in rows+boundary:
        counts.update(row['peak_phase']['counts'])
        branches.update(row['peak_phase']['branches'])
    sources = tuple(baseline['source_sha256'])+('frm2_peak_phase.py',
        'verify_vqm_peak_phase_owned.py', 'verify_vqm_peak_phase.py')
    report = dict(baseline)
    report.update(source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        peak_phase_contract=contract, owned_peak_phase_checks=owned, cases=rows,
        owned_geometry_checks=dict(baseline['owned_geometry_checks'], cases=boundary),
        peak_phase_summary=dict(cases=len(rows+boundary), actual_checked_cases=sum(
            bool(row['peak_phase']['counts'].get('calls')) for row in rows+boundary),
            counts=dict(sorted(counts.items())), branches=dict(sorted(branches.items())),
            precomputed_spectrum_windows=sum(row['peak_phase']['precomputed_spectrum_windows'] for row in rows+boundary)),
        scope='Complete raw FFT peak records including phase, independently from PCM before DSE loads; '
            'the established mode1/2 candidate/pitch/confidence results are preserved',
        limits=[value for value in baseline['limits'] if value !=
            'Same FFT mathematical implementation with an independently owned public descriptor; peak phase is excluded'
            and not value.startswith('Peak phase, original maker scores')]+[
            'System float32 atan2 and the pinned public FFT supply elementary math; phase interpolation and corrections are independent',
            'The two manual mode1 cases skip candidate dispatch and have no phase comparison claimed',
            'The new raw peak phase is not feature bit17; saved nonzero bit17, maker scores, BRWSP input/configuration and zero arrays remain unresolved'])
    args.output.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report['peak_phase_summary']))


if __name__ == '__main__':
    main()
