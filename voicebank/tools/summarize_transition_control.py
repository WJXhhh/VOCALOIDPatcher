"""Verify isolated transition controls and write a compact, non-audio report."""
import argparse
import hashlib
import json
import math
import statistics
import struct
import wave
from pathlib import Path


def check(condition, message):
    if not condition:
        raise ValueError(message)


def read_wave(path):
    with wave.open(str(path), "rb") as stream:
        check(stream.getsampwidth() == 2 and stream.getframerate() == 44100,
              "Only this fixture's signed-16/44100 output is certified")
        return stream.getnchannels(), stream.readframes(stream.getnframes())


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def summarize(directory):
    document = json.loads((directory / "transition-control.json").read_text(encoding="utf-8-sig"))
    rows = document["reports"]
    keys = [(r["fixture"], r["variant"]) for r in rows]
    check(len(keys) == len(set(keys)), "Duplicate fixture/variant")
    fixtures = list(dict.fromkeys(r["fixture"] for r in rows))
    check(len(fixtures) == 11 and len(rows) == 132, "Incomplete transition matrix")
    plans, results, missing = [], [], []
    for name in fixtures:
        group = [r for r in rows if r["fixture"] == name]
        baseline = next(r for r in group if r["variant"] == "unhooked")
        neutral = next(r for r in group if r["variant"] == "neutral")
        channels, original = read_wave(directory / (name + "-neutral.wav"))
        check(hashlib.sha256(original).hexdigest().upper() == neutral["hash"], "PCM hash mismatch")
        target = baseline["target"]
        gains = []
        for row in group:
            if "unavailable" in row:
                missing.append({"fixture": name, "variant": row["variant"], "reason": row["unavailable"]})
                continue
            check(all(v == 0 for v in row["planningCounters"].values()), "Planner synthesized audio")
            check(len(row["planningMs"]) == 5, "Missing planning repeats")
            plans.extend(row["planningMs"])
            check(row["boundaries"] == baseline["boundaries"], "Protected phoneme boundary moved")
            if row["variant"] in ("neutral", "gain-restored", "restored"):
                check(row["hash"] == baseline["hash"], "Neutral/restored PCM changed")
            if not row["variant"].startswith("gain-") or row["variant"] == "gain-restored":
                continue
            check(row["frames"] == baseline["frames"], "Gain changed source mapping")
            check(row["rows"] == baseline["rows"], "Gain changed source rows")
            changed_channels, changed = read_wave(directory / (name + "-" + row["variant"] + ".wav"))
            check(channels == changed_channels and len(original) == len(changed), "WAV layout changed")
            check(hashlib.sha256(changed).hexdigest().upper() == row["hash"], "Changed PCM hash mismatch")
            # This private fixture uses native origin 3087 and 256 samples/frame.
            # The final +1 is the measured overlap-add tail, not another ART.
            first = (target["Begin"] - 3087) * 256
            last = (target["End"] - 3087 + 1) * 256
            check(0 <= first < last <= len(original) // (2 * channels), "Invalid output support")
            check(original[:first * channels * 2] == changed[:first * channels * 2] and
                  original[last * channels * 2:] == changed[last * channels * 2:],
                  "Gain changed PCM beyond ART and its one-frame overlap tail")
            interior_start = (target["Begin"] - 3087 + 2) * 256 * channels
            interior_end = (target["End"] - 3087 - 2) * 256 * channels
            ratio = None
            if interior_end > interior_start:
                fmt = "<" + "h" * (len(original) // 2)
                reference = struct.unpack(fmt, original)[interior_start:interior_end]
                adjusted = struct.unpack(fmt, changed)[interior_start:interior_end]
                energy = sum(x * x for x in reference)
                if energy:
                    ratio = math.sqrt(sum(x * x for x in adjusted) / energy)
                    check(abs(ratio - row["multiplier"]) < .0002, "Interior gain ratio differs")
                    if row["multiplier"] == 0:
                        check(all(x == 0 for x in adjusted), "Muted interior is not zero")
            trace = row["gainFrames"]
            selected = [f for f in trace if f["Selected"]]
            check(selected and any(f["AppliedGain"] != f["OriginalGain"] for f in selected), "Gain never applied")
            check([(f["Time"], f["Begin"], f["End"], f["Lane"], f["InputHash"], f["OriginalGain"]) for f in trace] ==
                  [(f["Time"], f["Begin"], f["End"], f["Lane"], f["InputHash"], f["OriginalGain"]) for f in neutral["gainFrames"]],
                  "Gain changed synthesis input vectors/native gains")
            check(all(f["Selected"] or f["AppliedGain"] == f["OriginalGain"] for f in trace), "Gain leaked to another ART/STA")
            gains.append({"variant": row["variant"], "requestedMultiplier": row["multiplier"],
                          "interiorRmsRatio": ratio, "outsideSupportBytesUnchanged": True,
                          "lanes": sorted({f["Lane"] for f in selected}), "hash": row["hash"]})
        results.append({"fixture": name, "target": {k: target[k] for k in
                       ("Begin", "End", "LeftLength", "From", "To", "Stage", "Reverse")},
                        "alignmentGroups": len(target["Material"]["Alignment"]) // 4,
                        "timing": [{k: r[k] for k in ("variant", "movedLeft", "movedRight", "sameRawRowCount", "hash")}
                                   for r in group if "unavailable" not in r and
                                   r["variant"] in ("left-long", "left-short", "right-long", "right-short", "timing-and-gain")],
                        "gain": gains})
    ordered = sorted(plans)
    return document, {"profile": document["profile"], "sdkVersion": document["sdk"],
                      "fixtures": len(fixtures), "renders": len(rows) - len(missing),
                      "purePlans": len(plans), "unavailable": missing,
                      "planningLatencyMs": {"median": statistics.median(plans),
                                            "p95NearestRank": ordered[math.ceil(len(ordered) * .95) - 1], "max": max(plans)},
                      "results": results}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--old", type=Path, required=True)
    parser.add_argument("--new", type=Path, required=True)
    parser.add_argument("--old-editor", type=Path, required=True)
    parser.add_argument("--new-editor", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    old, old_summary = summarize(args.old)
    new, new_summary = summarize(args.new)
    check(old["sdk"] == "6.13.1.1" and new["sdk"] == "6.13.3.1", "Wrong actual managed SDKs")
    fields = ("fixture", "variant", "unavailable", "multiplier", "movedLeft", "movedRight", "hash", "target",
              "rows", "boundaries", "anchors", "frames", "gainFrames", "sameRawRowCount")
    check([{k: r.get(k) for k in fields} for r in old["reports"]] ==
          [{k: r.get(k) for k in fields} for r in new["reports"]], "Real versions differ")
    binaries = {}
    for label, root in (("old", args.old_editor), ("new", args.new_editor)):
        binaries[label] = {name: digest(root / name) for name in ("VOCALOID6.dll", "DSE.dll", "VSM.dll")}
    report = {"date": "2026-10-17", "scope": "Independent native timing and pre-overlap-add ART-frame gain; no UI/deployment",
              "binaries": binaries, "crossVersionExactMatch": True, "old": old_summary, "new": new_summary,
              "limitations": ["Only these fixtures and exact component tuples; not universal reachability or audio quality acceptance.",
                              "Gain scales ART-driven synthesis-frame contributions, not independently separable raw recordings.",
                              "Protected phoneme/context anchors may leave no independent internal timing interval.",
                              "Timing changes can alter STA repetition count without changing ART/STA material source runs.",
                              "Existing external phoneme timing/duration controls, all reverse/fallback cases, DVQM overlays, task routing/history/cache and production smoothing remain to validate."],
              "sourceSha256": {name: digest(Path(__file__).parent / name) for name in
                               ("evec_render_harness/TransitionControlProbe.cs", "evec_render_harness/DurationCostHook.cs",
                                "evec_render_harness/Program.cs", "summarize_transition_control.py")}}
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"crossVersionExactMatch": True, "rendersPerVersion": new_summary["renders"],
                      "purePlansPerVersion": new_summary["purePlans"], "unavailablePerVersion": len(new_summary["unavailable"])}, ensure_ascii=False))


if __name__ == "__main__":
    main()
