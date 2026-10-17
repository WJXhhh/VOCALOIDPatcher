"""Check saved VQM fields in active native TENSE/BRWSP mix paths.

Only owned FRM2/PCM fixtures are used. Child processes isolate native calls;
the report contains output hashes and aggregate comparisons, not payloads.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from audit_frm2_numeric import SHA256
from verify_vqm_unresolved_feature_mix import OUTPUT_KEYS, VARIANTS, case


OUTER_CASES = ((0, 0), (10, 2049), (255, 4097))


def signature(row: dict) -> tuple:
    return tuple(row[key] for key in OUTPUT_KEYS)


def run(dll: Path, scratch: Path, vqm_class: str = "tense") -> dict:
    if not scratch.is_dir():
        raise ValueError("Research scratch directory does not exist")
    if vqm_class not in ("tense", "breathiness"):
        raise ValueError("unsupported VQM class")
    rows = []
    for spectrum in (False, True):
        baseline = case(dll, scratch, spectrum, {}, vqm_class=vqm_class)
        if (baseline["vqm_class"] != vqm_class
                or min(baseline["mixed_real_nonzero"],
                       baseline["mixed_imag_nonzero"]) <= 0
                or baseline["inverse_fft_nonzero"] != 4096):
            raise AssertionError("VQM baseline did not reach the native mix")
        original = signature(baseline)
        growl_reference = case(dll, scratch, spectrum, {},
                               vqm_class="growl")
        if signature(growl_reference) == original:
            raise AssertionError("VQM class and Growl baseline unexpectedly agree")
        repeated = case(dll, scratch, spectrum, {}, unbuffered=False,
                        vqm_class=vqm_class)
        if signature(repeated) != original:
            raise AssertionError("VQM baseline changed between child launches")
        field_cases = [(name, case(dll, scratch, spectrum, changes,
                                   vqm_class=vqm_class))
                       for name, changes in VARIANTS[1:]]
        changed_fields = [name for name, result in field_cases
                          if signature(result) != original]
        filled = case(dll, scratch, spectrum, VARIANTS[-1][1],
                      buffer_fill=165, vqm_class=vqm_class)
        zero = case(dll, scratch, spectrum, VARIANTS[-1][1],
                    case_name="native_zero_gain", vqm_class=vqm_class)
        outer_cases = [
            {"outer18": a, "outer19": b,
             "same_output": signature(case(dll, scratch, spectrum, {},
                                           vqm_class=vqm_class,
                                           outer18=a, outer19=b)) == original}
            for a, b in OUTER_CASES
        ]
        if any(zero[key] for key in ("mixed_real_nonzero",
                                     "mixed_imag_nonzero",
                                     "inverse_fft_nonzero")):
            raise AssertionError("VQM zero gain unexpectedly produced audio")
        rows.append({
            "vqm_disk_spectrum": spectrum,
            "interpolated_spectrum_bins": baseline[
                "interpolated_vqm_spectrum_count"],
            "native_cases": 3 + len(field_cases) + 1 + 1 + len(outer_cases),
            "differs_from_growl_class_baseline": True,
            "different_field_variants": changed_fields,
            "combined_alternate_buffer_fill_same_output":
                signature(filled) == original,
            "outer_cases": outer_cases,
            "zero_gain_output_is_zero": True,
            "baseline_output": {key: baseline[key] for key in OUTPUT_KEYS},
        })
    return {
        "dse_sha256": SHA256,
        "class": ("DSE5::CVQMTense" if vqm_class == "tense"
                  else "DSE5::CVQMBreathiness"),
        "cases": rows,
        "scope": "Owned three-frame VQM object, native PCM target spectrum, "
                 "class mixer and inverse FFT. The results cover this "
                 "fixture and do not establish full host output or authoring "
                 "formulas.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--scratch", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--vqm-class", choices=("tense", "breathiness"),
                        default="tense")
    args = parser.parse_args()
    report = run(args.dll, args.scratch, args.vqm_class)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({"native_cases": sum(row["native_cases"] for row in
                                          report["cases"]),
                      "different_field_variants": [row[
                          "different_field_variants"] for row in report["cases"]],
                      "outer_same": [all(item["same_output"] for item in row[
                          "outer_cases"]) for row in report["cases"]]}))


if __name__ == "__main__":
    main()
