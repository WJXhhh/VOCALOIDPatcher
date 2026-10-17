"""Check generic frame interpolation of owned VQM control bytes under Growl's mask."""

from __future__ import annotations

import argparse
import ctypes as c
import json
import math
from pathlib import Path

from audit_frm2_numeric import B, F, I, Native, P, Q, SHA256


def run(dll: Path) -> dict:
    native = Native(dll)
    initialize = native.fn(0x12a670, P, P)
    blend = native.fn(0x1753a0, None, P, P, P, I)
    context = native.alloc(0x24800)
    first, second, output = (native.alloc(0x328) for _ in range(3))
    for frame in (first, second, output):
        initialize(frame)
    for frame, f0, feature, bit18, bit19 in (
        (first, 220.0, 0.1, 10, 2049),
        (second, 440.0, 0.3, 20, 4097),
    ):
        Q.from_address(frame + 0x150).value = 0xc2200
        F.from_address(frame + 0x1d0).value = f0
        feature_object = native.alloc(0x228)
        P.from_address(frame + 0x208).value = feature_object
        I.from_address(feature_object).value = 1 << 2
        F.from_address(feature_object + 0x0c).value = feature
        B.from_address(frame + 0x258).value = bit18
        I.from_address(frame + 0x25c).value = bit19
    output_feature = native.alloc(0x228)
    P.from_address(output + 0x208).value = output_feature

    rows = []
    for requested_mask in (0x1ff7, 0xc2200):
        for fraction_value in (0.25, 0.75):
            Q.from_address(output + 0x150).value = 0
            I.from_address(output_feature).value = 0
            F.from_address(output_feature + 0x0c).value = 0.9
            B.from_address(output + 0x258).value = 0x5a
            I.from_address(output + 0x25c).value = 1234567
            P.from_address(context + 0x247b8).value = first
            P.from_address(context + 0x247c0).value = second
            fraction = F(fraction_value)
            blend(context, c.byref(fraction), output, requested_mask)
            rows.append({
                "requested_mask": hex(requested_mask),
                "fraction": fraction_value,
                "output_mask": hex(Q.from_address(output + 0x150).value),
                "feature_flags": I.from_address(output_feature).value,
                "feature_scalar_2": F.from_address(output_feature + 0x0c).value,
                "bit18": B.from_address(output + 0x258).value,
                "bit19": I.from_address(output + 0x25c).value,
                "f0_hz": F.from_address(output + 0x1d0).value,
            })
            actual = rows[-1]
            if requested_mask == 0x1ff7:
                assert actual["output_mask"] == "0x200"
                assert actual["feature_flags"] == 0
                assert actual["feature_scalar_2"] == F(0.9).value
                assert actual["bit18"] == 0x5a
                assert actual["bit19"] == 1234567
            else:
                assert actual["output_mask"] == "0xc2200"
                assert actual["feature_flags"] == 4
                assert math.isclose(actual["feature_scalar_2"],
                                    0.1 + 0.2 * fraction_value, abs_tol=2e-8)
                assert actual["bit18"] == 10
                assert actual["bit19"] == 2049
    return {
        "dse_sha256": SHA256,
        "entry_rva": "0x1753a0",
        "interpolator_rva": "0x1307b0",
        "cases": rows,
        "scope": "Owned runtime frames and generic interpolation helper. Growl's use of 0x1ff7 is verified separately by call-site inspection.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result["cases"]))


if __name__ == "__main__":
    main()
