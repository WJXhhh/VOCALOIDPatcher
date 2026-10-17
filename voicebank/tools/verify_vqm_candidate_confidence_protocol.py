"""Review and observe the extra candidate analyzer's conditional bit4 writer.

Ordinary owned analysis contexts and unmodified dispatch supply all inputs.
Commercial samples stay in memory; persisted evidence contains aggregates only.
"""
import argparse
import ctypes as c
import hashlib
import importlib
import json
import os
from pathlib import Path
import statistics
import struct
import subprocess
import sys
import traceback

from audit_frm2_numeric import Native, P, I, F, D, Q, SHA256
from audit_vqm_v4_module_leads import PE
from audit_vqm_feature_calls_cfg import runtime_groups
from frm2_candidate_confidence import candidate_confidence
from probe_frm2_batch_outputs import run
from verify_vqm_mode5_pcm import signal
from verify_vqm_alternate_score_paths import frames
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences, metrics


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
        result = {}
        for start, end, _ in groups[root]:
            result.update({op.address - pe.image_base: op for op in
                decoder.disasm(data[pe.offset(start):pe.offset(start) + end - start], pe.image_base + start)})
        return result
    caller, writer = decode(0xd2cc0), decode(0xe6a70)
    for site in (0xd580b, 0xd5a80):
        assert caller[site].group(cs.CS_GRP_CALL)
        assert caller[site].operands[0].imm - pe.image_base == 0xe60a0
    stores = (0xe8a65, 0xe8a88, 0xe8abd, 0xe8afc)
    for site in stores:
        assert writer[site].mnemonic == 'movss'
        assert writer[site].operands[0].mem.disp == 0x14
    assert writer[0xe8a13].operands[0].imm - pe.image_base == 0x217dde
    assert writer[0xe8a32].operands[1].imm == 2
    assert writer[0xe8974].mnemonic == 'jle'  # The native loop includes a sentinel iteration.
    constants = {0x5bf900:('d',20.),0x5bf130:('d',1/32768.),0x5bfa50:('d',500.),
                 0x5bfbd0:('d',32768.),0x5bf998:('d',100.),0x5bfa90:('d',1000.),
                 0x5bf540:('d',.5),0x5bf2e8:('d',.06),0x5bf8a0:('f',2.5),
                 0x5bf9a8:('f',3.5),0x5bf378:('d',.12),0x5bf3a8:('d',.15)}
    for rva, (format, expected) in constants.items():
        assert struct.unpack_from('<'+format, data, pe.offset(rva))[0] == expected
    return dict(caller_rva='0xd2cc0', call_rvas=['0xd580b','0xd5a80'], dispatcher_rva='0xe60a0',
                writer_rva='0xe6a70', history_repair_rva='0xe8c90', object_offset='0xc88',
                descriptor_queue_offset='0xc30', confidence_store_rvas=list(map(hex, stores)),
                bin_stride=2, native_peak_loop_includes_sentinel=True, constants_checked=len(constants),
                decoder_version=cs.__version__)


