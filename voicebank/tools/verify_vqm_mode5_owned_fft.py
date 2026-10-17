"""Precompute mode5 scores before loading DSE, then compare normal batches.

The producer owns its FFT descriptor and resolves math by exported names.
Installed PCM and numerical arrays stay in memory; only aggregates are saved.
"""
import argparse
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

from audit_frm2_numeric import P, Q, I, F, D, B, SHA256, f32
from frm2_mode5_math_backend import PublicRealFft, SystemFloatLog, FFT_SHA256, module_loaded
from frm2_mode5_pcm import Mode5PcmModel
from probe_frm2_batch_outputs import run as analyze_batch
from verify_vqm_mode5_pcm import signal
from verify_vqm_mode5_scores import geometry
from verify_vqm_alternate_score_paths import frames as final_frames
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences, metrics


def child(args, sequence=None, parser_hash=None, ddi_hash=None):
    frequency = (sequence.get('pitch1', statistics.median(row['f0'] for row in sequence['frames']))
                 if sequence else args.frequency)
    pcm = [f32(value) for value in sequence['pcm']] if sequence else signal(args.shape, frequency)
    count = math.ceil(len(pcm) / 256)
    if sequence:
        assert count == len(sequence['frames'])
    assert not module_loaded('DSE.dll')
    log = SystemFloatLog()
    predictions = []
    indices = tuple(range(4, 1345))
    with PublicRealFft(args.dll.parent / 'DSE_DFT.dll') as transform:
        model = Mode5PcmModel(fft_backend=transform, log_backend=log)
        for at in range(count):
            power = model.power_at(pcm, at)
            result = model.from_power(power, indices)
            # All results exist before the reference analyzer is loaded.
            # Discard unused preparation arrays; retain only compared values.
            predictions.append(dict(power=bytes((F * 2048)(*power)),
                                    scores=bytes((F * len(indices))(*(result['scores'][i] for i in indices))),
                                    confidence=result['confidence']))
        production = dict(dse_absent_before_and_after=True, fft_calls=transform.calls,
                          ccs_guards_checked=transform.guards_checked,
                          fft_configuration=transform.configuration,
                          system_log=log.metadata())
        assert transform.calls == transform.guards_checked == count
        assert not module_loaded('DSE.dll')
    assert not transform.handle and not module_loaded('DSE.dll')
    production['owned_fft_descriptor_freed_before_oracle'] = True
    del model, result, power

    finished = []
    captured = {}
    controls = dict(power_values_byte_exact=0, cache_scores_byte_exact=0,
                    confidence_byte_exact=0, leading_windows=0, trailing_windows=0)

    def setup(native, ctx):
        assert module_loaded('DSE.dll')
        obj = P.from_address(ctx + 0xc70).value
        table = P.from_address(ctx).value
        original = c.CFUNCTYPE(Q, P)(P.from_address(table + 8).value)
        private = native.alloc(0x100)
        c.memmove(private, table, 0x100)

        def step(ctx):
            try:
                queue = P.from_address(ctx + 0xc20).value
                queue_count = I.from_address(ctx + 0xc18).value
                slot = P.from_address(ctx + 0xc48).value
                assert queue <= slot < queue + queue_count * 8
                descriptor = P.from_address(slot).value
                source_frame = P.from_address(descriptor + 0x28).value
                source_time = D.from_address(source_frame + 0x48).value
                result = original(ctx)
                output = P.from_address(ctx + 0xcc8).value
                if result & 0xffffffff or not output or D.from_address(output + 0x48).value < 0:
                    return result
                time = D.from_address(output + 0x48).value
                at = round(time * 44100 / 256)
                assert abs(time * 44100 / 256 - at) < 1e-5
                assert source_time == time and 0 <= at < count and at not in captured
                retained = slot - 8 if slot > queue else queue + (queue_count - 1) * 8
                assert P.from_address(retained).value == descriptor
                predicted = predictions[at]
                assert c.string_at(P.from_address(descriptor + 8).value, 2048 * 4) == predicted['power']
                assert geometry(obj) == (2048, 22050., 6000., 22050.)
                flags = P.from_address(obj + 0x3e8).value
                cache = P.from_address(obj + 0x3e0).value
                ready = tuple(index for index in range(3, 4458) if B.from_address(flags + index).value)
                assert ready == indices
                assert c.string_at(cache + indices[0] * 4, len(indices) * 4) == predicted['scores']
                feature = P.from_address(output + 0x128).value
                assert c.string_at(feature + 0x14, 4) == struct.pack('<f', predicted['confidence'])
                captured[at] = predicted['confidence']
                controls['power_values_byte_exact'] += 2048
                controls['cache_scores_byte_exact'] += len(indices)
                controls['confidence_byte_exact'] += 1
                controls['leading_windows'] += at * 256 - 512 < 0
                controls['trailing_windows'] += at * 256 + 512 >= len(pcm)
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
    observed = analyze_batch(args.dll, 1, 1, args.shape, frequency, setup_observer=setup, **options)
    baseline = analyze_batch(args.dll, 1, 1, args.shape, frequency, **options)
    assert observed == baseline and len(finished) == 2 and finished[0] == finished[1]
    assert set(captured) == set(finished[0]) == set(range(count))
    assert all(struct.pack('<f', captured[at]) == struct.pack('<f', finished[0][at]['values'][4]) for at in captured)
    saved = ([captured[at] - sequence['frames'][at]['features'][4]
              for at in range(12, count - 12)] if sequence else None)
    return dict(case=sequence.get('category', args.sample) if sequence else f'{args.shape}_{args.frequency:g}',
                frames=count, production=production, controls=controls,
                observed_matches_fresh_baseline=True, final_confidence_byte_exact=count,
                saved_confidence_comparison=metrics(saved) if saved is not None else None,
                parser_sha256=parser_hash, ddi_sha256=ddi_hash)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--child', action='store_true')
    parser.add_argument('--sample', type=int, choices=range(5))
    parser.add_argument('--shape', choices=('steady', 'attack', 'gap', 'silence', 'dc', 'dc_tone', 'impulse', 'noise'), default='steady')
    parser.add_argument('--frequency', type=float, default=220.)
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
    specifications = [dict(shape=shape, frequency=frequency) for shape in ('steady', 'attack')
                      for frequency in (110., 220., 440.)]
    specifications += [dict(shape=shape, frequency=220.) for shape in ('gap', 'silence', 'dc', 'dc_tone', 'impulse', 'noise')]
    specifications += [dict(sample=index) for index in range(5)]
    rows = []
    for specification in specifications:
        command = [sys.executable, '-B', str(Path(__file__).resolve()), '--child']
        for name in ('dll', 'manifest', 'voice-root', 'ddb-tools', 'aux-vdb'):
            command += ['--' + name, str(getattr(args, name.replace('-', '_')))]
        for name, value in specification.items():
            command += ['--' + name, str(value)]
        process = subprocess.run(command, capture_output=True, text=True, timeout=180)
        assert process.returncode == 0, process.stderr[-3000:]
        row = json.loads(process.stdout)
        rows.append(row)
        print(json.dumps(dict(case=row['case'], frames=row['frames'], byte_exact=True)), flush=True)
    sources = ('verify_vqm_mode5_owned_fft.py', 'frm2_mode5_math_backend.py', 'frm2_mode5_pcm.py',
               'verify_vqm_mode5_pcm.py', 'frm2_mode5_scores.py', 'frm2_mode5_confidence.py',
               'verify_frm2_analysis_windows.py', 'verify_vqm_mode5_scores.py',
               'probe_frm2_batch_outputs.py', 'verify_vqm_alternate_score_paths.py',
               'verify_vqm_bit16_spectral_power.py', 'verify_vqm_bit16_peak_interpolation.py',
               'audit_frm2_numeric.py', 'audit_vqm_v4_module_leads.py', 'audit_vqm_feature_calls_cfg.py')
    report = dict(schema=1, dse_sha256=SHA256, fft_runtime_sha256=FFT_SHA256,
                  auxiliary_sha256=hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest(),
                  source_sha256={name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
                  cases=rows, summary=dict(cases=len(rows), frames=sum(row['frames'] for row in rows),
                    controls={name: sum(row['controls'][name] for row in rows) for name in rows[0]['controls']},
                    producer_dse_absent_cases=sum(row['production']['dse_absent_before_and_after'] for row in rows),
                    owned_descriptors_freed_before_oracle=sum(row['production']['owned_fft_descriptor_freed_before_oracle'] for row in rows)),
                  scope='PCM to current default mode5 power, all ready scores and confidence. All predictions '
                        'are complete before DSE.dll is loaded; FFT uses an owned descriptor in the installed '
                        'hash-pinned mathematical library and logarithm uses the system CRT export.',
                  limits=['Same FFT numerical implementation, not an independently implemented FFT algorithm',
                          'Original maker confidence still differs; original input and authoring contract unverified',
                          'Only default 44.1 kHz, hop256, window1025/kind11/lookup0/factor4 geometry modeled',
                          'Only ready indices [4,1345) compared; logical-outside preparation tail contract unresolved',
                          'FFT results compared for output frames, not the extra analyzer tail processing steps',
                          'Fresh baseline covers returned metadata, six scalars and four feature arrays, not all payloads',
                          'No commercial PCM, spectra, score arrays or proprietary code persisted'],
                  installed_files_unchanged=True, no_windows_temp=True)
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__ == '__main__':
    main()
