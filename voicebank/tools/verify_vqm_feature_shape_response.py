"""Compare bit-13 feature responses to equal-RMS, owned PCM waveforms."""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import random
import statistics

from audit_frm2_numeric import SHA256, f32
from verify_vqm_feature_gain_response import capture


SAMPLE_COUNT = 11025
SAMPLE_RATE = 44100
FREQUENCY = 440


def equal_rms(samples: list[float]) -> list[float]:
    rms = math.sqrt(statistics.fmean(value * value for value in samples))
    if not math.isfinite(rms) or rms <= 0:
        raise ValueError("waveform has no finite energy")
    return [f32(5000 * value / rms) for value in samples]


def describe(dll: Path, samples: list[float]) -> dict:
    values = capture(dll, equal_rms(samples), FREQUENCY, FREQUENCY)
    interior = [values[index] for index in range(8, 36)]
    if len(interior) != 28:
        raise AssertionError("expected 28 aligned interior feature frames")
    return {
        "interior_frames": len(interior),
        "median_feature2_energy": statistics.median(row[2] for row in interior),
        "median_bit6": statistics.median(row[6] for row in interior),
        "median_bit16": statistics.median(row[16] for row in interior),
        "median_bit17": statistics.median(row[17] for row in interior),
        "median_bit18": statistics.median(row[18] for row in interior),
    }


def run(dll: Path) -> dict:
    sine = [math.sin(math.tau * FREQUENCY * index / SAMPLE_RATE)
            for index in range(SAMPLE_COUNT)]
    harmonic = [sum(math.sin(math.tau * FREQUENCY * k * index / SAMPLE_RATE
                             + k * 0.23) / k for k in range(1, 6))
                for index in range(SAMPLE_COUNT)]
    noise_rng = random.Random(104)
    noise = [noise_rng.uniform(-1, 1) for _ in range(SAMPLE_COUNT)]
    shapes = {
        "sine": describe(dll, sine),
        "five_harmonics": describe(dll, harmonic),
        "white_noise": describe(dll, noise),
    }
    mixtures = []
    for ratio in (0.0, 0.01, 0.03, 0.1, 0.3, 1.0, 3.0):
        samples = [tone + ratio * random_value
                   for tone, random_value in zip(sine, noise)]
        mixtures.append({"noise_coefficient": ratio, **describe(dll, samples)})
    secondary_tones = []
    for secondary_hz in (500, 1000, 2000, 5000, 10000, 15000):
        samples = [tone + 0.1 * math.sin(
            math.tau * secondary_hz * index / SAMPLE_RATE + 0.31)
            for index, tone in enumerate(sine)]
        secondary_tones.append({"secondary_frequency_hz": secondary_hz,
                                "secondary_coefficient": 0.1,
                                **describe(dll, samples)})
    if not all(left["median_bit6"] < right["median_bit6"]
               and left["median_bit18"] < right["median_bit18"]
               for left, right in zip(mixtures, mixtures[1:])):
        raise AssertionError("tested feature response is not monotonic")
    if shapes["white_noise"]["median_bit6"] <= 1000 * shapes["five_harmonics"]["median_bit6"]:
        raise AssertionError("bit6 did not separate owned noise and harmonic PCM")
    if (max(row["median_bit6"] for row in secondary_tones) >= 0.001
            or mixtures[3]["median_bit6"] <=
            100 * max(row["median_bit6"] for row in secondary_tones)):
        raise AssertionError("bit6 did not separate noise from a secondary tone")
    if any(row["median_bit17"] != 0
           for row in [*shapes.values(), *mixtures, *secondary_tones]):
        raise AssertionError("default external-pitch bit17 unexpectedly changed")
    energy_values = [row["median_feature2_energy"]
                     for row in [*shapes.values(), *mixtures, *secondary_tones]]
    return {
        "dse_sha256": SHA256,
        "sample_rate": SAMPLE_RATE,
        "signal_frequency_hz": FREQUENCY,
        "target_rms_pcm16_units": 5000,
        "shapes": shapes,
        "sine_noise_mixtures": mixtures,
        "sine_secondary_tones": secondary_tones,
        "median_energy_relative_span":
            (max(energy_values) - min(energy_values)) / statistics.fmean(energy_values),
        "scope": "Equal-RMS synthetic PCM in the default external-pitch DRS batch; monotonicity identifies sensitivity, not a complete feature-production formula.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "shape_cases": len(report["shapes"]),
        "mixtures": len(report["sine_noise_mixtures"]),
        "secondary_tones": len(report["sine_secondary_tones"]),
        "interior_frames_per_case": 28,
        "median_energy_relative_span": report["median_energy_relative_span"],
    }))


if __name__ == "__main__":
    main()
