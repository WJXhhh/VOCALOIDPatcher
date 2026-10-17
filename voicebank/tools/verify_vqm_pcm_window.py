"""Exercise Growl's initialized PCM-window sampler with owned float samples."""

from __future__ import annotations

import argparse
import cmath
import ctypes as c
import json
import math
from pathlib import Path

from audit_frm2_numeric import B, D, F, I, Native, P, Q, SHA256, f32


def run(dll: Path) -> dict:
    native = Native(dll)
    construct_growl = native.fn(0x155e70, P, P, I, D, F, B, I, B, B, B)
    sample_window = native.fn(0x152ae0, None, P, P, D, D, P, I)
    analyze = native.fn(0x14ab30, Q, P, P, I, B, Q, P, P, P, B, P, F, I)
    growl = native.alloc(0x200)
    if construct_growl(growl, 1024, 0.01, 44100.0, 0, 1, 0, 0, 0) != growl:
        raise AssertionError("Growl construction did not return its object")
    table = native.lib._handle + 0xa1fff0
    if I.from_address(table).value != 16 or not P.from_address(table + 8).value:
        raise AssertionError("Growl did not initialize its 16-point interpolation table")

    input_size = 4096
    shift = 11
    original = [f32(320.0 * math.sin(math.tau * n / 73.0)
                    + 90.0 * math.cos(math.tau * n / 127.0))
                for n in range(input_size)]
    shifted = [0.0] * shift + original[:-shift]
    input_original = native.array(F, original)
    input_shifted = native.array(F, shifted)
    frame_count = 64
    constant = native.array(F, [500.0] * input_size)
    constant_output = (F * frame_count)()
    sample_window(table, constant, 512.75, 0.75,
                  c.addressof(constant_output), frame_count)
    coefficient_address = P.from_address(table + 8).value
    phase_sums = [sum(F.from_address(coefficient_address + 4 * (phase * 16 + tap)).value
                      for tap in range(16)) for phase in (0, 4, 8, 12)]
    constant_predictions = [500.0 * phase_sums[(3 - index) % 4]
                            for index in range(frame_count)]
    constant_relative_error = max(
        abs(actual - predicted) / abs(predicted)
        for actual, predicted in zip(constant_output, constant_predictions)
    )
    if constant_relative_error > 1e-6:
        raise AssertionError(f"PCM-window table gain differs: {constant_relative_error}")
    cases = []
    for position, step in ((320.25, 1.0), (512.75, 0.75), (777.5, 1.25)):
        first = (F * (frame_count + 4))(*([1234567.0] * (frame_count + 4)))
        second = (F * (frame_count + 4))(*([1234567.0] * (frame_count + 4)))
        sample_window(table, input_original, position, step,
                      c.addressof(first) + 8, frame_count)
        sample_window(table, input_shifted, position + shift, step,
                      c.addressof(second) + 8, frame_count)
        before_after = (first[0], first[1], first[-2], first[-1],
                        second[0], second[1], second[-2], second[-1])
        if before_after != (1234567.0,) * 8:
            raise AssertionError("PCM-window sampler changed output guards")
        actual = [first[n + 2] for n in range(frame_count)]
        translated = [second[n + 2] for n in range(frame_count)]
        if not all(math.isfinite(value) for value in actual + translated):
            raise AssertionError("PCM-window sampler produced nonfinite values")
        errors = [abs(a - b) for a, b in zip(actual, translated)]
        if max(errors) > 0.0001:
            raise AssertionError(f"PCM-window translation differs: {max(errors)}")
        tap_errors = []
        for index, value in enumerate(actual):
            coordinate = position + index * step
            base = math.floor(coordinate)
            phase = int((coordinate - base) * 16)
            predicted = sum(
                original[base + tap]
                * F.from_address(coefficient_address + 4 * (phase * 16 + tap)).value
                for tap in range(16)
            )
            tap_errors.append(abs(value - predicted))
        if max(tap_errors) > 3.0:
            raise AssertionError(f"PCM-window table dot product differs: {max(tap_errors)}")
        cases.append({
            "sample_position": position,
            "sample_step": step,
            "output_count": frame_count,
            "input_shift_samples": shift,
            "translation_max_error": max(errors),
            "output_min": min(actual),
            "output_max": max(actual),
            "table_dot_max_absolute_error": max(tap_errors),
        })
    spectral_input = (F * 512)()
    sample_window(table, input_original, 320.25, 0.75,
                  c.addressof(spectral_input), 512)
    fft_context = P.from_address(growl + 0x18).value
    fft_scale = I.from_address(growl + 0x44).value
    window_kind = B.from_address(growl + 0x5c).value
    if not fft_context or fft_scale != 1 or window_kind != 0:
        raise AssertionError("Owned Growl object FFT configuration differs")
    spectrum = {}
    checked_bins = (0, 4, 8, 256)
    for mode in (0, 1):
        amplitudes = P()
        phases = P()
        nyquist = I(-1)
        result = analyze(fft_context, c.addressof(spectral_input), 512,
                         window_kind, 0, c.byref(amplitudes), c.byref(phases),
                         c.byref(nyquist), mode, None, float(fft_scale), 1)
        if result != 0 or nyquist.value != 256 or not amplitudes.value or not phases.value:
            raise AssertionError("Growl FFT context did not analyze owned PCM window")
        spectrum[mode] = [F.from_address(amplitudes.value + 4 * k).value
                          for k in checked_bins]
    log_errors = [abs(saved - f32(20.0 * math.log10(linear / 32768.0)))
                  for saved, linear in zip(spectrum[0], spectrum[1]) if linear > 0]
    if not log_errors or max(log_errors) > 0.0001:
        raise AssertionError("Connected Growl FFT magnitude scale differs")
    fft_window_address = P.from_address(fft_context + 8).value
    if not fft_window_address:
        raise AssertionError("Growl FFT context did not allocate coefficients")
    fft_coefficients = [F.from_address(fft_window_address + 4 * index).value
                        for index in range(512)]
    direct = [abs(sum(spectral_input[index] * fft_coefficients[index]
                      * cmath.exp(-1j * math.tau * k * index / 512)
                      for index in range(510))) for k in checked_bins]
    direct_relative_error = max(
        abs(actual - expected) / max(1.0, expected)
        for actual, expected in zip(spectrum[1][:-1], direct[:-1])
    )
    impulse_checks = []
    impulse_input = (F * 512)()
    for index in (0, 1, 2, 127, 255, 256, 509, 510, 511):
        impulse_input[index] = 1.0
        impulse_amplitudes = P()
        impulse_phases = P()
        impulse_nyquist = I(-1)
        analyze(fft_context, c.addressof(impulse_input), 512,
                window_kind, 0, c.byref(impulse_amplitudes), c.byref(impulse_phases),
                c.byref(impulse_nyquist), 1, None, float(fft_scale), 1)
        dc = F.from_address(impulse_amplitudes.value).value
        phase = F.from_address(impulse_phases.value).value
        impulse_checks.append((index, -dc if abs(phase) > 1.0 else dc,
                               fft_coefficients[index]))
        impulse_input[index] = 0.0
    if direct_relative_error > 0.001 or spectrum[1][-1] != 0.0:
        raise AssertionError(f"Connected Growl FFT differs from direct DFT: {direct_relative_error}; native={spectrum[1]}; direct={direct}; impulse={impulse_checks}")
    growl_two = native.alloc(0x200)
    if construct_growl(growl_two, 1024, 0.01, 44100.0, 0, 2, 0, 0, 0) != growl_two:
        raise AssertionError("Second Growl configuration did not initialize")
    fft_context_two = P.from_address(growl_two + 0x18).value
    second_amplitudes = P()
    second_phases = P()
    second_nyquist = I(-1)
    analyze(fft_context_two, c.addressof(spectral_input), 512, 0, 0,
            c.byref(second_amplitudes), c.byref(second_phases),
            c.byref(second_nyquist), 1, None, 2.0, 1)
    if second_nyquist.value != 512 or not second_amplitudes.value:
        raise AssertionError("Second Growl FFT configuration differs")
    checked_bins_two = (0, 8, 16, 512)
    spectrum_two = [F.from_address(second_amplitudes.value + 4 * k).value
                    for k in checked_bins_two]
    fft_coefficients_two_address = P.from_address(fft_context_two + 8).value
    fft_coefficients_two = [F.from_address(fft_coefficients_two_address + 4 * index).value
                            for index in range(512)]
    direct_two = [abs(sum(spectral_input[index] * fft_coefficients_two[index]
                          * cmath.exp(-1j * math.tau * k * index / 1024)
                          for index in range(510))) for k in checked_bins_two[:-1]]
    direct_two_error = max(abs(actual - expected) / max(1.0, expected)
                           for actual, expected in zip(spectrum_two[:-1], direct_two))
    if direct_two_error > 0.001 or spectrum_two[-1] != 0.0:
        raise AssertionError("Second Growl FFT magnitude differs from direct DFT")
    second_log = P()
    second_log_phase = P()
    second_log_nyquist = I(-1)
    analyze(fft_context_two, c.addressof(spectral_input), 512, 0, 0,
            c.byref(second_log), c.byref(second_log_phase),
            c.byref(second_log_nyquist), 0, None, 2.0, 1)
    log_two_errors = [abs(F.from_address(second_log.value + 4 * k).value
                          - f32(20.0 * math.log10(linear / 32768.0)))
                      for k, linear in zip(checked_bins_two, spectrum_two) if linear > 0]
    if not log_two_errors or max(log_two_errors) > 0.0001:
        raise AssertionError("Second Growl FFT log amplitude differs")
    impulse_two = []
    for index in (509, 510, 511):
        impulse_input[index] = 1.0
        impulse_amplitudes = P()
        impulse_phases = P()
        impulse_nyquist = I(-1)
        analyze(fft_context_two, c.addressof(impulse_input), 512, 0, 0,
                c.byref(impulse_amplitudes), c.byref(impulse_phases),
                c.byref(impulse_nyquist), 1, None, 2.0, 1)
        impulse_two.append((index, F.from_address(impulse_amplitudes.value).value))
        impulse_input[index] = 0.0
    size_cases = []
    for growl_object, scale in ((growl, 1), (growl_two, 2)):
        context_for_size = P.from_address(growl_object + 0x18).value
        for count in (1025, 2049, 4097):
            generated = (F * count)()
            sample_window(table, input_original, 320.25, 0.75,
                          c.addressof(generated), count)
            amplitudes = P()
            phases = P()
            nyquist = I(-1)
            analyze(context_for_size, c.addressof(generated), count, 0, 0,
                    c.byref(amplitudes), c.byref(phases), c.byref(nyquist),
                    1, None, float(scale), 1)
            if nyquist.value <= 32 or not amplitudes.value:
                raise AssertionError(f"Growl FFT size invalid for {count} samples, scale {scale}: got {nyquist.value}")
            chosen_bins = (0, 16, 32, nyquist.value)
            linear_values = [F.from_address(amplitudes.value + 4 * k).value
                             for k in chosen_bins]
            log_amplitudes = P()
            log_phases = P()
            log_nyquist = I(-1)
            analyze(context_for_size, c.addressof(generated), count, 0, 0,
                    c.byref(log_amplitudes), c.byref(log_phases), c.byref(log_nyquist),
                    0, None, float(scale), 1)
            errors = [abs(F.from_address(log_amplitudes.value + 4 * k).value
                          - f32(20.0 * math.log10(value / 32768.0)))
                      for k, value in zip(chosen_bins, linear_values) if value > 0]
            if log_nyquist.value != nyquist.value or not errors or max(errors) > 0.0001:
                raise AssertionError("Growl FFT size-matrix log scale differs")
            size_cases.append({
                "input_count": count,
                "scale": scale,
                "nyquist_index": nyquist.value,
                "bin_count_including_nyquist": nyquist.value + 1,
                "mode0_vs_mode1_log_max_error": max(errors),
            })
    return {
        "dse_sha256": SHA256,
        "growl_constructor_rva": "0x155e70",
        "sample_window_rva": "0x152ae0",
        "table_width": I.from_address(table).value,
        "constant_response_table_sum_max_relative_error": constant_relative_error,
        "coefficient_sum_min_max": [min(phase_sums), max(phase_sums)],
        "cases": cases,
        "connected_window_fft": {
            "input_count": 512,
            "nyquist_index": 256,
            "checked_bins": checked_bins,
            "mode0_vs_mode1_log_max_error": max(log_errors),
            "mode1_vs_direct_dft_max_relative_error": direct_relative_error,
            "excluded_final_input_samples": 2,
            "nyquist_amplitude": spectrum[1][-1],
        },
        "scale2_tail_probe": {
            "nyquist_index": second_nyquist.value,
            "tail_dc_amplitudes": impulse_two,
            "mode1_vs_direct_dft_max_relative_error": direct_two_error,
            "mode0_vs_mode1_log_max_error": max(log_two_errors),
        },
        "runtime_size_matrix": size_cases,
        "scope": "Owned PCM and connected window/FFT helpers on a constructed Growl object; not the complete Growl event.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = run(args.dll)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps([{
        "sample_position": row["sample_position"],
        "sample_step": row["sample_step"],
        "translation_max_error": row["translation_max_error"],
    } for row in result["cases"]]))


if __name__ == "__main__":
    main()
