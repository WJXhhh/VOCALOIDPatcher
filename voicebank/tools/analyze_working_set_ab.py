"""Summarize isolated --working-set-render-ab output using paired observations."""

import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import random
import statistics


def percentile(values, fraction):
    ordered = sorted(values)
    index = (len(ordered) - 1) * fraction
    lower = int(index)
    upper = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (index - lower)


def median_interval(values):
    rng = random.Random(20261023)
    bootstraps = [statistics.median(rng.choices(values, k=len(values))) for _ in range(10000)]
    return [percentile(bootstraps, 0.025), percentile(bootstraps, 0.975)]


def arm_summary(rows):
    metric = lambda f: statistics.median([f(row) for row in rows])
    return {
        "samples": len(rows),
        "total_median_ms": metric(lambda r: r["Render"]["TotalMs"]),
        "total_p95_ms": percentile([r["Render"]["TotalMs"] for r in rows], 0.95),
        "render_median_ms": metric(lambda r: r["Render"]["RenderMs"]),
        "edit_median_ms": metric(lambda r: r["Render"]["EditMs"]),
        "trim_median_ms": metric(lambda r: r["TrimMs"]),
        "working_set_before_trim_mib": metric(lambda r: r["BeforeTrim"]["WorkingSet"] / 2**20),
        "working_set_after_trim_mib": metric(lambda r: r["AfterTrim"]["WorkingSet"] / 2**20),
        "working_set_after_render_mib": metric(lambda r: r["Render"]["After"]["WorkingSet"] / 2**20),
        "private_bytes_before_trim_mib": metric(lambda r: r["BeforeTrim"]["PrivateBytes"] / 2**20),
        "private_bytes_after_trim_mib": metric(lambda r: r["AfterTrim"]["PrivateBytes"] / 2**20),
        "render_page_faults_median": metric(lambda r: r["Render"]["After"]["PageFaults"] - r["Render"]["Before"]["PageFaults"]),
        "resume_page_faults_median": metric(lambda r: r["Render"]["After"]["PageFaults"] - r["AfterTrim"]["PageFaults"]),
        "render_cpu_median_ms": metric(lambda r: r["Render"]["After"]["CpuMs"] - r["Render"]["Before"]["CpuMs"]),
        "process_read_bytes_median": metric(lambda r: r["Render"]["After"]["ReadBytes"] - r["Render"]["Before"]["ReadBytes"]),
        "idle_median_ms": metric(lambda r: r["IdleMs"]),
        "dse_steps_range": [min(r["Render"]["Dse"]["Steps"] for r in rows), max(r["Render"]["Dse"]["Steps"] for r in rows)],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    directory = args.directory
    if not (directory / "complete.json").exists():
        raise ValueError("Benchmark did not complete; do not summarize partial observations.")
    raw = (directory / "working-set-render-ab.jsonl").read_bytes()
    metadata = json.loads((directory / "metadata.json").read_text(encoding="utf-8-sig"))
    rows = [json.loads(line) for line in raw.decode("utf-8-sig").splitlines()]
    groups = defaultdict(list)
    for row in rows:
        groups[(row["Bank"], row["EditScope"], row["WaitMs"])].append(row)
        if row["Render"]["Dse"]["Steps"] <= 0 or row["Render"]["Dse"]["Samples"] <= 0:
            raise ValueError("Measurement did not synthesize fresh PCM.")
        if row["WaitMs"] == 0 and row["TrimMs"] != 0:
            raise ValueError("Immediate sham control unexpectedly trimmed.")
        if "Arm" in row and row["Trim"] != (row["Arm"] == "trim"):
            raise ValueError("Arm label disagrees with trim flag.")
    summaries = []
    for (bank, edit, wait), group in sorted(groups.items()):
        paired = defaultdict(dict)
        for row in group:
            arm = row.get("Arm", "trim" if row["Trim"] else "retain")
            if arm in paired[row["Pair"]]:
                raise ValueError("Duplicate pair/arm.")
            paired[row["Pair"]][arm] = row
        expected_arms = {"trim", "retain", "sham"} if metadata.get("triplets") else {"trim", "retain"}
        if len(paired) != metadata["pairs"] or any(set(p) != expected_arms for p in paired.values()):
            raise ValueError("Incomplete paired case.")
        if len({r["Render"]["Sha256"] for r in group}) != 1:
            raise ValueError("Identical-input audio hashes differed.")
        retain = arm_summary([p["retain"] for p in paired.values()])
        trimmed = arm_summary([p["trim"] for p in paired.values()])
        deltas = [p["trim"]["Render"]["TotalMs"] - p["retain"]["Render"]["TotalMs"] for p in paired.values()]
        ratios = [100 * (p["trim"]["Render"]["TotalMs"] / p["retain"]["Render"]["TotalMs"] - 1) for p in paired.values()]
        summary = {
            "bank": bank, "edit_scope": edit, "wait_ms": wait, "actual_trim": wait != 0,
            "pairs": len(paired), "retain": retain, "trim": trimmed,
            "difference_of_arm_medians_ms": trimmed["total_median_ms"] - retain["total_median_ms"],
            "difference_of_arm_medians_pct": 100 * (trimmed["total_median_ms"] / retain["total_median_ms"] - 1),
            "paired_median_delta_ms": statistics.median(deltas),
            "paired_median_delta_bootstrap_95_ci_ms": median_interval(deltas),
            "paired_mean_delta_ms": statistics.mean(deltas),
            "paired_median_penalty_pct": statistics.median(ratios),
            "trim_slower_pairs": sum(d > 0 for d in deltas),
            "sha256": group[0]["Render"]["Sha256"],
            "order_counts": dict(Counter(",".join(arm for arm, _ in sorted(p.items(), key=lambda item: item[1]["Position"]))
                                         for p in paired.values())),
        }
        if metadata.get("triplets"):
            sham_deltas = [p["sham"]["Render"]["TotalMs"] - p["retain"]["Render"]["TotalMs"] for p in paired.values()]
            versus_sham = [p["trim"]["Render"]["TotalMs"] - p["sham"]["Render"]["TotalMs"] for p in paired.values()]
            mean_controls = [(p["retain"]["Render"]["TotalMs"] + p["sham"]["Render"]["TotalMs"]) / 2 for p in paired.values()]
            versus_controls = [p["trim"]["Render"]["TotalMs"] - control for p, control in zip(paired.values(), mean_controls)]
            summary["sham"] = arm_summary([p["sham"] for p in paired.values()])
            summary["sham_minus_retain_median_ms"] = statistics.median(sham_deltas)
            summary["sham_minus_retain_bootstrap_95_ci_ms"] = median_interval(sham_deltas)
            summary["trim_minus_sham_median_ms"] = statistics.median(versus_sham)
            summary["trim_minus_sham_bootstrap_95_ci_ms"] = median_interval(versus_sham)
            summary["mean_controls_median_ms"] = statistics.median(mean_controls)
            summary["trim_minus_mean_controls_median_ms"] = statistics.median(versus_controls)
            summary["trim_minus_mean_controls_bootstrap_95_ci_ms"] = median_interval(versus_controls)
            summary["trim_vs_mean_controls_median_penalty_pct"] = statistics.median([
                100 * (p["trim"]["Render"]["TotalMs"] / control - 1) for p, control in zip(paired.values(), mean_controls)])
        summaries.append(summary)
        print(f"{bank} {edit:6} wait={wait:4} A={retain['total_median_ms']:.2f} B={trimmed['total_median_ms']:.2f} "
              f"paired_delta={statistics.median(deltas):+.2f} ms CI={summary['paired_median_delta_bootstrap_95_ci_ms']} "
              f"slower={summary['trim_slower_pairs']}/{len(paired)}")
        if metadata.get("triplets"):
            print(f"  sham-A={summary['sham_minus_retain_median_ms']:+.2f} ms CI={summary['sham_minus_retain_bootstrap_95_ci_ms']} "
                  f"B-sham={summary['trim_minus_sham_median_ms']:+.2f} ms CI={summary['trim_minus_sham_bootstrap_95_ci_ms']}")
            print(f"  B-mean(A,C)={summary['trim_minus_mean_controls_median_ms']:+.2f} ms "
                  f"CI={summary['trim_minus_mean_controls_bootstrap_95_ci_ms']} "
                  f"penalty={summary['trim_vs_mean_controls_median_penalty_pct']:+.2f}%")
    expected_groups = len(metadata["banks"].split(",")) * len(metadata["waits"]) * len(metadata.get("editScopes", ["all", "single"]))
    if len(groups) != expected_groups:
        raise ValueError("Missing benchmark cases.")
    available = [r["Render"]["Before"]["AvailablePhysicalBytes"] / 2**20 for r in rows]
    result = {
        "schema_version": 1, "metadata": metadata,
        "raw": {"file": "working-set-render-ab.jsonl", "bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest(), "measurements": len(rows)},
        "available_physical_mib": {"min": min(available), "median": statistics.median(available), "max": max(available)},
        "method": "A=retain, B=trim, optional C=sham. " + metadata.get("ordering", "AB/BA alternating pairs.") +
            " Percentile bootstrap of within-block median delta, seed 20261023, 10000 resamples. Immediate arms all omit trim.",
        "limitations": [
            "Native VSM/DSE isolation, not Editor UI/Patcher DSP or export latency.",
            "Page faults include soft and hard faults. Process I/O counters are not physical disk-read counters.",
            "Idle wait, trim API duration, observer setup, metrics snapshots, stop/cleanup and output verification are outside edit-to-complete time.",
            "A common primer reheats the active native path; this is the next-render effect, not separate long-running policy processes.",
            "Cases run sequentially; AB/BA balances arm order but not scenario order or machine background load.",
            "DSE Samples is the counter's last-start diagnostic, not a sum of all output samples.",
        ],
        "cases": summaries,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
