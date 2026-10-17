"""Independent peak score and primary reference-cache production verification.

Native score calls use owned fixtures only. Normal batches provide read-only
filtered peaks and pre-step history; all predicted costs are computed in Python.
"""
import argparse
from collections import Counter
import ctypes as c
import hashlib
import importlib
import json
import os
from pathlib import Path
import random
import statistics
import struct
import subprocess
import sys
import traceback

from audit_frm2_numeric import Native,P,I,F,D,Q,SHA256,f32
from audit_vqm_v4_module_leads import PE
from audit_vqm_feature_calls_cfg import runtime_groups
from frm2_peak_candidate_score import candidate_score,peak_ranks
from frm2_candidate_reference_cache import reference_cache,candidate_eligible
from frm2_candidate_gate import confidence_gate,exceptional_confidence
from frm2_candidate_confidence import candidate_confidence
from verify_vqm_candidate_gate import configured_batch,owned_signal,frames_with_exceptional_confidence,frame_identity,PROFILES
from verify_vqm_candidate_confidence_protocol import review
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences


def review_reference(dll,decoder_dir):
    result = review(dll,decoder_dir)
    data = dll.read_bytes()
    pe = PE(data)
    groups,_ = runtime_groups(data,pe)
    cs = importlib.import_module('capstone')
    decoder = cs.Cs(cs.CS_ARCH_X86,cs.CS_MODE_64)
    decoder.detail = True
    def decode(root):
        ops = {}
        for start,end,_ in groups[root]:
            ops.update({op.address-pe.image_base:op for op in decoder.disasm(
                data[pe.offset(start):pe.offset(start)+end-start],pe.image_base+start)})
        return ops
    caller,score,eligibility = (decode(root) for root in (0xe6a70,0xe9ca0,0xe9690))
    score_calls = (0xe755e,0xe75f4,0xe76e4,0xe7b1d,0xe7bf0,0xe7cf0)
    seed_calls = (0xe710d,0xe7180,0xe7201,0xe726c,0xe7324,0xe739a)
    for target,sites in ((0xe9ca0,score_calls),(0xe9690,seed_calls)):
        for site in sites:
            assert caller[site].group(cs.CS_GRP_CALL)
            assert caller[site].operands[0].imm-pe.image_base == target
    for site in (0xea1e9,0xea1ed,0xea1f1):
        assert score[site].mnemonic == 'mulss'
    for site in (0xea537,0xea53b):
        assert score[site].mnemonic == 'addss'
    assert score[0xea882].mnemonic == 'movss'
    assert eligibility[0xe9856].mnemonic == 'comiss'
    assert eligibility[0xe9859].mnemonic == 'ja'
    assert eligibility[0xe9859].operands[0].imm-pe.image_base == 0xe98e2
    floats = {0x5bfa58:4.,0x5bf210:1.,0x5bf128:.5,0x5bf9a8:3.5,0x5bf0f0:.35,
        0x5bffbc:-10000.,0x5bfc4c:15.,0x5bfc28:12.,0x5bf03c:.05,0x5bf298:1.2,
        0x5bf7d0:2.2,0x5bf0fc:.4,0x5bf154:.7,0x5bfc5c:20.,0x5bf140:.6,
        0x5bfd24:600.,0x5bfc84:25.,0x5bfe38:1e7,0x5bfc9c:40.,0x5bfd2c:800.,
        0x5bfca8:55.,0x5bff80:-45.,0x5bffb0:-1000.,0x5bfe3c:1e8,
        0x5bf1c0:.95,0x5bf23c:1.06,0x5befc0:0.}
    doubles = {0x5bf090:1e-8,0x5bf650:1.,0x5bf8c0:10.,0x5bf700:1.8,
        0x5bf7c0:3.3,0x5bf348:.1,0x5bfee0:-10.,0x5bf938:40.,0x5bf8e8:15.,
        0x5bf3e8:.2,0x5bf230:.01,0x5bf678:1.06,0x5bf6e0:1.5,0x5bf880:6.}
    for rva,value in floats.items():
        assert struct.unpack_from('<f',data,pe.offset(rva))[0] == f32(value)
    for rva,value in doubles.items():
        assert struct.unpack_from('<d',data,pe.offset(rva))[0] == value
    result['reference_production'] = dict(score_rva='0xe9ca0',rank_rva='0xea8b0',
        eligibility_rva='0xe9690',score_call_rvas=list(map(hex,score_calls)),
        eligibility_call_rvas=list(map(hex,seed_calls)),primary_cache_offset='0x5d0',
        constants_checked=len(floats)+len(doubles),
        rounding_order_sites=list(map(hex,(0xea1e9,0xea1ed,0xea1f1,0xea537,0xea53b))),
        positive_threshold_down_comparison_preserved=True)
    return result


