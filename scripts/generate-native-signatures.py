"""Generate bounded x64 code signatures from legally installed SDK components.

Development-only dependencies: pefile 2024.8.26, capstone 5.0.3. No DLLs are
copied. Review changes to signatures/layout contracts before accepting output.
"""
import argparse
import bisect
import json
from pathlib import Path

import capstone as cs
import pefile


def generate(directory, profile):
    values = dict(line.split("=", 1) for line in profile.read_text().splitlines() if "=" in line)
    output = []
    for module in ("DSE", "VSM", "VDM", "VSMDirectory"):
        pe = pefile.PE(str(directory / (("VSM" if module == "VSMDirectory" else module) + ".dll")))
        image = pe.get_memory_mapped_image()
        decoder = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
        decoder.detail = True
        functions = sorted((e.struct.BeginAddress, e.struct.EndAddress) for e in pe.DIRECTORY_ENTRY_EXCEPTION)
        starts = [f[0] for f in functions]

        def owner(address):
            i = bisect.bisect_right(starts, address) - 1
            return functions[i] if i >= 0 and address < functions[i][1] else None

        def pattern(instructions):
            result = []
            for ins in instructions:
                data = [f"{b:02X}" for b in ins.bytes]
                if ins.group(cs.CS_GRP_CALL) or ins.group(cs.CS_GRP_JUMP):
                    if ins.imm_size:
                        data[ins.imm_offset:ins.imm_offset + ins.imm_size] = ["??"] * ins.imm_size
                if any(op.type == cs.x86.X86_OP_MEM and op.mem.base == cs.x86.X86_REG_RIP for op in ins.operands):
                    data[ins.disp_offset:ins.disp_offset + ins.disp_size] = ["??"] * ins.disp_size
                result.extend(data)
            return " ".join(result)

        def window(address, before=32, after=64):
            function = owner(address)
            start = function[0] if function else address
            instructions = list(decoder.disasm(image[start:address + after + 16], start))
            chosen = [ins for ins in instructions if address - before <= ins.address < address + after]
            if not chosen:
                raise ValueError(f"No instruction window at {address:x}")
            return chosen

        targets = {key: int(value, 16) for key, value in values.items() if value.startswith("0x") and
                   ((module == "DSE" and (key.startswith("Dse") or key.startswith("DurationCaller"))) or
                    (module == "VSM" and key.startswith("Vsm")))}
        if module == "VDM":
            targets = {"VdmCatalogBranch": 0xDC3D0}
        if module == "VSMDirectory":
            # Known constructor/destructor contracts for the optional sequence
            # directory repair adapter, independent of the timing bridge table.
            current = int(values["VsmPrepare"], 16) == 0x60570
            targets = {"SequenceDirectoryConstructor": 0x8C230 if current else 0x8C310,
                       "SequenceDirectoryDestructor": 0x31E70 if current else 0x31E90}
        data_targets = {key: address for key, address in targets.items() if not any(
            s.Characteristics & 0x20000000 and s.VirtualAddress <= address < s.VirtualAddress + s.Misc_VirtualSize for s in pe.sections)}
        references = {}
        if data_targets:
            decoder.skipdata = True
            # Only .text is decoded; output contains short identification windows.
            for section in pe.sections:
                if not section.Characteristics & 0x20000000:
                    continue
                for ins in decoder.disasm(image[section.VirtualAddress:section.VirtualAddress + section.Misc_VirtualSize], section.VirtualAddress):
                    if ins.id == 0:
                        continue
                    for op in ins.operands:
                        if op.type == cs.x86.X86_OP_MEM and op.mem.base == cs.x86.X86_REG_RIP:
                            target = ins.address + ins.size + op.mem.disp
                            for key, address in data_targets.items():
                                if target == address and key not in references:
                                    references[key] = ins
            decoder.skipdata = False
        for key, address in targets.items():
            relative_offset = relative_end = -1
            if key in data_targets:
                ins = references[key]
                instructions = window(ins.address)
                relative_offset = ins.address - instructions[0].address + ins.disp_offset
                relative_end = ins.address - instructions[0].address + ins.size
                result_offset = 0
            else:
                instructions = window(address, before=32 if key.startswith("DurationCaller") or key in ("DsePaddingCaller", "VdmCatalogBranch") else 0)
                result_offset = address - instructions[0].address
            entry = dict(Name=key, Module=module, Pattern=pattern(instructions), ResultOffset=result_offset,
                         RelativeOffset=relative_offset, RelativeEnd=relative_end, Guards=[], Links=[],
                         FunctionEntry=key not in data_targets and result_offset == 0 and owner(address) is not None and owner(address)[0] == address)
            # Private field offsets and stack/calling contracts stay exact. Sample
            # the body as well as the entry so a generic prologue cannot qualify.
            function = owner(address)
            if key not in data_targets and function and function[0] == address and function[1] - address > 128:
                body = list(decoder.disasm(image[address:function[1]], address))
                guards = []
                for fraction in (0.25, 0.5, 0.75, 0.95):
                    index = min(len(body) - 1, int(len(body) * fraction))
                    guards.append(pattern(body[max(0, index - 2):index + 4]))
                layout = {int(v, 16) for k, v in values.items() if v.startswith("0x") and k not in targets and 0x10 <= int(v, 16) < 0x40000}
                if module == "VSMDirectory":
                    layout.update((0x598, 0x5A8, 0x5B0))
                for ins in body:
                    if any(op.type == cs.x86.X86_OP_MEM and op.mem.base not in (cs.x86.X86_REG_RIP, cs.x86.X86_REG_RSP, cs.x86.X86_REG_RBP) and op.mem.disp in layout for op in ins.operands):
                        guards.append(pattern([ins]))
                entry["Guards"] = sorted(set(guards))
            # Cross-check references to other resolved symbols, not just masked bytes.
            for ins in instructions:
                target = None
                displacement = -1
                if (ins.group(cs.CS_GRP_CALL) or ins.group(cs.CS_GRP_JUMP)) and ins.imm_size == 4:
                    target, displacement = ins.operands[0].imm, ins.imm_offset
                for op in ins.operands:
                    if op.type == cs.x86.X86_OP_MEM and op.mem.base == cs.x86.X86_REG_RIP:
                        target, displacement = ins.address + ins.size + op.mem.disp, ins.disp_offset
                names = [k for k, a in targets.items() if a == target]
                if names:
                    entry["Links"].append(dict(Name=names[0], Offset=ins.address - instructions[0].address + displacement,
                                               End=ins.address - instructions[0].address + ins.size))
            output.append(entry)
    return output


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sdk", type=Path, action="append", required=True)
    parser.add_argument("--profile", type=Path, action="append", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if len(args.sdk) != len(args.profile):
        parser.error("Pass one profile per SDK")
    entries = []
    for sdk, profile in zip(args.sdk, args.profile):
        for entry in generate(sdk, profile):
            if entry not in entries:
                entries.append(entry)
    args.output.write_text(json.dumps(entries, indent=2) + "\n", encoding="utf-8")
    print(f"Generated {len(entries)} signature alternatives")
