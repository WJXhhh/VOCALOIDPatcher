"""Predict candidate confidence write decisions without using observed bit4.

Normal constructors/dispatch provide candidates, filter output and prior
state. History cost and gates are predicted; only owned input curves change.
Commercial payloads remain in memory; reports contain aggregates only.
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
import types

from audit_frm2_numeric import Native, P, I, F, D, Q, SHA256, f32
from audit_vqm_v4_module_leads import PE
from frm2_candidate_gate import confidence_gate, exceptional_confidence
from frm2_candidate_confidence import candidate_confidence
from probe_frm2_batch_outputs import run
from verify_vqm_candidate_confidence_protocol import review
from verify_vqm_mode5_pcm import signal
from verify_vqm_alternate_score_paths import OFFSETS
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences, metrics


CURVE_IDS = (0xf,0x14,0x16,0x17,0x18,0x19,0x1c)
PROFILES = {'default':{}, 'manual_pitch':{0x14:0.}, 'tight_cost':{0xf:2.},
            'tiny_feature4':{0x17:1e-6}, 'high_energy_floor':{0x19:.01},
            'low_maximum_peak':{0x16:100.}, 'skip_crossing_limit':{0x1c:0.},
            'skip_feature_gates':{0x17:0.},
            'late_feature_pair':{0x17:1.,0x16:22050.,0x1c:0.}}


def owned_signal(shape,frequency):
    if shape not in ('pitch_up','pitch_down','pitch_octave'):
        return signal(shape,frequency)
    first,second = {'pitch_up':(220.,330.),'pitch_down':(330.,220.),'pitch_octave':(220.,440.)}[shape]
    count = 22050
    switch = count//2
    return [f32(5000.*sum(math.sin(math.tau*k*((first*min(i,switch)+second*max(0,i-switch))/44100.)+k*.23)/k
                          for k in range(1,6))) for i in range(count)]


def frames_with_exceptional_confidence(native,context,collection):
    track = P.from_address(context + 0xcd0).value
    regions = P.from_address(track + 0x148).value
    result = {}
    for i in range(I.from_address(track + 0x140).value):
        region = P.from_address(regions + i*8).value
        pointers = P.from_address(region + 0x20).value
        for j in range(I.from_address(region + 0x18).value):
            frame = P.from_address(pointers + j*8).value
            feature = P.from_address(frame + 0x128).value
            assert feature and Q.from_address(frame + 0x50).value & (1 << 13)
            time = D.from_address(frame + 0x48).value
            at = round(time*44100/256)
            assert abs(time*44100/256-at) < 1e-5 and at not in result
            values = {bit:F.from_address(feature + offset).value for bit,offset in OFFSETS.items()}
            assert all(math.isfinite(value) for bit,value in values.items() if bit != 4)
            result[at] = dict(values=values,feature_flags=I.from_address(feature).value,
                source_arrays_sha256=hashlib.sha256(c.string_at(feature + 0x2c,0x1e0)).hexdigest(),
                source_array_nonzero=sum(F.from_address(feature + offset).value != 0 for offset in range(0x2c,0x20c,4)))
    return result


def frame_identity(rows):
    # Compare IEEE representations; Python NaN equality cannot validate two
    # independently analyzed outputs containing genuine nonfinite bit4.
    return {at:(tuple((bit,struct.pack('<f',value)) for bit,value in row['values'].items()),
                row['feature_flags'],row['source_arrays_sha256'],row['source_array_nonzero']) for at,row in rows.items()}


def configured_batch(profile):
    overrides = PROFILES[profile]
    class ConfiguredNative(Native):
        def fn(self,rva,result,*arguments):
            original = super().fn(rva,result,*arguments)
            if rva != 0xcdde0 or not overrides:
                return original
            def construct(context,rate,config):
                # Apply after the batch's standard input curves, before any
                # analysis object is constructed. Curve14 is otherwise reset
                # by the batch harness's ordinary automatic-pitch argument.
                for key,value in overrides.items():
                    for time in (0.,1.):
                        self.add(config + 0x5d0 + key*0xa0,time,value)
                return original(context,rate,config)
            return construct
    batch = types.FunctionType(run.__code__,dict(run.__globals__,Native=ConfiguredNative),run.__name__,run.__defaults__)
    batch.__kwdefaults__ = run.__kwdefaults__
    return batch


def child(args,sequence=None,parser_hash=None,ddi_hash=None):
    frequency = sequence.get('pitch1',statistics.median(row['f0'] for row in sequence['frames'])) if sequence else args.frequency
    pcm = sequence['pcm'] if sequence else owned_signal(args.shape,frequency)
    batch = configured_batch(args.profile)
    finished,contracts = [],[]
    captured = {}
    reasons = Counter()
    history_reasons = Counter()
    counts = dict(steps=0,dispatcher_calls=0,predicted_write_calls=0,predicted_no_write_calls=0,
                  exact_gate_calls=0,byte_exact_confidence_calls=0,spectral_mask_calls=0,
                  manual_dispatch_bypass_frames=0,manual_leaf_skipped_dispatch_calls=0,
                  zero_energy_write_calls=0,nonfinite_confidence_calls=0,nan_confidence_calls=0,infinite_confidence_calls=0,
                  predicted_history_cost_calls=0,exact_history_cost_calls=0,history_replacement_calls=0)
    def setup(native,ctx):
        obj = P.from_address(ctx + 0xc88).value
        config = P.from_address(obj + 0x598).value
        assert I.from_address(config + 0x5b0).value == I.from_address(obj + 0x78c).value == 0
        delay = I.from_address(obj + 0x5b8).value
        history = P.from_address(obj + 0x5c0).value
        count = I.from_address(obj + 0x5c8).value
        assert delay == 1 and count == 20
        curves = {key:native.evaluate(config + 0x5d0 + key*0xa0,0.) for key in CURVE_IDS}
        assert all(native.evaluate(config + 0x5d0 + key*0xa0,1.) == value for key,value in curves.items())
        assert all(curves[key] == f32(value) for key,value in PROFILES[args.profile].items())
        contracts.append(dict(delay=delay,history_entries=count,curves={hex(key):value for key,value in curves.items()},
                              prepared_peak_flag=0,reanalysis_flag=0))
        table = P.from_address(ctx).value
        original = c.CFUNCTYPE(Q,P)(P.from_address(table + 8).value)
        private = native.alloc(0x100)
        c.memmove(private,table,0x100)
        def step(ctx):
            try:
                counts['steps'] += 1
                assert counts['steps'] < 10000
                last = P.from_address(history + (count-1)*8).value
                old_count = I.from_address(obj + 0x710).value
                old_variation = F.from_address(obj + 0x718).value
                old_prior = F.from_address(obj + 0x714).value
                old_previous = [(F.from_address(P.from_address(history + (count-2-i)*8).value + 0x18).value,
                                 F.from_address(P.from_address(history + (count-2-i)*8).value + 0x1c).value) for i in range(2)]
                slots = P.from_address(ctx + 0xc30).value
                descriptor = P.from_address(slots + delay*8).value
                target = P.from_address(descriptor + 0x28).value
                previous = P.from_address(P.from_address(slots).value + 0x28).value
                old_time = D.from_address(obj + 0x5b0).value
                returned = original(ctx)
                if D.from_address(obj + 0x5b0).value == old_time:
                    return returned
                assert P.from_address(history + (count-2)*8).value == last
                counts['dispatcher_calls'] += 1
                counts['manual_leaf_skipped_dispatch_calls'] += curves[0x14] == 0
                feature = P.from_address(target + 0x128).value
                time = D.from_address(target + 0x48).value
                at = round(time*44100/256)
                assert time >= 0 and abs(time*44100/256-at) < 1e-5 and at not in captured
                values = [F.from_address(feature + offset).value for offset in (4,8,0xc,0x1c)]
                previous_feature = P.from_address(previous + 0x128).value
                previous_energy = F.from_address(previous_feature + 0xc).value if previous_feature else None
                original_count = I.from_address(descriptor + 0x18).value
                assert 0 <= original_count < 1500
                peaks = [(F.from_address(P.from_address(descriptor + 0x20).value + i*20).value,
                          F.from_address(P.from_address(descriptor + 0x20).value + i*20 + 4).value) for i in range(original_count)]
                if curves[0x14] != 0:
                    filtered_count = I.from_address(obj + 0x730).value
                    assert 0 <= filtered_count < 1500
                    filtered = [(F.from_address(P.from_address(obj + 0x720).value + i*8).value,
                                 F.from_address(P.from_address(obj + 0x720).value + i*8 + 4).value) for i in range(filtered_count)]
                    pairs = [(F.from_address(last + i*8).value,F.from_address(last + i*8 + 4).value) for i in range(3)]
                    best_hz,best_cost = min(reversed(pairs),key=lambda pair:pair[1])
                else:
                    filtered = []
                    pairs = [(0.,0.)]*3
                    best_hz = best_cost = 0.
                prior_pitch = (old_prior if old_prior > 0 else old_previous[0][0] if old_previous[0][0] > 0
                               else F.from_address(obj + 0x750).value)
                state = dict(pairs=pairs,prior_pitch=prior_pitch,voiced_count=old_count,variation=old_variation,
                    reference_cache=(F.from_address(obj + 0x5d0).value,F.from_address(obj + 0x5d4).value),previous=old_previous)
                prediction = confidence_gate(peaks,filtered,best_hz,best_cost,*values,previous_energy,curves,state)
                correction = prediction['history_correction']
                if correction is not None:
                    counts['predicted_history_cost_calls'] += 1
                    observed_cost = F.from_address(last + 0x1c).value
                    assert struct.pack('<f',correction['cost']) == struct.pack('<f',observed_cost), (args.mode,args.shape,at,'history cost')
                    counts['exact_history_cost_calls'] += 1
                    counts['history_replacement_calls'] += correction['changed']
                    history_reasons[correction['reason']] += 1
                # Predict the decision and value before loading observed bit4.
                expected = -1.
                if prediction['writes']:
                    size = I.from_address(descriptor).value
                    assert size == 2048
                    psd = [F.from_address(P.from_address(descriptor + 8).value + i*4).value for i in range(size)]
                    geometry = (F.from_address(obj + 0x74c).value,F.from_address(obj + 0x754).value,
                                F.from_address(obj + 0x5a8).value)
                    if values[2] == 0:
                        assert struct.pack('<f',values[2]) == bytes(4)
                        counts['zero_energy_write_calls'] += 1
                        expected = exceptional_confidence(psd,peaks,best_hz,*geometry,values[0])
                    else:
                        expected = candidate_confidence(psd,peaks,best_hz,*geometry,values[0],values[2])['confidence']
                    counts['nonfinite_confidence_calls'] += not math.isfinite(expected)
                    counts['nan_confidence_calls'] += math.isnan(expected)
                    counts['infinite_confidence_calls'] += math.isinf(expected)
                actual = F.from_address(feature + 0x14).value
                assert prediction['writes'] == (actual != -1), (args.profile,args.mode,args.sample,args.shape,at,prediction['reason'])
                assert struct.pack('<f',expected) == struct.pack('<f',actual), (args.profile,args.mode,at,'value mismatch')
                reasons[prediction['reason']] += 1
                counts['spectral_mask_calls'] += prediction['spectral_mask'] != 0
                counts['exact_gate_calls'] += 1
                counts['predicted_write_calls'] += prediction['writes']
                counts['predicted_no_write_calls'] += not prediction['writes']
                counts['byte_exact_confidence_calls'] += prediction['writes']
                captured[at] = expected
                return returned
            except BaseException:
                traceback.print_exc()
                os._exit(2)
        callback = c.CFUNCTYPE(Q,P)(step)
        native.keep.extend([original,callback])
        P.from_address(private + 8).value = c.cast(callback,P).value
        P.from_address(ctx).value = private
    def finish(native,ctx,collection):
        finished.append(frames_with_exceptional_confidence(native,ctx,collection))
    options = dict(analysis_mode=args.mode,pcm_override=pcm,result_observer=finish)
    observed = batch(args.dll,1,1,args.shape,frequency,setup_observer=setup,**options)
    baseline = batch(args.dll,1,1,args.shape,frequency,**options)
    assert observed == baseline and frame_identity(finished[0]) == frame_identity(finished[1])
    assert set(finished[0]) == set(range(observed['frames']))
    if args.profile == 'manual_pitch':
        # Manual mode1 bypasses the dispatcher. Mode2 enters the dispatcher
        # but skips its candidate leaf. Neither writes this confidence.
        assert counts['predicted_write_calls'] == 0
        final_prediction = {at:-1. for at in finished[0]}
        if counts['dispatcher_calls'] == 0:
            assert not captured
            counts['manual_dispatch_bypass_frames'] = len(finished[0])
    else:
        final_prediction = captured
    assert all(struct.pack('<f',final_prediction[at]) == struct.pack('<f',row['values'][4]) for at,row in finished[0].items())
    comparisons = {}
    if sequence:
        for bit in (4,14,15,17):
            comparisons[str(bit)] = metrics([finished[0][at]['values'][bit] - sequence['frames'][at]['features'][bit]
                                            for at in range(12,len(finished[0])-12)])
    return dict(case=sequence.get('category',args.sample) if sequence else args.shape,
        mode=args.mode,profile=args.profile,frequency=frequency,frames=len(finished[0]),contract=contracts[0],
        controls=counts,gate_reasons=dict(sorted(reasons.items())),history_reasons=dict(sorted(history_reasons.items())),
        observed_matches_fresh_baseline=True,final_output_matches_prediction=True,saved_comparisons=comparisons,
        final_write_frames=sum(row['values'][4] != -1 for row in finished[0].values()),
        joint_saved_score_matches=(sum(all(abs(finished[0][at]['values'][bit]-sequence['frames'][at]['features'][bit])<=1e-6
            for bit in (4,14,15)) for at in range(12,len(finished[0])-12)) if sequence else None),
        bit17_nonzero=sum(row['values'][17] != 0 for row in finished[0].values()),
        final_nonfinite_confidence_frames=sum(not math.isfinite(row['values'][4]) for row in finished[0].values()),
        nonzero_array_frames=sum(row['source_array_nonzero'] != 0 for row in finished[0].values()),
        parser_sha256=parser_hash,ddi_sha256=ddi_hash)


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
    parser.add_argument('--profile',choices=PROFILES,default='default')
    args = parser.parse_args()
    if args.child:
        if args.sample is None:
            report = child(args)
        else:
            parser_hash,ddi_hash,sequences = load_sequences(args.manifest,args.voice_root,args.ddb_tools)
            sequences += auxiliary_sequences(args.aux_vdb)
            report = child(args,sequences[args.sample],parser_hash,ddi_hash)
        print(json.dumps(report))
        return
    assert args.output
    static = review(args.dll,args.decoder_dir)
    data = args.dll.read_bytes()
    pe = PE(data)
    constants = {0x5bfca0:('f',45.),0x5bfd04:('f',450.),0x5bfd3c:('f',1000.),
                 0x5bfc90:('f',30.),0x5bffb0:('f',-1000.),0x5bfdac:('f',6500.),
                 0x5bfde0:('f',12500.),0x5bfad8:('f',5.),0x5bf0f4:('f',f32(.37)),
                 0x5bf128:('f',.5),0x5bffa4:('f',-200.),0x5bf5e8:('d',.9),
                 0x5bf840:('d',5.),0x5bf778:('d',2.5)}
    constants.update({0x5bf16c:('f',f32(.8)),0x5bf2a8:('f',1.25),0x5bf740:('f',2.),
        0x5bfc1c:('f',10.),0x5bfa30:('f',f32(3.84)),0x5bfa70:('f',f32(4.12)),
        0x5bf110:('f',f32(.45)),0x5bf138:('f',f32(.55)),0x5bf5c8:('d',.8),
        0x5bf748:('d',2.),0x5bf718:('d',1.9),0x5bf758:('d',2.12),
        0x5bf790:('d',2.9),0x5bf7a0:('d',3.12),0x5bf6d8:('d',1.43),0x5bf6e8:('d',1.57),
        0x5bf06c:('f',f32(.125))})
    for rva,(format,expected) in constants.items():
        assert struct.unpack_from('<'+format,data,pe.offset(rva))[0] == expected
    static['additional_gate_constants_checked'] = len(constants)
    static['reviewed_gate_curve_ids'] = list(map(hex,CURVE_IDS))
    specs = [dict(sample=sample,mode=mode) for sample in range(5) for mode in (1,2)]
    shapes = [('steady',hz) for hz in (110,220,440,880)] + [('gap',hz) for hz in (110,220,440)]
    shapes += [(shape,220) for shape in ('attack','noise','silence','dc_tone','impulse','pitch_up','pitch_down','pitch_octave')]
    specs += [dict(shape=shape,frequency=hz,mode=mode) for shape,hz in shapes for mode in (1,2)]
    specs += [dict(profile=profile,shape=shape,mode=mode) for profile in PROFILES if profile != 'default'
              for shape in ('gap','noise') for mode in (1,2)]
    rows = []
    for spec in specs:
        command = [sys.executable,'-B',str(Path(__file__).resolve()),'--child']
        for name in ('dll','manifest','voice-root','ddb-tools','aux-vdb','decoder-dir'):
            command += ['--'+name,str(getattr(args,name.replace('-','_')))]
        for name,value in spec.items():
            command += ['--'+name.replace('_','-'),str(value)]
        process = subprocess.run(command,capture_output=True,text=True,timeout=180)
        assert process.returncode == 0,process.stderr[-3000:]
        row = json.loads(process.stdout)
        rows.append(row)
        print(json.dumps(dict(case=row['case'],mode=row['mode'],profile=row['profile'],frames=row['frames'],
                              predicted=row['controls']['predicted_write_calls'])),flush=True)
    sources = ('verify_vqm_candidate_gate.py','frm2_candidate_gate.py','frm2_candidate_history_cost.py','frm2_candidate_confidence.py',
               'verify_vqm_candidate_confidence_protocol.py','probe_frm2_batch_outputs.py','verify_vqm_mode5_pcm.py',
               'verify_vqm_alternate_score_paths.py','verify_vqm_bit16_spectral_power.py',
               'verify_vqm_bit16_peak_interpolation.py','audit_frm2_numeric.py',
               'audit_vqm_v4_module_leads.py','audit_vqm_feature_calls_cfg.py')
    reason_counts = Counter()
    history_counts = Counter()
    for row in rows:
        reason_counts.update(row['gate_reasons'])
        history_counts.update(row['history_reasons'])
    report = dict(schema=1,dse_sha256=SHA256,auxiliary_sha256=hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest(),
        source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        reviewed_contract=static,cases=rows,summary=dict(cases=len(rows),frames=sum(row['frames'] for row in rows),
            controls={name:sum(row['controls'][name] for row in rows) for name in rows[0]['controls']},
            gate_reasons=dict(sorted(reason_counts.items())),history_reasons=dict(sorted(history_counts.items())),final_write_frames=sum(row['final_write_frames'] for row in rows),
            bit17_nonzero=sum(row['bit17_nonzero'] for row in rows),nonzero_array_frames=sum(row['nonzero_array_frames'] for row in rows),
            final_nonfinite_confidence_frames=sum(row['final_nonfinite_confidence_frames'] for row in rows),
            joint_saved_score_matches=sum(row['joint_saved_score_matches'] or 0 for row in rows),
            saved_interior_frames=sum(row['saved_comparisons'].get('4',{}).get('frames',0) for row in rows)),
        scope='Independent late history-cost correction, gate and confidence from native candidates/filter/cache, pre-step history, features and constructor curves',
        limits=['Candidate generation, filtering, the prior-reference candidate cache and preceding feature production remain native inputs',
                'The model predicts before reading observed bit4; it does not claim independent PCM-to-decision production',
                'Prepared-peak and reanalysis flags0, history20/delay1 and PSD2048 only',
                'Constant reviewed constructor curves; zero-energy exception covered only with its observed +0 energy and positive final scale',
                'Native sentinel peak iteration not modeled beyond the logical list; same limits as445',
                'Original maker scores, bit17 and zero-array production remain unresolved',
                'No commercial payloads, proprietary code or decompilation persisted'],
        installed_files_unchanged=True,no_windows_temp=True)
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__ == '__main__':
    main()