def owned_checks(dll):
    native = Native(dll)
    score = native.fn(0xe9ca0,Q,P,P,F)
    rng = random.Random(447)
    exact = 0
    for case in range(960):
        count = (0,1,2,3,4,8,17,40)[case%8]
        peaks = sorted((f32(rng.uniform(40,6000)),f32(rng.uniform(-130,-10))) for _ in range(count))
        frequency = f32(rng.choice((110,220,440,700,1200,2000))*(.7+rng.random()*.6))
        reference = max((db for hz,db in peaks),default=-200.)
        obj = native.alloc(0x7b0)
        I.from_address(obj + 0x780).value = 1
        I.from_address(obj + 0x730).value = count
        ranks = peak_ranks(peaks)
        P.from_address(obj + 0x720).value = native.array(F,[v for peak in peaks for v in peak])
        P.from_address(obj + 0x738).value = native.array(I,ranks)
        prefix = sorted(range(ranks[0]),key=lambda i:-peaks[i][1]) if ranks else []
        P.from_address(obj + 0x740).value = native.array(I,prefix)
        pair = native.array(F,[frequency,0.])
        expected = candidate_score(peaks,frequency,reference)
        assert score(obj,pair,reference) == 0
        assert struct.pack('<ff',expected['hz'],expected['cost']) == c.string_at(pair,8),case
        exact += 1
    config = native.alloc(0x35f0)
    native.fn(0x90de0,P,P)(config)
    eligibility = tuple(native.evaluate(config + 0x5d0 + key*0xa0,0.) for key in (0x1a,0x1b))
    eligible = native.fn(0xe9690,Q,P,F,Q,F,F)
    checked = rejects = 0
    for contrast in (0.,24.99999,25.,25.00001,40.,80.):
        peaks = [(f32(100.*(i+1)),f32(-100.+contrast*(i%2))) for i in range(8)]
        for frequency in (100.,101.,99.):
            for lower,upper in ((40.,1000.),(100.,100.),(101.,300.)):
                obj = native.alloc(0x7b0)
                P.from_address(obj + 0x598).value = config
                I.from_address(obj + 0x730).value = len(peaks)
                P.from_address(obj + 0x720).value = native.array(F,[v for peak in peaks for v in peak])
                expected = candidate_eligible(peaks,frequency,lower,upper,*eligibility)
                assert eligible(obj,frequency,0,lower,upper) == expected
                checked += 1
                rejects += not expected
    return dict(cases=960,byte_exact=exact,eligibility_cases=checked,eligibility_rejects=rejects,
                eligibility_curves=list(eligibility),peak_counts=[0,1,2,3,4,8,17,40],
                native_calls_on_owned_inputs_only=True)


