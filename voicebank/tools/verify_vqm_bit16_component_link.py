"""Compare final feature bit16 with the first component amplitude.

DRS assembly at 0x1800d7391..0x1800d73b9 writes first-amplitude / 10
at an intermediate stage when pitch, harmonic count and first frequency
are positive. Test whether that relationship survives to final frames.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

from audit_frm2_numeric import F, I, Native, P, SHA256, f32
from probe_frm2_batch_outputs import owned_pcm, run as analyze_batch
from verify_vqm_bit16_spectral_power import load_sequences


def metrics(rows: list[tuple[float, float]]) -> dict:
    differences = [f32(f32(amplitude) / f32(10)) - feature
                   for feature, amplitude in rows]
    errors = [abs(value) for value in differences]
    return {"frames": len(rows),
            "exact_float32": sum(error == 0 for error in errors),
            "within_1e_6": sum(error <= 1e-6 for error in errors),
            "mean_signed_difference": sum(differences) / len(differences),
            "minimum_signed_difference": min(differences),
            "maximum_signed_difference": max(differences),
            "mean_absolute_error": sum(errors) / len(errors),
            "max_absolute_error": max(errors)}


def native_metrics(dll: Path, pcm: list[float] | tuple[int, ...],
                   pitch: float) -> dict:
    captured = []
    analyze_batch(dll, 0, 1, "steady", pitch, analysis_mode=4,
                  pitch_seed=pitch, pcm_override=pcm,
                  setup_observer=lambda native, context:
                  captured.append((native, context)))
    native, context = captured[0]
    track = P.from_address(context + 0xcd0).value
    regions = P.from_address(track + 0x148).value
    rows = []
    for region_index in range(I.from_address(track + 0x140).value):
        region = P.from_address(regions + 8 * region_index).value
        frames = P.from_address(region + 0x20).value
        for frame_index in range(I.from_address(region + 0x18).value):
            frame = P.from_address(frames + 8 * frame_index).value
            feature = P.from_address(frame + 0x128).value
            count = I.from_address(frame + 0x58).value
            frequencies = P.from_address(frame + 0x60).value
            amplitudes = P.from_address(frame + 0x68).value
            if (not feature or not count or not frequencies or not amplitudes
                    or F.from_address(frequencies).value <= 0):
                continue
            rows.append((F.from_address(feature + 0x218).value,
                         F.from_address(amplitudes).value))
    del native
    if not rows:
        raise AssertionError("No valid first harmonic")
    return metrics(rows)


def run(dll: Path, manifest: Path, voice_root: Path,
        parser_root: Path) -> dict:
    native = Native(dll)
    divisor = F.from_address(native.lib._handle + 0x5bfc1c).value
    if divisor != 10:
        raise AssertionError("DRS divisor changed")
    del native
    parser_sha, ddi_sha, sequences = load_sequences(
        manifest, voice_root, parser_root)
    saved = []
    native_rows = [native_metrics(dll, owned_pcm("steady", 440), 440)]
    for sequence in sequences:
        rows = [(frame["bit16"], frame["first_component_amplitude"])
                for frame in sequence["frames"]
                if frame["first_component_amplitude"] is not None]
        if len(rows) != len(sequence["frames"]):
            raise AssertionError("VQM component missing")
        if any(not math.isfinite(value) for row in rows for value in row):
            raise AssertionError("VQM nonfinite value")
        saved.append(metrics(rows))
        native_rows.append(native_metrics(
            dll, sequence["pcm"], sequence["pitch1"]))
    if [row["frames"] for row in saved] != [72, 87, 87]:
        raise AssertionError("VQM saved frame count changed")
    return {"dse_sha256": SHA256, "ddi_sha256": ddi_sha,
            "parser_sha256": parser_sha, "divisor": divisor,
            "native_cases": native_rows, "saved_vqm_cases": saved,
            "scope": "DRS writes first component amplitude / 10 at one "
                     "intermediate point. This report checks final DRS and "
                     "saved V4X VQM frames separately; it does not assume "
                     "the intermediate value survives later processing."}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--voice-root", required=True, type=Path)
    parser.add_argument("--ddb-tools", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = run(args.dll, args.manifest, args.voice_root,
                 args.ddb_tools)
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({"native_frames": sum(row["frames"] for row in
                                           report["native_cases"]),
                      "saved_frames": sum(row["frames"] for row in
                                          report["saved_vqm_cases"])}))


if __name__ == "__main__":
    main()
