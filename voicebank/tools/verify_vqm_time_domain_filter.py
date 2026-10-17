"""Produce normal mode-2 filtered PCM before DSE loads and verify its consumer.

The existing candidate verifier is reused without changing its fixed sources.
All commercial PCM, filter results and stage memories remain in memory.
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

import verify_vqm_candidate_geometry as base
from audit_frm2_numeric import P,I,F,D,Q,f32,SHA256
from frm2_candidate_pcm_source import RING_SIZE
from frm2_mode5_math_backend import module_loaded
from frm2_time_domain_filter import prepare_filter,COEFFICIENTS
from verify_vqm_time_domain_filter_owned import owned_filter_checks


class FilterObserver:
    def __init__(self, pcm):
        assert not module_loaded('DSE.dll')
        self.pcm = [f32(value) for value in pcm]
        self.values,self.states,self.clipped = prepare_filter(self.pcm)
        self.packed = bytes((D*len(self.values))(*self.values))
        self.raw = bytes((F*len(self.pcm))(*self.pcm))
        assert not module_loaded('DSE.dll')
        self.total = 0
        self.counts = dict(before_step_calls=0,new_input_blocks=0,raw_values_byte_exact=0,
            filtered_values_byte_exact=0,stage_state_values_byte_exact=0,
            physical_wrap_pushes=0,tail_zero_input_values=0,tail_nonzero_filter_values=0,
            clipped_compared_values=0,full_ring_values_byte_exact=0)

    def check_input(self, context):
        time = D.from_address(context+0xaf8).value*44100.
        total = round(time)
        assert abs(time-total) < 1e-5
        assert self.total <= total <= len(self.values)
        assert I.from_address(context+0xaf4).value == total%RING_SIZE
        if total == self.total:
            return
        assert total in self.states,(total,'unreviewed push boundary')
        count = total-self.total
        assert 0 < count <= 256
        at = self.total%RING_SIZE
        first = min(count,RING_SIZE-at)
        raw = P.from_address(context+0xae0).value
        filtered = P.from_address(context+0xae8).value
        # Both expected buffers existed before the analysis DLL was loaded.
        raw_expected = self.raw[self.total*4:min(total,len(self.pcm))*4]
        raw_expected += bytes(count*4-len(raw_expected))
        expected = self.packed[self.total*8:total*8]
        for pointer,stride,want in ((raw,4,raw_expected),(filtered,8,expected)):
            assert c.string_at(pointer+at*stride,first*stride) == want[:first*stride],(self.total,total,'first',stride)
            if first < count:
                assert c.string_at(pointer,(count-first)*stride) == want[first*stride:],(self.total,total,'wrap',stride)
        assert c.string_at(context+0xb28,96) == self.states[total],(self.total,total,'stage states')
        self.counts['new_input_blocks'] += 1
        self.counts['raw_values_byte_exact'] += count
        self.counts['filtered_values_byte_exact'] += count
        self.counts['stage_state_values_byte_exact'] += 12
        self.counts['physical_wrap_pushes'] += first < count
        self.counts['tail_zero_input_values'] += max(0,total-max(self.total,len(self.pcm)))
        self.counts['tail_nonzero_filter_values'] += sum(value != 0 for value in self.values[max(self.total,len(self.pcm)):total])
        self.counts['clipped_compared_values'] += sum(abs(value) == 32768. for value in self.values[self.total:total])
        self.total = total

    def install(self, native, context):
        estimator = P.from_address(context+0xc90).value
        config = P.from_address(context+0xad0).value
        assert estimator and I.from_address(config+0x5a0).value == 0
        assert P.from_address(estimator+0x20).value == P.from_address(context+0xae8).value
        assert I.from_address(estimator+0x28).value == I.from_address(context+0xaf0).value == RING_SIZE
        assert c.string_at(context+0xb28,96) == bytes(96)
        table = P.from_address(context).value
        original = c.CFUNCTYPE(Q,P)(P.from_address(table+8).value)
        private = native.alloc(0x100)
        c.memmove(private,table,0x100)
        def step(ctx):
            try:
                self.counts['before_step_calls'] += 1
                assert self.counts['before_step_calls'] < 10000
                self.check_input(ctx)
                return original(ctx)
            except BaseException:
                traceback.print_exc()
                os._exit(2)
        callback = c.CFUNCTYPE(Q,P)(step)
        native.keep.extend((original,callback))
        P.from_address(private+8).value = c.cast(callback,P).value
        P.from_address(context).value = private

    def finish(self, context):
        self.check_input(context)
        assert self.total > len(self.pcm)
        expected = bytearray(RING_SIZE*8)
        start = max(0,self.total-RING_SIZE)
        position = start%RING_SIZE
        count = self.total-start
        first = min(count,RING_SIZE-position)
        expected[position*8:(position+first)*8] = self.packed[start*8:(start+first)*8]
        if first < count:
            expected[:(count-first)*8] = self.packed[(start+first)*8:self.total*8]
        assert c.string_at(P.from_address(context+0xae8).value,RING_SIZE*8) == expected
        self.counts['full_ring_values_byte_exact'] += RING_SIZE

    def report(self):
        return dict(active=True,producer_dse_absent=True,precomputed_values=len(self.values),
            precomputed_state_boundaries=len(self.states),precomputed_clipped_values=self.clipped,
            actual_input_values=self.total,controls=self.counts,
            raw_and_filtered_inputs_match=True,full_final_ring_matches=True)


def child(args):
    frequency = args.frequency
    pcm = None
    if args.sample is not None:
        _,_,sequences = base.load_sequences(args.manifest,args.voice_root,args.ddb_tools)
        sequences += base.auxiliary_sequences(args.aux_vdb)
        sequence = sequences[args.sample]
        pcm = sequence['pcm']
        frequency = sequence.get('pitch1',statistics.median(row['f0'] for row in sequence['frames']))
    pcm = base.owned_signal(args.shape,frequency) if pcm is None else pcm
    if args.mode == 1:
        row = base.child(args,pcm,frequency)
        row['time_filter'] = dict(active=False,reason='No time-domain estimator in mode1')
        return row
    observer = FilterObserver(pcm)
    def configured_batch(profile):
        batch = base.configured_batch(profile)
        def observed(*arguments,**options):
            old_setup = options.get('setup_observer')
            if old_setup is not None:
                old_finish = options['result_observer']
                def setup(native,context):
                    old_setup(native,context)
                    observer.install(native,context)
                def finish(native,context,collection):
                    observer.finish(context)
                    old_finish(native,context,collection)
                options.update(setup_observer=setup,result_observer=finish)
            return batch(*arguments,**options)
        return observed
    independent_child = types.FunctionType(base.child.__code__,
        dict(base.child.__globals__,configured_batch=configured_batch),base.child.__name__,base.child.__defaults__)
    row = independent_child(args,pcm,frequency)
    row['time_filter'] = observer.report()
    return row


def review_filter(dll, decoder_dir):
    sys.path.insert(0,str(decoder_dir))
    import capstone as cs
    from audit_vqm_v4_module_leads import PE
    from audit_vqm_feature_calls_cfg import runtime_groups
    data = dll.read_bytes()
    assert hashlib.sha256(data).hexdigest() == SHA256
    pe = PE(data)
    groups,_ = runtime_groups(data,pe)
    decoder = cs.Cs(cs.CS_ARCH_X86,cs.CS_MODE_64)
    decoder.detail = True
    def decode(root):
        result = {}
        for start,end,_ in groups[root]:
            result.update({op.address-pe.image_base:op for op in decoder.disasm(
                data[pe.offset(start):pe.offset(start)+end-start],pe.image_base+start)})
        return result
    push,constructor = decode(0xd1140),decode(0xcdde0)
    for rva,value in COEFFICIENTS.items():
        assert data[pe.offset(rva):pe.offset(rva)+8] == struct.pack('<d',value)
    assert push[0xd15d5].operands[1].mem.disp == 0x5a0
    assert push[0xd15ef].operands[0].mem.disp == 0xc90
    assert push[0xd15e9].mnemonic == 'jne'
    assert push[0xd15f7].mnemonic == 'je'
    assert all(push[site].operands[0].imm-pe.image_base == 0xd2516 for site in (0xd15e9,0xd15f7))
    assert constructor[0xced87].operands[0].mem.disp == 0x20
    assert constructor[0xced87].operands[1].reg == cs.x86.X86_REG_R8
    assert constructor[0xced60].operands[1].mem.disp == 0xae8
    assert constructor[0xceee5].operands[0].mem.disp == 0xc90
    arithmetic = [op for address,op in push.items() if 0xd15ef <= address < 0xd2516]
    assert not any(op.mnemonic.startswith('vfm') for op in arithmetic)
    state_writes = sorted({operand.mem.disp for op in arithmetic if op.mnemonic == 'movsd'
        for operand in op.operands[:1] if operand.type == cs.CS_OP_MEM and 0xb28 <= operand.mem.disp <= 0xb80})
    assert state_writes == list(range(0xb28,0xb88,8))
    return dict(push_rva='0xd1140',filter_start='0xd15ef',filter_end='0xd2516',
        enabled_selector_offset='0x5a0',enabled_selector=0,requires_estimator_offset='0xc90',
        raw_ring_offset='0xae0',filtered_ring_offset='0xae8',estimator_input_offset='0x20',
        state_offsets=[hex(offset) for offset in state_writes],coefficients={hex(k):v for k,v in COEFFICIENTS.items()},
        arithmetic='Four recursive stages in double precision; final gain5 and +/-32768 clamp do not feed back',
        ring_size=RING_SIZE,rate=44100,normal_push_size=128,zero_tail_push_size=256)


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
    parser.add_argument('--profile',choices=base.PROFILES,default='default')
    args = parser.parse_args()
    if args.child:
        print(json.dumps(child(args)))
        return
    assert args.output
    previous = json.loads(Path('voicebank_doc/evidence/453_vqm_candidate_geometry.json').read_text(encoding='utf-8'))
    static = base.review_reference(args.dll,args.decoder_dir)
    filter_static = review_filter(args.dll,args.decoder_dir)
    owned_pcm = base.owned_pcm_source_checks(args.dll)
    owned_time = owned_filter_checks(args.dll)
    owned = base.owned_checks(args.dll)
    owned_filter = base.owned_filter_checks(args.dll)
    owned_history = base.owned_history_checks(args.dll)
    owned_source = base.owned_peak_source_checks(args.dll)
    def run_spec(spec):
        command = [sys.executable,'-B',str(Path(__file__).resolve()),'--child']
        for name in ('dll','manifest','voice-root','ddb-tools','aux-vdb','decoder-dir'):
            command += ['--'+name,str(getattr(args,name.replace('-','_')))]
        for name,value in spec.items():
            command += ['--'+name.replace('_','-'),str(value)]
        process = subprocess.run(command,capture_output=True,text=True,timeout=180)
        assert process.returncode == 0,process.stderr[-3000:]
        row = json.loads(process.stdout)
        print(json.dumps(dict(sample=row['sample'],shape=row['shape'],mode=row['mode'],profile=row['profile'],
            filtered_values=row['time_filter'].get('controls',{}).get('filtered_values_byte_exact',0))),flush=True)
        return row
    def repeat_cases(rows):
        result = []
        for old in rows:
            spec = {key:old[key] for key in ('shape','frequency','mode','profile')}
            if old['sample'] is not None:
                spec['sample'] = old['sample']
            row = run_spec(spec)
            assert all(row[key] == value for key,value in old.items()),(spec,'previous results changed')
            result.append(row)
        return result
    rows = repeat_cases(previous['cases'])
    boundary = repeat_cases(previous['owned_geometry_checks']['cases'])
    active = [row['time_filter'] for row in rows+boundary if row['time_filter']['active']]
    keys = active[0]['controls']
    summary = dict(cases=len(active),producer_dse_absent_cases=sum(row['producer_dse_absent'] for row in active),
        precomputed_values=sum(row['precomputed_values'] for row in active),
        actual_input_values=sum(row['actual_input_values'] for row in active),
        controls={key:sum(row['controls'][key] for row in active) for key in keys})
    assert summary['controls']['physical_wrap_pushes'] > 0
    assert summary['controls']['tail_nonzero_filter_values'] > 0
    sources = tuple(previous['source_sha256'])+('verify_vqm_time_domain_filter.py',
        'frm2_time_domain_filter.py','verify_vqm_time_domain_filter_owned.py')
    report = dict(previous)
    report.update(source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        reviewed_contract=static,time_filter_contract=filter_static,
        owned_score_checks=owned,owned_filter_checks=owned_filter,owned_history_checks=owned_history,
        owned_peak_source_checks=owned_source,owned_pcm_source_checks=owned_pcm,
        owned_time_filter_checks=owned_time,cases=rows,
        owned_geometry_checks=dict(previous['owned_geometry_checks'],cases=boundary),
        time_filter_summary=summary,
        scope='PCM independently produces the exact normal mode2 time-domain filtered ring and all12 stage memories before DSE loads; existing candidate/history/confidence results preserved',
        limits=list(previous['limits'])+[
            'The independent filtered PCM is verified at the actual time-domain consumer; event/period history and coarse pitch production remain native',
            'Only the fixed coefficient cascade, selector0 and stated44.1kHz normal batch protocol modeled; other rates or filter protocols not inferred'])
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(summary))


if __name__ == '__main__':
    main()
