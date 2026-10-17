"""Check VQM feature bit17 in the existing owned-PCM DRS phase harness.

Each native case runs in a separate child process. Only aggregate counts and
configuration values are written, never PCM or frame payloads.
"""

from __future__ import annotations

import argparse
import json
from itertools import product
from pathlib import Path
import subprocess
import sys

from audit_frm2_numeric import F, I, P, SHA256
from verify_frm2_phase_pipeline import run_case


class FeatureObserver:
    def __init__(self) -> None:
        self.values: list[float] = []
        self.flags: list[int] = []

    def before(self, context: int) -> int:
        return I.from_address(context + 0xa90).value

    def after(self, context: int, previous: int) -> None:
        if I.from_address(context + 0xa90).value != previous + 1:
            return
        frame = P.from_address(context + 0xcc8).value
        feature = P.from_address(frame + 0x128).value
        if not feature:
            raise AssertionError("phase frame has no feature object")
        self.flags.append(I.from_address(feature).value)
        self.values.append(F.from_address(feature + 0x21c).value)


def child(dll: Path, variant: str, hop: int, features: int) -> dict:
    observer = FeatureObserver()
    result = run_case(dll, variant, hop, features, 220,
                      source_controls=True, step_observer=observer)
    if (len(observer.values) != result["frames"]
            or any(value != 0.0 for value in observer.values)
            or set(observer.flags) != {0x1fffff}):
        raise AssertionError("phase-harness feature bit17 changed")
    return {
        "variant": variant,
        "hop": hop,
        "features": features,
        "frames": result["frames"],
        "config_id_0x17": result["runtime_profile"]["0x17"],
        "bit17_nonzero": 0,
        "feature_flags": "0x1fffff",
    }


def run(dll: Path) -> dict:
    cases = []
    for variant, hop, features in product(("stable", "varying"),
                                          (256, 512), (0, 1)):
        command = [sys.executable, "-B", str(Path(__file__).resolve()),
                   "--dll", str(dll), "--child", variant,
                   "--hop", str(hop), "--features", str(features)]
        process = subprocess.run(command, capture_output=True, text=True,
                                 timeout=60, check=False)
        if process.returncode:
            raise RuntimeError(
                f"phase child {variant}/{hop}/{features} failed: "
                f"{process.stderr[-1500:]}")
        cases.append(json.loads(process.stdout))
    if len(cases) != 8 or any(row["config_id_0x17"] != 0.25
                              for row in cases):
        raise AssertionError("known phase configuration changed")
    return {
        "dse_sha256": SHA256,
        "cases": cases,
        "total_frames": sum(row["frames"] for row in cases),
        "scope": "Owned PCM and injected harmonic frames in the verified DRS phase harness. A default 0.25 config value coexists with zero internal bit17; this does not identify VQM authoring settings or disprove other phase producers.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--child", choices=("stable", "varying"))
    parser.add_argument("--hop", type=int, choices=(256, 512), default=256)
    parser.add_argument("--features", type=int, choices=(0, 1), default=1)
    args = parser.parse_args()
    if args.child:
        print(json.dumps(child(args.dll, args.child, args.hop, args.features)))
        return
    if args.output is None:
        parser.error("--output is required for the full matrix")
    report = run(args.dll)
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({"cases": len(report["cases"]),
                      "frames": report["total_frames"]}))


if __name__ == "__main__":
    main()