def child(args, sequence=None, parser_hash=None, ddi_hash=None):
    frequency = sequence.get('pitch1', statistics.median(row['f0'] for row in sequence['frames'])) if sequence else args.frequency
    pcm = sequence['pcm'] if sequence else signal(args.shape, frequency)
    finished, contracts = [], []
    captured = {}
    counts = dict(steps=0, analyzer_calls=0, nondefault_confidence_calls=0, byte_exact_calls=0,
                  sampled_bins=0, psd_values=0, original_peaks=0, peak_index_nonzero_calls=0,
                  confidence_reject_calls=0, cost_above_2_5_without_confidence=0,
                  refreshed_frequency_interval_calls=0)
    errors = []
    def setup(native, ctx):
        obj = P.from_address(ctx + 0xc88).value
        config = P.from_address(obj + 0x598).value
        assert I.from_address(config + 0x5b0).value == 0
        assert I.from_address(obj + 0x78c).value == 0
        delay = I.from_address(obj + 0x5b8).value
        history = P.from_address(obj + 0x5c0).value
        count = I.from_address(obj + 0x5c8).value
        assert delay == 1 and count == 20
        lower, upper, ceiling = (F.from_address(obj + offset).value for offset in (0x74c, 0x754, 0x5a8))
        contracts.append(dict(delay=delay, history_entries=count, frequency_ceiling=ceiling,
                              peak_frequency_interval=[lower,upper], prepared_peak_flag=0, reanalysis_flag=0))
        table = P.from_address(ctx).value
        original = c.CFUNCTYPE(Q,P)(P.from_address(table + 8).value)
        private = native.alloc(0x100)
        c.memmove(private, table, 0x100)
        def step(ctx):
            try:
                counts['steps'] += 1
                assert counts['steps'] < 10000
                last = P.from_address(history + (count - 1)*8).value
                slots = P.from_address(ctx + 0xc30).value
                descriptor = P.from_address(slots + delay*8).value
                frame = P.from_address(descriptor + 0x28).value
                old_time = D.from_address(obj + 0x5b0).value
                returned = original(ctx)
                if D.from_address(obj + 0x5b0).value == old_time:
                    return returned
                # Dispatcher frees the first history entry, shifts, and creates
                # an uninitialized last entry. The saved pointer remains live.
                assert P.from_address(history + (count - 2)*8).value == last
                counts['analyzer_calls'] += 1
                time = D.from_address(frame + 0x48).value
                at = round(time*44100/256)
                assert time >= 0 and abs(time*44100/256 - at) < 1e-5 and at not in captured
                feature = P.from_address(frame + 0x128).value
                actual = F.from_address(feature + 0x14).value
                cost = F.from_address(last + 0x1c).value
                captured[at] = actual
                current_lower = F.from_address(obj + 0x74c).value
                current_upper = F.from_address(obj + 0x754).value
                counts['refreshed_frequency_interval_calls'] += (current_lower,current_upper) != (lower,upper)
                if actual == -1:
                    counts['cost_above_2_5_without_confidence'] += cost > 2.5
                    return returned
                counts['nondefault_confidence_calls'] += 1
                assert cost > 2.5
                pairs = [(F.from_address(last + i*8).value, F.from_address(last + i*8 + 4).value) for i in range(3)]
                best = min(reversed(pairs), key=lambda pair: pair[1])
                size, peaks_count = I.from_address(descriptor).value, I.from_address(descriptor + 0x18).value
                assert size == 2048 and 0 < peaks_count < 1500 and best[0] > 0
                psd = [F.from_address(P.from_address(descriptor + 8).value + i*4).value for i in range(size)]
                peaks = [(F.from_address(P.from_address(descriptor + 0x20).value + i*20).value,
                          F.from_address(P.from_address(descriptor + 0x20).value + i*20 + 4).value) for i in range(peaks_count)]
                # Caller refreshes the frequency interval from the current
                # pitch curve before invoking this analyzer (especially mode2).
                prediction = candidate_confidence(psd, peaks, best[0], current_lower, current_upper, ceiling,
                    F.from_address(feature + 4).value, F.from_address(feature + 0xc).value)
                counts['sampled_bins'] += prediction['sampled_bins']
                counts['psd_values'] += size
                counts['original_peaks'] += peaks_count
                counts['peak_index_nonzero_calls'] += prediction['peak_index'] != 0
                counts['confidence_reject_calls'] += prediction['rejects_selected_pitch']
                if prediction['rejects_selected_pitch']:
                    assert F.from_address(last + 0x18).value == 0
                error = abs(actual - prediction['confidence'])
                errors.append(error)
                exact = struct.pack('<f', actual) == struct.pack('<f', prediction['confidence'])
                counts['byte_exact_calls'] += exact
                assert exact, (args.mode, args.shape, at, error)
                return returned
            except BaseException:
                traceback.print_exc()
                os._exit(2)
        callback = c.CFUNCTYPE(Q,P)(step)
        native.keep.extend([original,callback])
        P.from_address(private + 8).value = c.cast(callback,P).value
        P.from_address(ctx).value = private
    def finish(native, ctx, collection):
        finished.append(frames(native,ctx,collection))
    options = dict(analysis_mode=args.mode, pcm_override=pcm, result_observer=finish)
    observed = run(args.dll,1,1,args.shape,frequency,setup_observer=setup,**options)
    baseline = run(args.dll,1,1,args.shape,frequency,**options)
    assert observed == baseline and finished[0] == finished[1]
    assert set(finished[0]) == set(range(observed['frames']))
    assert all(at in captured and struct.pack('<f',captured[at]) == struct.pack('<f',row['values'][4])
               for at,row in finished[0].items())
    comparisons = {}
    if sequence:
        for bit in (4,14,15,17):
            comparisons[str(bit)] = metrics([finished[0][at]['values'][bit] - sequence['frames'][at]['features'][bit]
                                            for at in range(12,len(finished[0])-12)])
    return dict(case=sequence.get('category',args.sample) if sequence else args.shape,
                mode=args.mode, frequency=frequency, frames=len(finished[0]), contract=contracts[0], controls=counts,
                max_confidence_error=max(errors,default=0), observed_matches_fresh_baseline=True,
                final_nondefault_confidence_frames=sum(row['values'][4] != -1 for row in finished[0].values()),
                final_confidence_range=[min(row['values'][4] for row in finished[0].values()),
                                        max(row['values'][4] for row in finished[0].values())],
                final_output_matches_observed=True, saved_comparisons=comparisons,
                joint_saved_score_matches=(sum(all(abs(finished[0][at]['values'][bit]-sequence['frames'][at]['features'][bit])<=1e-6
                    for bit in (4,14,15)) for at in range(12,len(finished[0])-12)) if sequence else None),
                bit17_nonzero=sum(row['values'][17] != 0 for row in finished[0].values()),
                nonzero_array_frames=sum(row['source_array_nonzero'] != 0 for row in finished[0].values()),
                parser_sha256=parser_hash, ddi_sha256=ddi_hash)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('dll','manifest','voice-root','ddb-tools','aux-vdb','decoder-dir'):
        parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--output',type=Path)
    parser.add_argument('--child',action='store_true')
    parser.add_argument('--sample',type=int,choices=range(5))
    parser.add_argument('--shape',default='steady')
    parser.add_argument('--frequency',type=float,default=220.)
    parser.add_argument('--mode',type=int,choices=(1,2),default=1)
    args = parser.parse_args()
    if args.child:
        if args.sample is None:
            report = child(args)
        else:
            parser_hash, ddi_hash, sequences = load_sequences(args.manifest,args.voice_root,args.ddb_tools)
            sequences += auxiliary_sequences(args.aux_vdb)
            report = child(args,sequences[args.sample],parser_hash,ddi_hash)
        print(json.dumps(report))
        return
    assert args.output
    static = review(args.dll,args.decoder_dir)
    specifications = [dict(sample=sample,mode=mode) for sample in range(5) for mode in (1,2)]
    shapes = [('steady',hz) for hz in (110,220,440,880)] + [('gap',hz) for hz in (110,220,440)]
    shapes += [(shape,220) for shape in ('attack','noise','silence','dc_tone','impulse')]
    specifications += [dict(shape=shape,frequency=hz,mode=mode) for shape,hz in shapes for mode in (1,2)]
    rows = []
    for specification in specifications:
        command = [sys.executable,'-B',str(Path(__file__).resolve()),'--child']
        for name in ('dll','manifest','voice-root','ddb-tools','aux-vdb','decoder-dir'):
            command += ['--'+name,str(getattr(args,name.replace('-','_')))]
        for name,value in specification.items():
            command += ['--'+name.replace('_','-'),str(value)]
        process = subprocess.run(command,capture_output=True,text=True,timeout=180)
        assert process.returncode == 0, process.stderr[-3000:]
        row = json.loads(process.stdout)
        rows.append(row)
        print(json.dumps(dict(case=row['case'],mode=row['mode'],frames=row['frames'],exact=row['controls']['byte_exact_calls'])),flush=True)
    sources = ('verify_vqm_candidate_confidence_protocol.py','frm2_candidate_confidence.py','probe_frm2_batch_outputs.py',
               'verify_vqm_mode5_pcm.py','verify_vqm_alternate_score_paths.py','verify_vqm_bit16_spectral_power.py',
               'verify_vqm_bit16_peak_interpolation.py','audit_frm2_numeric.py','audit_vqm_v4_module_leads.py','audit_vqm_feature_calls_cfg.py')
    report = dict(schema=1,dse_sha256=SHA256,auxiliary_sha256=hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest(),
        source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        reviewed_contract=static,cases=rows,summary=dict(cases=len(rows),frames=sum(row['frames'] for row in rows),
            controls={name:sum(row['controls'][name] for row in rows) for name in rows[0]['controls']},
            final_nondefault_confidence_frames=sum(row['final_nondefault_confidence_frames'] for row in rows),
            bit17_nonzero=sum(row['bit17_nonzero'] for row in rows),
            nonzero_array_frames=sum(row['nonzero_array_frames'] for row in rows),
            joint_saved_score_matches=sum(row['joint_saved_score_matches'] or 0 for row in rows),
            saved_interior_frames=sum(row['saved_comparisons'].get('4',{}).get('frames',0) for row in rows)),
        scope='Conditional arithmetic from genuine normal mode1/2 candidate analyzer inputs, not independent candidate or voicing production',
        limits=['Write presence is observed, not independently predicted; the preceding candidate/voicing/history decisions remain native',
                'Only the reviewed prepared-peak and reanalysis flags0, history20/delay1 and PSD2048 geometry',
                'Natural extra tail calls are counted separately from finalized output frames',
                'Native peak scan includes one sentinel iteration; model reads only logical peaks and verifies results for this corpus',
                'Standard Python log10 gives exact float32 confidence in this corpus; no universal CRT rounding equivalence claimed',
                'The post-confidence >0.06 pitch rejection has no positive native example in this corpus; confirmed by instruction review only',
                'No original maker identity or recovery of saved scores, zero arrays or bit17 producer',
                'No commercial payloads, proprietary instructions or decompilation persisted'],
        installed_files_unchanged=True,no_windows_temp=True)
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__ == '__main__':
    main()
