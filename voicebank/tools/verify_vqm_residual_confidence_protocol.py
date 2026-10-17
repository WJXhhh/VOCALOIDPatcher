"""Verify constructor curve42 and the genuine residual confidence writer.

Only owned configuration inputs change. Native code, source PCM, frames and
score buffers are not replaced. Reports retain aggregates, never payloads.
"""
import argparse
import ctypes as c
import hashlib
import importlib
import json
import math
import os
from pathlib import Path
import statistics
import struct
import subprocess
import sys
import traceback
import types

from audit_frm2_numeric import Native, P, I, B, F, D, Q, SHA256, f32
from audit_vqm_v4_module_leads import PE
from audit_vqm_feature_calls_cfg import runtime_groups
from frm2_residual_confidence import residual_confidence
from probe_frm2_batch_outputs import run as analyze_batch
from verify_vqm_mode5_pcm import signal
from verify_vqm_alternate_score_paths import frames as final_frames
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences, metrics


def configured_batch(mode):
    if mode not in range(5):
        raise ValueError('Unreviewed residual constructor selector')

    class ConfiguredNative(Native):
        def fn(self, rva, result, *arguments):
            original = super().fn(rva, result, *arguments)
            if rva != 0x90de0:
                return original

            def construct(config):
                returned = original(config)
                for time in (0., 1.):
                    self.add(config + 0x5d0 + 0x2a * 0xa0, time, float(mode))
                assert self.evaluate(config + 0x5d0 + 0x2a * 0xa0, 0.) == mode
                assert self.evaluate(config + 0x5d0 + 0x2a * 0xa0, 1.) == mode
                return returned
            return construct

    # Give the existing owned batch harness a private Python dependency map.
    # Its native constructor/dispatcher still run normally. The original
    # module and all previously pinned source files remain unchanged.
    batch = types.FunctionType(analyze_batch.__code__, dict(analyze_batch.__globals__, Native=ConfiguredNative),
                               analyze_batch.__name__, analyze_batch.__defaults__)
    batch.__kwdefaults__ = analyze_batch.__kwdefaults__
    return batch


def review(dll, decoder_dir):
    data = dll.read_bytes()
    assert hashlib.sha256(data).hexdigest() == SHA256
    pe = PE(data)
    groups, _ = runtime_groups(data, pe)
    sys.path.insert(0, str(decoder_dir.resolve()))
    cs = importlib.import_module('capstone')
    assert cs.__version__ == '5.0.3'
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    decoder.detail = True

    def decode(root):
        ops = []
        for start, end, _ in groups[root]:
            ops += list(decoder.disasm(data[pe.offset(start):pe.offset(start) + end - start], pe.image_base + start))
        return {op.address - pe.image_base: op for op in ops}

    caller = decode(0xd2cc0)
    branch = decode(0xf6220)
    constructor = decode(0xf5410)
    for ops, site, target in ((caller, 0xd6ed9, 0xf6220), (branch, 0xf6327, 0xf7eb0),
                              (branch, 0xf632f, 0xf9940)):
        op = ops[site]
        assert op.group(cs.CS_GRP_CALL) and op.operands[0].imm - pe.image_base == target
    assert caller[0xd6ecb].operands[1].mem.disp == 0xc58
    assert caller[0xd6ed2].operands[1].mem.disp == 0xca0
    assert branch[0xf6323].mnemonic == 'cmp' and branch[0xf6323].operands[1].imm == 4
    assert branch[0xf6325].mnemonic == 'je' and branch[0xf6325].operands[0].imm - pe.image_base == 0xf6336
    selector_reads = [op for op in constructor.values() if op.mnemonic == 'mov' and len(op.operands) == 2
                      and op.operands[1].type == cs.x86.X86_OP_IMM and op.operands[1].imm == 0x2a]
    selector_stores = [op for op in constructor.values() if op.mnemonic == 'mov' and len(op.operands) == 2
                       and op.operands[0].type == cs.x86.X86_OP_MEM and op.operands[0].mem.disp == 0x3c0]
    assert len(selector_reads) == len(selector_stores) == 1
    return dict(caller_rva='0xd2cc0', call_rva='0xd6ed9', dispatcher_rva='0xf6220',
                writer_rva='0xf7eb0', next_stage_rva='0xf9940', constructor_rva='0xf5410',
                curve_id='0x2a', curve_selector_read_rva=hex(selector_reads[0].address - pe.image_base),
                selector_store_rva=hex(selector_stores[0].address - pe.image_base),
                mode4_skips_ratio=True, source_slot_offset='0xc58', object_offset='0xca0',
                decoder_version=cs.__version__)


