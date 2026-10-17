"""Audit the DRS +0x5a0 construction/analysis timing with owned PCM.

Only the three supported timing combinations are run. Switching from a
minimal context to the full analysis path after construction is invalid.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from audit_frm2_numeric import F, I, P, SHA256
from probe_frm2_batch_outputs import owned_pcm, run as analyze_batch


ARRAYS = {10: (0x2c, 40), 11: (0xcc, 40),
          12: (0x16c, 20), 13: (0x1bc, 20)}


def inspect(dll: Path, constructed: int, analyzed: int) -> dict:
    observed = []

    def after_construct(native, context):
        config = P.from_address(context + 0xad0).value
        if not config:
            raise AssertionError("DRS config pointer missing")
        allocation_offsets = (0x8e * 8, 0x8f * 8, 0x18e * 8,
                              0x191 * 8, 0x193 * 8, 0x198 * 8)
        allocations = [bool(P.from_address(context + offset).value)
                       for offset in allocation_offsets]
        observed.append((native, context, allocations))
        I.from_address(config + 0x5a0).value = analyzed

    summary = analyze_batch(
        dll, 0, 1, "steady", 440, analysis_mode=4, pitch_seed=440,
        pcm_override=owned_pcm("steady", 440),
        config_5a0=constructed, setup_observer=after_construct)
    native, context, allocations = observed[0]
    track = P.from_address(context + 0xcd0).value
    regions = P.from_address(track + 0x148).value
    counts = {bit: 0 for bit in ARRAYS}
    energy_nonzero = 0
    angle_nonzero = 0
    frames = 0
    for region_index in range(I.from_address(track + 0x140).value):
        region = P.from_address(regions + 8 * region_index).value
        frame_list = P.from_address(region + 0x20).value
        for frame_index in range(I.from_address(region + 0x18).value):
            frame = P.from_address(frame_list + 8 * frame_index).value
            feature = P.from_address(frame + 0x128).value
            if not feature or I.from_address(feature).value != 0x1fffff:
                raise AssertionError("Unexpected feature flags")
            frames += 1
            energy_nonzero += F.from_address(feature + 0x0c).value != 0
            angle_nonzero += F.from_address(feature + 0x21c).value != 0
            for bit, (offset, length) in ARRAYS.items():
                counts[bit] += sum(
                    F.from_address(feature + offset + 4 * i).value != 0
                    for i in range(length))
    if frames != summary["frames"] or frames != 44:
        raise AssertionError("Unexpected frame count")
    del native
    return {"construct_5a0": constructed, "analyze_5a0": analyzed,
            "frames": frames, "full_path_allocations": allocations,
            "energy_nonzero_frames": energy_nonzero,
            "angle_nonzero_frames": angle_nonzero,
            "array_nonzero": {str(bit): counts[bit] for bit in ARRAYS}}


def run(dll: Path) -> dict:
    rows = [inspect(dll, *settings) for settings in ((0, 0), (1, 1), (0, 1))]
    if ([row["array_nonzero"] for row in rows]
            != [{"10": 1760, "11": 1504, "12": 880, "13": 880},
                {"10": 292, "11": 0, "12": 0, "13": 0},
                {"10": 0, "11": 0, "12": 0, "13": 0}]):
        raise AssertionError("DRS timing matrix changed")
    if [row["full_path_allocations"] for row in rows] != [
            [True] * 6, [False] * 6, [True] * 6]:
        raise AssertionError("DRS construction allocations changed")
    return {"dse_sha256": SHA256, "cases": rows,
            "scope": "Owned 440 Hz PCM, mode 4. The constructor reads +0x5a0 "
                     "to allocate full-path state; the analysis-stage switch "
                     "also affects feature production. Not a VQM authoring path."}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = run(args.dll)
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({"cases": len(report["cases"]),
                      "frames": sum(row["frames"] for row in report["cases"])}))


if __name__ == "__main__":
    main()
