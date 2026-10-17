"""Measure owned VQM feature changes through the active native Growl mix path.

The supplied scratch directory holds generated FRM2 files only while each
child process runs. Evidence contains aggregate values and hashes, no frames.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256


VARIANTS = (
    ("baseline", {}),
    ("feature2_005", {"feature2": 0.05}),
    ("feature2_010", {"feature2": 0.10}),
    ("feature17_zero", {"feature17": 0.0}),
    ("feature17_pi", {"feature17": 3.14159265}),
    ("feature18_zero", {"feature18": 0.0}),
    ("feature18_five", {"feature18": 5.0}),
    ("combined", {"feature2": 0.05, "feature17": 3.14159265,
                  "feature18": 5.0}),
)


def native_case(dll: Path, scratch: Path, changes: dict,
                case: str = "native_positive") -> dict:
    command = [sys.executable, "-u",
               str(Path(__file__).with_name("probe_vqm_growl_frequency_mix.py")),
               "--dll", str(dll), "--scratch", str(scratch),
               "--output", str(scratch / "unused_growl_case.json"),
               "--single-case", case]
    for bit, value in changes.items():
        command.extend(("--" + bit, str(value)))
    child = subprocess.run(command, capture_output=True, text=True, check=False)
    if child.returncode:
        raise RuntimeError(f"native Growl case {changes} failed: "
                           f"{child.stderr[-500:]}")
    result = json.loads(child.stdout)
    if not result["internal_features_preserved_on_native_read"]:
        raise AssertionError("native read did not preserve VQM features")
    return result


def run(dll: Path, scratch: Path) -> dict:
    if not scratch.is_dir():
        raise ValueError("scratch directory does not exist")
    cases = [(name, changes, native_case(dll, scratch, changes))
             for name, changes in VARIANTS]
    baseline = cases[0][2]
    if (baseline["mixed_real_nonzero"] <= 0
            or baseline["mixed_imag_nonzero"] <= 0
            or baseline["inverse_fft_nonzero"] != 4096):
        raise AssertionError("positive Growl path was inactive")
    compared = {}
    for name, changes, result in cases:
        compared[name] = {
            "requested_changes": changes,
            "native_feature_float32_hex": result["internal_feature_float32_hex"],
            "changed_result_keys": sorted(
                key for key in baseline if key != "internal_feature_float32_hex"
                and result[key] != baseline[key]),
            "mixed_real_nonzero": result["mixed_real_nonzero"],
            "mixed_imag_nonzero": result["mixed_imag_nonzero"],
            "mixed_real_sha256": result["mixed_real_sha256"],
            "mixed_imag_sha256": result["mixed_imag_sha256"],
            "inverse_fft_sha256": result["inverse_fft_sha256"],
        }
    zero = native_case(dll, scratch, {"feature2": 0.05,
                                     "feature17": 3.14159265,
                                     "feature18": 5.0},
                       "native_zero_gain")
    if (zero["mixed_real_nonzero"] or zero["mixed_imag_nonzero"]
            or zero["inverse_fft_nonzero"]):
        raise AssertionError("zero-gain control did not silence Growl mix")
    return {
        "dse_sha256": SHA256,
        "feature_fields": {"2": "outer bit13/internal bit2 at +0x0c",
                           "17": "outer bit13/internal bit17 at +0x21c",
                           "18": "outer bit13/internal bit18 at +0x220"},
        "cases": compared,
        "all_non_feature_results_identical": all(
            not item["changed_result_keys"] for item in compared.values()),
        "zero_gain_control": {
            "mixed_real_nonzero": zero["mixed_real_nonzero"],
            "mixed_imag_nonzero": zero["mixed_imag_nonzero"],
            "inverse_fft_nonzero": zero["inverse_fft_nonzero"],
        },
        "scope": "Owned no-spectrum VQM frames through native DSE5 read, native PCM target spectrum, Growl event and frequency mixer, and native inverse FFT. Tests these three saved feature values in one active path only; authoring formulas and other paths remain unverified.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--scratch", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = run(args.dll, args.scratch)
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({"cases": len(report["cases"]),
                      "all_identical": report["all_non_feature_results_identical"],
                      "changes": {name: item["changed_result_keys"]
                                  for name, item in report["cases"].items()}}))


if __name__ == "__main__":
    main()
