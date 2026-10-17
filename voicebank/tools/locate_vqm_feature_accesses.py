"""Locate bounded DSE instruction candidates for VQM feature/outer offsets."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import struct

from audit_frm2_numeric import SHA256


def executable_sections(data: bytes):
    pe_offset = struct.unpack_from("<I", data, 0x3c)[0]
    if data[pe_offset:pe_offset + 4] != b"PE\0\0":
        raise ValueError("not a PE image")
    count = struct.unpack_from("<H", data, pe_offset + 6)[0]
    optional_size = struct.unpack_from("<H", data, pe_offset + 20)[0]
    section_base = pe_offset + 24 + optional_size
    for index in range(count):
        off = section_base + index * 40
        name = data[off:off + 8].split(b"\0", 1)[0].decode("ascii")
        virtual_address, raw_size, raw_offset = struct.unpack_from("<III", data, off + 12)
        characteristics = struct.unpack_from("<I", data, off + 36)[0]
        if characteristics & 0x20000000:
            yield name, virtual_address, data[raw_offset:raw_offset + raw_size]


def run(path: Path) -> dict[int, list[int]]:
    data = path.read_bytes()
    if hashlib.sha256(data).hexdigest() != SHA256:
        raise ValueError("DSE version differs; review offsets first")
    hits = {offset: [] for offset in
            (0x214, 0x218, 0x21c, 0x220, 0x224, 0x258, 0x25c)}
    for _, virtual_address, section in executable_sections(data):
        for offset in hits:
            needle = struct.pack("<I", offset)
            start = 0
            while (position := section.find(needle, start)) >= 0:
                hits[offset].append(virtual_address + position)
                start = position + 1
    return hits


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--json", action="store_true", help="print a machine-readable candidate report")
    parser.add_argument("--only-offset", type=lambda value: int(value, 0),
                        help="limit the report to one feature-object byte offset")
    parser.add_argument("--outer-only", action="store_true",
                        help="include only outer frame offsets +0x258 and +0x25c")
    parser.add_argument("--output", type=Path,
                        help="write a JSON report to this path instead of stdout")
    args = parser.parse_args()
    hits = run(args.dll)
    if args.outer_only:
        hits = {offset: hits[offset] for offset in (0x258, 0x25c)}
    if args.only_offset is not None:
        if args.only_offset not in hits:
            parser.error("unsupported offset; see the offsets in run()")
        hits = {args.only_offset: hits[args.only_offset]}
    if args.json or args.output:
        report = json.dumps({
            "dll_sha256": SHA256,
            "scope": "four-byte offset sequences in executable PE sections; candidates require instruction and object-layout review",
            "candidate_rvas": {f"0x{offset:x}": [f"0x{rva:x}" for rva in rvas]
                               for offset, rvas in hits.items()},
        }, indent=2)
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(report + "\n", encoding="utf-8")
        else:
            print(report)
        return
    for offset, rvas in hits.items():
        print(f"+0x{offset:x}: {len(rvas)} executable-section candidates")
        print(" ".join(f"0x{rva:x}" for rva in rvas[:80]))


if __name__ == "__main__":
    main()
