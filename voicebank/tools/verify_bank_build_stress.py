"""Synthetic scale/pressure acceptance through the existing DDB/DDI chain.

All WAV/SMS2/tree/bank artifacts live in the explicit external work directory.
Repeated acoustic templates stress structure, I/O and allocation, not recording
diversity or analysis throughput. No Editor process or installed bank is used.
"""
from __future__ import annotations

import argparse
import copy
import ctypes as c
from ctypes import wintypes
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import wave
from array import array

import analyze_recording_units as analysis
import assemble_recording_units_ddb as assembly
import finalize_planned_ddi as finalize
import plan_ddi_tree as planner
import verify_planned_ddi as verifier
from audit_frm2_numeric import SHA256

TOOLS=Path(__file__).resolve().parent
TREE_DLL=TOOLS/'tree_harness/bin/Release/net8.0-windows/TreeHarness.dll'
STEM='StressBank'


def write_json(path,value):
    path.parent.mkdir(parents=True,exist_ok=True)
    analysis.write_json_atomic(path,value)


def seeds(root,dll):
    root.mkdir()
    result=[]
    count=128*256
    for layer,frequency in enumerate((220.,330.,440.)):
        path=root/f'layer{layer}.wav'
        values=array('h',(round(9000.*math.sin(math.tau*frequency*k/44100.)+
            1800.*math.sin(2*math.tau*frequency*k/44100.+.2)) for k in range(count)))
        with wave.open(str(path),'wb') as stream:
            stream.setnchannels(1);stream.setsampwidth(2);stream.setframerate(44100);stream.writeframes(values.tobytes())
        unit=dict(unit_id=f'STA_SEED_{layer:03}',kind='stationary',phoneme='p00',
            output_relative_wav=path.name,output_wav_sha256=analysis.file_sha256(path),
            approval_status='unapproved_extracted_candidate',builder_spec={'f0_hz':frequency},
            frame_alignment={'frame_count_estimate':128},wav=dict(sample_rate=44100,channels=1,
                bit_depth=16,frame_count=count,duration_seconds=count/44100.))
        source=analysis.preflight_unit(unit,root)
        sms2=root/f'layer{layer}.sms2'
        mode,f0,_,_=analysis.run_drs(dll,source,sms2)
        sta=analysis.validate_analysis(source,sms2,mode,f0)
        art_unit=copy.deepcopy(unit);art_unit.update(unit_id=f'ART_SEED_{layer:04}',kind='articulation',
            edge=['p00','p01'],voicing={'source':'voiced','target':'voiced'},role='synthetic')
        art_unit['builder_spec'].update(boundary_seconds=64*256/44100.,
            source_inner_seconds=[4*256/44100.,60*256/44100.],
            target_inner_seconds=[68*256/44100.,124*256/44100.])
        art=analysis.validate_analysis(analysis.preflight_unit(art_unit,root),sms2,mode,f0)
        result.append(dict(unit=unit,art_unit=art_unit,sta=sta,art=art,wav=str(path),sms2=str(sms2)))
    write_json(root/'seeds.json',result)
    return result


