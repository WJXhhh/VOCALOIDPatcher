"""Test the known DRS config +0x5a0 branch against saved VQM PCM.

Installed voicebank data is read only. The report contains aggregate feature
counts and configuration values, not PCM or frame payloads.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from audit_frm2_numeric import SHA256
from compare_vqm_drs_fields import analyze


FIELDS = (0, 1, 2, 4, 6, 9, 14, 15, 16, 17, 18, 19)


def run(manifest: Path, voice_root: Path, parser_root: Path,
        dll: Path) -> dict:
    cases = []
    for sample_index, mode in [
        *((0, mode) for mode in range(6)),
        (1, 4), (2, 4),
    ]:
        result = analyze(manifest, voice_root, parser_root, dll,
                         sample_index, mode, 0, 1, None)
        nonzero = {str(bit): result["produced_feature_ranges"][str(bit)][
            "nonzero_over_1e_6"] for bit in FIELDS}
        if (result["effective_config_5a0"] != 1
                or result["produced_feature_frames"] != result["saved_frames"]
                or result["feature_flags"] != {
                    "0x1fffff": result["saved_frames"]}
                or any(nonzero.values())):
            raise AssertionError("known compact DRS branch changed")
        cases.append({
            "sample_index": sample_index,
            "analysis_mode": mode,
            "frames": result["produced_feature_frames"],
            "feature_flags": result["feature_flags"],
            "nonzero_feature_counts": nonzero,
        })
    return {
        "dse_sha256": SHA256,
        "cases": cases,
        "scope": "Only the known config +0x5a0=1 branch, external pitch, and the specified modes/samples. Zero values reject this branch as a direct reproduction of saved VQM features; they do not identify the original authoring configuration.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--voice-root", type=Path, required=True)
    parser.add_argument("--ddb-tools", type=Path, required=True)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.manifest, args.voice_root, args.ddb_tools, args.dll)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"cases": len(report["cases"]),
                      "frames": sum(row["frames"] for row in report["cases"])}))


if __name__ == "__main__":
    main()
