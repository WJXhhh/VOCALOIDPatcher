"""Build and validate an offline bank privately, then publish one directory.

Calls the existing assembly/plan/tree/finalizer/public-parser/native-loader
chain. Failed attempts retain diagnostics under a sibling private directory;
the requested output is absent until every gate succeeds. Retry rebuilds from
validated inputs, rather than trusting partial output as a checkpoint.
"""
from __future__ import annotations

import argparse
import ctypes as c
from ctypes import wintypes as w
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid

import analyze_recording_units as analysis
from audit_frm2_numeric import SHA256

TOOLS=Path(__file__).resolve().parent
TREE_DLL=TOOLS/'tree_harness/bin/Release/net8.0-windows/TreeHarness.dll'
CHAIN_SOURCES=('build_planned_bank.py','analyze_recording_units.py','assemble_recording_units_ddb.py',
    'build_unit_ddb.py','plan_ddi_tree.py','finalize_planned_ddi.py','verify_planned_ddi.py',
    'finalize_stationary_ddi.py','finalize_minimal_articulation_ddi.py','pitch2_reference.py',
    'probe_frm2.py','probe_sms2.py','validate_main_sms2.py')


class BuildFailure(Exception):
    pass


class BasicLimits(c.Structure):
    _fields_=[('ProcessTime',c.c_int64),('JobTime',c.c_int64),('Flags',w.DWORD),
        ('MinWorkingSet',c.c_size_t),('MaxWorkingSet',c.c_size_t),('ActiveProcesses',w.DWORD),
        ('Affinity',c.c_size_t),('Priority',w.DWORD),('Scheduling',w.DWORD)]


class ExtendedLimits(c.Structure):
    _fields_=[('Basic',BasicLimits),('Io',c.c_uint64*6),('ProcessMemory',c.c_size_t),
        ('JobMemory',c.c_size_t),('PeakProcessMemory',c.c_size_t),('PeakJobMemory',c.c_size_t)]


class MemoryCounters(c.Structure):
    _fields_=[('cb',w.DWORD),('PageFaults',w.DWORD)]+[(name,c.c_size_t) for name in
        ('PeakWorkingSet','WorkingSet','PeakPaged','Paged','PeakNonPaged','NonPaged','Pagefile','PeakPagefile')]


class ProcessJob:
    def __init__(self,memory_limit_mib):
        self.kernel=c.WinDLL('kernel32',use_last_error=True)
        k=self.kernel
        k.CreateJobObjectW.argtypes,k.CreateJobObjectW.restype=[c.c_void_p,w.LPCWSTR],w.HANDLE
        k.SetInformationJobObject.argtypes,k.SetInformationJobObject.restype=[w.HANDLE,c.c_int,c.c_void_p,w.DWORD],w.BOOL
        k.QueryInformationJobObject.argtypes,k.QueryInformationJobObject.restype=[w.HANDLE,c.c_int,c.c_void_p,w.DWORD,c.c_void_p],w.BOOL
        k.AssignProcessToJobObject.argtypes,k.AssignProcessToJobObject.restype=[w.HANDLE,w.HANDLE],w.BOOL
        k.TerminateJobObject.argtypes,k.TerminateJobObject.restype=[w.HANDLE,w.UINT],w.BOOL
        k.CloseHandle.argtypes,k.CloseHandle.restype=[w.HANDLE],w.BOOL
        self.handle=k.CreateJobObjectW(None,None)
        if not self.handle:raise c.WinError(c.get_last_error())
        limits=ExtendedLimits();limits.Basic.Flags=0x2000 # KILL_ON_JOB_CLOSE
        if memory_limit_mib:
            limits.Basic.Flags|=0x100 # PROCESS_MEMORY
            limits.ProcessMemory=memory_limit_mib*1024*1024
        if not k.SetInformationJobObject(self.handle,9,c.byref(limits),c.sizeof(limits)):
            error=c.WinError(c.get_last_error());self.close();raise error

    def assign(self,process):
        if not self.kernel.AssignProcessToJobObject(self.handle,int(process._handle)):
            if process.poll() is None:raise c.WinError(c.get_last_error())

    def peaks(self):
        limits=ExtendedLimits()
        if not self.kernel.QueryInformationJobObject(self.handle,9,c.byref(limits),c.sizeof(limits),None):raise c.WinError(c.get_last_error())
        return dict(peak_process_commit_bytes=limits.PeakProcessMemory,peak_job_commit_bytes=limits.PeakJobMemory)

    def terminate(self):
        if not self.kernel.TerminateJobObject(self.handle,2):raise c.WinError(c.get_last_error())

    def close(self):
        if self.handle:self.kernel.CloseHandle(self.handle);self.handle=None


