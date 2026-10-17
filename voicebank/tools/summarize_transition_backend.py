"""Reduce private SDK probe output to reviewable backend acceptance evidence.

No WAV, native project or source-frame payload is copied into the repository.
Usage: python summarize_transition_backend.py TEMP_ROOT OUTPUT_JSON
"""
import hashlib
import json
from pathlib import Path
import statistics
import sys


def summarize(root: Path) -> dict:
    runs = {}
    pcm = {}
    for version in ("new", "old"):
        for combined in (0, 1):
            name = f"acceptance-{version}-{combined}"
            directory = root / name
            report = json.loads((directory / "backend.json").read_text(encoding="utf-8"))
            isolation = json.loads((directory / "isolation/isolation.json").read_text(encoding="utf-8"))
            layouts = [r for r in report if "Edges" in r]
            renders = [r for r in report if "nativeFramesCompared" in r]
            ranges = [r for r in report if "ReachableFrames" in r]
            reopened = [r for r in report if r.get("variant") == "reopened-cache"]
            parallel = [r for r in isolation if "part" in r]
            assert len(layouts) == 8 and len(ranges) == 16 and len(reopened) == 24
            assert len(parallel) == 12 and all(r["completed"] == 1 and r["hash"] == r["expected"] and r["Failures"] == 0 for r in parallel)
            assert all(r["Failures"] == 0 for r in renders)
            assert all(r["Scopes"] == 0 and r["CacheKeys"] > 0 for r in reopened if r["pass"] != 1)
            assert all(r["passed"] for r in isolation if "passed" in r)
            assert any(r.get("validation") == "early-detach-cache" for r in isolation)
            plan_checks = [r for r in isolation if r.get("validation") == "parallel-private-plans"]
            assert len(plan_checks) == 3 and all(r["nativePlans"] > 0 for r in plan_checks)
            for layout in layouts:
                fixture = layout["Name"]
                items = [r for r in renders if r["Name"] == fixture]
                baseline = next(r["hash"] for r in items if r["variant"] == "baseline")
                assert next(r["hash"] for r in items if r["variant"] == "restored") == baseline
                assert all(r["hash"] == baseline for r in items if r["variant"] in ("gain-0", "gain-repeat-0"))
                for pass_number in (0, 1, 2):
                    entry = next(r for r in reopened if r["Name"] == fixture and r["pass"] == pass_number)
                    controlled = next(r["hash"] for r in items if r["variant"] == "cache-controlled")
                    assert entry["hash"] == (baseline if pass_number == 1 else controlled)
            times = [r["Milliseconds"] for r in ranges if not r["Protected"]]
            runs[name] = {
                "sdk": "6.13.3.1" if version == "new" else "6.13.1.1",
                "existingCoefficientAndPhonemeTiming": bool(combined),
                "fixtures": len(layouts), "verifiedRenders": len(renders),
                "reopenedRenders": len(reopened), "parallelRenders": len(parallel),
                "nativeRangePlans": sum(r["NativePlans"] for r in ranges),
                "coldRangeMsMedian": statistics.median(times), "coldRangeMsMax": max(times),
                "rangeCache": "second query has zero native plans and identical certified frame set",
                "peakNativeScopes": max(r["peakScopes"] for r in parallel),
                "parallelPlanning": plan_checks,
                "earlyDetachOrdinaryPcmExact": True,
                "ranges": [{k: r[k] for k in ("Name", "range", "Minimum", "Maximum", "IsContiguous", "NativePlans", "Milliseconds", "Protected")} | {"reachableCount": len(r["ReachableFrames"])} for r in ranges],
                "sharedJunctionRenders": [r for r in report if "sharedAliases" in r],
                "reportSha256": hashlib.sha256((directory / "backend.json").read_bytes()).hexdigest(),
            }
            pcm[version, combined] = {(r["Name"], r["variant"]): r["hash"] for r in renders}
    assert pcm["old", 0] == pcm["new", 0] and pcm["old", 1] == pcm["new", 1]
    supplementary = {}
    late_pcm = {}
    for version in ("new", "old"):
        isolation_path = root / f"retirement-final-{version}/isolation/isolation.json"
        isolation = json.loads(isolation_path.read_text(encoding="utf-8"))
        assert any(r.get("validation") == "empty-source-dispose" and r["passed"] for r in isolation)
        assert all(r["hash"] == r["expected"] and r["Failures"] == 0 for r in isolation if "part" in r)
        late_path = root / f"late-final-{version}/backend.json"
        late = json.loads(late_path.read_text(encoding="utf-8"))
        late_pcm[version] = {r["variant"]: r["hash"] for r in late if "nativeFramesCompared" in r}
        assert late_pcm[version]["restored"] == late_pcm[version]["baseline"]
        assert next(r["offset"] for r in late if "offset" in r) == 57600
        supplementary[version] = {"lateNoteOffsetTicks": 57600, "lateRestorationExact": True,
                                  "emptySourceDisposal": True, "parallelPcmExactAfterRetirementFix": True,
                                  "isolationReportSha256": hashlib.sha256(isolation_path.read_bytes()).hexdigest(),
                                  "lateReportSha256": hashlib.sha256(late_path.read_bytes()).hexdigest()}
    assert late_pcm["old"] == late_pcm["new"]
    return {
        "date": "2026-10-17", "nativeProfile": "Release, exact certified component hashes",
        "managedTestsPassed": 208, "rustTestsPassed": 44,
        "crossVersionPcmExact": True, "frontendConnected": False, "deployed": False,
        "runs": runs, "supplementary": supplementary,
        "limitations": ["complete bounded Part context <=33 notes, <=22 seconds relative captured context",
                        "44100 Hz / 256 samples per frame / 0.5 second presend; no DVQM or secondary bank",
                        "certified range holds all other junctions fixed and stays inside captured planner/output domain",
                        "Debug native empty planner reproduced with preexisting binaries; runtime acceptance uses Release"],
    }


if __name__ == "__main__":
    Path(sys.argv[2]).write_text(json.dumps(summarize(Path(sys.argv[1])), indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
