"""Condense isolated timing-coefficient measurements; retain failed model witnesses."""
import argparse
import hashlib
import json
import struct
from datetime import datetime, timezone
from pathlib import Path


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def spans(values):
    result = []
    for index, value in enumerate(values):
        if result and result[-1]["boundaries"] == value:
            result[-1]["lastIndex"] = index
        else:
            result.append(dict(firstIndex=index, lastIndex=index, boundaries=value))
    return result


def summarize(raw, root, editor):
    data = json.loads(raw.read_text(encoding="utf-8"))
    images = {name: digest(editor / name) for name in ["DSE.dll", "VSM.dll", "VOCALOID6.dll"]}
    assert images["DSE.dll"] == data["dseSha256"]
    rows = []
    for item in data["results"]:
        assert item["nativeMatches"] == 128
        assert item["queryDse"] == dict(Starts=0, Steps=0, Samples=0)
        dense = item["dense"]
        assert len(dense) == 513 and all(struct.pack("<f", row["coefficient"]) == struct.pack("<f", i / 512)
                                        for i, row in enumerate(dense))
        assert len({tuple(row["boundaries"]) for row in dense}) == item["denseDistinct"]
        ordinary = [row["predicted"] for row in item["ordinaryPlans"]]
        assert len({tuple(value) for value in ordinary}) == item["ordinaryDistinct"]
        witnesses = [dict(index=i, coefficient=row["coefficient"], actual=row["boundaries"][0],
                          prepared=row["preparedOnset"]) for i, row in enumerate(dense)
                     if row["boundaries"][0] != row["preparedOnset"]]
        assert len(witnesses) == item["preparedMismatches"]
        controls = [row for row in item["renders"] if row["mode"] != "continuous"]
        continuous = [row for row in item["renders"] if row["mode"] == "continuous"]
        assert len(controls) == 4 and all(row["pcmSame"] and row["expectedBoundary"] for row in controls)
        assert all(row["dse"]["Steps"] > 0 and row["dse"]["Samples"] > 0 for row in item["renders"])
        assert all(not row["pcmSame"] and row["expectedBoundary"] and row["injections"] == 1 for row in continuous)
        assert all(tuple(row["actual"]) not in {tuple(value) for value in ordinary} for row in continuous)
        signatures = {tuple(sorted({(row["sourceId"], row["stage"], row["from"], row["to"])
                                    for row in plan["plan"]})) for plan in item["ordinaryPlans"]}
        rows.append({key: item[key] for key in ["Name", "Phones", "Legato", "Bank", "Duration", "Pitch", "Gap",
                     "original", "originalHash", "ordinaryDistinct", "denseDistinct", "nativeMatches", "newCoefficient",
                     "newBoundaries", "latency", "queryDse", "prepared", "preparedMismatches", "modelIterations",
                     "modelNanoseconds", "checksum", "baselinePlan", "continuousPlan", "renders"]} | dict(
                         materialPathVariants=len(signatures), ordinaryCurve=spans(ordinary),
                         coefficientCurve=spans([row["boundaries"] for row in dense]),
                         preparedWitnesses=witnesses[:4],
                         onsetReversals=sum(a["boundaries"][0] > b["boundaries"][0]
                                            for a, b in zip(dense, dense[1:]))))
    assert len(rows) == 14
    source_files = ["voicebank/tools/evec_render_harness/TimingCoefficientProbe.cs",
                    "voicebank/tools/evec_render_harness/Program.cs",
                    "voicebank/tools/summarize_timing_coefficient.py"]
    return dict(schema=1, capturedAtUtc=datetime.now(timezone.utc).isoformat(), images=images,
                rawSha256=digest(raw), sourceSha256={name: digest(root / name) for name in source_files},
                scope="Private disposable process, captured bounded input, native coefficient mutation before NOTE commit. "
                      "No Editor interaction or deployment. Dense curves are no-PCM planning; only listed renders are PCM oracles.",
                assumptions=dict(sampleRate=44100, blockSamples=256, nativePrerollFrames=3087, presendSeconds=.5,
                                 fixtureTempo=120, targetTick=960, denseCoefficientDenominator=512,
                                 performance="Prepared input and mapping only; excludes capture, input preparation, cold startup and UI.",
                                 modelPerformance="Arithmetic-only C# microbenchmark, Debug; excludes tick conversion and preparation.",
                                 repeatability="One warmup render followed by identical 63-to-64 invalidation before every reference/control."),
                totals=dict(fixtures=len(rows), ordinaryBackendMatches=sum(row["nativeMatches"] for row in rows),
                            densePlannedPoints=513 * len(rows),
                            extraReachabilityFixtures=sum(row["newCoefficient"] is not None for row in rows),
                            continuousPcmChecks=sum(row["mode"] == "continuous" for item in rows for row in item["renders"]),
                            unchangedPcmControls=sum(row["mode"] != "continuous" for item in rows for row in item["renders"]),
                            fullyMatchingPreparedFixtures=sum(row["preparedMismatches"] == 0 for row in rows),
                            matchingPreparedPoints=513 * len(rows) - sum(row["preparedMismatches"] for row in rows),
                            mismatchingPreparedPoints=sum(row["preparedMismatches"] for row in rows)), cases=rows)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("raw", type=Path)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--editor", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = summarize(args.raw, args.root, args.editor)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report["totals"]))
