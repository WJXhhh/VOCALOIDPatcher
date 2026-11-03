"""Compare raw native transition stress renders with offline production repairs.

Usage: python analyze_transition_repair.py <probe-directory> <summary.json>
Requires numpy. Writes metrics only; never modifies input audio.
"""
import hashlib
import json
import sys
import wave
from pathlib import Path

import numpy as np


def pcm(path):
    with wave.open(str(path), "rb") as stream:
        assert stream.getnchannels() == 1 and stream.getsampwidth() == 2
        return np.frombuffer(stream.readframes(stream.getnframes()), dtype="<i2").astype(float), stream.getframerate()


def metrics(audio):
    same = (audio[1:] == audio[:-1]) & (np.abs(audio[1:]) > 100)
    best = count = 0
    for flag in same:
        count = count + 1 if flag else 0
        best = max(best, count)
    return {"peakPcm16": int(np.max(np.abs(audio))),
            "fullScaleSamples": int(np.count_nonzero((audio == -32768) | (audio == 32767))),
            "maxAdjacentJump": int(np.max(np.abs(np.diff(audio)))),
            "longestNonSilentPlateau": best + 1 if best else 0}


def main(root, output):
    records = json.loads((root / "transition-artifacts.json").read_text(encoding="utf-8-sig"))
    repairs = {r["file"]: r for r in json.loads((root / "reprocessed.json").read_text(encoding="utf-8-sig"))}
    cases = []
    baselines = {r["fixture"]: pcm(root / r["file"])[0] for r in records if r.get("variant") == "baseline"}
    restored = []
    max_source_step = 0.0
    for row in records:
        if "file" not in row:
            continue
        assert row["passed"] and row["exactConsumedSourceMap"] and row["comparedFrames"] > 0
        assert row["timingFailures"] == row["gainFailures"] == 0
        assert row["planningCounters"] == {"Starts": 0, "Steps": 0, "Samples": 0}
        for segment in row["rows"]:
            split = segment["Split"] - segment["Begin"]
            for side in (segment["sources"][:split], segment["sources"][split:]):
                if len(side) > 1:
                    max_source_step = max(max_source_step, float(np.max(np.abs(np.diff(side)))))
        x, rate = pcm(root / row["file"])
        repair = repairs[row["file"]]
        y, repaired_rate = pcm(root / repair["output"])
        assert rate == repaired_rate and len(x) == len(y)
        outside = np.ones(len(x), dtype=bool)
        # Default 60 ms envelope dominates 25 ms repair + one-hop scan.
        for seam in repair["junctions"]:
            at = seam["JunctionSample"]
            margin = round(rate * .06)
            outside[max(0, at - margin):min(len(x), at + margin)] = False
        assert np.array_equal(x[outside], y[outside]), row["file"]
        raw, processed = metrics(x), metrics(y)
        assert processed["fullScaleSamples"] <= raw["fullScaleSamples"], row["file"]
        if row["variant"] == "restored":
            exact = np.array_equal(x, baselines[row["fixture"]])
            restored.append({"fixture": row["fixture"], "pcmExact": exact,
                             "maximumPcm16Difference": int(np.max(np.abs(x - baselines[row["fixture"]])))})
        cases.append({"fixture": row["fixture"], "variant": row["variant"], "raw": raw,
                      "processed": processed, "changedSamples": int(np.count_nonzero(x != y)),
                      "repairedSeams": sum(r["Intervened"] for r in repair["results"])})
    repo = Path(__file__).resolve().parents[2]
    sources = ["TransitionSmoother.cs", "TransitionSmoothingHost.cs", "TransitionJunctionPlanner.cs", "WaveJunctionProcessor.cs"]
    directory = repo / "VOCALOIDPatcher/VOCALOIDPatcher/ConsonantTiming/Smoothing"
    summary = {"date": "2026-10-22", "environment": json.loads((root / "artifact-environment.json").read_text()),
               "renderCount": len(cases), "rejectedCandidates": sum("rejected" in r for r in records),
               "skippedFixtures": [r for r in records if "skipped" in r],
               "rawFullScaleSamples": sum(r["raw"]["fullScaleSamples"] for r in cases),
               "processedFullScaleSamples": sum(r["processed"]["fullScaleSamples"] for r in cases),
               "rawPeakPcm16": max(r["raw"]["peakPcm16"] for r in cases),
               "processedPeakPcm16": max(r["processed"]["peakPcm16"] for r in cases),
               "maxRawPlateau": max(r["raw"]["longestNonSilentPlateau"] for r in cases),
               "maxSourceFramesPerOutputFrame": max_source_step,
               "changedFiles": sum(r["changedSamples"] > 0 for r in cases),
               "jumpIncreasedFiles": sum(r["processed"]["maxAdjacentJump"] > r["raw"]["maxAdjacentJump"] for r in cases),
               "bodyOutsideWindowsExact": True, "consumedSourceMapsExact": True, "restoration": restored,
               "sourceSha256": {name: hashlib.sha256((directory / name).read_bytes()).hexdigest() for name in sources},
               "largestJumpIncreases": sorted(cases, key=lambda r: r["processed"]["maxAdjacentJump"] - r["raw"]["maxAdjacentJump"], reverse=True)[:10],
               "deployed": False, "editorStarted": False, "listeningValidation": False,
               "limitations": ["Synthetic fixtures; no reproduction of the user's project or subjective listening.",
                               "Low-level or upstream clipping cannot be excluded by the absence of PCM16 full scale.",
                               "Restoration equality is reported per fixture; native PCM can differ at low level even with exact consumed source maps.",
                               "Neighbouring-sample and plateau metrics do not identify perceived square-wave timbre."]}
    output.write_text(json.dumps(summary, indent=2), encoding="utf-8")
    (root / "repair-case-metrics.json").write_text(json.dumps(cases, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in summary.items() if k not in ("sourceSha256", "largestJumpIncreases", "restoration")}, indent=2))


if __name__ == "__main__":
    main(Path(sys.argv[1]), Path(sys.argv[2]))
