"""Check native CSMSFrame outer-field initialization using owned memory."""

from __future__ import annotations

import argparse
import ctypes as c
import json
from pathlib import Path

from audit_frm2_numeric import B, I, Native, P, Q, SHA256


def run(dll: Path) -> dict:
    native = Native(dll)
    initialize = native.fn(0x12a670, P, P)
    clear = native.fn(0x12cf40, Q, P, Q, I, I)
    cases = []
    for fill in (0x00, 0xA5):
        frame = native.alloc(0x328)
        c.memset(frame, fill, 0x328)
        if initialize(frame) != frame:
            raise AssertionError("native frame constructor returned another address")
        mask = Q.from_address(frame + 0x150).value
        outer_bit18 = B.from_address(frame + 0x258).value
        outer_bit19 = I.from_address(frame + 0x25c).value
        if mask != 0 or outer_bit18 != 255 or outer_bit19 != 0:
            raise AssertionError({"fill": fill, "mask": mask,
                                  "outer_bit18": outer_bit18,
                                  "outer_bit19": outer_bit19})
        Q.from_address(frame + 0x150).value = 0xC0000
        B.from_address(frame + 0x258).value = 10
        I.from_address(frame + 0x25c).value = 2049
        if clear(frame, 0xC0000, 0, 0) != 0:
            raise AssertionError("native outer-field clear failed")
        cleared = {"frame_mask": f"0x{Q.from_address(frame + 0x150).value:x}",
                   "outer_bit18": B.from_address(frame + 0x258).value,
                   "outer_bit19": I.from_address(frame + 0x25c).value}
        if cleared != {"frame_mask": "0x0", "outer_bit18": 255,
                       "outer_bit19": 0}:
            raise AssertionError({"fill": fill, "cleared": cleared})
        cases.append({"preinitialization_fill": fill,
                      "frame_mask": f"0x{mask:x}",
                      "outer_bit18": outer_bit18,
                      "outer_bit19": outer_bit19,
                      "cleared_from_10_2049": cleared})
    return {
        "dse_sha256": SHA256,
        "native_constructor_rva": "0x12a670",
        "native_reset_rva": "0x1303b0",
        "native_clear_rva": "0x12cf40",
        "cases": cases,
        "scope": "The generic frame constructor initializes owned memory to outer bit18=255 and bit19=0, and field-clear restores the same values from 10/2049; saved VQM values 10/2049 therefore require a later assignment or file read. This does not identify the original authoring operation.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report["cases"]))


if __name__ == "__main__":
    main()
