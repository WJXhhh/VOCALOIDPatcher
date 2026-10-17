"""Verify mode5's real confidence writer against independent cache moments.

Observe owned DRS instances after their normal steps; no original output, cache
or frame is replaced. Commercial PCM/cache arrays stay in process memory only.
"""
from __future__ import annotations

import argparse
import ctypes as c
import hashlib
import importlib
import json
import math
import os
from pathlib import Path
import statistics
import subprocess
import sys
import traceback

from audit_frm2_numeric import Native,P,Q,I,F,D,B,SHA256,f32
from frm2_mode5_confidence import interval,confidence
from probe_frm2_batch_outputs import run as analyze_batch
from verify_vqm_alternate_score_paths import frames as final_frames
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit16_peak_interpolation import auxiliary_sequences,metrics
from audit_vqm_v4_module_leads import PE
from audit_vqm_feature_calls_cfg import runtime_groups


def review_writer(dll,decoder_dir):
    data=dll.read_bytes();assert hashlib.sha256(data).hexdigest()==SHA256
    pe=PE(data);groups,_=runtime_groups(data,pe)
    sys.path.insert(0,str(decoder_dir.resolve()))
    cs=importlib.import_module('capstone');assert cs.__version__=='5.0.3'
    decoder=cs.Cs(cs.CS_ARCH_X86,cs.CS_MODE_64);decoder.detail=True
    def decode(root):
        result=[]
        for start,end,_ in groups[root]:
            result+=list(decoder.disasm(data[pe.offset(start):pe.offset(start)+end-start],pe.image_base+start))
        return {op.address-pe.image_base:op for op in result}
    caller=decode(0xd2cc0)
    op=caller[0xd600f]
    assert op.group(cs.CS_GRP_CALL) and op.operands[0].type==cs.x86.X86_OP_IMM
    assert op.operands[0].imm-pe.image_base==0xf4170
    op=caller[0xd601b]
    assert op.mnemonic=='movss' and op.operands[0].type==cs.x86.X86_OP_MEM
    assert op.operands[0].mem.disp==0x14 and op.reg_name(op.operands[1].reg)=='xmm0'
    ratio=decode(0xf6220)
    op=ratio[0xf6323]
    assert op.mnemonic=='cmp' and op.operands[1].type==cs.x86.X86_OP_IMM and op.operands[1].imm==4
    branch=ratio[0xf6325]
    assert branch.mnemonic=='je' and branch.operands[0].imm-pe.image_base==0xf6336
    assert ratio[0xf6327].operands[0].imm-pe.image_base==0xf7eb0
    leaf=decode(0xf4170)
    assert leaf[0xf4232].operands[0].imm-pe.image_base==0xf3ce0
    return dict(aligned_direct_confidence_call=True,aligned_return_value_store=True,
        ratio_mode4_branch_skips_call=True,missing_cache_calls_score_helper=True,
        confidence_leaf_decoded_instructions=len(leaf),decoder_version=cs.__version__)


def owned_checks(dll):
    native=Native(dll);check=native.fn(0xf4170,F,P)
    count=0;above_one=0
    for size in (16,33,65,128,257):
        for bounds in ((0.,0.),(40.,1000.),(96.,2200.),(20.,11025.)):
            lo,hi=interval(size,1024,11025.,*bounds)
            for shape in range(6):
                if shape==0:values=[0.]*size
                elif shape==1:values=[f32(.25)]*size
                elif shape==2:values=[f32(20*math.sin(i*.23)+i*.03) for i in range(size)]
                elif shape==3:values=[f32(1000*math.sin(i*.13)) for i in range(size)]
                elif shape==4:values=[f32((-1)**i*(i%11)*.4) for i in range(size)]
                else:values=[f32(i*i*.07-13) for i in range(size)]
                obj=native.alloc(0x520);scores=native.array(F,values+[12345.]*8)
                flags=native.array(B,[1]*size+[0xa5]*32)
                P.from_address(obj+0x3e0).value=scores;P.from_address(obj+0x3e8).value=flags
                I.from_address(obj+0x3c0).value=size;I.from_address(obj+0x324).value=1024
                F.from_address(obj+0x328).value=11025.
                F.from_address(obj+0x34c).value=bounds[0];F.from_address(obj+0x350).value=bounds[1]
                before=[c.string_at(obj,0x520),c.string_at(scores,(size+8)*4),c.string_at(flags,size+32)]
                actual=check(obj);predicted=confidence(values,lo,hi)
                assert actual==predicted,(size,bounds,shape,actual,predicted)
                assert before==[c.string_at(obj,0x520),c.string_at(scores,(size+8)*4),c.string_at(flags,size+32)]
                count+=1;above_one+=actual>1
    assert above_one>0
    return dict(cases=count,exact=count,above_one=above_one,inputs_and_guards_unchanged=True,
        scope='Owned fully populated cache and finite nonempty ranges; native4170 versus independent moments. '
              'This checks both boundary protocols and uncapped confidence, not PCM-to-cache production.')