def prepare(root,seed_rows,stationaries,edges,layers):
    root.mkdir();(root/'units').mkdir();(root/'analysis/sms2').mkdir(parents=True)
    names=[f'p{k:02}' for k in range(61)]
    selected_edges=[(a,b) for a in names for b in names][:edges]
    art_units=[];sta_units=[];analyzed=[]
    for kind,groups in (('stationary',names[:stationaries]),('articulation',selected_edges)):
        for group_index,group in enumerate(groups):
            for layer in range(layers):
                seed=seed_rows[layer]
                unit=copy.deepcopy(seed['unit'] if kind=='stationary' else seed['art_unit'])
                unit_id=(f'STA_L{layer}_{group_index:03}' if kind=='stationary' else f'ART_L{layer}_{group_index:04}')
                unit.update(unit_id=unit_id,layer_id=f'L{layer}',output_relative_wav=unit_id+'.wav')
                if kind=='stationary':unit['phoneme']=group
                else:unit['edge']=list(group);unit.pop('phoneme',None)
                for source,target in ((seed['wav'],root/'units'/unit['output_relative_wav']),
                    (seed['sms2'],root/'analysis/sms2'/(unit_id+'.sms2'))):
                    try:os.link(source,target)
                    except OSError as error:
                        if getattr(error,'winerror',None)!=1142:raise
                        # NTFS caps links per file. Copies preserve identical
                        # inputs after the cap; this is fixture setup, not build I/O.
                        shutil.copyfile(source,target)
                item=copy.deepcopy(seed['sta'] if kind=='stationary' else seed['art'])
                item.update(unit_id=unit_id,input_relative_wav=unit['output_relative_wav'],
                    output_relative_sms2='sms2/'+unit_id+'.sms2')
                if kind=='stationary':item['phoneme']=group
                else:item['edge']=list(group);item['role']='synthetic'
                (sta_units if kind=='stationary' else art_units).append(unit)
                analyzed.append(item)
    units=art_units+sta_units
    unit_manifest=dict(format='vocaloid-extracted-recording-units-v1',
        summary=dict(articulation_units=len(art_units),stationary_units=len(sta_units),total_units=len(units),
            approval_complete=False,input_coverage_complete=True,unit_manifest_sha256=analysis.expected_manifest_digest(units)),
        articulation_units=art_units,stationary_units=sta_units)
    unit_path=root/'unit_manifest.json';write_json(unit_path,unit_manifest)
    # The analysis list may use a different order; IDs and hashes bind every unit.
    manifest=dict(format='vocaloid-recording-unit-drs-analysis-v1',
        source=dict(unit_manifest_sha256=analysis.file_sha256(unit_path),
            unit_manifest_canonical_sha256=unit_manifest['summary']['unit_manifest_sha256']),
        summary=dict(input_units=len(units),selected_units=len(units),articulation_units=len(art_units),
            stationary_units=len(sta_units),input_coverage_complete=True,selection_complete=True,
            analysis_complete=True,coverage_complete=True,approval_complete=False,
            analysis_manifest_sha256=analysis.analysis_manifest_digest(analyzed)),units=analyzed)
    write_json(root/'analysis/analysis_manifest.json',manifest)
    graph=dict(aggregate=dict(phonemes=dict(intersection=['Sil']+names,voiced_intersection=names,unvoiced_intersection=['Sil']),
        stationary={'intersection':names[:stationaries]},art={'intersection':[list(e) for e in selected_edges]}))
    write_json(root/'graph.json',graph)
    return dict(units=len(units),stationary_parts=len(sta_units),articulation_parts=len(art_units),
        art_edges=edges,layers=layers,frames=len(units)*128,phonemes=62)


def worker(phase,root,parser_root):
    ddb=root/'bank'/f'{STEM}.ddb';ddi=ddb.with_suffix('.ddi');manifest=root/'bank/ddb_manifest.json'
    if phase=='assemble':assembly.build(root/'unit_manifest.json',root/'analysis/analysis_manifest.json',root/'units',root/'analysis',root/'bank',STEM)
    elif phase=='plan':planner.build(root/'graph.json',manifest,root/'tree_plan.json')
    elif phase=='finalize':finalize.build(root/'tree_plan.json',manifest,root/'skeleton'/f'{STEM}.tree',ddb,ddi,root/'bank/ddi_manifest.json',0.,.6,90.)
    elif phase=='verify':write_json(root/'public_verification.json',verifier.verify(root/'tree_plan.json',manifest,ddi,ddb,parser_root))
    else:raise ValueError(phase)


class MemoryCounters(c.Structure):
    _fields_=[('cb',wintypes.DWORD),('PageFaultCount',wintypes.DWORD)]+[(n,c.c_size_t) for n in
        ('PeakWorkingSetSize','WorkingSetSize','QuotaPeakPagedPoolUsage','QuotaPagedPoolUsage',
         'QuotaPeakNonPagedPoolUsage','QuotaNonPagedPoolUsage','PagefileUsage','PeakPagefileUsage')]


