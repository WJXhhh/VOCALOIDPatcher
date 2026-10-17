"""Bounded native phase-entry diagnosis; never handles or resumes native faults."""
import argparse
import ctypes as c
import json
import hashlib
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import F, I, P, Q, SHA256
from probe_frm2_batch_outputs import run


def case(dll, mode, automatic, phase, region=False, streaming=False):
    held, faults = [], []
    kernel = c.WinDLL('kernel32', use_last_error=True)
    kernel.AddVectoredExceptionHandler.argtypes = [c.c_ulong, P]
    kernel.AddVectoredExceptionHandler.restype = P
    kernel.RemoveVectoredExceptionHandler.argtypes = [P]
    kernel.RemoveVectoredExceptionHandler.restype = c.c_ulong

    @c.WINFUNCTYPE(c.c_long, P)
    def observe(info):
        record = P.from_address(info).value
        if c.c_ulong.from_address(record).value == 0xc0000005 and held:
            native, context = held[0]
            address = P.from_address(record + 0x10).value
            if native.lib._handle <= address < native.lib._handle + 0x600000:
                faults.append(hex(address - native.lib._handle))
        return 0  # EXCEPTION_CONTINUE_SEARCH; ctypes retains its normal handling.

    token = kernel.AddVectoredExceptionHandler(1, c.cast(observe, P))
    if not token:
        raise c.WinError(c.get_last_error())
    try:
        try:
            output = run(dll, automatic, 0, 'steady', 220, analysis_mode=mode,
                         phase_analysis=phase, region_analysis=region, streaming=streaming, setup_observer=lambda n, ctx: held.append((n, ctx)))
            result = dict(completed=True, frames=output['frames'], bit37_frames=output['bit37_frames'],
                          metadata_sha256=hashlib.sha256(json.dumps(output,sort_keys=True).encode()).hexdigest())
        except OSError as error:
            if not faults:
                raise
            native, context = held[0]
            queue = P.from_address(context + 0xc50).value
            link = P.from_address(queue).value
            frame = P.from_address(link + 0x28).value
            result = dict(completed=False, exception=type(error).__name__, fault_rvas=faults,
                          harmonic_count=I.from_address(frame + 0x58).value,
                          frequency_present=bool(P.from_address(frame + 0x60).value),
                          amplitude_present=bool(P.from_address(frame + 0x68).value),
                          phase_present=bool(P.from_address(frame + 0x70).value),
                          f0=F.from_address(frame + 0xf0).value,
                          mask=hex(Q.from_address(frame + 0x50).value))
        return dict(mode=mode, automatic=automatic, phase_enabled=phase, region_analysis=region, streaming=streaming, result=result)
    finally:
        if not kernel.RemoveVectoredExceptionHandler(token):
            raise c.WinError(c.get_last_error())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--case', type=int, choices=range(6))
    parser.add_argument('--automatic', type=int, choices=(0, 1), default=1)
    parser.add_argument('--phase', type=int, choices=(0, 1), default=1)
    parser.add_argument('--region', action='store_true')
    parser.add_argument('--streaming', action='store_true')
    args = parser.parse_args()
    if args.case is not None:
        print(json.dumps(case(args.dll, args.case, args.automatic, args.phase, args.region, args.streaming)))
        return
    if args.output is None:
        parser.error('--output required')
    rows = []
    for mode in range(6):
        for automatic in (0, 1):
            for phase in (0, 1):
                child = subprocess.run([sys.executable, __file__, '--dll', str(args.dll),
                    '--case', str(mode), '--automatic', str(automatic), '--phase', str(phase)] + (['--region'] if args.region else []) + (['--streaming'] if args.streaming else []),
                    capture_output=True, text=True, timeout=60)
                if child.returncode:
                    raise RuntimeError(child.stderr[-4000:])
                rows.append(json.loads(child.stdout))
        print(mode, flush=True)
    for row in rows:
        result = row['result']
        if not row['phase_enabled']:
            if not result['completed'] or result['frames'] != 44:
                raise AssertionError(('Baseline changed', row))
        else:
            # Invalid queue data can fault on the node or its frame dereference.
            expected = {'0xd46ab', '0xd46af'} if row['mode'] in (0, 3, 5) else {'0xd762d'}
            if result['completed'] or len(result['fault_rvas']) != 1 or result['fault_rvas'][0] not in expected:
                raise AssertionError(('Unexpected failure boundary', row))
            if result['harmonic_count'] or result['phase_present']:
                raise AssertionError(('Unexpected phase input', row))
    args.output.write_text(json.dumps(dict(dll_sha256=SHA256, cases=rows,
        scope='Owned steady 220 Hz PCM, six analysis modes and two pitch modes, phase flag only; no injected frame arrays, no private history initialization, no DLL modifications. Exceptions are observed and allowed to propagate normally in isolated children.'), indent=2)+'\n', encoding='utf-8')


if __name__ == '__main__':
    main()
