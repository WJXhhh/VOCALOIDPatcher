"""Distinguish VQM bit6 band-component count from energy and harmonicity.

All signals are generated locally. Reports contain aggregate feature values,
not audio or frame payloads.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import statistics

from audit_frm2_numeric import SHA256, f32
from verify_vqm_feature_gain_response import capture


SAMPLE_RATE = 44100
SAMPLE_COUNT = 11025


def measure(dll: Path, fundamental: int, frequencies: list[int]) -> dict:
    primary = [math.sin(math.tau * fundamental * index / SAMPLE_RATE)
               for index in range(SAMPLE_COUNT)]
    added = [sum(math.sin(math.tau * frequency * index / SAMPLE_RATE
                          + (component + 1) * 0.23)
                 for component, frequency in enumerate(frequencies))
             for index in range(SAMPLE_COUNT)]
    primary_rms = math.sqrt(statistics.fmean(value * value
                                             for value in primary))
    added_rms = math.sqrt(statistics.fmean(value * value for value in added))
    if added_rms <= 0:
        raise ValueError("added signal has no energy")
    combined = [base + 0.1 * primary_rms * extra / added_rms
                for base, extra in zip(primary, added)]
    total_rms = math.sqrt(statistics.fmean(value * value
                                           for value in combined))
    frames = capture(dll, [f32(5000 * value / total_rms)
                           for value in combined], fundamental, fundamental)
    interior = [frames[index] for index in range(8, 36)]
    if len(interior) != 28:
        raise AssertionError("unexpected feature frame count")
    return {
        "frequency_count": len(frequencies),
        "first_frequency_hz": frequencies[0],
        "last_frequency_hz": frequencies[-1],
        "interior_frames": len(interior),
        "median_feature2_energy": statistics.median(row[2]
                                                      for row in interior),
        "median_bit6": statistics.median(row[6] for row in interior),
        "median_bit18": statistics.median(row[18] for row in interior),
    }


def run(dll: Path) -> dict:
    cases = []
    for fundamental, high, low in (
        (200, [7200 + 200 * k for k in range(8)],
         [1000 + 200 * k for k in range(8)]),
        (250, [7500 + 250 * k for k in range(6)],
         [1000 + 250 * k for k in range(6)]),
    ):
        shapes = {
            "high_single": [high[len(high) // 2]],
            "high_harmonic_group": high,
            "high_shifted_group": [value + 73 for value in high],
            "low_harmonic_group": low,
            "low_shifted_group": [value + 73 for value in low],
        }
        results = {name: measure(dll, fundamental, frequencies)
                   for name, frequencies in shapes.items()}
        single = results["high_single"]["median_bit6"]
        harmonic = results["high_harmonic_group"]["median_bit6"]
        shifted = results["high_shifted_group"]["median_bit6"]
        if (harmonic <= 5 * single
                or abs(harmonic - shifted) >= 0.002
                or any(results[name]["median_bit6"] >= 0.001
                       for name in ("low_harmonic_group",
                                    "low_shifted_group"))):
            raise AssertionError("tested bit6 component response changed")
        energies = [row["median_feature2_energy"]
                    for row in results.values()]
        relative_energy_span = ((max(energies) - min(energies))
                                / statistics.fmean(energies))
        if relative_energy_span >= 0.02:
            raise AssertionError("equal-RMS case energies diverged")
        cases.append({"fundamental_hz": fundamental,
                      "added_to_primary_rms_ratio": 0.1,
                      "median_energy_relative_span": relative_energy_span,
                      "shapes": results})
    return {
        "dse_sha256": SHA256,
        "sample_rate": SAMPLE_RATE,
        "sample_count": SAMPLE_COUNT,
        "target_total_rms_pcm16_units": 5000,
        "cases": cases,
        "scope": "Default external-pitch DRS batch and owned equal-RMS signals. Bit6 responds to the number/distribution of in-band components, not just band RMS or harmonic alignment; its exact transform remains unknown.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll)
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({"fundamentals": len(report["cases"]),
                      "shapes": sum(len(row["shapes"])
                                    for row in report["cases"])}))


if __name__ == "__main__":
    main()