def child(args,pcm=None,frequency=None):
    frequency = args.frequency if frequency is None else frequency
    pcm = owned_signal(args.shape,frequency) if pcm is None else pcm
    batch = configured_batch(args.profile)
    counts = dict(steps=0,dispatcher_calls=0,produced_cache_calls=0,byte_exact_cache_calls=0,
                  independently_scored_candidates=0,rank_values=0,unchanged_cache_calls=0,no_cache_calls=0,
                  exact_gate_calls=0,predicted_write_calls=0,byte_exact_confidence_calls=0,exact_history_cost_calls=0)
    seeds = Counter()
    finished = []
    confidence_predictions = {}
    def setup(native,ctx):
        obj = P.from_address(ctx + 0xc88).value
        config = P.from_address(obj + 0x598).value
        assert I.from_address(config + 0x5b0).value == I.from_address(obj + 0x78c).value == 0
        eligibility = tuple(native.evaluate(config + 0x5d0 + key*0xa0,0.) for key in (0x1a,0x1b))
        assert all(native.evaluate(config + 0x5d0 + key*0xa0,1.) == value for key,value in zip((0x1a,0x1b),eligibility))
        automatic = native.evaluate(config + 0x5d0 + 0x14*0xa0,0.)
        feature_gate = native.evaluate(config + 0x5d0 + 0x17*0xa0,0.)
        curves = {key:native.evaluate(config + 0x5d0 + key*0xa0,0.) for key in (0xf,0x14,0x16,0x17,0x18,0x19,0x1c)}
        history = P.from_address(obj + 0x5c0).value
        count = I.from_address(obj + 0x5c8).value
        delay = I.from_address(obj + 0x5b8).value
        assert count == 20 and delay == 1
        table = P.from_address(ctx).value
        original = c.CFUNCTYPE(Q,P)(P.from_address(table + 8).value)
        private = native.alloc(0x100)
        c.memmove(private,table,0x100)
        last_prediction = None
        def step(ctx):
            nonlocal last_prediction
            try:
                counts['steps'] += 1
                assert counts['steps'] < 10000
                old_count = I.from_address(obj + 0x710).value
                old_prior = F.from_address(obj + 0x714).value
                old_variation = F.from_address(obj + 0x718).value
                previous = [F.from_address(P.from_address(history + (count-2-i)*8).value + 0x18).value for i in range(2)]
                previous_costs = [F.from_address(P.from_address(history + (count-2-i)*8).value + 0x1c).value for i in range(2)]
                last = P.from_address(history + (count-1)*8).value
                slots = P.from_address(ctx + 0xc30).value
                descriptor = P.from_address(slots + delay*8).value
                target = P.from_address(descriptor + 0x28).value
                previous_frame = P.from_address(P.from_address(slots).value + 0x28).value
                old_time = D.from_address(obj + 0x5b0).value
                returned = original(ctx)
                if D.from_address(obj + 0x5b0).value == old_time:
                    return returned
                counts['dispatcher_calls'] += 1
                assert P.from_address(history + (count-2)*8).value == last
                original_count = I.from_address(descriptor + 0x18).value
                assert 0 <= original_count < 1500
                prior = old_prior if old_prior > 0 else previous[0] if previous[0] > 0 else F.from_address(obj + 0x750).value
                peaks = []
                if automatic == 0 or original_count == 0:
                    prediction = None
                else:
                    filtered_count = I.from_address(obj + 0x730).value
                    assert 0 <= filtered_count < 1500
                    peaks = [(F.from_address(P.from_address(obj + 0x720).value + i*8).value,
                              F.from_address(P.from_address(obj + 0x720).value + i*8 + 4).value) for i in range(filtered_count)]
                    ranks = peak_ranks(peaks)
                    if ranks:
                        assert ranks == [I.from_address(P.from_address(obj + 0x738).value + i*4).value for i in range(filtered_count)]
                        counts['rank_values'] += filtered_count
                    prediction = reference_cache(peaks,prior,previous,old_count,
                        F.from_address(obj + 0x74c).value,F.from_address(obj + 0x754).value,
                        F.from_address(obj + 0x750).value,feature_gate,eligibility)
                if prediction is not None:
                    counts['produced_cache_calls'] += 1
                    counts['independently_scored_candidates'] += prediction['score_calls']
                    seeds[prediction['seed_source']] += 1
                    last_prediction = prediction
                else:
                    counts['no_cache_calls'] += 1
                    if last_prediction is not None:
                        counts['unchanged_cache_calls'] += 1
                feature = P.from_address(target + 0x128).value
                previous_feature = P.from_address(previous_frame + 0x128).value
                previous_energy = F.from_address(previous_feature + 0xc).value if previous_feature else None
                original_peaks = [(F.from_address(P.from_address(descriptor + 0x20).value + i*20).value,
                                   F.from_address(P.from_address(descriptor + 0x20).value + i*20 + 4).value) for i in range(original_count)]
                values = [F.from_address(feature + offset).value for offset in (4,8,0xc,0x1c)]
                pairs = [(F.from_address(last + i*8).value,F.from_address(last + i*8 + 4).value) for i in range(3)] if automatic else [(0.,0.)]*3
                best_hz,best_cost = min(reversed(pairs),key=lambda pair:pair[1])
                independent_cache = (last_prediction['hz'],last_prediction['cost']) if last_prediction else (0.,0.)
                state = dict(pairs=pairs,prior_pitch=prior,voiced_count=old_count,variation=old_variation,
                             previous=list(zip(previous,previous_costs)),reference_cache=independent_cache)
                gate = confidence_gate(original_peaks,peaks,best_hz,best_cost,*values,previous_energy,curves,state)
                correction = gate['history_correction']
                if correction is not None:
                    assert last_prediction is not None
                    assert struct.pack('<f',correction['cost']) == c.string_at(last + 0x1c,4)
                    counts['exact_history_cost_calls'] += 1
                expected = -1.
                if gate['writes']:
                    size = I.from_address(descriptor).value
                    assert size == 2048
                    psd = [F.from_address(P.from_address(descriptor + 8).value + i*4).value for i in range(size)]
                    geometry = (F.from_address(obj + 0x74c).value,F.from_address(obj + 0x754).value,F.from_address(obj + 0x5a8).value)
                    if values[2] == 0:
                        expected = exceptional_confidence(psd,original_peaks,best_hz,*geometry,values[0])
                    else:
                        expected = candidate_confidence(psd,original_peaks,best_hz,*geometry,values[0],values[2])['confidence']
                # Neither observed cache nor confidence supplies any input
                # to the completed production/gate calculations above.
                actual = F.from_address(feature + 0x14).value
                assert gate['writes'] == (actual != -1)
                assert struct.pack('<f',expected) == struct.pack('<f',actual)
                counts['exact_gate_calls'] += 1
                counts['predicted_write_calls'] += gate['writes']
                counts['byte_exact_confidence_calls'] += gate['writes']
                at = round(D.from_address(target + 0x48).value*44100/256)
                assert at not in confidence_predictions
                confidence_predictions[at] = expected
                if last_prediction is not None:
                    # Actual cache is loaded only after independent production.
                    assert struct.pack('<ff',last_prediction['hz'],last_prediction['cost']) == c.string_at(obj + 0x5d0,8), (args.sample,args.mode,args.profile,counts['dispatcher_calls'],
                        abs(last_prediction['hz']-F.from_address(obj+0x5d0).value),
                        abs(last_prediction['cost']-F.from_address(obj+0x5d4).value),last_prediction['seed_source'])
                    counts['byte_exact_cache_calls'] += 1
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
    if args.profile == 'manual_pitch' and args.mode == 1:
        assert not confidence_predictions
        confidence_predictions = {at:-1. for at in finished[0]}
    assert all(struct.pack('<f',confidence_predictions[at]) == struct.pack('<f',row['values'][4]) for at,row in finished[0].items())
    return dict(sample=args.sample,shape=args.shape,frequency=frequency,mode=args.mode,profile=args.profile,
                frames=len(finished[0]),controls=counts,seeds=dict(sorted(seeds.items())),
                observed_matches_fresh_baseline=True,
                final_confidence_matches_prediction=True,
                bit17_nonzero=sum(row['values'][17] != 0 for row in finished[0].values()),
                nonzero_array_frames=sum(row['source_array_nonzero'] != 0 for row in finished[0].values()))


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
        pcm = frequency = None
        if args.sample is not None:
            _,_,sequences = load_sequences(args.manifest,args.voice_root,args.ddb_tools)
            sequences += auxiliary_sequences(args.aux_vdb)
            sequence = sequences[args.sample]
            pcm = sequence['pcm']
            frequency = sequence.get('pitch1',statistics.median(row['f0'] for row in sequence['frames']))
        print(json.dumps(child(args,pcm,frequency)))
        return
    assert args.output
    static = review_reference(args.dll,args.decoder_dir)
    owned = owned_checks(args.dll)
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
        print(json.dumps(dict(sample=row['sample'],shape=row['shape'],mode=row['mode'],profile=row['profile'],
                              cache_calls=row['controls']['produced_cache_calls'])),flush=True)
    sources = ('verify_vqm_candidate_reference_cache.py','frm2_peak_candidate_score.py','frm2_candidate_reference_cache.py',
               'verify_vqm_candidate_gate.py','frm2_candidate_gate.py','frm2_candidate_history_cost.py',
               'verify_vqm_candidate_confidence_protocol.py','frm2_candidate_confidence.py','probe_frm2_batch_outputs.py',
               'verify_vqm_mode5_pcm.py','verify_vqm_alternate_score_paths.py','verify_vqm_bit16_spectral_power.py',
               'verify_vqm_bit16_peak_interpolation.py','audit_frm2_numeric.py','audit_vqm_v4_module_leads.py','audit_vqm_feature_calls_cfg.py')
    seeds = Counter()
    for row in rows:
        seeds.update(row['seeds'])
    report = dict(schema=1,dse_sha256=SHA256,
        source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        reviewed_contract=static,owned_score_checks=owned,cases=rows,summary=dict(cases=len(rows),frames=sum(row['frames'] for row in rows),
            controls={name:sum(row['controls'][name] for row in rows) for name in rows[0]['controls']},seeds=dict(sorted(seeds.items())),
            bit17_nonzero=sum(row['bit17_nonzero'] for row in rows),nonzero_array_frames=sum(row['nonzero_array_frames'] for row in rows)),
        scope='Independent filtered-peak ranking, candidate mismatch score and primary cache production, connected to late history/gate/confidence without native reference cache input',
        limits=['Filtered peak and preceding feature/PCM production remain native inputs',
                'Reviewed constant eligibility curves26/27; secondary and final retained candidates not independently produced here',
                'Original maker scores, bit17 and zero arrays remain unresolved',
                'No commercial payloads or proprietary code/decompilation persisted'],
        installed_files_unchanged=True,no_windows_temp=True)
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__ == '__main__':
    main()
