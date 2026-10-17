"""Check nondefault VQM outer controls through native I/O and interpolation.

All FRM2 scratch files are generated locally. The report stores only scalar
outcomes, hashes, and field-preservation results.
"""

from __future__ import annotations

import argparse
import ctypes as c
import json
from pathlib import Path

from audit_frm2_numeric import B, F, I, Native, P, Q, SHA256
from verify_vqm_feature_dse5_roundtrip import run as dse5_roundtrip


VALUES = ((0, 0), (1, 1), (10, 2049), (20, 513), (255, 4097))


def interpolation_cases(dll: Path) -> list[dict]:
    native = Native(dll)
    initialize = native.fn(0x12a670, P, P)
    blend = native.fn(0x1753a0, None, P, P, P, I)
    context = native.alloc(0x24800)
    first, second, output = (native.alloc(0x328) for _ in range(3))
    for frame in (first, second, output):
        initialize(frame)
        P.from_address(frame + 0x208).value = native.alloc(0x228)
    P.from_address(context + 0x247b8).value = first
    P.from_address(context + 0x247c0).value = second
    rows = []
    for bit18, bit19 in VALUES:
        second18 = (bit18 + 37) & 0xff
        second19 = (bit19 + 101) & 0xffffffff
        for frame, f0, feature, outer18, outer19 in (
            (first, 220.0, 0.1, bit18, bit19),
            (second, 440.0, 0.3, second18, second19),
        ):
            Q.from_address(frame + 0x150).value = 0xc2200
            F.from_address(frame + 0x1d0).value = f0
            pointer = P.from_address(frame + 0x208).value
            I.from_address(pointer).value = 1 << 2
            F.from_address(pointer + 0x0c).value = feature
            B.from_address(frame + 0x258).value = outer18
            I.from_address(frame + 0x25c).value = outer19
        for fraction_value in (0.25, 0.75):
            Q.from_address(output + 0x150).value = 0
            pointer = P.from_address(output + 0x208).value
            I.from_address(pointer).value = 0
            B.from_address(output + 0x258).value = 0x5a
            I.from_address(output + 0x25c).value = 0x12345678
            P.from_address(context + 0x247b8).value = first
            P.from_address(context + 0x247c0).value = second
            fraction = F(fraction_value)
            blend(context, c.byref(fraction), output, 0xc2200)
            result18 = B.from_address(output + 0x258).value
            result19 = I.from_address(output + 0x25c).value
            if (result18 != bit18 or result19 != bit19
                    or Q.from_address(output + 0x150).value != 0xc2200
                    or I.from_address(pointer).value != 1 << 2):
                raise AssertionError({
                    "first": (bit18, bit19),
                    "second": (second18, second19),
                    "fraction": fraction_value,
                    "output": (result18, result19),
                    "mask": hex(Q.from_address(output + 0x150).value),
                    "feature_flags": I.from_address(pointer).value,
                })
            rows.append({
                "first_bit18": bit18,
                "second_bit18": second18,
                "first_bit19": bit19,
                "second_bit19": second19,
                "fraction": fraction_value,
                "output_bit18": result18,
                "output_bit19": result19,
            })
    return rows


def run(dll: Path, source_base: Path, output_base: Path) -> dict:
    rows = []
    for index, (bit18, bit19) in enumerate(VALUES):
        source = source_base.with_name(
            f"{source_base.stem}_{index}{source_base.suffix}")
        roundtrip = output_base.with_name(
            f"{output_base.stem}_{index}{output_base.suffix}")
        saved = dse5_roundtrip(dll, source, roundtrip, bit18, bit19)
        rows.append({
            "requested_bit18": bit18,
            "requested_bit19": bit19,
            "readback_bit18": saved["outer_bit18"],
            "readback_bit19": saved["outer_bit19"],
            "fixture_file_mask": saved["fixture_file_mask"],
            "roundtrip_file_mask": saved["roundtrip_file_mask"],
            "arrays_and_controls_preserved": saved["arrays_and_controls_preserved"],
        })
    interpolation = interpolation_cases(dll)
    return {
        "dse_sha256": SHA256,
        "native_dse5_read_write_cases": rows,
        "native_interpolation_cases": interpolation,
        "scope": "Owned nondefault VQM frame controls through DSE5 read/write/read and generic frame interpolation. These paths preserve/copy values but do not establish authoring names or complete Growl-event consumption.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--scratch-source-base", type=Path, required=True)
    parser.add_argument("--scratch-roundtrip-base", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.scratch_source_base,
                 args.scratch_roundtrip_base)
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({
        "io_cases": len(report["native_dse5_read_write_cases"]),
        "interpolation_cases": len(report["native_interpolation_cases"]),
    }))


if __name__ == "__main__":
    main()
