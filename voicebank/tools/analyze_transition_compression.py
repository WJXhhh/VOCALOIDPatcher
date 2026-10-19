"""Check real PCM support and protected bounds in the isolated compression probe.

Usage: python analyze_transition_compression.py <probe-output-directory>
No production processing, threshold-based trimming or synthesis is performed.
"""
import array
import json
import pathlib
import statistics
import sys
import wave


def analyze(root):
    records = json.loads((root / "transition-host.json").read_text(encoding="utf-8-sig"))
    assert any(r["name"] == "compression-acceptance" for r in records), "Incomplete host acceptance"
    result = []
    for record in records:
        if not record["name"].endswith("-plan") or "ratio" not in record:
            continue
        name = record["name"][:-5]
        with wave.open(str(root / (name + ".wav")), "rb") as source:
            assert source.getnchannels() == 1 and source.getsampwidth() == 2
            rate = source.getframerate()
            values = array.array("h", source.readframes(source.getnframes()))
        if sys.byteorder != "little":
            values.byteswap()
        peak = max(map(abs, values))
        row = {
            "mode": record["mode"], "ratio": record["ratio"],
            "phoneFrames": record["protectedFrames"][-1] - record["protectedFrames"][0],
            "allocationFrames": record["rows"][-1]["End"] - record["rows"][0]["Begin"],
            "planningMilliseconds": record["planningMilliseconds"],
        }
        for label, threshold in [("lsb", 1), ("onePercent", peak * .01), ("tenPercent", peak * .1)]:
            indices = [i for i, value in enumerate(values) if abs(value) > threshold]
            assert indices, "Fixture is silent"
            row[label + "Ms"] = (indices[-1] - indices[0] + 1) * 1000 / rate
        result.append(row)
    assert len(result) == 20
    cases = {(r["mode"], r["ratio"]): r for r in result}
    for mode in ["plain", "phase", "endpoints", "legacy", "all"]:
        widths = [cases[mode, ratio]["onePercentMs"] for ratio in [1, .5, .15, .03]]
        assert all(a > b for a, b in zip(widths, widths[1:])), (mode, widths)
        for ratio in [1, .5, .15, .03]:
            assert cases[mode, ratio]["phoneFrames"] == cases["plain", ratio]["phoneFrames"]
        assert widths[-1] < 120, (mode, "Fixed extension floor remains", widths[-1])
    assert cases["endpoints", .03]["allocationFrames"] - cases["plain", .03]["allocationFrames"] <= 2
    assert cases["legacy", .03]["allocationFrames"] == cases["plain", .03]["allocationFrames"]
    summary = {
        "cases": result,
        "planningMedianMilliseconds": statistics.median(r["planningMilliseconds"] for r in result),
        "planningMaximumMilliseconds": max(r["planningMilliseconds"] for r in result),
        "accepted": True,
    }
    (root / "compression-metrics.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    analyze(pathlib.Path(sys.argv[1]))