def child(args,sequence=None,parser_hash=None,ddi_hash=None):
    reference=(sequence.get('pitch1',statistics.median(row['f0'] for row in sequence['frames']))
               if sequence else args.frequency)
    captured={};contracts=[];calls=0;finished=[]
    def setup(native,ctx):
        score_object=P.from_address(ctx+0xc70).value
        other_object=P.from_address(ctx+0xca0).value
        config=P.from_address(ctx+0xad0).value
        assert score_object and other_object and B.from_address(config+0x558).value==5
        # The absolute-value ratio leaf is gated by a separate object's mode.
        # Do not confuse config+558 with this internal selector.
        internal_mode=B.from_address(other_object+0x3c0).value
        assert internal_mode==4
        contracts.append(dict(outer_analysis_mode=5,ratio_object_internal_mode=internal_mode,
            confidence_object_offset='0xc70',ratio_object_offset='0xca0'))
        table=P.from_address(ctx).value
        original=c.CFUNCTYPE(Q,P)(P.from_address(table+8).value)
        private=native.alloc(0x100);c.memmove(private,table,0x100)
        def step(ctx):
            nonlocal calls
            try:
                calls+=1;assert calls<10000
                result=original(ctx)
                if result&0xffffffff:
                    return result
                frame=P.from_address(ctx+0xcc8).value
                if not frame or D.from_address(frame+0x48).value<0:
                    return result
                time=D.from_address(frame+0x48).value;at=round(time*44100/256)
                assert abs(time*44100/256-at)<1e-5 and at not in captured
                feature=P.from_address(frame+0x128).value
                count=I.from_address(score_object+0x3c0).value
                bins=I.from_address(score_object+0x324).value
                rate=F.from_address(score_object+0x328).value
                low=F.from_address(score_object+0x34c).value
                high=F.from_address(score_object+0x350).value
                assert feature and 4<=count<=8192
                start,stop=interval(count,bins,rate,low,high)
                array=P.from_address(score_object+0x3e0).value
                flags=P.from_address(score_object+0x3e8).value
                assert array and flags
                assert all(B.from_address(flags+i).value for i in range(start,stop))
                source=[0.]*start+[F.from_address(array+4*i).value for i in range(start,stop)]
                predicted=confidence(source,start,stop)
                actual=F.from_address(feature+0x14).value
                assert math.isfinite(actual) and predicted==actual,(at,predicted,actual)
                captured[at]=dict(error=predicted-actual,actual=actual,
                    geometry=(count,bins,rate,low,high,start,stop),
                    ready_cache_slots=stop-start)
                return result
            except BaseException:
                traceback.print_exc();sys.stderr.flush();os._exit(2)
        callback=c.CFUNCTYPE(Q,P)(step)
        P.from_address(private+8).value=c.cast(callback,P).value
        P.from_address(ctx).value=private;native.keep.extend([original,callback])
    def finish(native,ctx,collection):
        finished.append(final_frames(native,ctx,collection))
    options=dict(analysis_mode=5,result_observer=finish)
    if sequence:
        options['pcm_override']=sequence['pcm']
    observed=analyze_batch(args.dll,1,1,args.shape,reference,setup_observer=setup,**options)
    baseline=analyze_batch(args.dll,1,1,args.shape,reference,**options)
    assert observed==baseline and len(finished)==2 and finished[0]==finished[1]
    final=finished[0];assert set(captured)==set(final)==set(range(observed['frames']))
    assert all(captured[at]['actual']==final[at]['values'][4] for at in final)
    if sequence:
        assert len(final)==len(sequence['frames'])
    geometry=sorted({row['geometry'] for row in captured.values()})
    saved_errors=([captured[at]['actual']-sequence['frames'][at]['features'][4]
                   for at in range(12,len(final)-12)] if sequence else None)
    return dict(case=sequence.get('category',args.sample) if sequence else f'{args.shape}_{args.frequency:g}',
        frames=len(final),calls=calls,contract=contracts[0],geometries=geometry,
        independent_to_native=metrics([row['error'] for row in captured.values()]),
        scores_consumed=sum(row['ready_cache_slots'] for row in captured.values()),
        ready_cache_flags_verified=True,selected_candidate_not_an_input=True,
        observed_matches_fresh_baseline=True,stage_value_survives_to_output=True,
        saved_confidence_comparison=metrics(saved_errors) if saved_errors is not None else None,
        parser_sha256=parser_hash,ddi_sha256=ddi_hash)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('dll','manifest','voice-root','ddb-tools','aux-vdb','decoder-dir'):
        parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--output',type=Path)
    parser.add_argument('--child',action='store_true');parser.add_argument('--sample',type=int,choices=range(5))
    parser.add_argument('--shape',choices=('steady','attack'),default='steady')
    parser.add_argument('--frequency',type=float,default=220.)
    args=parser.parse_args()
    if args.child:
        if args.sample is None:
            report=child(args)
        else:
            parser_hash,ddi_hash,sequences=load_sequences(args.manifest,args.voice_root,args.ddb_tools)
            sequences+=auxiliary_sequences(args.aux_vdb)
            report=child(args,sequences[args.sample],parser_hash,ddi_hash)
        print(json.dumps(report));return
    assert args.output
    writer_review=review_writer(args.dll,args.decoder_dir)
    owned=owned_checks(args.dll);rows=[]
    specifications=[dict(shape=shape,frequency=frequency) for shape in ('steady','attack')
                    for frequency in (110.,220.,440.)]+[dict(sample=i) for i in range(5)]
    for spec in specifications:
        command=[sys.executable,'-B',str(Path(__file__).resolve()),'--child']
        for name in ('dll','manifest','voice-root','ddb-tools','aux-vdb','decoder-dir'):
            command+=['--'+name,str(getattr(args,name.replace('-','_')))]
        for name,value in spec.items():command+=['--'+name,str(value)]
        result=subprocess.run(command,capture_output=True,text=True,timeout=120)
        assert result.returncode==0,result.stderr[-2000:]
        row=json.loads(result.stdout);rows.append(row)
        print(json.dumps(dict(case=row['case'],frames=row['frames'],max_error=row['independent_to_native']['max_absolute_error'])),flush=True)
    sources=('verify_vqm_mode5_confidence.py','frm2_mode5_confidence.py','probe_frm2_batch_outputs.py',
        'verify_vqm_alternate_score_paths.py','verify_vqm_bit16_spectral_power.py',
        'verify_vqm_bit16_peak_interpolation.py','audit_frm2_numeric.py',
        'audit_vqm_v4_module_leads.py','audit_vqm_feature_calls_cfg.py')
    report=dict(schema=1,dse_sha256=SHA256,
        auxiliary_sha256=hashlib.sha256(args.aux_vdb.read_bytes()).hexdigest(),
        source_sha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest() for name in sources},
        owned_checks=owned,writer_review=writer_review,cases=rows,summary=dict(cases=len(rows),frames=sum(row['frames'] for row in rows),
            scores_consumed=sum(row['scores_consumed'] for row in rows),
            exact=sum(row['independent_to_native']['within_1e_6'] for row in rows)),
        actual_writer=dict(caller_rva='0xd2cc0',confidence_call_rva='0xd600f',callee_rva='0xf4170',
            feature_store_rva='0xd601b',feature_offset='0x14',context_object_offset='0xc70'),
        scope='Mode5 automatic confidence from fully prepared live cache; independent float32 moments '
            'and range conversion, natural owned/installed PCM and unchanged final state versus fresh baseline.',
        limits=['PCM-to-cache production still native; not an independent full confidence producer from PCM',
            'Only ordinary default mode5 protocol; other algorithms, settings and maker inputs not inferred',
            'A fixed cache makes selected candidate irrelevant to this moment statistic; changing upstream '
                'analysis or frequency range may change cache and is not excluded',
            'Saved confidence compared only on interior frames; original maker rule still unverified',
            'No commercial cache arrays, PCM, proprietary code bytes or disassembly persisted'],
        installed_files_unchanged=True,no_windows_temp=True)
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__=='__main__':
    main()
