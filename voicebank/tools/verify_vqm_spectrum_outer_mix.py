"""Compare owned VQM disk spectra and outer fields in the active Growl mix."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256


PAIRS = ((0, 0), (1, 1), (10, 2049), (20, 513), (255, 4097))
VARIANTS = (("none", None), ("low_peak", -15.0), ("high_peak", 45.0))
OUTPUT_KEYS = ("mixed_real_sha256", "mixed_imag_sha256",
               "inverse_fft_sha256", "mixed_real_nonzero",
               "mixed_imag_nonzero", "inverse_fft_nonzero")


def case(dll: Path, scratch: Path, outer18: int, outer19: int,
         peak: float | None, gain: str = "native_positive") -> dict:
    command = [sys.executable, "-u",
               str(Path(__file__).with_name("probe_vqm_growl_frequency_mix.py")),
               "--dll", str(dll), "--scratch", str(scratch),
               "--output", str(scratch / "unused_growl_case.json"),
               "--single-case", gain, "--outer18", str(outer18),
               "--outer19", str(outer19)]
    if peak is not None:
        command.extend(("--vqm-spectrum", "--vqm-spectrum-peak-db", str(peak)))
    child = subprocess.run(command, capture_output=True, text=True, check=False)
    if child.returncode:
        raise RuntimeError(f"Growl owned case {outer18}/{outer19}/{peak}/{gain} "
                           f"failed: {child.stderr[-1000:]}")
    result = json.loads(child.stdout)
    if (result["outer_bit18"] != outer18 or result["outer_bit19"] != outer19
            or not result["outer_fields_preserved_on_native_read"]
            or result["interpolated_vqm_spectrum_count"] !=
            (0 if peak is None else 2049)):
        raise AssertionError("owned VQM read/interpolation did not preserve inputs")
    return result


def run(dll: Path, scratch: Path) -> dict:
    if not scratch.is_dir():
        raise ValueError("scratch directory does not exist")
    results = {(name, outer18, outer19):
               case(dll, scratch, outer18, outer19, peak)
               for name, peak in VARIANTS for outer18, outer19 in PAIRS}
    spectra = {name: results[(name, 10, 2049)][
        "interpolated_vqm_amplitude_sha256"] for name, _ in VARIANTS}
    if spectra["none"] is not None or not spectra["low_peak"] or not spectra[
            "high_peak"] or spectra["low_peak"] == spectra["high_peak"]:
        raise AssertionError("distinct saved VQM spectra did not reach interpolation")
    signatures = {key: tuple(value[field] for field in OUTPUT_KEYS)
                  for key, value in results.items()}
    if len(set(signatures.values())) != 1:
        raise AssertionError("Growl output changed across tested spectrum/outer inputs")
    baseline = results[("none", 10, 2049)]
    if (baseline["mixed_real_nonzero"] <= 0 or
            baseline["mixed_imag_nonzero"] <= 0 or
            baseline["inverse_fft_nonzero"] != 4096):
        raise AssertionError("positive Growl mix did not produce a full audio block")
    zero = case(dll, scratch, 255, 4097, 45.0, "native_zero_gain")
    if any(zero[field] for field in ("mixed_real_nonzero",
                                      "mixed_imag_nonzero",
                                      "inverse_fft_nonzero")):
        raise AssertionError("zero-gain control is not zero")
    return {
        "dse_sha256": SHA256,
        "outer_pairs": [{"bit18": a, "bit19": b} for a, b in PAIRS],
        "spectrum_variants": [{"name": name, "peak_db": peak,
                               "interpolated_amplitude_sha256": spectra[name]}
                              for name, peak in VARIANTS],
        "case_count": len(results),
        "all_frequency_and_inverse_outputs_identical": True,
        "positive_output": {field: baseline[field] for field in OUTPUT_KEYS},
        "zero_gain_with_spectrum_and_nondefault_outer_fields": True,
        "scope": "Owned three-frame VQM read and interpolation, native mode-3 target "
                 "spectrum, active Growl frequency mix, and inverse FFT. Results "
                 "do not establish the authoring meaning of outer fields or all "
                 "host paths.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--scratch", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.scratch)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"case_count": report["case_count"],
                      "identical": report[
                          "all_frequency_and_inverse_outputs_identical"],
                      "nonzero_time_samples": report[
                          "positive_output"]["inverse_fft_nonzero"]}))


if __name__ == "__main__":
    main()
