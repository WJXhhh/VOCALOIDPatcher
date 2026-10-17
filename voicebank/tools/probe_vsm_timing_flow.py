"""Bounded read-only PE/disassembly inspection for VSM timing research.

Does not load the module, change Ghidra state or serialize proprietary code.
"""
from __future__ import annotations

import argparse
import bisect
import hashlib
import importlib
from pathlib import Path
import struct
import sys

from audit_vqm_v4_module_leads import PE


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--decoder-dir', type=Path, required=True)
    parser.add_argument('--export', action='append', default=[])
    parser.add_argument('--rva', action='append', type=lambda x: int(x, 0), default=[])
    parser.add_argument('--instructions', type=int, default=160)
    parser.add_argument('--skip-instructions', type=int, default=0)
    parser.add_argument('--calls-only', action='store_true')
    args = parser.parse_args()
    if (not 1 <= args.instructions <= 400 or len(args.rva) > 12
            or not 0 <= args.skip_instructions <= 2000):
        raise ValueError('Inspection range exceeds bound')
    sys.path.insert(0, str(args.decoder_dir.resolve()))
    cs = importlib.import_module('capstone')
    data = args.dll.read_bytes()
    image = PE(data)
    if image.machine != 0x8664:
        raise ValueError('Expected x64 module')
    exports = image.exports()
    names = {int(item['rva'], 16): item['name'] for item in exports}
    exception_rva, exception_size = struct.unpack_from('<II', data, image.directories + 3 * 8)
    start = image.offset(exception_rva)
    functions = [struct.unpack_from('<III', data, start + i)
                 for i in range(0, exception_size - 11, 12)]
    functions.sort()
    starts = [f[0] for f in functions]
    targets = [(int(item['rva'], 16), item['name']) for item in exports
               if any(token in item['name'] for token in args.export)]
    targets += [(rva, names.get(rva, 'internal')) for rva in args.rva]
    if len(targets) > 24:
        raise ValueError('Too many targets; narrow the export filter')
    print(f'sha256={hashlib.sha256(data).hexdigest()};image_base={image.image_base:#x}')
    decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
    for rva, name in targets:
        index = bisect.bisect_right(starts, rva) - 1
        requested_rva = rva
        if index >= 0 and rva < functions[index][1]:
            # Decode from the known instruction boundary, never from an arbitrary
            # address in the middle of a runtime function.
            rva, end = functions[index][:2]
        else:
            end = rva + 512
        payload = data[image.offset(rva):image.offset(rva) + min(end - rva, 16384)]
        print(f'\n{name}: requested_rva={requested_rva:#x};rva={rva:#x};runtime_function_end={end:#x}')
        for i, instruction in enumerate(decoder.disasm(payload, image.image_base + rva)):
            if i < args.skip_instructions:
                continue
            if i >= args.skip_instructions + args.instructions:
                print('... instruction limit ...')
                break
            if args.calls_only and instruction.mnemonic not in ('call', 'jmp'):
                continue
            label = ''
            if instruction.mnemonic in ('call', 'jmp') and instruction.op_str.startswith('0x'):
                destination = int(instruction.op_str, 16) - image.image_base
                label = f' ; {names[destination]}' if destination in names else ''
            print(f'{instruction.address - image.image_base:08x} {instruction.mnemonic} {instruction.op_str}{label}')


if __name__ == '__main__':
    main()