def run_process(command,log,environment,timeout,memory_limit_mib=0):
    job=ProcessJob(memory_limit_mib);process=None;start=time.perf_counter();peak_rss=0;timed_out=False
    psapi=c.WinDLL('psapi',use_last_error=True)
    psapi.GetProcessMemoryInfo.argtypes,psapi.GetProcessMemoryInfo.restype=[w.HANDLE,c.c_void_p,w.DWORD],w.BOOL
    try:
        with log.open('xb') as output:
            process=subprocess.Popen(command,stdout=output,stderr=subprocess.STDOUT,env=environment)
            job.assign(process)
            while True:
                counters=MemoryCounters();counters.cb=c.sizeof(counters)
                if psapi.GetProcessMemoryInfo(int(process._handle),c.byref(counters),c.sizeof(counters)):
                    peak_rss=max(peak_rss,counters.PeakWorkingSet)
                if process.poll() is not None:break
                if time.perf_counter()-start>timeout:
                    timed_out=True;job.terminate();process.wait();break
                time.sleep(.05)
        return dict(exit_code=process.returncode,timed_out=timed_out,elapsed_seconds=time.perf_counter()-start,
            observed_peak_working_set_bytes=peak_rss,working_set_poll_interval_seconds=.05,
            **job.peaks())
    finally:
        if process is not None and process.poll() is None:
            job.terminate();process.wait()
        job.close()


def worker(request,phase,stage):
    import assemble_recording_units_ddb as assembly
    import finalize_planned_ddi as finalize
    import plan_ddi_tree as planner
    import verify_planned_ddi as verifier
    bank=stage/'bank';stem=request['stem'];ddb=bank/(stem+'.ddb');manifest=bank/'ddb_manifest.json'
    if phase=='assemble':assembly.build(*(Path(request[k]) for k in ('unit_manifest','analysis_manifest','unit_root','analysis_root')),bank,stem)
    elif phase=='plan':planner.build(Path(request['graph']),manifest,stage/'tree_plan.json')
    elif phase=='finalize':finalize.build(stage/'tree_plan.json',manifest,stage/'skeleton'/(stem+'.tree'),ddb,
        ddb.with_suffix('.ddi'),bank/'ddi_manifest.json',0.,.6,90.)
    elif phase=='verify':
        saved=json.loads((bank/'ddi_manifest.json').read_text(encoding='utf-8'))
        if analysis.file_sha256(ddb.with_suffix('.ddi'))!=saved['output']['ddi_sha256']:
            raise BuildFailure('DDI changed after finalization')
        analysis.write_json_atomic(stage/'public_verification.json',verifier.verify(
            stage/'tree_plan.json',manifest,ddb.with_suffix('.ddi'),ddb,Path(request['parser_root'])))
    else:raise BuildFailure('unsupported phase')


