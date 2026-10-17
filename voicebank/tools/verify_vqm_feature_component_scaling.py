"""Measure VQM feature bit6 against in-band component count and level.

Signals are locally generated; the report contains only aggregate feature values.
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
FUNDAMENTAL_HZ = 200
FREQUENCIES = {
    1: (8000,),
    2: (7800, 8200),
    4: (7600, 7800, 8200, 8400),
    8: tuple(range(7200, 8601, 200)),
}
RATIOS = (0.03, 0.1, 0.3)


def measure(dll: Path, frequencies: tuple[int, ...], ratio: float,
            phases: tuple[float, ...] | None = None) -> dict:
    if phases is None:
        phases = tuple((component + 1) * 0.23
                       for component in range(len(frequencies)))
    if len(phases) != len(frequencies):
        raise ValueError("phase count differs from frequency count")
    primary = [math.sin(math.tau * FUNDAMENTAL_HZ * index / SAMPLE_RATE)
               for index in range(SAMPLE_COUNT)]
    added = [sum(math.sin(math.tau * frequency * index / SAMPLE_RATE
                          + phase)
                 for frequency, phase in zip(frequencies, phases))
             for index in range(SAMPLE_COUNT)]
    primary_rms = math.sqrt(statistics.fmean(value * value
                                             for value in primary))
    added_rms = math.sqrt(statistics.fmean(value * value for value in added))
    combined = [base + ratio * primary_rms * extra / added_rms
                for base, extra in zip(primary, added)]
    total_rms = math.sqrt(statistics.fmean(value * value
                                           for value in combined))
    frames = capture(dll, [f32(5000 * value / total_rms)
                           for value in combined],
                     FUNDAMENTAL_HZ, FUNDAMENTAL_HZ)
    interior = [frames[index] for index in range(8, 36)]
    if len(interior) != 28:
        raise AssertionError("unexpected feature frame count")
    return {
        "component_count": len(frequencies),
        "frequencies_hz": frequencies,
        "added_to_primary_rms_ratio": ratio,
        "median_bit6": statistics.median(row[6] for row in interior),
        "median_bit18": statistics.median(row[18] for row in interior),
        "median_feature2_energy": statistics.median(row[2]
                                                      for row in interior),
    }


def run(dll: Path) -> dict:
    cases = [measure(dll, frequencies, ratio)
             for ratio in RATIOS
             for frequencies in FREQUENCIES.values()]
    by_pair = {(row["added_to_primary_rms_ratio"], row["component_count"]):
               row["median_bit6"] for row in cases}
    for ratio in RATIOS:
        if not all(by_pair[ratio, left] < by_pair[ratio, right]
                   for left, right in ((1, 2), (2, 4), (4, 8))):
            raise AssertionError("bit6 component-count response changed")
    frequencies = FREQUENCIES[8]
    phase_patterns = {
        "ramp": tuple((index + 1) * 0.23 for index in range(8)),
        "zero": (0.0,) * 8,
        "alternating": tuple((index % 2) * math.pi / 2
                             for index in range(8)),
        "spread": tuple((index * index * 1.618) % math.tau
                        for index in range(8)),
    }
    phase_cases = [{"phase_pattern": name,
                    **measure(dll, frequencies, 0.1, phases)}
                   for name, phases in phase_patterns.items()]
    return {
        "dse_sha256": SHA256,
        "sample_rate": SAMPLE_RATE,
        "sample_count": SAMPLE_COUNT,
        "fundamental_hz": FUNDAMENTAL_HZ,
        "target_total_rms_pcm16_units": 5000,
        "frames_per_case": 28,
        "cases": cases,
        "phase_cases": phase_cases,
        "scope": "Owned PCM and default external-pitch DRS batch. Tests level, component-count and phase effects within the bit6-sensitive band; it does not establish the VQM authoring formula.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll)
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({"cases": len(report["cases"])}))


if __name__ == "__main__":
    main()
