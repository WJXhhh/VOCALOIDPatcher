"""Probe VQM feature bit6 with equal-RMS owned harmonic signals.

Only aggregate feature medians are written; no voicebank PCM or frame payload
is copied into the report.
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
CASES = {
    220.5: (10, 20, 28, 32, 36, 40, 48),
    441.0: (5, 10, 12, 14, 16, 18, 20, 24),
    882.0: (3, 5, 6, 7, 8, 9, 10, 12, 14, 18),
}


def measure(dll: Path, frequency: float, harmonics: int) -> dict:
    samples = [
        sum(math.sin(math.tau * frequency * k * index / SAMPLE_RATE + k * 0.23) / k
            for k in range(1, harmonics + 1))
        for index in range(SAMPLE_COUNT)
    ]
    rms = math.sqrt(statistics.fmean(value * value for value in samples))
    features = capture(dll, [f32(5000 * value / rms) for value in samples],
                       frequency, frequency)
    interior = [features[index] for index in range(8, 36)]
    if len(interior) != 28:
        raise AssertionError("unexpected feature frame count")
    return {
        "fundamental_hz": frequency,
        "harmonic_count": harmonics,
        "highest_harmonic_hz": frequency * harmonics,
        "interior_frames": len(interior),
        "median_bit6": statistics.median(row[6] for row in interior),
        "median_bit18": statistics.median(row[18] for row in interior),
    }


def run(dll: Path) -> dict:
    cases = [measure(dll, frequency, count)
             for frequency, counts in CASES.items() for count in counts]
    by_pair = {(row["fundamental_hz"], row["highest_harmonic_hz"]): row
               for row in cases}
    for frequency in CASES:
        low = by_pair[(frequency, 6174.0)]["median_bit6"]
        high = by_pair[(frequency, 7056.0)]["median_bit6"]
        if not (low < 0.001 and high > 0.001 and high > 10 * low):
            raise AssertionError("tested high-harmonic response changed")
    return {
        "dse_sha256": SHA256,
        "sample_rate": SAMPLE_RATE,
        "sample_count": SAMPLE_COUNT,
        "target_rms_pcm16_units": 5000,
        "harmonic_amplitude": "1/k, phase k*0.23 radians",
        "cases": cases,
        "scope": "Default external-pitch DRS batch and owned equal-RMS PCM. A shared upper-frequency transition is observed, not an exact bit6 formula or VQM authoring preset.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"cases": len(report["cases"]),
                      "frames_per_case": 28}))


if __name__ == "__main__":
    main()
