"""Check the VQM PCM-to-spectrum helper with an owned 512-sample signal."""

from __future__ import annotations

import argparse
import cmath
import ctypes as c
import json
import math
from pathlib import Path

from audit_frm2_numeric import B, F, I, Native, P, Q, SHA256, f32


def run(dll: Path) -> dict:
    native = Native(dll)
    initialize = native.fn(0x147d10, P, P, I, B, F, I, I, F, I)
    analyze = native.fn(0x14ab30, Q, P, P, I, B, Q, P, P, P, B, P, F, I)
    context = native.alloc(0x330)
    initialize(context, 512, 0, 8.0, 50, 0, 0.0, 2)
    sample_count = 512
    input_pcm = [f32(2400.0 * math.sin(math.tau * 4 * n / sample_count))
                 for n in range(sample_count)]
    input_address = native.array(F, input_pcm)
    window_address = P.from_address(context + 8).value
    if not window_address:
        raise AssertionError("FFT context did not allocate window coefficients")
    cases = []
    for scale in (1.0, 2.0, 8.0):
        transform_length = sample_count * int(scale)
        expected_nyquist_index = transform_length // 2
        checked_bins = (0, 4 * int(scale), 8 * int(scale), expected_nyquist_index)
        output = {}
        for mode in (0, 1):
            amplitudes = P()
            phases = P()
            bins = I(-1)
            result = analyze(context, input_address, sample_count, 0, 0,
                             c.byref(amplitudes), c.byref(phases), c.byref(bins),
                             mode, None, scale, 1)
            if result != 0 or bins.value != expected_nyquist_index or not amplitudes.value or not phases.value:
                raise AssertionError(f"Unexpected FFT result for scale {scale}, mode {mode}: {result}, {bins.value}")
            output[mode] = {
                "amplitude": [F.from_address(amplitudes.value + 4 * k).value
                              for k in checked_bins],
                "phase": [F.from_address(phases.value + 4 * k).value
                          for k in checked_bins],
            }
        window = [F.from_address(window_address + 4 * n).value for n in range(sample_count)]
        direct = {}
        for k in checked_bins:
            value = sum(input_pcm[n] * window[n] * cmath.exp(
                -1j * math.tau * k * n / transform_length)
                for n in range(sample_count))
            direct[str(k)] = abs(value)
        linear = output[1]["amplitude"]
        log_values = output[0]["amplitude"]
        if any(not math.isfinite(value) for value in linear + log_values):
            raise AssertionError("FFT returned nonfinite amplitude")
        direct_error = max(abs(actual - direct[str(k)]) for k, actual in
                           zip(checked_bins, linear))
        peak_relative_error = abs(linear[1] - direct[str(checked_bins[1])]) / direct[str(checked_bins[1])]
        if direct_error > 0.25 or peak_relative_error > 0.001:
            raise AssertionError(f"FFT magnitude differs from direct DFT at scale {scale}: {direct_error}, {peak_relative_error}")
        log_predictions = [f32(20.0 * math.log10(value / 32768.0)) if value > 0 else None
                           for value in linear]
        log_errors = [abs(actual - predicted) for actual, predicted in
                      zip(log_values, log_predictions) if predicted is not None]
        if max(log_errors) > 0.0001:
            raise AssertionError(f"FFT log amplitude differs at scale {scale}: {max(log_errors)}")
        cases.append({
            "scale_argument": scale,
            "window_kind": 0,
            "nyquist_index": expected_nyquist_index,
            "bin_count_including_nyquist": expected_nyquist_index + 1,
            "checked_bins": checked_bins,
            "direct_dft_magnitude_max_error": direct_error,
            "peak_dft_magnitude_relative_error": peak_relative_error,
            "mode0_vs_mode1_log_max_error": max(log_errors),
            "mode0_amplitudes": log_values,
        })
    return {
        "dse_sha256": SHA256,
        "fft_entry_rva": "0x14ab30",
        "input_samples": sample_count,
        "input_origin": "owned 2400-amplitude four-cycle sine",
        "cases": cases,
        "scope": "Isolated FFT helper and owned PCM; not the complete Growl event pipeline.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps([{key: case[key] for key in (
        "scale_argument", "bin_count_including_nyquist", "direct_dft_magnitude_max_error",
        "mode0_vs_mode1_log_max_error")} for case in result["cases"]]))


if __name__ == "__main__":
    main()
