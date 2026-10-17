"""Test owned VQM scalar and outer-field mutations in continuous native output.

Runs each event in a fresh process. The temporary PCM/FRM2 source is written
only at the explicitly supplied research path and deleted by the child.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import SHA256, f32


MUTATIONS = (
    ("feature_2_energy", 2, 0.12, None),
    ("feature_4_low", 4, -1., None),
    ("feature_4_high", 4, 1., None),
    ("feature_6_low", 6, 0., None),
    ("feature_6_high", 6, 2., None),
    ("feature_14_low", 14, 0., None),
    ("feature_14_high", 14, 1., None),
    ("feature_15_low", 15, 0., None),
    ("feature_15_high", 15, 1., None),
    ("feature_16_low", 16, -10., None),
    ("feature_16_high", 16, 0., None),
    ("feature_17_low", 17, 0., None),
    ("feature_17_high", 17, math.pi, None),
    ("feature_18_low", 18, 0., None),
    ("feature_18_high", 18, 5., None),
    ("feature_19_low", 19, 0., None),
    ("feature_19_high", 19, 1., None),
    ("outer_zero", None, None, (0, 0)),
    ("outer_max", None, None, (255, 4097)),
)


def one(dll: Path, scratch: Path, bit: int | None = None,
        value: float | None = None,
        outer: tuple[int, int] | None = None) -> dict:
    command = [sys.executable, "-u",
               str(Path(__file__).with_name("probe_vqm_continuous_output.py")),
               "--dll", str(dll), "--scratch-source", str(scratch),
               "--single-energy", "0.03"]
    if bit is not None:
        command += ["--feature-bit", str(bit), "--feature-value", str(value)]
    if outer is not None:
        command += ["--outer-bit18", str(outer[0]),
                    "--outer-bit19", str(outer[1])]
    child = subprocess.run(command, capture_output=True, text=True,
                           check=False, timeout=120)
    if child.returncode:
        raise RuntimeError(f"native event failed: {child.stderr[-1500:]}")
    return json.loads(child.stdout)


def changed(first: dict, second: dict, rows: str, field: str) -> int:
    left, right = first[rows], second[rows]
    if len(left) != len(right):
        raise AssertionError("event lengths differ")
    return sum(a[field] != b[field] for a, b in zip(left, right))


def run(dll: Path, scratch: Path) -> dict:
    baseline = one(dll, scratch)
    if baseline["nonzero_blocks"] != 56 or baseline["blocks_run"] != 106:
        raise AssertionError("continuous baseline changed")
    rows = []
    for name, bit, value, outer in MUTATIONS:
        case = one(dll, scratch, bit, value, outer)
        if case["nonzero_blocks"] != baseline["nonzero_blocks"]:
            raise AssertionError(f"event activity changed in {name}")
        if bit is not None:
            candidate_values = {item["probed_feature_value"]
                                for item in case["observations"]}
            if candidate_values != {f32(value)}:
                raise AssertionError(f"candidate feature mismatch in {name}: "
                                     f"{candidate_values}")
        else:
            candidate_values = {
                (item["probed_outer_bit18"], item["probed_outer_bit19"])
                for item in case["observations"]}
            if candidate_values != {outer}:
                raise AssertionError(f"candidate outer field mismatch in {name}: "
                                     f"{candidate_values}")
        output_blocks = case["output_blocks"]
        rows.append({
            "name": name,
            "field": f"internal bit{bit}" if bit is not None else "outer bit18/19",
            "requested_value": value if bit is not None else list(outer),
            "candidate_values": (sorted(candidate_values) if bit is not None
                                 else [list(pair) for pair in sorted(candidate_values)]),
            "candidate_spectrum_changed_blocks": changed(
                baseline, case, "observations", "candidate_spectrum_sha256"),
            "final_spectrum_changed_blocks": changed(
                baseline, case, "observations", "final_spectrum_sha256"),
            "output_pcm_changed_blocks": changed(
                baseline, case, "output_blocks", "sha256"),
            "output_stream_sha256": case["output_stream_sha256"],
            "output_peak_abs": max(block["peak_abs"] for block in output_blocks),
            "clipped_samples": sum(block["clipped_samples"] for block in output_blocks),
        })
    repeat = one(dll, scratch)
    if repeat != baseline:
        raise AssertionError("baseline repeat changed")
    if rows[0]["output_pcm_changed_blocks"] != 56:
        raise AssertionError("positive energy control did not change output")
    if any(row["clipped_samples"] for row in rows):
        raise AssertionError("clipped output invalidates comparison")
    return {
        "dse_sha256": SHA256,
        "baseline": {
            "energy": baseline["saved_energy"],
            "blocks_run": baseline["blocks_run"],
            "nonzero_blocks": baseline["nonzero_blocks"],
            "output_stream_sha256": baseline["output_stream_sha256"],
            "output_peak_abs": max(block["peak_abs"]
                                   for block in baseline["output_blocks"]),
        },
        "baseline_repeat_identical": True,
        "rows": rows,
        "scope": "Owned two-layer VQM event, 440 Hz generated PCM, 56 active "
                 "blocks; one field changed per native event. No installed "
                 "voicebank PCM or Editor host.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--scratch-source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.write_text(json.dumps(run(args.dll, args.scratch_source),
                                      indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
