"""Offline fault/retry acceptance for build_planned_bank; only owned fixtures.

Requires an existing small and medium synthetic profile from
verify_bank_build_stress.py. Never alters those inputs or installed libraries.
All fault copies, logs and retained attempts go under --work-root/recovery.
"""
from __future__ import annotations

import argparse
import contextlib
import copy
import ctypes as c
from ctypes import wintypes as w
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

import analyze_recording_units as analysis
import build_planned_bank as builder


def request(profile,dll,parser):
    return dict(unit_manifest=str(profile/'unit_manifest.json'),
        analysis_manifest=str(profile/'analysis/analysis_manifest.json'),
        unit_root=str(profile/'units'),analysis_root=str(profile/'analysis'),
        graph=str(profile/'graph.json'),dll=str(dll),parser_root=str(parser),stem='StressBank')


def command(req,output,extra=()):
    result=[sys.executable,'-B',str(Path(builder.__file__).resolve())]
    for key,value in req.items():result.extend(['--'+key.replace('_','-'),value])
    return result+['--output',str(output),*extra]


def snapshot(root):
    return {str(p.relative_to(root)):analysis.file_sha256(p) for p in root.rglob('*') if p.is_file()}


def attempt(output):
    matches=list(output.parent.glob('.'+output.name+'.build-*'))
    assert len(matches)==1,(output,len(matches))
    return matches[0]


def launch(req,output,log,extra=()):
    with log.open('xb') as stream:
        return subprocess.Popen(command(req,output,extra),stdout=stream,stderr=subprocess.STDOUT)


def wait_until(process,predicate,seconds=30):
    deadline=time.monotonic()+seconds
    while time.monotonic()<deadline:
        result=predicate()
        if result:return result
        if process.poll() is not None:raise AssertionError('build stopped before observation')
        time.sleep(.01)
    raise TimeoutError('fault observation timed out')


def run_cli(req,output,log,extra=()):
    process=launch(req,output,log,extra)
    try:return process.wait(timeout=300)
    finally:
        if process.poll() is None:process.kill();process.wait()


def success(req,output,log,expected):
    assert run_cli(req,output,log)==0
    report=json.loads((output/'build_manifest.json').read_text())
    assert report['summary']['public_parser_valid'] and report['summary']['native_loader_valid']
    assert report['summary']['coverage_complete'] and not report['summary']['approval_complete']
    for suffix in ('.ddi','.ddb'):
        assert report['output'][suffix]['sha256']==expected[suffix]
        assert analysis.file_sha256(output/('StressBank'+suffix))==expected[suffix]
    return report['output']


def failed(output,stage=None):
    assert not output.exists(),'failed build published output'
    private=attempt(output)
    state=json.loads((private/'build_state.json').read_text())
    assert state['status']=='failed',state
    if stage:assert state['stage']==stage,state
    return dict(private_directory=str(private),state=state)


def lock_new(path):
    kernel=c.WinDLL('kernel32',use_last_error=True)
    kernel.CreateFileW.argtypes=[w.LPCWSTR,w.DWORD,w.DWORD,c.c_void_p,w.DWORD,w.DWORD,w.HANDLE]
    kernel.CreateFileW.restype=w.HANDLE
    kernel.CloseHandle.argtypes=[w.HANDLE];kernel.CloseHandle.restype=w.BOOL
    handle=kernel.CreateFileW(str(path),0x40000000,0,None,1,0,None) # CREATE_NEW, share none
    if handle==c.c_void_p(-1).value:raise c.WinError(c.get_last_error())
    return kernel,handle


