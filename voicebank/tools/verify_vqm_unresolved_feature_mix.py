"""Test unresolved VQM scalars in an active owned Growl audio-block path."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256


VARIANTS = (
    ("baseline", {}),
    ("bit4_low", {"feature4": 0.01}),
    ("bit4_high", {"feature4": 0.9}),
    ("bit6_zero", {"feature6": 0.0}),
    ("bit6_high", {"feature6": 1.0}),
    ("bit14_low", {"feature14": 0.1}),
    ("bit14_high", {"feature14": 10.0}),
    ("bit15_zero", {"feature15": 0.0}),
    ("bit15_high", {"feature15": 0.5}),
    ("bit16_low", {"feature16": -10.0}),
    ("bit16_high", {"feature16": 5.0}),
    ("bit17_zero", {"feature17": 0.0}),
    ("bit17_pi", {"feature17": 3.14159265}),
    ("bit18_zero", {"feature18": 0.0}),
    ("bit18_five", {"feature18": 5.0}),
    ("combined", {"feature4": 0.9, "feature6": 1.0,
                  "feature14": 10.0, "feature15": 0.5,
                  "feature16": 5.0, "feature17": 3.14159265,
                  "feature18": 5.0}),
)
OUTPUT_KEYS = ("mixed_real_sha256", "mixed_imag_sha256",
               "inverse_fft_sha256", "mixed_real_nonzero",
               "mixed_imag_nonzero", "inverse_fft_nonzero")


def case(dll: Path, scratch: Path, spectrum: bool, changes: dict,
         case_name: str = "native_positive", unbuffered: bool = True,
         buffer_fill: int = 0, vqm_class: str = "growl",
         outer18: int = 10, outer19: int = 2049) -> dict:
    command = [sys.executable]
    if unbuffered:
        command.append("-u")
    command.extend((
               str(Path(__file__).with_name("probe_vqm_growl_frequency_mix.py")),
               "--dll", str(dll), "--scratch", str(scratch),
               "--output", str(scratch / "unused_growl_case.json"),
               "--single-case", case_name,
               "--vqm-class", vqm_class,
               "--outer18", str(outer18), "--outer19", str(outer19),
               "--growl-buffer-fill", str(buffer_fill)))
    if spectrum:
        command.extend(("--vqm-spectrum", "--vqm-spectrum-peak-db", "45"))
    for key, value in changes.items():
        command.extend(("--" + key, str(value)))
    child = subprocess.run(command, capture_output=True, text=True, check=False)
    if child.returncode:
        raise RuntimeError(f"owned case {spectrum}/{changes}/{case_name} failed: "
                           f"{child.stderr[-1000:]}")
    result = json.loads(child.stdout)
    if (not result["internal_features_preserved_on_native_read"]
            or result["interpolated_vqm_spectrum_count"] !=
            (2049 if spectrum else 0)):
        raise AssertionError("native read/interpolation did not preserve inputs")
    return result


def run(dll: Path, scratch: Path) -> dict:
    if not scratch.is_dir():
        raise ValueError("scratch directory does not exist")
    reports = []
    all_signatures = set()
    for spectrum in (False, True):
        cases = [(name, changes, [case(dll, scratch, spectrum, changes,
                                      unbuffered=unbuffered)
                                  for unbuffered in (False, True)])
                 for name, changes in VARIANTS]
        baseline = cases[0][2][0]
        if (baseline["mixed_real_nonzero"] <= 0
                or baseline["mixed_imag_nonzero"] <= 0
                or baseline["inverse_fft_nonzero"] != 4096):
            raise AssertionError("positive Growl mix was not active")
        base_signature = tuple(baseline[key] for key in OUTPUT_KEYS)
        differences = []
        launcher_mismatches = []
        for name, changes, results in cases:
            signatures = [tuple(result[key] for key in OUTPUT_KEYS)
                          for result in results]
            all_signatures.update(signatures)
            if signatures[0] != signatures[1]:
                launcher_mismatches.append(name)
            if signatures[0] != base_signature:
                differences.append(name)
        if launcher_mismatches:
            raise AssertionError("Growl output varies with Python output buffering")
        fill_controls = {}
        for name, changes in (VARIANTS[0], VARIANTS[-1]):
            filled = case(dll, scratch, spectrum, changes, buffer_fill=165)
            signature = tuple(filled[key] for key in OUTPUT_KEYS)
            fill_controls[name] = signature == base_signature
            all_signatures.add(signature)
        if not all(fill_controls.values()):
            raise AssertionError("Growl output varies with prefilled work buffers")
        zero = case(dll, scratch, spectrum, VARIANTS[-1][1],
                    "native_zero_gain")
        if any(zero[key] for key in ("mixed_real_nonzero",
                                     "mixed_imag_nonzero",
                                     "inverse_fft_nonzero")):
            raise AssertionError("zero-gain control is not zero")
        reports.append({
            "vqm_disk_spectrum": spectrum,
            "interpolated_spectrum_bins": baseline[
                "interpolated_vqm_spectrum_count"],
            "case_count": len(cases) * 2,
            "different_output_variants": differences,
            "launcher_mismatches": launcher_mismatches,
            "alternate_buffer_fill_controls": fill_controls,
            "baseline_output": {key: baseline[key] for key in OUTPUT_KEYS},
            "zero_gain_control": True,
        })
    return {
        "dse_sha256": SHA256,
        "feature_bits": {"4": "+0x14", "6": "+0x1c",
                         "14": "+0x20c", "15": "+0x214",
                         "16": "+0x218", "17": "+0x21c",
                         "18": "+0x220"},
        "variants": [{"name": name, "changed_scalars": changes}
                     for name, changes in VARIANTS],
        "spectrum_cases": reports,
        "distinct_positive_output_signatures": len(all_signatures),
        "scope": "Three owned VQM frames per case, constructed Growl "
                 "work buffers explicitly zeroed, native read and "
                 "interpolation, native PCM target spectrum, frequency mix, "
                 "inverse FFT. Each positive case is run with buffered and "
                 "unbuffered Python output; baseline and combined variants "
                 "also use a different prefill byte. This tests the listed scalar perturbations in "
                 "one active path, not full host audio or authoring formulas.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--scratch", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.scratch)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"cases": sum(item["case_count"] for item in report[
                          "spectrum_cases"]),
                      "distinct_outputs": report[
                          "distinct_positive_output_signatures"],
                      "different_variants": [item["different_output_variants"]
                                             for item in report[
                                                 "spectrum_cases"]]}))


if __name__ == "__main__":
    main()
