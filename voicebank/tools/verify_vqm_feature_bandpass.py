"""Map the default DRS VQM bit6 response to one owned secondary tone."""

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
PRIMARY_HZ = 441
SECONDARY_HZ = tuple(range(6000, 10501, 250))


def measure(dll: Path, secondary_hz: int) -> dict:
    samples = [math.sin(math.tau * PRIMARY_HZ * index / SAMPLE_RATE)
               + 0.1 * math.sin(math.tau * secondary_hz * index / SAMPLE_RATE + 0.31)
               for index in range(SAMPLE_COUNT)]
    rms = math.sqrt(statistics.fmean(value * value for value in samples))
    rows = capture(dll, [f32(5000 * value / rms) for value in samples],
                   PRIMARY_HZ, PRIMARY_HZ)
    interior = [rows[index] for index in range(8, 36)]
    if len(interior) != 28:
        raise AssertionError("unexpected feature frame count")
    return {
        "secondary_hz": secondary_hz,
        "interior_frames": len(interior),
        "median_bit6": statistics.median(row[6] for row in interior),
        "median_bit18": statistics.median(row[18] for row in interior),
    }


def run(dll: Path) -> dict:
    cases = [measure(dll, secondary_hz) for secondary_hz in SECONDARY_HZ]
    by_hz = {row["secondary_hz"]: row["median_bit6"] for row in cases}
    if not (max(by_hz[hz] for hz in range(6000, 7001, 250)) < 0.001
            and min(by_hz[hz] for hz in range(7250, 8751, 250)) > 0.05
            and max(by_hz[hz] for hz in range(9000, 10501, 250)) < 0.001):
        raise AssertionError("tested frequency response changed")
    return {
        "dse_sha256": SHA256,
        "sample_rate": SAMPLE_RATE,
        "sample_count": SAMPLE_COUNT,
        "primary_hz": PRIMARY_HZ,
        "secondary_coefficient": 0.1,
        "target_rms_pcm16_units": 5000,
        "cases": cases,
        "scope": "Owned equal-RMS PCM and default external-pitch DRS batch. This localizes a tested frequency response; it is not the exact bit6 production formula or VQM authoring preset.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"cases": len(report["cases"]), "frames_per_case": 28}))


if __name__ == "__main__":
    main()
