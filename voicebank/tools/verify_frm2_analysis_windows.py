#!/usr/bin/env python3
"""Independent DRS window and FFT checks, using only owned numeric signals.

The installed DLL is hash-pinned. Lookup windows read local runtime coefficients
as inputs; no proprietary table or PCM is written to the report.
"""
from __future__ import annotations

import argparse
import cmath
import ctypes as c
import json
from itertools import product
import math
from pathlib import Path
import random

from audit_frm2_numeric import Native, SHA256, F, D, I, P, Q, B, f32, stats


def window(native, size, kind, lookup=0, derivative=0):
    if lookup and kind not in (10, 13, 14, 15) and size <= 10001:
        row = kind if kind < 11 else kind - 1
        step = int(10001 * 65536 / (size - 1))
        raw = [F.from_address(native.lib._handle + 0x50e1a0 +
                             4 * (row * 10001 + ((32768 + i * step) >> 16))).value
               for i in range(size)]
        scale = f32(2 / sum(raw))
        return [f32(v * scale) for v in raw]
    raw = []
    if 1 <= kind <= 7:
        beta = (1.7, 1.8, 1.9, 2., 2.5, 3., 3.5)[kind - 1] * math.pi

        def bessel(value):
            result = 1 + value * value
            factorial = 1.
            for k in range(2, 50):
                factorial *= k
                result += (value ** k / factorial) ** 2
            return result

        normalizer = bessel(beta * .5)
        right = [f32(bessel(math.sqrt(1 - (i / (size * .5)) ** 2) * beta * .5) / normalizer)
                 for i in range(size - size // 2)]
        raw = list(reversed(right[1:] if size % 2 else right)) + right
        denominator = sum(right + list(reversed(right[1:] if size % 2 else right))) * .5
        return [f32(v / denominator) for v in raw]
    coefficients = {
        8: (.44959, .49364, .05677), 9: (.42323, .49755, .07922),
        11: (.40217, .49703, .09892, .00188), 12: (.35875, .48829, .14128, .01168),
    }
    if kind in coefficients:
        cs = [f32(v) for v in coefficients[kind]]
        step = math.tau / (size - 1)
        for i in range(size):
            v = cs[0] - math.cos(i * step) * cs[1] + math.cos(i * (step + step)) * cs[2]
            if len(cs) == 4:
                v -= math.cos(i * (step * 3)) * cs[3]
            raw.append(f32(v))
        return [f32(v / (sum(raw) * .5)) for v in raw]
    total = 0.
    if kind == 13:
        raw = [0.] * size
        divisor = size if size % 2 == 0 else size - 1
        for i in range(size // 2 + 1):
            raw[i] = f32(2 * i / divisor)
            total = f32(total + raw[i])
        for i in range(size // 2):
            raw[size - 1 - i] = f32(2 * i / divisor)
            total = f32(total + raw[size - 1 - i])
    else:
        for i in range(size):
            if kind == 14:
                value = 1.
            elif kind == 0:
                value = .54 - .46 * math.cos(i * math.tau / (size - 1))
            else:
                angle = i * (math.tau / (size - 1)) if kind == 10 else (i / (size - 1)) ** 1.946402604329978 * math.tau
                value = ((.47 - math.cos(angle) * .45) - math.cos(angle + angle) * .01) - math.cos(angle * 3) * .01
            value = f32(value)
            total = f32(total + value)
            raw.append(0. if kind == 15 and i in (0, size - 1) else value)
    denominator = f32(total * .5)
    if kind == 10 and derivative:
        raw = []
        for i in range(size):
            angle = i * (math.tau / (size - 1))
            twice = math.sin(angle + angle) * .01 * math.tau / (size - 1)
            value = ((math.sin(angle * 3) * .01 * math.tau / (size - 1)) * 3
                     + math.sin(angle) * .45 * math.tau / (size - 1) + twice + twice)
            raw.append(f32(-value))
    return [f32(v / denominator) for v in raw]


def fft(values):
    if len(values) == 1:
        return [complex(values[0])]
    even, odd = fft(values[::2]), fft(values[1::2])
    rotated = [v * cmath.exp(-2j * math.pi * k / len(values)) for k, v in enumerate(odd)]
    return [a + b for a, b in zip(even, rotated)] + [a - b for a, b in zip(even, rotated)]


def prepare(signal, weights, factor, centered):
    size = len(signal)
    value = int(f32(f32(f32((size - 1) * factor) + 1.) - 2.))
    nfft = min(131072, 1 << value.bit_length())
    products = [f32(a * b) for a, b in zip(signal, weights)]
    data = [0.] * nfft
    if not centered:
        data[:min(size, nfft)] = products[:nfft]
    elif factor < 1 or nfft + 2 < size:
        middle = (size - 1) // 2
        right, left = 0, nfft - 1
        for i in range(middle):
            if size % 2 == 0 or i < middle - 1:
                data[right] = f32(data[right] + products[middle + i])
            if size % 2 == 0 or i > 0:
                data[left] = f32(data[left] + products[middle - i])
                left = (left - 1) % nfft
            right = (right + 1) % nfft
    else:
        middle = (size - 1) // 2
        data[nfft - middle:] = products[:middle]
        data[:middle] = products[middle:2 * middle]
    return data


def verify(native):
    make_window = native.fn(0x4c250, Q, P, I, B, P, I, I)
    window_errors, sums = [], []
    for size in (31, 32, 63, 64, 127, 128):
        for kind in range(16):
            for lookup, derivative in product((0, 1), (0, 1)):
                address = native.array(F, [0.] * size)
                pointer = P(address)
                make_window(0, size, kind, c.byref(pointer), lookup, derivative)
                actual = [F.from_address(address + 4 * k).value for k in range(size)]
                expected = window(native, size, kind, lookup, derivative)
                error = max(abs(a - b) for a, b in zip(actual, expected))
                window_errors.extend(abs(a - b) for a, b in zip(actual, expected))
                if error > 1e-7:
                    raise AssertionError(f'Window {size}/{kind}/{lookup}/{derivative}: {error}')
                sums.append(dict(size=size, kind=kind, lookup=lookup, derivative=derivative,
                                 sum=sum(actual), max_error=error))
    config = native.alloc(0x35f0)
    native.fn(0x90de0, P, P)(config)
    context = native.alloc(0x400)
    native.fn(0x49790, P, P, I, P, I)(context, 0, config, 0)
    analyze = native.fn(0x4e8e0, Q, P, P, I, B, F, P, P, P, B, P, F, I, I, I, I)
    complex_errors, spectral_errors = [], []
    cases = 0
    rng = random.Random(314159)
    for size in (31, 32, 33, 63, 64, 65):
        signals = ([1.] * size, [(-1.) ** i for i in range(size)],
                   [1. if i == size // 3 else 0. for i in range(size)],
                   [f32(math.sin(i * .61) * (i / size)) for i in range(size)],
                   [f32(rng.uniform(-1, 1)) for _ in range(size)])
        # This FFT entry explicitly requests derivative=0 from its window
        # generator; derivative windows are verified separately above.
        for kind, lookup, factor, centered in product(range(16), (0, 1), (.5, 1., 2.), (0, 1)):
            for signal in signals:
                output, packed, half = P(), P(), I()
                result = analyze(context, native.array(F, signal), size, kind, 1.,
                                 c.byref(output), 0, c.byref(half), 2, c.byref(packed),
                                 factor, lookup, 0, 0, centered)
                if result:
                    raise AssertionError(f'Native FFT returned {result}')
                w = window(native, size, kind, lookup)
                data = prepare(signal, w, factor, centered)
                if len(data) != half.value * 2:
                    raise AssertionError('FFT size differs')
                transformed = fft(data)
                actual_complex = [complex(F.from_address(packed.value + 8 * k).value,
                                          F.from_address(packed.value + 8 * k + 4).value)
                                  for k in range(half.value + 1)]
                error = max(abs(a - b) for a, b in zip(actual_complex, transformed))
                if error > 2e-6:
                    raise AssertionError(f'FFT {size}/{kind}/{lookup}/{factor}/{centered}: {error}')
                complex_errors.extend(abs(a - b) for a, b in zip(actual_complex, transformed))
                # This DLL returns DC magnitude, interior power, and reads
                # Im(DC) as its last scalar bin despite using CCS FFT output.
                expected = [abs(actual_complex[0].real)]
                expected += [f32(f32(z.real * z.real) + f32(z.imag * z.imag))
                             for z in actual_complex[1:-1]]
                expected.append(abs(actual_complex[0].imag))
                spectral_errors.extend(abs(v - F.from_address(output.value + 4 * k).value)
                                       for k, v in enumerate(expected))
                cases += 1
    # A separate legacy helper lacks a transform in this binary. Its identity-
    # window impulse output must not be cited as a working FFT implementation.
    legacy = native.alloc(0x400)
    P.from_address(legacy + 0x190).value = native.array(F, [0.] * 32)
    legacy_call = native.fn(0x519a0, None, P, P, P, I, P, I, B)
    legacy_dft_mismatches = 0
    for slot in (0, 1, 4):
        signal = [0.] * 32
        signal[slot] = 1.
        output = native.array(F, [0.] * 17)
        legacy_call(legacy, native.array(F, signal), native.array(F, [1.] * 32), 32, output, 32, 0)
        legacy_dft_mismatches += sum(F.from_address(output + 4 * k).value != 1. for k in range(17))
    if max(complex_errors) > 2e-6 or max(spectral_errors) > 2e-6:
        raise AssertionError(dict(complex=stats(complex_errors), scalar=stats(spectral_errors)))
    return dict(window_coefficients=stats(window_errors), windows=sums,
                fft_cases=cases, complex_fft=stats(complex_errors), scalar_spectrum=stats(spectral_errors),
                legacy_helper=dict(impulses=3, bins=51, dft_mismatches=legacy_dft_mismatches),
                scope='16 window IDs, analytic/table branches, arbitrary owned signals, centered/folded/zero-padded FFT')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = dict(dll_sha256=SHA256, **verify(Native(args.dll)))
    args.output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in report.items() if k != 'windows'}, indent=2))


if __name__ == '__main__':
    main()
