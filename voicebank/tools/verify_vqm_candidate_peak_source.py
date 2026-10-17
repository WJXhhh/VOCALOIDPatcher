"""Independent spectrum-to-peak production connected to candidate history/state.

Native score calls use owned fixtures only. Normal batches provide read-only
scalar spectra and features; original peaks and downstream state are independent.
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
from frm2_candidate_reference_cache import candidate_eligible
from frm2_candidate_production import produce_candidates
from frm2_candidate_peak_filter import filter_candidate_peaks
from frm2_candidate_history_state import selected_pair,repair_history,update_reference
from frm2_candidate_peak_source import candidate_peak_source
from verify_vqm_candidate_peak_source_owned import owned_peak_source_checks
from verify_vqm_candidate_history_owned import owned_history_checks
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
    # XMM12 is saved before it is reused for the double proximity constant.
    # The second/third refinements reload that same strongest-peak dB value.
    for site in (0xe708f,0xe70d7):
        op = caller[site]
        assert op.mnemonic == 'movss' and op.operands[0].mem.disp == 0x68
        assert op.reg_name(op.operands[1].reg) == 'xmm12'
    for site,register in ((0xe7553,'xmm12'),(0xe76cb,'xmm12'),(0xe7b09,'xmm12'),
                          (0xe7bda,'xmm9'),(0xe7cda,'xmm15')):
        op = caller[site]
        assert op.mnemonic == 'movaps'
        assert [op.reg_name(operand.reg) for operand in op.operands] == ['xmm2',register]
    for site,register in ((0xe7bc9,'xmm9'),(0xe7cbf,'xmm15')):
        op = caller[site]
        assert op.mnemonic == 'movss' and op.operands[1].mem.disp == 0x68
        assert op.reg_name(op.operands[0].reg) == register
    assert caller[0xe7220].mnemonic == 'cmp'
    assert caller[0xe7220].operands[1].imm == 39
    for site,mnemonic in ((0xe6e94,'jbe'),(0xe6e9e,'jbe'),(0xe6ec5,'jbe'),
                          (0xe6edd,'jbe'),(0xe6f10,'ja'),(0xe6fa7,'jbe')):
        assert caller[site].mnemonic == mnemonic
    assert caller[0xe6ebc].mnemonic == 'cmp'
    assert caller[0xe6ebc].operands[1].imm == -1
    assert caller[0xe6fdd].mnemonic == 'cmp'
    assert caller[0xe6fdd].operands[0].mem.disp == 0x730
    assert caller[0xe6fdd].operands[1].imm == 1500
    assert caller[0xe6fe7].mnemonic == 'jge'
    floats = {0x5bfa58:4.,0x5bf210:1.,0x5bf128:.5,0x5bf9a8:3.5,0x5bf0f0:.35,
        0x5bffbc:-10000.,0x5bfc4c:15.,0x5bfc28:12.,0x5bf03c:.05,0x5bf298:1.2,
        0x5bf7d0:2.2,0x5bf0fc:.4,0x5bf154:.7,0x5bfc5c:20.,0x5bf140:.6,
        0x5bfd24:600.,0x5bfc84:25.,0x5bfe38:1e7,0x5bfc9c:40.,0x5bfd2c:800.,
        0x5bfca8:55.,0x5bff80:-45.,0x5bffb0:-1000.,0x5bfe3c:1e8,
        0x5bf1c0:.95,0x5bf23c:1.06,0x5befc0:0.,0x5bf1e8:.97,0x5bf220:1.03,
        0x5bff60:-10.,0x5bfd3c:1000.,0x5bfca4:50.,0x5bf158:.75,
        0x5bffec:-3.4028234663852886e38}
    doubles = {0x5bf090:1e-8,0x5bf650:1.,0x5bf8c0:10.,0x5bf700:1.8,
        0x5bf7c0:3.3,0x5bf348:.1,0x5bfee0:-10.,0x5bf938:40.,0x5bf8e8:15.,
        0x5bf3e8:.2,0x5bf230:.01,0x5bf678:1.06,0x5bf6e0:1.5,0x5bf880:6.,
        0x5bf5f8:.93,0x5bf680:1.07,0x5bf600:.95,
        0x5bf808:4.4,0x5bfb30:5000.,0x5bfb08:3000.,0x5bf5e8:.9}
    for rva,value in floats.items():
        assert struct.unpack_from('<f',data,pe.offset(rva))[0] == f32(value)
    for rva,value in doubles.items():
        assert struct.unpack_from('<d',data,pe.offset(rva))[0] == value
    result['reference_production'] = dict(score_rva='0xe9ca0',rank_rva='0xea8b0',
        eligibility_rva='0xe9690',score_call_rvas=list(map(hex,score_calls)),
        eligibility_call_rvas=list(map(hex,seed_calls)),primary_cache_offset='0x5d0',
        constants_checked=len(floats)+len(doubles),
        rounding_order_sites=list(map(hex,(0xea1e9,0xea1ed,0xea1f1,0xea537,0xea53b))),
        positive_threshold_down_comparison_preserved=True,
        shared_reference_db_stack_offset='0x68',pool_stop_count=39,
        shared_reference_db_sites=list(map(hex,(0xe708f,0xe70d7,0xe7bc9,0xe7bda,0xe7cbf,0xe7cda))))
    result['peak_filter'] = dict(input_stride=20,shape_field_offset='0xc',output_stride=8,
        maximum_output_pairs=1500,shape_signed_sentinel=-1,
        strict_comparison_sites=list(map(hex,(0xe6e94,0xe6e9e,0xe6ec5,0xe6edd,0xe6f10,0xe6fa7))),
        capacity_compare_rva='0xe6fdd',frequency_ceiling_bounds=[3000.,5000.],
        dominant_frequency_multiplier=4.4,lower_multiplier=.9,db_floor_distance=50.,shadow_slope=.75)
    dispatcher,history_repair = decode(0xe60a0),decode(0xe8c90)
    for site in (0xe61d7,0xe67b8):
        assert dispatcher[site].group(cs.CS_GRP_CALL)
        assert dispatcher[site].operands[0].imm-pe.image_base == 0xe8c90
    stores = (0xe8cf9,0xe8da5,0xe8e67,0xe8ec0)
    for site in stores:
        assert history_repair[site].mnemonic in ('mov','movss')
        assert history_repair[site].operands[0].mem.disp == 0x18
    assert history_repair[0xe8ec5].mnemonic == 'cmp'
    assert history_repair[0xe8ec5].operands[0].mem.disp == 0x590
    state_constants = {0x5bf6d0:('d',1.4),0x5bf480:('d',.35),0x5bf688:('d',1.1),
        0x5bf0bc:('f',f32(.3)),0x5bf0fc:('f',f32(.4)),0x5bf930:('f',3.),
        0x5bf040:('f',f32(.06)),0x5bf540:('d',.5),0x5bf348:('d',.1),
        0x5bf3e8:('d',.2),0x5bf5e8:('d',.9),0x5bf158:('f',f32(.75))}
    for rva,(format,value) in state_constants.items():
        assert struct.unpack_from('<'+format,data,pe.offset(rva))[0] == value
    result['history_feedback'] = dict(dispatcher_rva='0xe60a0',repair_rva='0xe8c90',
        repair_call_rvas=list(map(hex,(0xe61d7,0xe67b8))),ordinary_store_rvas=list(map(hex,stores)),
        extended_repair_flag_offset='0x590',reanalysis_flag_offset='0x784',
        scalar_state_offsets=['0x710','0x714','0x718'],history_entries=20,
        delayed_voicing_entry_before_shift=18,latest_weighted_entry_before_shift=19,
        state_constants_checked=len(state_constants))
    main_caller,wrapper = decode(0xd2cc0),decode(0xd04c0)
    for ops,site,target in ((main_caller,0xd4f8c,0xd04c0),(wrapper,0xd05e3,0x51e60)):
        assert ops[site].group(cs.CS_GRP_CALL)
        assert ops[site].operands[0].imm-pe.image_base == target
    for site,key in ((0xd4f20,0x76),(0xd4f3e,2),(0xd4f45,0x75)):
        assert main_caller[site].mnemonic == 'mov'
        assert main_caller[site].operands[1].imm == key
    for site,offset in ((0xd0589,0x520),(0xd0598,0xad8),(0xd05f6,0x588)):
        assert wrapper[site].mnemonic == 'movd'
        assert wrapper[site].operands[1].mem.disp == offset
    for site,offset in ((0xd05db,8),(0xd05df,16)):
        assert wrapper[site].mnemonic == 'mov'
        assert wrapper[site].operands[1].mem.disp == offset
    result['peak_source'] = dict(wrapper_rva='0xd04c0',detector_rva='0x51e60',
        main_call_rva='0xd4f8c',wrapper_call_rva='0xd05e3',
        descriptor_spectrum_offset='0x8',descriptor_complex_offset='0x10',
        input_record_stride=20,compared_offsets=['0x0','0x4','0xc','0x10'],
        threshold_curve=3,default_cutoff_curve=2,alternate_cutoff_control='0x75',
        cutoff_selector_control='0x76',alternate_cutoff_config_offset='0x584',
        interpolation_config_offset='0x520',
        post_filter_config_offset='0x588',phase_excluded=True,
        span_semantics='Detector run count closed by trough/plateau/next peak; unfinished tail uses -1')
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


def owned_filter_checks(dll):
    native = Native(dll)
    config = native.alloc(0x35f0)
    native.fn(0x90de0,P,P)(config)
    for key,value in ((0x14,1.),(0xd,220.)):
        for time in (0.,1.):
            native.add(config+0x5d0+key*0xa0,time,value)
    assert I.from_address(config+0x5b0).value == 0
    construct = native.fn(0xe5ce0,P,P,P,F)
    analyze = native.fn(0xe6a70,Q,P,P,I,P,F)
    rng = random.Random(449)
    fixtures = []
    for case in range(192):
        count = (0,1,2,3,4,8,17,40)[case%8]
        fixtures.append(sorted((f32(rng.uniform(0,14000)),f32(rng.uniform(-130,-10)),
                                rng.choice((-1,0,1,2,3,4,20))) for _ in range(count)))
    fixtures += [
        [(f32(36.+delta),-20.,shape)] for delta in (-.0001,0.,.0001) for shape in (-1,2,3)]
    fixtures += [
        [(500.,-20.,3),(f32(3000.+delta),-21.,3)] for delta in (-.0003,0.,.0003)]
    fixtures += [
        [(1000.,-20.,3),(f32(4400.+delta),-21.,3)] for delta in (-.0005,0.,.0005)]
    fixtures += [
        [(1500.,-20.,3),(f32(5000.+delta),-21.,3)] for delta in (-.0005,0.,.0005)]
    fixtures += [[(f32(1000.+delta),-20.,shape)]
                 for delta in (-.0001,0.,.0001) for shape in (-1,2,3)]
    fixtures += [[(500.,-20.,3),(540.,f32(-50.+delta),3)] for delta in (-.00001,0.,.00001)]
    fixtures += [[(460.,f32(-80.+delta),3),(500.,-50.,3)] for delta in (-.00001,0.,.00001)]
    fixtures += [[(500.,-20.,3),(700.,f32(-70.+delta),3)] for delta in (-.00001,0.,.00001)]
    fixtures.append([(f32(1000.+2*i),f32(-50.+.0001*i),3) for i in range(1504)])
    controls = Counter()
    pairs = candidates = empty = 0
    for case,peaks in enumerate(fixtures):
        obj = native.alloc(0x7c0)
        construct(obj,config,44100.)
        frame = native.alloc(0x158)
        feature = native.alloc(0x80)
        P.from_address(frame+0x128).value = feature
        for offset,value in ((4,.0001),(8,0.),(0xc,.001),(0x14,-1.),(0x1c,.1)):
            F.from_address(feature+offset).value = value
        descriptor = native.alloc(0x30)
        I.from_address(descriptor).value = 2048
        P.from_address(descriptor+8).value = native.array(F,[1.]*2048)
        I.from_address(descriptor+0x18).value = len(peaks)
        guarded = peaks+[(22050.,-200.,-1)]
        payload = b''.join(struct.pack('<fffiI',hz,db,0.,shape,0) for hz,db,shape in guarded)
        P.from_address(descriptor+0x20).value = native.array(c.c_ubyte,payload)
        P.from_address(descriptor+0x28).value = frame
        # The normal dispatcher supplies a descriptor queue for its previous
        # energy check; the owned leaf requires that same queue contract.
        P.from_address(obj+0x5a0).value = native.array(P,[descriptor])
        lower,upper,fallback = [F.from_address(obj+offset).value for offset in (0x74c,0x754,0x750)]
        expected = filter_candidate_peaks(peaks,lower)
        eligibility = tuple(native.evaluate(config+0x5d0+key*0xa0,0.) for key in (26,27))
        prediction = produce_candidates(expected['peaks'],220.,[0.,0.],0,lower,upper,fallback,
                                        native.evaluate(config+0x5d0+23*0xa0,0.),eligibility)
        history = P.from_address(obj+0x5c0).value
        count = I.from_address(obj+0x5c8).value
        try:
            returned = analyze(obj,history,count,descriptor,220.)
        except OSError as error:
            raise AssertionError((case,len(peaks),len(expected['peaks']),count,'owned leaf protocol',
                I.from_address(obj+0x730).value,I.from_address(obj+0x5cc).value,
                bool(P.from_address(obj+0x738).value),bool(P.from_address(obj+0x740).value),
                I.from_address(obj+0x780).value)) from error
        assert returned == 0
        assert I.from_address(obj+0x730).value == len(expected['peaks']),case
        for i,pair in enumerate(expected['peaks']):
            assert struct.pack('<ff',*pair) == c.string_at(P.from_address(obj+0x720).value+i*8,8),(case,i)
        if prediction:
            assert I.from_address(obj+0x5cc).value == len(prediction['pool'])
            for i,pair in enumerate(prediction['pool']):
                assert struct.pack('<ff',*pair) == c.string_at(obj+0x5d0+i*8,8),(case,'pool',i)
            last = P.from_address(history+(count-1)*8).value
            for i,pair in enumerate(prediction['pairs']):
                assert struct.pack('<ff',*pair) == c.string_at(last+i*8,8),(case,'final',i)
            candidates += len(prediction['pool'])
        pairs += len(expected['peaks'])
        empty += not expected['peaks']
        controls.update(expected['controls'])
    assert controls['capacity_stops'] == 1
    assert all(controls[key] > 0 for key in ('frequency_rejects','shape_rejects','level_rejects',
                                           'previous_shadow_rejects','backward_deletions'))
    return dict(cases=len(fixtures),exact_cases=len(fixtures),empty_cases=empty,
                filtered_pairs=pairs,candidate_pool_pairs=candidates,controls=dict(sorted(controls.items())),
                owned_leaf_constructor_rva='0xe5ce0',owned_leaf_rva='0xe6a70',
                capacity_input_records=1504,capacity_output_pairs=1500,
                native_calls_on_owned_inputs_only=True)


def child(args,pcm=None,frequency=None):
    frequency = args.frequency if frequency is None else frequency
    pcm = owned_signal(args.shape,frequency) if pcm is None else pcm
    batch = configured_batch(args.profile)
    counts = dict(steps=0,dispatcher_calls=0,produced_cache_calls=0,byte_exact_cache_calls=0,
                  independently_scored_candidates=0,rank_values=0,unchanged_cache_calls=0,no_cache_calls=0,
                  exact_gate_calls=0,predicted_write_calls=0,byte_exact_confidence_calls=0,exact_history_cost_calls=0,
                  pool_pairs=0,final_pairs=0,secondary_seed_calls=0,secondary_refinement_calls=0,
                  secondary_refinement_steps=0,final_refinement_steps=0,final_skipped=0,near_duplicates=0,replacements=0,
                  final_pair_comparisons=0,empty_final_pair_calls=0,original_peak_records=0,
                  filtered_peak_pairs=0,filter_calls=0,filter_empty_calls=0,
                  history_state_updates=0,history_pitch_comparisons=0,history_cost_comparisons=0,
                  count_caps=0,prior_extrapolations=0,peak_source_calls=0,
                  peak_source_records=0,peak_source_empty_calls=0,peak_source_sentinel_spans=0,
                  peak_source_small_spans=0,peak_source_large_spans=0)
    seeds = Counter()
    pool_sizes,retained_counts = Counter(),Counter()
    filter_controls = Counter()
    repair_events = Counter()
    finished = []
    confidence_predictions = {}
    def setup(native,ctx):
        obj = P.from_address(ctx + 0xc88).value
        config = P.from_address(obj + 0x598).value
        assert I.from_address(config + 0x5b0).value == I.from_address(obj + 0x78c).value == 0
        assert P.from_address(ctx+0xad0).value == config
        assert I.from_address(ctx+0xad8).value == 44100
        logarithmic = I.from_address(config+0x520).value
        assert logarithmic == I.from_address(config+0x588).value == 0
        source_curve_ids = (2,3,0x75,0x76)
        source_control = native.fn(0x2b050,F,P,I,F)
        source_curves = {key:source_control(config,key,0.) for key in source_curve_ids}
        assert all(source_control(config,key,1.) == value for key,value in source_curves.items())
        assert (source_curves[2],source_curves[3],source_curves[0x76]) == (22050.,-300.,0.)
        source_cutoff = source_curves[2 if source_curves[0x76] == 0 else 0x75]
        eligibility = tuple(native.evaluate(config + 0x5d0 + key*0xa0,0.) for key in (0x1a,0x1b))
        assert all(native.evaluate(config + 0x5d0 + key*0xa0,1.) == value for key,value in zip((0x1a,0x1b),eligibility))
        automatic = native.evaluate(config + 0x5d0 + 0x14*0xa0,0.)
        feature_gate = native.evaluate(config + 0x5d0 + 0x17*0xa0,0.)
        curves = {key:native.evaluate(config + 0x5d0 + key*0xa0,0.) for key in (0xf,0x14,0x16,0x17,0x18,0x19,0x1c)}
        history = P.from_address(obj + 0x5c0).value
        count = I.from_address(obj + 0x5c8).value
        delay = I.from_address(obj + 0x5b8).value
        assert count == 20 and delay == 1
        assert I.from_address(obj+0x590).value == I.from_address(obj+0x784).value == 0
        own_history = [(0.,0.)]*count
        own_state = dict(voiced_count=0,prior_pitch=0.,variation=0.)
        table = P.from_address(ctx).value
        original = c.CFUNCTYPE(Q,P)(P.from_address(table + 8).value)
        private = native.alloc(0x100)
        c.memmove(private,table,0x100)
        last_prediction = None
        def step(ctx):
            nonlocal last_prediction,own_history,own_state
            try:
                counts['steps'] += 1
                assert counts['steps'] < 10000
                old_count,old_prior,old_variation = [own_state[key] for key in ('voiced_count','prior_pitch','variation')]
                assert old_count == I.from_address(obj+0x710).value
                assert struct.pack('<ff',old_prior,old_variation) == c.string_at(obj+0x714,8)
                previous = [own_history[-2-i][0] for i in range(2)]
                previous_costs = [own_history[-2-i][1] for i in range(2)]
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
                size = I.from_address(descriptor).value
                assert size == 2048
                psd = [F.from_address(P.from_address(descriptor+8).value+i*4).value for i in range(size)]
                independent = candidate_peak_source(psd,44100.,logarithmic,source_curves[3],source_cutoff)
                records = independent['records']
                assert len(records) == original_count, (args.sample,args.mode,args.profile,counts['dispatcher_calls'],'peak count')
                # The native peak payload is read only for comparison after production.
                # Phase +8 is neither an input nor a claimed output of this stage.
                for i,(hz,db,span,bin_index) in enumerate(records):
                    address = P.from_address(descriptor+0x20).value+i*20
                    expected_record = struct.pack('<ffiI',hz,db,span,bin_index)
                    assert expected_record == c.string_at(address,8)+c.string_at(address+0xc,8), (args.sample,args.mode,args.profile,counts['dispatcher_calls'],'peak',i)
                counts['peak_source_calls'] += 1
                counts['peak_source_records'] += len(records)
                counts['peak_source_empty_calls'] += not records
                counts['peak_source_sentinel_spans'] += sum(span == -1 for hz,db,span,bin_index in records)
                counts['peak_source_small_spans'] += sum(0 <= span <= 2 for hz,db,span,bin_index in records)
                counts['peak_source_large_spans'] += sum(span > 2 for hz,db,span,bin_index in records)
                raw_peaks = [(hz,db,span) for hz,db,span,bin_index in records]
                original_peaks = [(hz,db) for hz,db,shape in raw_peaks]
                peaks = []
                if automatic == 0 or original_count == 0:
                    prediction = None
                else:
                    filtered = filter_candidate_peaks(raw_peaks,F.from_address(obj+0x74c).value)
                    peaks = filtered['peaks']
                    filtered_count = len(peaks)
                    filter_controls.update(filtered['controls'])
                    counts['filter_calls'] += 1
                    counts['filter_empty_calls'] += not peaks
                    counts['original_peak_records'] += original_count
                    counts['filtered_peak_pairs'] += filtered_count
                    # Native filter output supplies no input to production.
                    assert I.from_address(obj+0x730).value == filtered_count
                    for i,pair in enumerate(peaks):
                        assert struct.pack('<ff',*pair) == c.string_at(P.from_address(obj+0x720).value+i*8,8), (args.sample,args.mode,args.profile,counts['dispatcher_calls'],'filter',i)
                    ranks = peak_ranks(peaks)
                    if ranks:
                        assert ranks == [I.from_address(P.from_address(obj + 0x738).value + i*4).value for i in range(filtered_count)]
                        counts['rank_values'] += filtered_count
                    prediction = produce_candidates(peaks,prior,previous,old_count,
                        F.from_address(obj + 0x74c).value,F.from_address(obj + 0x754).value,
                        F.from_address(obj + 0x750).value,feature_gate,eligibility)
                if prediction is not None:
                    counts['produced_cache_calls'] += 1
                    counts['independently_scored_candidates'] += prediction['score_calls']
                    seeds[prediction['seed_source']] += 1
                    last_prediction = prediction
                    counts['pool_pairs'] += len(prediction['pool'])
                    counts['final_pairs'] += 3
                    pool_sizes[len(prediction['pool'])] += 1
                    retained_counts[prediction['retained_count']] += 1
                    counts['secondary_seed_calls'] += prediction['secondary_seed']
                    counts['secondary_refinement_calls'] += prediction['secondary_steps'] > 0
                    for key in ('final_skipped','near_duplicates','replacements'):
                        counts[key] += prediction[key]
                    counts['secondary_refinement_steps'] += prediction['secondary_steps']
                    counts['final_refinement_steps'] += prediction['final_steps']
                else:
                    counts['no_cache_calls'] += 1
                    if last_prediction is not None:
                        counts['unchanged_cache_calls'] += 1
                feature = P.from_address(target + 0x128).value
                previous_feature = P.from_address(previous_frame + 0x128).value
                previous_energy = F.from_address(previous_feature + 0xc).value if previous_feature else None
                values = [F.from_address(feature + offset).value for offset in (4,8,0xc,0x1c)]
                pairs = prediction['pairs'] if prediction else [(0.,0.)]*3
                # Padding raises the consumed candidate count to three;
                # retained_count describes unique entries before padding.
                participating = pairs
                best_hz,best_cost = min(reversed(participating),key=lambda pair:pair[1])
                independent_cache = last_prediction['pool'][0] if last_prediction else (0.,0.)
                state = dict(pairs=participating,prior_pitch=prior,voiced_count=old_count,variation=old_variation,
                             previous=list(zip(previous,previous_costs)),reference_cache=independent_cache)
                gate = confidence_gate(original_peaks,peaks,best_hz,best_cost,*values,previous_energy,curves,state)
                correction = gate['history_correction']
                if correction is not None:
                    assert last_prediction is not None
                    assert struct.pack('<f',correction['cost']) == c.string_at(last + 0x1c,4), (args.sample,args.mode,args.profile,counts['dispatcher_calls'],prediction['retained_count'],correction['reason'],'corrected cost')
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
                    assert struct.pack('<ff',*last_prediction['pool'][0]) == c.string_at(obj + 0x5d0,8), (args.sample,args.mode,args.profile,counts['dispatcher_calls'],'primary cache')
                    if prediction:
                        assert I.from_address(obj+0x5cc).value == len(prediction['pool'])
                        for i,pair in enumerate(prediction['pool']):
                            assert struct.pack('<ff',*pair) == c.string_at(obj+0x5d0+i*8,8), (args.sample,args.mode,args.profile,counts['dispatcher_calls'],'pool',i)
                if automatic:
                    for i,pair in enumerate(pairs):
                        assert struct.pack('<ff',*pair) == c.string_at(last+i*8,8), (args.sample,args.mode,args.profile,counts['dispatcher_calls'],'final',i)
                    counts['final_pair_comparisons'] += 3
                    counts['empty_final_pair_calls'] += prediction is None
                if last_prediction is not None:
                    counts['byte_exact_cache_calls'] += 1
                chosen = selected_pair(gate,(best_hz,best_cost),expected,
                    manual_pitch=F.from_address(obj+0x750).value if automatic == 0 else None)
                own_history[-1] = chosen
                if automatic:
                    own_history,events = repair_history(own_history,old_count,old_prior,old_variation)
                    repair_events.update(events)
                own_state = update_reference(own_history,old_count)
                assert own_state['voiced_count'] == I.from_address(obj+0x710).value, (args.sample,args.mode,args.profile,counts['dispatcher_calls'],'count')
                assert struct.pack('<ff',own_state['prior_pitch'],own_state['variation']) == c.string_at(obj+0x714,8), (args.sample,args.mode,args.profile,counts['dispatcher_calls'],'state')
                counts['history_state_updates'] += 1
                counts['count_caps'] += own_state['capped']
                counts['prior_extrapolations'] += own_state['extrapolated']
                own_history = own_history[1:]+[(0.,0.)]
                for i,(hz,cost) in enumerate(own_history[:-1]):
                    entry = P.from_address(history+i*8).value
                    assert struct.pack('<f',hz) == c.string_at(entry+0x18,4), (args.sample,args.mode,args.profile,counts['dispatcher_calls'],'history pitch',i)
                    counts['history_pitch_comparisons'] += 1
                    if automatic:
                        assert struct.pack('<f',cost) == c.string_at(entry+0x1c,4), (args.sample,args.mode,args.profile,counts['dispatcher_calls'],'history cost',i)
                        counts['history_cost_comparisons'] += 1
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
                pool_sizes=dict(sorted(pool_sizes.items())),retained_counts=dict(sorted(retained_counts.items())),
                filter_controls=dict(sorted(filter_controls.items())),repair_events=dict(sorted(repair_events.items())),
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
    owned_filter = owned_filter_checks(args.dll)
    owned_history = owned_history_checks(args.dll)
    owned_source = owned_peak_source_checks(args.dll)
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
    sources = ('verify_vqm_candidate_peak_source.py','frm2_candidate_peak_source.py',
               'verify_vqm_candidate_peak_source_owned.py','frm2_fft_peaks.py','verify_vqm_candidate_history_state.py','frm2_candidate_history_state.py',
               'verify_vqm_candidate_history_owned.py',
               'verify_vqm_candidate_peak_filter.py','frm2_candidate_peak_filter.py',
               'verify_vqm_candidate_production.py','frm2_candidate_production.py',
               'verify_vqm_candidate_reference_cache.py','frm2_peak_candidate_score.py','frm2_candidate_reference_cache.py',
               'verify_vqm_candidate_gate.py','frm2_candidate_gate.py','frm2_candidate_history_cost.py',
               'verify_vqm_candidate_confidence_protocol.py','frm2_candidate_confidence.py','probe_frm2_batch_outputs.py',
               'verify_vqm_mode5_pcm.py','verify_vqm_alternate_score_paths.py','verify_vqm_bit16_spectral_power.py',
               'verify_vqm_bit16_peak_interpolation.py','audit_frm2_numeric.py','audit_vqm_v4_module_leads.py','audit_vqm_feature_calls_cfg.py')
    seeds = Counter()
    pool_sizes,retained_counts = Counter(),Counter()
    filter_controls = Counter()
    repair_events = Counter()
    for row in rows:
        seeds.update(row['seeds'])
        pool_sizes.update(row['pool_sizes'])
        retained_counts.update(row['retained_counts'])
        filter_controls.update(row['filter_controls'])
        repair_events.update(row['repair_events'])
    report = dict(schema=1,dse_sha256=SHA256,
        source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        reviewed_contract=static,owned_score_checks=owned,owned_filter_checks=owned_filter,
        owned_history_checks=owned_history,owned_peak_source_checks=owned_source,
        cases=rows,summary=dict(cases=len(rows),frames=sum(row['frames'] for row in rows),
            controls={name:sum(row['controls'][name] for row in rows) for name in rows[0]['controls']},seeds=dict(sorted(seeds.items())),
            pool_sizes=dict(sorted(pool_sizes.items())),retained_counts=dict(sorted(retained_counts.items())),
            filter_controls=dict(sorted(filter_controls.items())),repair_events=dict(sorted(repair_events.items())),
            bit17_nonzero=sum(row['bit17_nonzero'] for row in rows),nonzero_array_frames=sum(row['nonzero_array_frames'] for row in rows)),
        scope='Independent scalar-spectrum to original peaks, filter, candidates, history repair/state and gate/confidence; no native peak/filter/candidate/cache/history/state input',
        limits=['Scalar spectrum and preceding feature/PCM production remain native inputs; peak phase is excluded',
                'Normal batches use default linear interpolation, disabled detector post-filter and constant curves3/2',
                'Refreshed search bounds and prior-frame feature inputs remain native; state feedback is ordinary repair only',
                'Reviewed constant eligibility curves26/27 and history20/delay1; extended repair and reanalysis flags0 only',
                'Original maker scores, bit17 and zero arrays remain unresolved',
                'No commercial payloads or proprietary code/decompilation persisted'],
        installed_files_unchanged=True,no_windows_temp=True)
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__ == '__main__':
    main()
