"""Sample instruction pointers of an owned, disposable audio probe only."""
import argparse
import ctypes as c
from ctypes import wintypes as w
import json
from pathlib import Path
import subprocess
import sys
import time

class ThreadEntry(c.Structure):
    _fields_ = [('size', w.DWORD), ('usage', w.DWORD), ('tid', w.DWORD),
                ('pid', w.DWORD), ('base', w.LONG), ('delta', w.LONG), ('flags', w.DWORD)]

class ModuleEntry(c.Structure):
    _fields_ = [('size', w.DWORD), ('id', w.DWORD), ('pid', w.DWORD),
                ('global_usage', w.DWORD), ('process_usage', w.DWORD),
                ('base', c.c_void_p), ('bytes', w.DWORD), ('module', c.c_void_p),
                ('name', w.WCHAR * 256), ('path', w.WCHAR * 260)]


def run(args):
    k = c.WinDLL('kernel32', use_last_error=True)
    definitions = {
        'CreateToolhelp32Snapshot': (w.HANDLE, [w.DWORD, w.DWORD]),
        'Thread32First': (w.BOOL, [w.HANDLE, c.POINTER(ThreadEntry)]),
        'Thread32Next': (w.BOOL, [w.HANDLE, c.POINTER(ThreadEntry)]),
        'Module32FirstW': (w.BOOL, [w.HANDLE, c.POINTER(ModuleEntry)]),
        'Module32NextW': (w.BOOL, [w.HANDLE, c.POINTER(ModuleEntry)]),
        'OpenThread': (w.HANDLE, [w.DWORD, w.BOOL, w.DWORD]),
        'SuspendThread': (w.DWORD, [w.HANDLE]),
        'ResumeThread': (w.DWORD, [w.HANDLE]),
        'GetThreadContext': (w.BOOL, [w.HANDLE, c.c_void_p]),
        'CloseHandle': (w.BOOL, [w.HANDLE]),
        'OpenProcess': (w.HANDLE, [w.DWORD, w.BOOL, w.DWORD]),
        'ReadProcessMemory': (w.BOOL, [w.HANDLE, c.c_void_p, c.c_void_p, c.c_size_t, c.POINTER(c.c_size_t)]),
    }
    for name, (result, types) in definitions.items():
        function = getattr(k, name)
        function.restype, function.argtypes = result, types
    command = [sys.executable, '-B', str(Path(__file__).with_name('verify_region_dvqm_segments.py')),
               '--audio', '--audio-start', '1', '--dll', str(args.dll),
               '--directory', str(args.directory), '--output', str(args.child_output)]
    if args.child_output.exists():
        raise FileExistsError(args.child_output)
    child = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    process = k.OpenProcess(0x410, False, child.pid)
    assert process
    rows = []
    modules = []
    try:
        control = None
        selected_sample = False
        for _ in range(5):
            line = child.stdout.readline()
            if line.startswith(b'audio sample '):
                assert b"['Accent2']" in line, line
                selected_sample = True
            if line.startswith(b'audio control '):
                assert selected_sample, line
                control = int(line.split()[-1], 16)
                break
            if not line:
                break
        assert selected_sample and control
        time.sleep(2)
        assert child.poll() is None
        snapshot = k.CreateToolhelp32Snapshot(0x18, child.pid)
        assert snapshot not in [None, c.c_void_p(-1).value]
        try:
            item = ModuleEntry()
            item.size = c.sizeof(item)
            valid = k.Module32FirstW(snapshot, c.byref(item))
            while valid:
                modules.append((item.name, item.base, item.bytes))
                valid = k.Module32NextW(snapshot, c.byref(item))
        finally:
            k.CloseHandle(snapshot)
        snapshot = k.CreateToolhelp32Snapshot(4, 0)
        tids = []
        try:
            item = ThreadEntry()
            item.size = c.sizeof(item)
            valid = k.Thread32First(snapshot, c.byref(item))
            while valid:
                if item.pid == child.pid:
                    tids.append(item.tid)
                valid = k.Thread32Next(snapshot, c.byref(item))
        finally:
            k.CloseHandle(snapshot)
        for turn in range(40):
            if child.poll() is not None:
                break
            for tid in tids:
                handle = k.OpenThread(0x4a, False, tid)
                assert handle
                suspended = False
                try:
                    assert k.SuspendThread(handle) != 0xffffffff
                    suspended = True
                    buffer = c.create_string_buffer(1250)
                    address = (c.addressof(buffer) + 15) & ~15
                    c.c_uint32.from_address(address + 48).value = 0x100003
                    assert k.GetThreadContext(handle, address), c.get_last_error()
                    rip = c.c_uint64.from_address(address + 248).value
                    stack = c.create_string_buffer(1024)
                    read = c.c_size_t()
                    rsp = c.c_uint64.from_address(address + 152).value
                    candidates = []
                    if k.ReadProcessMemory(process, rsp, stack, 1024, c.byref(read)):
                        values = (c.c_uint64 * (read.value // 8)).from_buffer(stack)
                        for slot, value in enumerate(values):
                            for name, base, size in modules:
                                if name.lower() == 'dse.dll' and 0x1000 <= value-base < 0x220000:
                                    candidates.append(dict(stack_offset=slot*8, rva=hex(value-base)))
                    analyzer = None
                    r14 = c.c_uint64.from_address(address + 232).value
                    def field(pointer, kind):
                        value = kind()
                        got = c.c_size_t()
                        if not k.ReadProcessMemory(process, pointer, c.byref(value), c.sizeof(value), c.byref(got)):
                            return None
                        return value.value
                    internal = field(control, c.c_void_p)
                    actual_analyzer = field(internal + 0x60, c.c_void_p) if internal else None
                    if actual_analyzer:
                        r14 = actual_analyzer
                    rate = field(r14 + 0x2dd4, c.c_float)
                    if rate in [22050., 44100., 48000.]:
                        frame = field(r14 + 0x2e88, c.c_void_p)
                        analyzer = dict(sample_rate=rate,
                                        cursor=field(r14 + 0x7c, c.c_int32),
                                        input_index=field(r14 + 0x68, c.c_int32),
                                        iteration_cursor=field(r14 + 0x74, c.c_int32),
                                        mapped_sample=field(r14 + 0x2ef8, c.c_double),
                                        alternate_cursor=field(r14 + 0x217c, c.c_int32),
                                        counter=field(r14 + 0x78, c.c_int32),
                                        frame_count=field(frame + 0x18, c.c_int32) if frame else None)
                    if analyzer:
                        lanes = []
                        # Only lane zero has been identified in the observed loop.
                        # The stride alone does not establish another active lane.
                        for lane in range(1):
                            state = r14 + 0x2ed8 + lane * 0x2f40
                            index = field(state + 0x18, c.c_int32)
                            entry = None
                            array = field(frame + 0x20, c.c_void_p) if frame else None
                            if array and index is not None and 0 <= index < analyzer['frame_count']:
                                entry = field(array + index*8, c.c_void_p)
                            next_entry = (field(array + (index+1)*8, c.c_void_p)
                                          if entry and index+1 < analyzer['frame_count'] else None)
                            produced = field(state + 0x10, c.c_void_p)
                            produced_count = field(produced + 0x18, c.c_int32) if produced else None
                            produced_array = field(produced + 0x20, c.c_void_p) if produced else None
                            last = (field(produced_array + (produced_count-1)*8, c.c_void_p)
                                    if produced_array and produced_count and 0 < produced_count < 1000000 else None)
                            recent_times = []
                            if produced_array and produced_count and 0 < produced_count < 1000000:
                                for prior in range(min(8, produced_count)):
                                    recent = field(produced_array + (produced_count-1-prior)*8, c.c_void_p)
                                    recent_times.append(field(recent + 0x48, c.c_double) if recent else None)
                            lanes.append(dict(lane=lane, index=index,
                                produced_count=produced_count,
                                recent_produced_times=recent_times,
                                last_produced_time=field(last + 0x48, c.c_double) if last else None,
                                last_produced_pitch=field(last + 0xf0, c.c_float) if last else None,
                                last_produced_mask=field(last + 0x50, c.c_uint32) if last else None,
                                previous_index=field(state + 0x1c, c.c_int32),
                                position=field(state + 0x20, c.c_double),
                                previous_position=field(state + 0x28, c.c_double),
                                source_time=field(entry + 0x48, c.c_double) if entry else None,
                                next_source_time=field(next_entry + 0x48, c.c_double) if next_entry else None,
                                source_f0=field(entry + 0xf0, c.c_float) if entry else None,
                                source_count=field(entry + 0x58, c.c_int32) if entry else None))
                        analyzer['lanes'] = lanes
                        analyzer['advance_mode'] = field(r14 + 0x2cec, c.c_int32)
                        analyzer['fallback_step'] = field(r14 + 0x2cf0, c.c_int32)
                        analyzer['lane_zero_ratio'] = field(r14 + 0x2d10, c.c_float)
                        analyzer['adjustment_gate_time'] = field(r14 + 0x2030, c.c_double)
                        analyzer['adjustment_curve'] = field(r14 + 0x1fd8, c.c_void_p)
                        if analyzer['adjustment_curve']:
                            analyzer['adjustment_curve_first'] = field(analyzer['adjustment_curve'], c.c_float)
                        analyzer.pop('adjustment_curve', None)
                        analyzer['reference_time'] = field(r14 + 0x2160, c.c_double)
                        analyzer['reference_limit'] = field(r14 + 0x2170, c.c_double)
                        analyzer['reference_object_present'] = bool(field(r14 + 0x2158, c.c_void_p))
                    matching = [(name, rip-base) for name, base, size in modules if base <= rip < base+size]
                    rows.append(dict(turn=turn, thread_index=tids.index(tid),
                                     location=[(name, hex(offset)) for name, offset in matching], stack_candidates=candidates, possible_analyzer=analyzer))
                finally:
                    if suspended:
                        k.ResumeThread(handle)
                    k.CloseHandle(handle)
            time.sleep(.05)
    finally:
        if child.poll() is None:
            child.kill()
        stdout, stderr = child.communicate(timeout=10)
        k.CloseHandle(process)
    return dict(rows=rows, stdout=stdout.decode(errors='replace'), stderr=stderr.decode(errors='replace'),
                scope='Read instruction pointers while briefly suspending only the subprocess created here; '
                      'no code or memory edits, no Editor/Ghidra process attachment; not proof of infinite loop')

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ['dll', 'directory', 'child-output', 'output']:
        parser.add_argument('--' + option, type=Path, required=True)
    args = parser.parse_args()
    result = run(args)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    from collections import Counter
    print(Counter(tuple(tuple(p) for p in row['location']) for row in result['rows']))
