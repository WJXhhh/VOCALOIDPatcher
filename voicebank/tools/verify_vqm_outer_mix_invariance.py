"""Compare non-default VQM outer fields through native Growl frequency mixing.

Each owned three-frame case runs in a separate process. The scratch directory
must be supplied explicitly; only report JSON is retained. No installed
voicebank frame or PCM is opened.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256


PAIRS = ((0, 0), (1, 1), (10, 2049), (20, 513), (255, 4097))


def native_case(dll: Path, scratch: Path, outer18: int, outer19: int,
                case: str = "native_positive") -> dict:
    child = subprocess.run(
        [sys.executable, "-u",
         str(Path(__file__).with_name("probe_vqm_growl_frequency_mix.py")),
         "--dll", str(dll), "--scratch", str(scratch),
         "--output", str(scratch / "unused_growl_case.json"),
         "--single-case", case, "--outer18", str(outer18),
         "--outer19", str(outer19)],
        capture_output=True, text=True, check=False)
    if child.returncode:
        raise RuntimeError(f"Growl owned case {outer18}/{outer19} failed: "
                           f"{child.stderr[-500:]}")
    result = json.loads(child.stdout)
    if (result["outer_bit18"] != outer18
            or result["outer_bit19"] != outer19
            or not result["outer_fields_preserved_on_native_read"]):
        raise AssertionError("native read changed tested VQM outer fields")
    return result


def run(dll: Path, scratch: Path) -> dict:
    if not scratch.is_dir():
        raise ValueError("scratch directory does not exist")
    cases = [native_case(dll, scratch, *pair) for pair in PAIRS]
    comparable = [{key: value for key, value in case.items()
                   if key not in ("outer_bit18", "outer_bit19")}
                  for case in cases]
    if any(case != comparable[0] for case in comparable[1:]):
        raise AssertionError("Growl mix differs across tested outer fields")
    baseline = cases[2]
    if (baseline["mixed_real_nonzero"] <= 0
            or baseline["mixed_imag_nonzero"] <= 0
            or baseline["inverse_fft_nonzero"] != 4096):
        raise AssertionError("positive Growl mix was not active")
    zero = native_case(dll, scratch, 255, 4097, "native_zero_gain")
    if (zero["mixed_real_nonzero"] or zero["mixed_imag_nonzero"]
            or zero["inverse_fft_nonzero"]):
        raise AssertionError("zero-gain Growl control is not zero")
    return {
        "dse_sha256": SHA256,
        "tested_outer_pairs": [{"bit18": a, "bit19": b} for a, b in PAIRS],
        "native_read_preserved_all_pairs": True,
        "all_non_outer_results_identical": True,
        "positive_mix": {
            "working_spectrum_bins": baseline["working_frame_1a8"],
            "mixed_real_nonzero": baseline["mixed_real_nonzero"],
            "mixed_imag_nonzero": baseline["mixed_imag_nonzero"],
            "mixed_real_sha256": baseline["mixed_real_sha256"],
            "mixed_imag_sha256": baseline["mixed_imag_sha256"],
            "inverse_fft_length": baseline["inverse_fft_length"],
            "inverse_fft_nonzero": baseline["inverse_fft_nonzero"],
            "inverse_fft_sha256": baseline["inverse_fft_sha256"],
        },
        "non_default_zero_gain": {
            "bit18": 255,
            "bit19": 4097,
            "mixed_real_nonzero": zero["mixed_real_nonzero"],
            "mixed_imag_nonzero": zero["mixed_imag_nonzero"],
            "inverse_fft_nonzero": zero["inverse_fft_nonzero"],
        },
        "scope": "Owned VQM frames through DSE5 native read, native mode-3 target spectrum, Growl event and frequency mixer, and native inverse FFT. The tested outer fields do not affect this active path; their authoring identity and other host paths remain unproven.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--scratch", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.scratch)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"tested_pairs": len(report["tested_outer_pairs"]),
                      "identical": report["all_non_outer_results_identical"],
                      "inverse_fft_nonzero": report["positive_mix"]["inverse_fft_nonzero"]}))


if __name__ == "__main__":
    main()