def owned_checks(dll):
    native = Native(dll)
    config = native.alloc(0x35f0)
    native.fn(0x90de0, P, P)(config)
    for time in (0., 1.):
        native.add(config + 0x5d0, time, 0.)
    writer = native.fn(0xf7eb0, Q, P)
    count = wraps = zeros = tail_writes = 0
    for size in (1, 3, 4, 5, 17, 256):
        capacity = size + 11
        for position in (0, capacity - 1):
            for shape in ('zero', 'regular', 'clipped'):
                source = ([0.] * capacity if shape == 'zero' else
                          [f32((50000. if shape == 'clipped' else 7000.) * math.sin(i * .61)) for i in range(capacity)])
                window = [source[(position + i) % capacity] for i in range(size)]
                expected = [f32(max(-32767., min(32767., value))) for value in window]
                obj = native.alloc(0x520)
                frame = native.alloc(0x230)
                native.frame_ctor(frame)
                feature = native.alloc(0x228)
                P.from_address(frame + 0x128).value = feature
                descriptor = native.alloc(0x30)
                P.from_address(descriptor + 0x28).value = frame
                link = native.array(P, [descriptor])
                source_ptr = native.array(F, source + [12345.] * 8)
                # The actual constructor allocates 2*N+1 float slots. A
                # wrapped no-harmonic path writes the extra tail sample;
                # confidence still consumes only the N logical samples.
                residual = native.array(F, [7.] * size + [0.] * size + [34567.] + [23456.] * 8)
                B.from_address(obj + 0x3c0).value = 1
                P.from_address(obj + 0x3b0).value = config
                P.from_address(obj + 0x3d8).value = link
                P.from_address(obj + 0x3e0).value = source_ptr
                I.from_address(obj + 0x3ec).value = position
                I.from_address(obj + 0x3e8).value = capacity
                I.from_address(obj + 0x418).value = size * 2
                P.from_address(obj + 0x410).value = residual
                P.from_address(obj + 0x400).value = native.array(F, [0.] * (size + 1))
                before = c.string_at(source_ptr, (capacity + 8) * 4)
                assert writer(obj) == 0
                assert c.string_at(residual, size * 4) == bytes((F * size)(*([7.] * size)))
                assert c.string_at(residual + size * 4, size * 4) == bytes((F * size)(*expected))
                assert c.string_at(residual + (size * 2 + 1) * 4, 32) == bytes((F * 8)(*([23456.] * 8)))
                wrapped = position + size > capacity
                expected_tail = source[(position + size) % capacity] if wrapped else 34567.
                assert F.from_address(residual + size * 8).value == expected_tail
                assert c.string_at(source_ptr, (capacity + 8) * 4) == before
                actual = F.from_address(feature + 0x14).value
                assert struct.pack('<f', actual) == struct.pack('<f', residual_confidence(window, expected))
                count += 1
                wraps += wrapped
                tail_writes += wrapped
                zeros += shape == 'zero'
    rejected = 0
    for source, residual in (([], []), ([1.], [1., 2.]), ([math.nan], [0.]),
                             ([math.inf], [0.]), ([1e40], [0.]), ([3e38, 3e38], [0., 0.])):
        try:
            residual_confidence(source, residual)
        except ValueError:
            rejected += 1
        else:
            raise AssertionError('Out-of-contract confidence input admitted')
    return dict(cases=count, wrapped_cases=wraps, zero_source_cases=zeros, model_invalid_inputs_rejected=rejected,
                exact=count, source_and_guards_unchanged=True,
                extra_tail_slot_write_cases=tail_writes,
                scope='Owned no-harmonic source, ring wrapping, clipping, float32 sums and zero fallback; '
                      'constructor-sized 2*N+1 residual storage includes the explicit wrap tail sample')


