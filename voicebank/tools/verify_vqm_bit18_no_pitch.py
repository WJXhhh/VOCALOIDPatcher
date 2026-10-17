"""Observe the DRS internal bit18 output when owned PCM has no stable F0.

Each case runs in a separate process. Only counts and aggregate values are
written; no generated or installed PCM data is retained.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import random
import statistics
import subprocess
import sys

from audit_frm2_numeric import F, P, Q, SHA256
from probe_frm2_batch_outputs import run as analyze_batch
from verify_vqm_bit18_band_ratio import replay


CASES = ("silence_auto_zero", "silence_external_zero",
         "silence_external_440", "noise_auto_zero")


def one(dll: Path, name: str) -> dict:
    silence = [0.0] * 11025
    generator = random.Random(326)
    noise = [generator.uniform(-5000, 5000) for _ in range(11025)]
    automatic = int(name in ("silence_auto_zero", "noise_auto_zero"))
    pitch_seed = 440.0 if name == "silence_external_440" else 0.0
    pcm = noise if name == "noise_auto_zero" else silence
    observed = []

    def step_observer(native, context: int, frame: int) -> None:
        feature = P.from_address(frame + 0x128).value
        if not feature:
            raise AssertionError("no feature block on DRS output")
        stored = F.from_address(frame + 0xf0).value
        mask = Q.from_address(frame + 0x50).value
        f0 = 440 * 2 ** (stored / 1200) if mask & (1 << 31) else stored
        observed.append({
            "f0": f0,
            "bit6": F.from_address(feature + 0x1c).value,
            "bit16": F.from_address(feature + 0x218).value,
            "bit17": F.from_address(feature + 0x21c).value,
            "bit18": F.from_address(feature + 0x220).value,
        })

    result = analyze_batch(dll, automatic, 1, "steady", 440,
                           pitch_seed=pitch_seed, pcm_override=pcm,
                           streaming=True, step_observer=step_observer)
    if result["frames"] != len(observed):
        raise AssertionError("DRS no-pitch frame count changed")
    zero_pitch = [row["bit18"] for row in observed if row["f0"] <= 0]
    voiced = [row["bit18"] for row in observed if row["f0"] > 0]
    if name == "silence_external_440":
        expected = replay([0.0] * 2048, 440, 44100)
        if len(voiced) != 44 or any(value != expected for value in voiced):
            raise AssertionError("positive-pitch silence ratio changed")
    elif len(zero_pitch) != 44 or any(value != 0 for value in zero_pitch):
        raise AssertionError("zero-pitch internal bit18 fallback changed")
    features = {}
    for bit in (6, 16, 17):
        values = [row[f"bit{bit}"] for row in observed]
        features[str(bit)] = {
            "min": min(values), "median": statistics.median(values),
            "max": max(values), "unique_count": len(set(values)),
            "all_finite": all(math.isfinite(value) for value in values),
        }
    if (features["16"]["min"] != -10 or features["16"]["max"] != -10
            or features["17"]["min"] != 0 or features["17"]["max"] != 0):
        raise AssertionError("DRS bit16/17 fallback changed")
    if name == "noise_auto_zero":
        if features["6"]["min"] <= 0 or features["6"]["unique_count"] <= 1:
            raise AssertionError("noise bit6 did not vary")
    elif features["6"]["min"] != -1 or features["6"]["max"] != -1:
        raise AssertionError("silent bit6 fallback changed")
    return {
        "case": name,
        "frames": len(observed),
        "zero_pitch_frames": len(zero_pitch),
        "positive_pitch_frames": len(voiced),
        "zero_pitch_bit18_unique": sorted(set(zero_pitch)),
        "positive_pitch_bit18_min": min(voiced) if voiced else None,
        "positive_pitch_bit18_max": max(voiced) if voiced else None,
        "all_bit18_finite": all(math.isfinite(row["bit18"])
                                for row in observed),
        "bit18_median": statistics.median(row["bit18"]
                                           for row in observed),
        "other_feature_summaries": features,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--case", choices=CASES)
    args = parser.parse_args()
    if args.case:
        print(json.dumps(one(args.dll, args.case)))
        return
    cases = []
    for name in CASES:
        child = subprocess.run(
            [sys.executable, "-u", str(Path(__file__).resolve()),
             "--dll", str(args.dll), "--output", str(args.output),
             "--case", name], capture_output=True, text=True, check=False)
        if child.returncode:
            raise RuntimeError(f"no-pitch DRS case {name} failed: "
                               f"{child.stderr[-500:]}")
        cases.append(json.loads(child.stdout))
    report = {
        "dse_sha256": SHA256,
        "source": "owned silence or deterministic noise through native DRS streaming",
        "cases": cases,
        "scope": "Observed internal bits6/16/17/18 under four owned silence/noise and pitch controls. Does not prove saved VQM authoring inputs or other DRS configurations.",
    }
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps(cases))


if __name__ == "__main__":
    main()
