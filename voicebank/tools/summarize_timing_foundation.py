"""Audit the isolated float timing foundation run and attach implementation hashes."""
import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def summarize(raw, root, editor):
    data = json.loads(raw.read_text(encoding="utf-8"))
    assert len(data["results"]) == 5 and len(data["contexts"]) == 5
    for row in data["results"]:
        assert row["matching"] == 128 and len(row["rendered"]) == 3
        assert row["queryDse"] == dict(Starts=0, Steps=0, Samples=0)
        assert all(x["Injections"] == 1 and len(x["Boundaries"]) >= 3 for x in row["rendered"])
    for row in data["contexts"]:
        assert row["comparisons"] == 7 and row["EncodedNotes"] <= 67 and row["WindowCount"] <= 5
        assert row["dse"] == dict(Starts=0, Steps=0, Samples=0)
    assert data["detachedOwnedQuery"] and data["invalidatedPreparation"]
    assert len(data["multiple"]) == 2 and all(row["Injections"] == 2 for row in data["multiple"])
    bounded = data["boundedCache"]
    assert bounded["invalidated"] == 33 < bounded["notes"]
    assert bounded["restoredHash"] == bounded["controlHash"] != bounded["changedHash"]
    lifecycle = data["lifecycle"]
    states = {row["name"]: row for row in lifecycle["snapshots"]}
    assert states["a"]["hash"] == states["undo_a"]["hash"]
    assert states["b"]["hash"] == states["redo_b"]["hash"] == lifecycle["reopenedHash"]
    assert states["baseline"]["hash"] == states["clear"]["hash"] == states["native_undo"]["hash"]
    assert states["native_pitch"]["hash"] == states["native_redo"]["hash"]
    assert states["a"]["hash"] != states["b"]["hash"] != states["baseline"]["hash"]
    assert lifecycle["saveReopen"] and lifecycle["nativeHistoryPreserved"]
    assert lifecycle["reservedTargetsPreserved"] and lifecycle["unsupportedRejected"]
    files = list((root / "VOCALOIDPatcher/VOCALOIDPatcher/ConsonantTiming/Backend").glob("*.cs"))
    files += list((root / "VOCALOIDPatcher/VOCALOIDPatcher/ConsonantTiming/Model").glob("*.cs"))
    files += [root / name for name in [
        "VOCALOIDPatcher/VOCALOIDPatcher/ConsonantTiming/SampledTimingSession.cs",
        "VOCALOIDPatcher/VOCALOIDPatcher/BreathVolume/ICustomParameterHistoryEdit.cs",
        "voicebank/tools/evec_render_harness/TimingFoundationProbe.cs",
        "voicebank/tools/evec_render_harness/TimingCoefficientProbe.cs",
        "VOCALOIDPatcher.McpTests/SampledTimingModelTests.cs",
        "voicebank/tools/summarize_timing_foundation.py"]]
    return dict(schema=1, capturedAtUtc=datetime.now(timezone.utc).isoformat(),
                scope="Isolated native VSM/DSE process, no Editor launch, deployment or drag connection. "
                      "Shared Editor history/UI and multi-Part render task routing are not host-validated.",
                images={name: sha(editor / name) for name in ["DSE.dll", "VSM.dll", "VOCALOID6.dll"]},
                sourceSha256={str(path.relative_to(root)).replace("\\", "/"): sha(path) for path in sorted(files)},
                rawSha256=sha(raw),
                totals=dict(ordinaryMatches=640, contextMatches=35, offGridPcmChecks=15,
                            simultaneousCoefficientParts=2, simultaneousCoefficientNotes=4,
                            maxNotesPerWindow=33, maxPreparedWindows=5, maxEncodedNotesPerPreparation=67,
                            automatedTests=147),
                measurements=data)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("raw", type=Path)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--editor", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = summarize(args.raw, args.root, args.editor)
    args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(report["totals"]))