def child(args, sequence=None, parser_hash=None, ddi_hash=None):
    frequency = (sequence.get('pitch1', statistics.median(row['f0'] for row in sequence['frames']))
                 if sequence else 220.)
    pcm = [f32(value) for value in sequence['pcm']] if sequence else signal(args.shape, frequency)
    batch = configured_batch(args.ratio_mode)
    finished = []
    captured = {}
    contracts = []
    calls = compared = source_values = residual_values = wraps = skipped = 0

    def setup(native, ctx):
        obj = P.from_address(ctx + 0xca0).value
        config = P.from_address(ctx + 0xad0).value
        assert B.from_address(config + 0x558).value == 5
        assert B.from_address(obj + 0x3c0).value == args.ratio_mode
        size = I.from_address(obj + 0x418).value // 2
        capacity = I.from_address(obj + 0x3e8).value
        hop = I.from_address(obj + 0x3f0).value
        assert 0 < size <= capacity < 200000 and hop == 256 and size % 4 == capacity % 4 == 0
        contracts.append(dict(outer_mode=5, internal_mode=args.ratio_mode, half_window=size,
                              ring_capacity=capacity, hop=hop))
        table = P.from_address(ctx).value
        original = c.CFUNCTYPE(Q, P)(P.from_address(table + 8).value)
        private = native.alloc(0x100)
        c.memmove(private, table, 0x100)

        def step(ctx):
            nonlocal calls, compared, source_values, residual_values, wraps, skipped
            try:
                calls += 1
                assert calls < 10000
                old_position = I.from_address(obj + 0x3ec).value
                assert 0 <= old_position < capacity and old_position % 4 == 0
                slot = P.from_address(ctx + 0xc58).value
                target = P.from_address(P.from_address(slot).value + 0x28).value
                time = D.from_address(target + 0x48).value
                ring = P.from_address(obj + 0x3e0).value
                samples = [F.from_address(ring + ((old_position + i) % capacity) * 4).value for i in range(size)]
                result = original(ctx)
                new_position = I.from_address(obj + 0x3ec).value
                if new_position == old_position or time < 0:
                    return result
                assert new_position == (old_position + hop) % capacity
                at = round(time * 44100 / 256)
                assert abs(time * 44100 / 256 - at) < 1e-5 and at not in captured
                feature = P.from_address(target + 0x128).value
                actual = F.from_address(feature + 0x14).value
                if args.ratio_mode in (1, 2, 3):
                    assert I.from_address(obj + 0x3c4).value == 0
                    residual = P.from_address(obj + 0x410).value + size * 4
                    differences = [F.from_address(residual + i * 4).value for i in range(size)]
                    predicted = residual_confidence(samples, differences)
                    assert struct.pack('<f', actual) == struct.pack('<f', predicted), (at, predicted, actual)
                    compared += 1
                    source_values += size
                    residual_values += size
                    wraps += old_position + size > capacity
                else:
                    skipped += 1
                captured[at] = actual
                return result
            except BaseException:
                traceback.print_exc()
                sys.stderr.flush()
                os._exit(2)

        callback = c.CFUNCTYPE(Q, P)(step)
        P.from_address(private + 8).value = c.cast(callback, P).value
        P.from_address(ctx).value = private
        native.keep.extend([original, callback])

    def finish(native, ctx, collection):
        finished.append(final_frames(native, ctx, collection))

    options = dict(analysis_mode=5, pcm_override=pcm, result_observer=finish)
    observed = batch(args.dll, 1, 1, args.shape, frequency, setup_observer=setup, **options)
    baseline = batch(args.dll, 1, 1, args.shape, frequency, **options)
    assert observed == baseline and len(finished) == 2 and finished[0] == finished[1]
    assert set(captured) == set(finished[0]) == set(range(observed['frames']))
    assert all(struct.pack('<f', captured[at]) == struct.pack('<f', finished[0][at]['values'][4]) for at in captured)
    comparisons = {}
    if sequence:
        for bit in (4, 14, 15, 17):
            comparisons[str(bit)] = metrics([finished[0][at]['values'][bit] - sequence['frames'][at]['features'][bit]
                                            for at in range(12, len(captured) - 12)])
    joint = (sum(all(abs(finished[0][at]['values'][bit] - sequence['frames'][at]['features'][bit]) <= 1e-6
                     for bit in (4, 14, 15)) for at in range(12, len(captured) - 12)) if sequence else None)
    return dict(case=sequence.get('category', args.sample) if sequence else args.shape,
                ratio_mode=args.ratio_mode, frames=len(captured), contract=contracts[0],
                controls=dict(steps=calls, ratio_byte_exact_frames=compared, source_values=source_values,
                              residual_values=residual_values, wrapped_frames=wraps, skipped_ratio_frames=skipped),
                confidence_range=[min(captured.values()), max(captured.values())],
                bit17_nonzero=sum(row['values'][17] != 0 for row in finished[0].values()),
                nonzero_array_frames=sum(row['source_array_nonzero'] != 0 for row in finished[0].values()),
                observed_matches_fresh_baseline=True, saved_comparisons=comparisons,
                joint_saved_score_matches=joint,
                parser_sha256=parser_hash, ddi_sha256=ddi_hash)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb', 'decoder-dir'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--child', action='store_true')
    parser.add_argument('--sample', type=int, choices=range(5))
    parser.add_argument('--shape', choices=('steady', 'gap', 'silence', 'noise'), default='steady')
    parser.add_argument('--ratio-mode', type=int, choices=range(5), default=1)
    args = parser.parse_args()
    if args.child:
        if args.sample is None:
            report = child(args)
        else:
            parser_hash, ddi_hash, sequences = load_sequences(args.manifest, args.voice_root, args.ddb_tools)
            sequences += auxiliary_sequences(args.aux_vdb)
            report = child(args, sequences[args.sample], parser_hash, ddi_hash)
        print(json.dumps(report))
        return
    assert args.output
    static = review(args.dll, args.decoder_dir)
    owned = owned_checks(args.dll)
    rows = []
    specifications = [dict(sample=sample, ratio_mode=mode) for sample in range(5) for mode in range(5)]
    specifications += [dict(shape=shape, ratio_mode=mode) for shape in ('steady', 'gap', 'silence', 'noise') for mode in (1, 2, 3)]
    for specification in specifications:
        command = [sys.executable, '-B', str(Path(__file__).resolve()), '--child']
        for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb', 'decoder-dir'):
            command += ['--' + name, str(getattr(args, name.replace('-', '_')))]
        for name, value in specification.items():
            command += ['--' + name.replace('_', '-'), str(value)]
        process = subprocess.run(command, capture_output=True, text=True, timeout=180)
        assert process.returncode == 0, process.stderr[-3000:]
        row = json.loads(process.stdout)
        rows.append(row)
        print(json.dumps(dict(case=row['case'], mode=row['ratio_mode'], frames=row['frames'], exact=row['controls']['ratio_byte_exact_frames'])), flush=True)
    sources = ('verify_vqm_residual_confidence_protocol.py', 'frm2_residual_confidence.py',
               'probe_frm2_batch_outputs.py', 'verify_vqm_mode5_pcm.py', 'verify_vqm_alternate_score_paths.py',
               'verify_vqm_bit16_spectral_power.py', 'verify_vqm_bit16_peak_interpolation.py',
               'audit_frm2_numeric.py', 'audit_vqm_v4_module_leads.py', 'audit_vqm_feature_calls_cfg.py')
    report = dict(schema=1, dse_sha256=SHA256, auxiliary_sha256=hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest(),
                  source_sha256={name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
                  reviewed_contract=static, owned_checks=owned, cases=rows,
                  summary=dict(cases=len(rows), frames=sum(row['frames'] for row in rows),
                    controls={name: sum(row['controls'][name] for row in rows) for name in rows[0]['controls']},
                    bit17_nonzero=sum(row['bit17_nonzero'] for row in rows),
                    nonzero_array_frames=sum(row['nonzero_array_frames'] for row in rows),
                    joint_saved_score_matches=sum(row['joint_saved_score_matches'] or 0 for row in rows),
                    saved_interior_frames=sum(row['saved_comparisons'].get('4', {}).get('frames', 0) for row in rows)),
                  scope='Constructor curve42 controls a separate residual analyzer. Modes1/2/3 '
                        'execute the absolute-sum confidence rule; modes0/4 are dispatch controls. '
                        'All ordinary batches use the same outer mode5; no output or cache injection.',
                  limits=['Residual synthesis remains native, not independently modeled here',
                          'Ratio is observed before queue rotation and compared again to final output',
                          'Constructor selector and actual numeric writes verified locally; no original maker identity',
                          'Curves42 modes are not the outer mode0..5 or estimator variant enum',
                          'Finite reviewed geometries and cache-state3c4=0 only',
                          'No commercial PCM, feature arrays, proprietary instructions or decompilation persisted',
                          'Ghidra metadata and bounded queries confirmed matching GUI DSE; large d2cc0 decompile timed out; '
                          'aligned native instruction review supplies the caller contract'],
                  installed_files_unchanged=True, no_windows_temp=True)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__ == '__main__':
    main()