def measured(command,log,timeout=1800,environment=None):
    start=time.perf_counter();peak_rss=peak_commit=0;timed_out=False
    psapi=c.WinDLL('psapi',use_last_error=True)
    psapi.GetProcessMemoryInfo.argtypes=[wintypes.HANDLE,c.c_void_p,wintypes.DWORD]
    psapi.GetProcessMemoryInfo.restype=wintypes.BOOL
    with log.open('wb') as output:
        process=subprocess.Popen(command,stdout=output,stderr=subprocess.STDOUT,env=environment)
        try:
            while True:
                counters=MemoryCounters();counters.cb=c.sizeof(counters)
                if psapi.GetProcessMemoryInfo(int(process._handle),c.byref(counters),c.sizeof(counters)):
                    peak_rss=max(peak_rss,counters.PeakWorkingSetSize);peak_commit=max(peak_commit,counters.PeakPagefileUsage)
                if process.poll() is not None:break
                if time.perf_counter()-start>timeout:
                    timed_out=True;process.kill();process.wait();break
                time.sleep(.05)
        finally:
            if process.poll() is None:process.kill();process.wait()
    result=dict(exit_code=process.returncode,elapsed_seconds=time.perf_counter()-start,
        peak_working_set_bytes=peak_rss,peak_commit_bytes=peak_commit,timed_out=timed_out)
    print(json.dumps(dict(phase=log.stem,**result)),flush=True)
    if timed_out or process.returncode:raise RuntimeError(f'Phase failed: {log.name}: {result}; see explicit work-root log')
    return result


def case(root,parser_root,dll):
    metrics={}
    for phase in ('assemble','plan','tree','finalize','verify','native_load'):
        env=None
        if phase in ('tree','native_load'):
            env={k:v for k,v in os.environ.items() if not k.startswith('TREE_HARNESS_')}
            target=root/('skeleton' if phase=='tree' else 'bank')
            if phase=='native_load':env['TREE_HARNESS_LOAD_EXISTING']='1'
            command=['dotnet',str(TREE_DLL),str(dll),str(target),STEM,'--plan',str(root/'tree_plan.json')]
        else:command=[sys.executable,'-B',str(Path(__file__).resolve()),'--worker',phase,'--case',str(root),'--parser-root',str(parser_root)]
        metrics[phase]=measured(command,root/(phase+'.log'),environment=env)
        if phase=='native_load':assert 'load.valid=True' in (root/'native_load.log').read_text(encoding='utf-8')
    ddb=json.loads((root/'bank/ddb_manifest.json').read_text());ddi=json.loads((root/'bank/ddi_manifest.json').read_text())
    result=dict(metrics=metrics,ddb_bytes=ddb['output']['ddb_bytes'],ddb_sha256=ddb['output']['ddb_sha256'],
        ddi_bytes=ddi['output']['ddi_bytes'],ddi_sha256=ddi['output']['ddi_sha256'],
        public_parser_valid=True,native_loader_valid=True)
    write_json(root/'case_result.json',result);return result


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--worker',choices=('assemble','plan','finalize','verify'))
    parser.add_argument('--case',type=Path);parser.add_argument('--parser-root',type=Path,required=True)
    parser.add_argument('--work-root',type=Path);parser.add_argument('--dll',type=Path)
    parser.add_argument('--profile',choices=('small','medium','full'),default='small')
    args=parser.parse_args()
    if args.worker:worker(args.worker,args.case,args.parser_root);return
    assert args.work_root and args.dll
    assert hashlib.sha256(args.dll.read_bytes()).hexdigest()==SHA256
    root=args.work_root.resolve();root.mkdir(parents=True,exist_ok=True)
    seed_root=root/'seeds'
    seed_rows=json.loads((seed_root/'seeds.json').read_text()) if seed_root.exists() else seeds(seed_root,args.dll)
    case_root=root/args.profile
    sizes={'small':(3,5,2),'medium':(38,300,3),'full':(38,2556,3)}
    shape=prepare(case_root,seed_rows,*sizes[args.profile])
    write_json(case_root/'shape.json',shape);print(json.dumps(shape),flush=True)
    result=case(case_root,args.parser_root,args.dll)
    print(json.dumps(dict(profile=args.profile,shape=shape,result=result)),flush=True)


if __name__=='__main__':main()