def build(request,output,timeout=1800,memory_limit_mib=0):
    if os.name!='nt':raise BuildFailure('Windows process supervision is required')
    output=output.resolve()
    if output.exists():raise BuildFailure('output already exists; no files were changed')
    if re.fullmatch(r'[A-Za-z0-9_-]+',request['stem']) is None:raise BuildFailure('invalid bank stem')
    for key in ('unit_manifest','analysis_manifest','graph','dll','parser_root','unit_root','analysis_root'):
        request[key]=str(Path(request[key]).resolve())
        if not Path(request[key]).exists():raise BuildFailure('missing input: '+key)
    if analysis.file_sha256(Path(request['dll']))!=SHA256:raise BuildFailure('unsupported DSE SHA-256')
    if not TREE_DLL.is_file():raise BuildFailure('build TreeHarness Release before running this tool')
    source_hashes={name:analysis.file_sha256(TOOLS/name) for name in CHAIN_SOURCES}
    output.parent.mkdir(parents=True,exist_ok=True)
    stage=output.parent/('.'+output.name+'.build-'+uuid.uuid4().hex)
    assert stage.parent.resolve()==output.parent
    stage.mkdir()
    request_path=stage/'build_request.json';analysis.write_json_atomic(request_path,request)
    state=dict(format='vocaloid-offline-bank-build-state-v1',status='building',phases={},stage=None)
    state_path=stage/'build_state.json';analysis.write_json_atomic(state_path,state)
    try:
        for phase in ('assemble','plan','tree','finalize','verify','native_load'):
            state['stage']=phase;analysis.write_json_atomic(state_path,state)
            environment={k:v for k,v in os.environ.items() if not k.upper().startswith('TREE_HARNESS_')}
            if phase in ('tree','native_load'):
                if phase=='native_load':environment['TREE_HARNESS_LOAD_EXISTING']='1'
                target=stage/('skeleton' if phase=='tree' else 'bank')
                command=['dotnet',str(TREE_DLL),request['dll'],str(target),request['stem'],'--plan',str(stage/'tree_plan.json')]
            else:command=[sys.executable,'-B',str(Path(__file__).resolve()),'--worker',phase,'--request',str(request_path)]
            metrics=run_process(command,stage/(phase+'.log'),environment,timeout,memory_limit_mib)
            state['phases'][phase]=metrics;analysis.write_json_atomic(state_path,state)
            print(json.dumps(dict(stage=phase,**metrics)),flush=True)
            if metrics['timed_out'] or metrics['exit_code']!=0:raise BuildFailure('stage failed: '+phase)
            if phase=='native_load' and 'load.valid=True' not in (stage/'native_load.log').read_text(encoding='utf-8'):
                raise BuildFailure('native loader did not confirm validity')
        bank=stage/'bank';stem=request['stem']
        public=json.loads((stage/'public_verification.json').read_text(encoding='utf-8'))
        saved=json.loads((bank/'ddi_manifest.json').read_text(encoding='utf-8'))
        output_hashes={suffix:analysis.file_sha256(bank/(stem+suffix)) for suffix in ('.ddi','.ddb')}
        if output_hashes!={'.ddi':saved['output']['ddi_sha256'],'.ddb':saved['source']['ddb_sha256']}:
            raise BuildFailure('bank changed after finalization or verification')
        if source_hashes!={name:analysis.file_sha256(TOOLS/name) for name in CHAIN_SOURCES}:
            raise BuildFailure('build sources changed during this attempt')
        report=dict(format='vocaloid-offline-verified-bank-build-v1',status='verified',
            summary=dict(public_parser_valid=public['summary']['public_parser_valid'],native_loader_valid=True,
                coverage_complete=public['summary']['coverage_complete'],approval_complete=False),
            output={suffix:dict(name=stem+suffix,bytes=(bank/(stem+suffix)).stat().st_size,
                sha256=output_hashes[suffix]) for suffix in ('.ddi','.ddb')},
            phases=state['phases'],source_sha256=source_hashes,
            dse_sha256=SHA256,tree_harness_sha256=analysis.file_sha256(TREE_DLL),
            parser_sha256=analysis.file_sha256(Path(request['parser_root'])/'utils/ddi_utils.py'),
            limitations=['Offline structure/loader acceptance; no Editor, licensing, recording quality or acoustic approval.',
                'Retry rebuilds from validated inputs; no per-unit checkpoint/resume.',
                'Working-set peaks are observed OS high-water counters; the final unsampled tail is not guaranteed.',
                'Job peak commit is the OS-maintained process/job high-water counter.'])
        analysis.write_json_atomic(bank/'build_manifest.json',report)
        # Whole validated bank directory is the publication boundary. On Windows
        # rename refuses an existing destination, including a concurrent winner.
        if bank.parent.resolve()!=stage.resolve() or output.parent!=stage.parent.resolve():raise BuildFailure('publication path changed')
        if output.exists():raise BuildFailure('another build published this output')
        bank.rename(output)
        # Rename is the commit point. Failure to update the private bookkeeping
        # must not turn an already verified, published bank into a failed build.
        state['status']='published';state['stage']='published'
        try:analysis.write_json_atomic(state_path,state)
        except (OSError,KeyboardInterrupt):
            print('Bank published; private bookkeeping update was interrupted.',file=sys.stderr)
        return report
    except BaseException as error:
        state['status']='failed';state['error_type']=type(error).__name__
        try:analysis.write_json_atomic(state_path,state)
        except OSError:pass
        print('Private failed-attempt diagnostics retained: '+str(stage),file=sys.stderr)
        raise


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--worker',choices=('assemble','plan','finalize','verify'))
    parser.add_argument('--request',type=Path)
    for name in ('unit-manifest','analysis-manifest','unit-root','analysis-root','graph','dll','parser-root','output'):
        parser.add_argument('--'+name,type=Path)
    parser.add_argument('--stem',default='ResearchVoice')
    parser.add_argument('--stage-timeout',type=float,default=1800.)
    parser.add_argument('--memory-limit-mib',type=int,default=0)
    args=parser.parse_args()
    try:
        if args.worker:
            if not args.request:raise BuildFailure('worker requires a request')
            worker(json.loads(args.request.read_text(encoding='utf-8')),args.worker,args.request.parent);return 0
        if not math_is_positive(args.stage_timeout) or args.memory_limit_mib<0:raise BuildFailure('invalid resource limit')
        keys=('unit_manifest','analysis_manifest','unit_root','analysis_root','graph','dll','parser_root')
        if args.output is None or any(getattr(args,k) is None for k in keys):raise BuildFailure('all build inputs and output are required')
        report=build({**{k:str(getattr(args,k)) for k in keys},'stem':args.stem},args.output,args.stage_timeout,args.memory_limit_mib)
        return 0 if report['summary']['coverage_complete'] else 3
    except (Exception,KeyboardInterrupt) as error:
        print(f'error: {type(error).__name__}: {error}',file=sys.stderr);return 2


def math_is_positive(value):
    import math
    return math.isfinite(value) and value>0.


if __name__=='__main__':raise SystemExit(main())
