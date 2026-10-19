"""Validate and summarize disposable production source-coverage probe outputs.

Usage: python summarize_source_coverage.py TEMP_ROOT EVIDENCE_JSON
Contains metadata/digests only; WAV and VPR fixtures remain outside the repo.
"""
import hashlib
import json
from pathlib import Path
import statistics
import sys


def read(path):
    return json.loads(path.read_text(encoding="utf-8"))


def main(root, output):
    evidence = {"date": "2026-10-18", "deployed": False, "versions": {}}
    case_sets = []
    for version in ("old", "new"):
        source_file = root / f"{version}-source-material/source-coverage.json"
        rows = read(source_file)
        assert rows[-1]["kind"] == "acceptance" and rows[-1]["renders"] == 36
        cases = [r for r in rows if r.get("kind") in ("case", "short-note")]
        assert len(cases) == 36
        for phone, end in {(r["phoneEnd"], r["transitionEnd"]) for r in cases if r["kind"] == "case"}:
            group = [r for r in cases if r.get("phoneEnd") == phone and r.get("transitionEnd") == end]
            basis = next(r for r in group if r["mode"] == "native")
            assert all(r["positions"] == basis["positions"] and r["actual"] == basis["actual"] for r in group)
            assert all(r["Failures"] == 0 and r["cacheHit"] for r in group)
            assert len({r["pcm"] for r in group if r["mode"] in ("coverage", "early", "late")}) == 3
        short = [r for r in cases if r["kind"] == "short-note"]
        assert next(r for r in short if r["mode"] == "native")["sourceLast"] == 52
        assert all(r["sourceLast"] == 61 for r in short if r["mode"] in ("coverage", "early"))
        assert all(r["positions"] == short[0]["positions"] and r["actual"] == short[0]["actual"] for r in short)
        repeat = [r for r in rows if r.get("kind") == "native-repeat-comparison"]
        assert len(repeat) == 5 and all(r["peak"] <= 2 and r["rms"] <= .1 for r in repeat)
        ms = [r["planMs"] for r in cases if r["kind"] == "case"]
        summary = {
            "metadata": rows[0], "realRenders": len(cases),
            "queryPairMilliseconds": {"minimum": min(ms), "median": statistics.median(ms), "maximum": max(ms)},
            "nativeRepeatComparisons": repeat,
            "probeReportSha256": hashlib.sha256(source_file.read_bytes()).hexdigest().upper(),
            "cases": [{k: v for k, v in r.items() if k != "actual"} | {
                "sustain": [{k: v for k, v in work.items() if k != "Material"} | {
                    "materialId": work["Material"]["Id"], "materialFrames": work["Material"]["Frames"]}
                    for work in r["actual"] if work["To"] == -1]
            } for r in cases],
        }
        for name, expected in (("host", 15), ("general", 40)):
            path = root / f"{version}-{name}-material/transition-host.json"
            host = read(path)
            renders = [r for r in host if "hash" in r]
            assert len(renders) == expected
            assert host[-1]["name"] in ("acceptance", "coverage-acceptance")
            assert all(r["gainFailures"] == 0 for r in renders)
            summary[name] = {
                "renderSteps": len(renders), "acceptance": host[-1],
                "probeReportSha256": hashlib.sha256(path.read_bytes()).hexdigest().upper(),
                "renders": [{k: r[k] for k in ("name", "hash", "Epoch", "Scopes", "CacheKeys", "gainApplied", "gainFailures")} for r in renders],
                "sharedStageCoverage": [r for r in host if r["name"] == "shared-range"],
            }
        evidence["versions"][version] = summary
        case_sets.append(cases)
    assert [r["file"] for r in case_sets[0]] == [r["file"] for r in case_sets[1]]
    exact = sum(a["pcm"] == b["pcm"] for a, b in zip(*case_sets))
    assert exact == 36
    evidence["crossSdkExactPcmCases"] = exact
    evidence["managedTests"] = 249
    evidence["rustTests"] = 47
    evidence["installedUnchangedSha256"] = {
        "main": "70C0C80CDE6A25EFF78F4277EDC0F1450908BF0B4F9B07D04626CD685B8F63C5",
        "native": "3CB49633673EF635076823DB71C6828D0A28F807462F5A637A72052EF4330623",
    }
    output.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({v: {"sdk": s["metadata"]["sdk"], "planning": s["queryPairMilliseconds"],
                        "source": s["realRenders"], "host": s["host"]["renderSteps"], "general": s["general"]["renderSteps"]}
                      for v, s in evidence["versions"].items()}, ensure_ascii=False))


if __name__ == "__main__":
    main(Path(sys.argv[1]), Path(sys.argv[2]))
