"""Measure raw diagnostic renders; metrics are not a perceptual click verdict.

Usage: python analyze_transition_artifacts.py <probe-root> [--evidence <json>]
Requires numpy. No audio is modified.
"""
import argparse
import hashlib
import json
from pathlib import Path
import wave

import numpy as np


def read_pcm(path):
    with wave.open(str(path), "rb") as w:
        assert w.getnchannels() == 1 and w.getsampwidth() == 2
        return np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float64), w.getframerate()


def measure(root):
    records = json.loads((root / "confirmed" / "transition-artifacts.json").read_text(encoding="utf-8-sig"))
    baselines = {r["fixture"]: read_pcm(root / "confirmed" / r["file"])[0]
                 for r in records if r.get("variant") == "baseline"}
    rows = []
    processed = []
    for r in records:
        if "file" not in r:
            continue
        assert r["passed"] and r["exactConsumedSourceMap"] and r["comparedFrames"] > 0
        assert r["planningCounters"] == {"Starts": 0, "Steps": 0, "Samples": 0}, r["planningCounters"]
        x, rate = read_pcm(root / "confirmed" / r["file"])
        peak = float(np.max(np.abs(x)))
        delta = np.abs(np.diff(x))
        baseline = baselines[r["fixture"]]
        if r.get("smoothing"):
            smoothed = r["smoothing"]
            y, processed_rate = read_pcm(root / "confirmed" / smoothed["file"])
            assert processed_rate == rate and len(x) == len(y)
            # Default host settings: 60 ms gain window per side. The shorter
            # adaptive waveform repair and zero-crossing search fit inside it.
            outside = np.ones(len(x), dtype=bool)
            margin = round(rate * .06)
            for at in (smoothed["begin"], smoothed["end"]):
                outside[max(0, at - margin):min(len(x), at + margin)] = False
            assert np.array_equal(x[outside], y[outside]), "Smoother changed the part body"
            assert np.max(np.abs(y)) < 32767, "Smoother introduced full-scale clipping"
            processed.append({"fixture": r["fixture"], "variant": r["variant"],
                "outsideWindowsExact": True, "rawPeakPcm16": float(np.max(np.abs(x))),
                "processedPeakPcm16": float(np.max(np.abs(y))),
                "processedMaxAdjacentJump": float(np.max(np.abs(np.diff(y))))})
        windows = []
        max_source_step = 0.0
        max_source_hold = 0
        for segment in r["rows"]:
            split = segment["Split"] - segment["Begin"]
            for side in (segment["sources"][:split], segment["sources"][split:]):
                if len(side) > 1:
                    steps = np.abs(np.diff(side))
                    max_source_step = max(max_source_step, float(np.max(steps)))
                    max_source_hold = max(max_source_hold, int(np.count_nonzero(steps < 1e-4)))
            for label in ("Begin", "Split", "End"):
                at = (segment[label] - 3087) * 256
                lo, hi = max(1, at - 256), min(len(x), at + 256)
                if hi <= lo:
                    continue
                local = x[lo:hi]
                base = baseline[lo:min(hi, len(baseline))]
                windows.append({"frame": segment[label], "label": label,
                    "from": segment["From"], "to": segment["To"],
                    "peak": float(np.max(np.abs(local))),
                    "rms": float(np.sqrt(np.mean(local ** 2))),
                    "maxAdjacentJump": float(np.max(delta[lo - 1:hi - 1])),
                    "baselineMaxAdjacentJump": float(np.max(np.abs(np.diff(base)))) if len(base) > 1 else None})
        rows.append({"fixture": r["fixture"], "variant": r["variant"], "file": r["file"],
            "passed": r["passed"], "peakPcm16": peak,
            "fullScaleSamples": int(np.count_nonzero((x == -32768) | (x == 32767))),
            "nearFullScaleSamples": int(np.count_nonzero(np.abs(x) >= 32760)),
            "maxAdjacentJump": float(np.max(delta)),
            "maxSameSignPlateauLength": longest_plateau(x),
            "maxSourceFramesPerOutputFrame": max_source_step,
            "maxNearHeldStepsPerSide": max_source_hold,
            "windows": windows})
    summary = {"renderCount": len(rows), "rejectedCount": sum("rejected" in r for r in records),
        "skippedFixtures": [r for r in records if "skipped" in r],
        "passedConsumedMapping": sum(r["passed"] for r in rows),
        "fullScaleSamples": sum(r["fullScaleSamples"] for r in rows),
        "peakPcm16": max(r["peakPcm16"] for r in rows),
        "maxSourceFramesPerOutputFrame": max(r["maxSourceFramesPerOutputFrame"] for r in rows),
        "smoothingCases": processed,
        "restoration": [{"fixture": r["fixture"], "pcmEqual":
            np.array_equal(baselines[r["fixture"]], read_pcm(root / "confirmed" / r["file"])[0])}
            for r in records if r.get("variant") == "restored"],
        "limitations": ["PCM16 output cannot rule out upstream clipping at a lower internal level.",
            "Adjacent-sample differences are diagnostic metrics, not proof of audible clicks.",
            "Unconsumed planned tail frames are excluded from consumed-map validation.",
            "No host playback, subjective listening or user project was tested."],
        "cases": rows}
    (root / "artifact-metrics.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in summary.items() if k != "cases"}, indent=2))
    return summary


def longest_plateau(x):
    # Ignore silence and tiny quantization plateaus; report evidence, not a clipping classifier.
    same = (x[1:] == x[:-1]) & (np.abs(x[1:]) > 100)
    best = run = 0
    for flag in same:
        run = run + 1 if flag else 0
        best = max(best, run)
    return best + 1 if best else 0


def evidence(root, summary, destination):
    synthetic = json.loads((root / "smoothing" / "smoother-artifacts.json").read_text(encoding="utf-8-sig"))
    selected = [r for r in synthetic if r["frequency"] == 220 and not r["phaseStep"]
                and r["align"] and r["mode"] == "AutoOnVolumeDifference"]
    repo = Path(__file__).resolve().parents[2]
    smoothing = repo / "VOCALOIDPatcher/VOCALOIDPatcher/ConsonantTiming/Smoothing"
    environment = json.loads((root / "confirmed" / "artifact-environment.json").read_text(encoding="utf-8-sig"))
    result = {"date": "2026-10-20", "environment": environment,
        "smoothingSourceSha256": {name: hashlib.sha256((smoothing / name).read_bytes()).hexdigest()
            for name in ("TransitionSmoother.cs", "TransitionSmoothingHost.cs")},
        "native": {k: v for k, v in summary.items() if k != "cases"},
        "v6ShortProgress": [{k: v for k, v in r.items() if k != "windows"} for r in summary["cases"]
            if r["fixture"] == "v6-short" and (r["variant"] == "baseline" or r["variant"].startswith("edge1-coverage-phase"))],
        "syntheticCaseCount": len(synthetic), "synthetic220Hz": selected,
        "productionChanges": False, "deployed": False, "editorStarted": False,
        "listeningValidation": False}
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(result, indent=2), encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    parser.add_argument("--evidence", type=Path)
    args = parser.parse_args()
    summary = measure(args.root)
    if args.evidence:
        evidence(args.root, summary, args.evidence)