def main():
    cli=argparse.ArgumentParser(description=__doc__)
    cli.add_argument('--work-root',type=Path,required=True)
    cli.add_argument('--dll',type=Path,required=True)
    cli.add_argument('--parser-root',type=Path,required=True)
    cli.add_argument('--suite-name',default='recovery')
    args=cli.parse_args();root=args.work_root.resolve()
    if not args.suite_name.replace('-','').isalnum():raise ValueError('invalid suite name')
    suite=root/args.suite_name;suite.mkdir()
    small=request(root/'small',args.dll.resolve(),args.parser_root.resolve())
    medium=request(root/'medium',args.dll.resolve(),args.parser_root.resolve())
    expected={}
    for profile in ('small','medium'):
        result=json.loads((root/profile/'case_result.json').read_text())
        expected[profile]={suffix:result[key+'_sha256'] for suffix,key in (('.ddi','ddi'),('.ddb','ddb'))}
    results={};started=time.perf_counter()

    def save(name,value):
        results[name]=value
        analysis.write_json_atomic(suite/'recovery_results.json',dict(results=results,elapsed_seconds=time.perf_counter()-started))
        print(json.dumps(dict(case=name,passed=True)),flush=True)

    # Successful output is immutable under a normal repeated invocation.
    output=root/'published-small';before=snapshot(output)
    assert run_cli(small,output,suite/'existing.log')==2
    assert snapshot(output)==before
    save('existing_output',dict(file_count=len(before),unchanged=True))

    # Hash-bound preflight catches each corruption before creating a bank.
    for mode in ('manifest','wav','sms2'):
        fixture=suite/('input-'+mode);fixture.mkdir()
        for name in ('unit_manifest.json','graph.json'):shutil.copyfile(root/'small'/name,fixture/name)
        for name in ('units','analysis'):shutil.copytree(root/'small'/name,fixture/name)
        req=request(fixture,args.dll.resolve(),args.parser_root.resolve())
        if mode=='manifest':path=fixture/'unit_manifest.json'
        elif mode=='wav':path=next((fixture/'units').glob('*.wav'))
        else:path=next((fixture/'analysis/sms2').glob('*.sms2'))
        original=path.read_bytes()
        if mode=='manifest':path.write_bytes(b'{malformed synthetic fixture')
        elif mode=='wav':path.write_bytes(original[:-2]+bytes([original[-2]^1,original[-1]]))
        else:path.write_bytes(original[:40])
        output=suite/('output-'+mode)
        assert run_cli(req,output,suite/(mode+'.log'))==2
        fault=failed(output,'assemble');assert not (Path(fault['private_directory'])/'bank').exists()
        path.write_bytes(original)
        restored=success(req,output,suite/(mode+'-retry.log'),expected['small'])
        save('corrupt_'+mode,dict(failure=fault,retry=restored))

    for name,extra in (('timeout',['--stage-timeout','0.001']),('memory_limit',['--memory-limit-mib','12'])):
        output=suite/('output-'+name)
        assert run_cli(small,output,suite/(name+'.log'),extra)==2
        fault=failed(output,'assemble');metrics=fault['state']['phases']['assemble']
        if name=='timeout':assert metrics['timed_out']
        else:assert metrics['exit_code']!=0
        restored=success(small,output,suite/(name+'-retry.log'),expected['small'])
        save(name,dict(failure=fault,retry=restored))

    # Real Windows sharing failures, once before DDB manifest and once after DDI.
    for name,filename,phase in (('ddb_manifest_io','ddb_manifest.json.tmp','assemble'),
                                ('ddi_report_io','ddi_manifest.json.tmp','finalize')):
        output=suite/('output-'+name);process=launch(small,output,suite/(name+'.log'))
        handle=None
        try:
            private=wait_until(process,lambda:next((p for p in suite.glob('.'+output.name+'.build-*') if (p/'bank').is_dir()),None))
            kernel,handle=lock_new(private/'bank'/filename)
            assert process.wait(timeout=30)==2
        finally:
            if handle is not None:kernel.CloseHandle(handle)
            if process.poll() is None:process.kill();process.wait()
        fault=failed(output,phase)
        if phase=='finalize':assert (private/'bank/StressBank.ddi').is_file()
        restored=success(small,output,suite/(name+'-retry.log'),expected['small'])
        save(name,dict(failure=fault,retry=restored))

    # Test-only interception corrupts private DDI before each independent gate.
    real_run=builder.run_process
    for name in ('verify','native_load_hash_guard','native_load_truncation'):
        gate='verify' if name=='verify' else 'native_load'
        output=suite/('output-'+name);injected=[]
        def intercept(cmd,log,*more,**kwargs):
            if log.stem==gate:
                path=log.parent/'bank/StressBank.ddi'
                with path.open('r+b') as stream:
                    if name.endswith('truncation'):stream.truncate(16)
                    else:stream.write(b'FAIL')
                injected.append(True)
            return real_run(cmd,log,*more,**kwargs)
        builder.run_process=intercept
        try:
            with (suite/(name+'.log')).open('x',encoding='utf-8') as log,contextlib.redirect_stdout(log),contextlib.redirect_stderr(log):
                try:builder.build(copy.deepcopy(small),output)
                except builder.BuildFailure:pass
                else:raise AssertionError('corrupt gate accepted')
        finally:builder.run_process=real_run
        assert injected
        fault=failed(output,gate)
        if name.endswith('truncation'):assert fault['state']['phases'][gate]['exit_code']!=0
        restored=success(small,output,suite/(name+'-retry.log'),expected['small'])
        save(name+'_rejection',dict(failure=fault,retry=restored))

    # A failed private ledger write after the directory commit is still success.
    output=suite/'output-postcommit';real_write=analysis.write_json_atomic;injected=[]
    def fail_ledger(path,value):
        if path.name=='build_state.json' and value.get('status')=='published':
            injected.append(True);raise OSError('test-only post-commit private ledger failure')
        return real_write(path,value)
    analysis.write_json_atomic=fail_ledger
    try:
        with (suite/'postcommit.log').open('x',encoding='utf-8') as log,contextlib.redirect_stdout(log),contextlib.redirect_stderr(log):
            report=builder.build(copy.deepcopy(small),output)
    finally:analysis.write_json_atomic=real_write
    assert injected and output.is_dir() and report['summary']['native_loader_valid']
    assert all(analysis.file_sha256(output/('StressBank'+s))==h for s,h in expected['small'].items())
    save('postcommit_ledger_failure',dict(published=True,output=report['output']))

    # Two actual publishers race at one destination; only one wins.
    output=suite/'output-race';a=launch(small,output,suite/'race-a.log');b=launch(small,output,suite/'race-b.log')
    try:codes=[a.wait(timeout=30),b.wait(timeout=30)]
    finally:
        for process in (a,b):
            if process.poll() is None:process.kill();process.wait()
    assert sorted(codes)==[0,2],codes
    winner=snapshot(output)
    assert all(analysis.file_sha256(output/('StressBank'+s))==h for s,h in expected['small'].items())
    assert run_cli(small,output,suite/'race-after.log')==2 and snapshot(output)==winner
    save('concurrent_publish',dict(exit_codes=codes,winner_unchanged=True,output=expected['small']))

    # Force-kill the supervisor only after its worker has written real data.
    output=suite/'output-interrupted';parent=launch(medium,output,suite/'interrupted.log')
    handles=[]
    kernel=c.WinDLL('kernel32',use_last_error=True)
    kernel.OpenProcess.argtypes=[w.DWORD,w.BOOL,w.DWORD];kernel.OpenProcess.restype=w.HANDLE
    kernel.WaitForSingleObject.argtypes=[w.HANDLE,w.DWORD];kernel.WaitForSingleObject.restype=w.DWORD
    kernel.CloseHandle.argtypes=[w.HANDLE]
    try:
        temporary=wait_until(parent,lambda:next((p for p in suite.glob('.'+output.name+'.build-*/bank/StressBank.ddb.tmp') if p.stat().st_size>1_000_000),None),90)
        written=temporary.stat().st_size
        query=f'Get-CimInstance Win32_Process -Filter "ParentProcessId={parent.pid}" | Select-Object -ExpandProperty ProcessId'
        child_ids=[int(p) for p in subprocess.check_output(['powershell','-NoProfile','-Command',query],text=True).split()]
        assert child_ids
        for pid in child_ids:
            handle=kernel.OpenProcess(0x100000,False,pid)
            assert handle;handles.append(handle)
        parent.kill();parent.wait(timeout=10)
        assert all(kernel.WaitForSingleObject(handle,10000)==0 for handle in handles),'worker survived supervisor'
        assert not output.exists()
        private=attempt(output);state=json.loads((private/'build_state.json').read_text())
        assert state['stage']=='assemble' and state['status']=='building'
    finally:
        if parent.poll() is None:parent.kill();parent.wait()
        for handle in handles:kernel.CloseHandle(handle)
    restored=success(medium,output,suite/'interrupted-retry.log',expected['medium'])
    save('forced_supervisor_termination',dict(private_directory=str(private),written_bytes_before_kill=written,
        parent_exit_code=parent.returncode,child_ids=child_ids,all_owned_children_exited=True,
        stale_private_state=state,retry=restored))
    print(json.dumps(dict(passed_cases=len(results),elapsed_seconds=time.perf_counter()-started)),flush=True)


if __name__=='__main__':main()
