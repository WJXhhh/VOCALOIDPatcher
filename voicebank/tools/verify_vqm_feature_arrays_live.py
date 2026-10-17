"""Check whether DRS populates the four bit-13 arrays on owned PCM.

The saved V4X VQM arrays are all zero. This audit samples the corresponding
in-memory DRS feature slots under exercised analysis modes without exporting PCM.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import random
import statistics
import struct

from audit_frm2_numeric import F, I, P, SHA256, f32
from probe_frm2_batch_outputs import owned_pcm, run as analyze_batch
from verify_vqm_bit16_spectral_power import load_sequences


ARRAYS = {10: (0x2c, 40), 11: (0xcc, 40),
          12: (0x16c, 20), 13: (0x1bc, 20)}


def signal(shape: str) -> list[float]:
    if shape == "harmonic":
        return owned_pcm("steady", 440)
    if shape == "noise":
        generator = random.Random(104)
        samples = [generator.uniform(-1.0, 1.0)
                   for _ in range(11025)]
        rms = math.sqrt(statistics.fmean(value * value
                                         for value in samples))
        return [f32(5000 * value / rms) for value in samples]
    raise ValueError(shape)


def inspect(dll: Path, mode: int, automatic: int, shape: str,
            pcm_override: list[float] | tuple[int, ...] | None = None,
            reference_pitch: float = 440,
            config_5a0: int | None = None) -> dict:
    holder = []
    summary = analyze_batch(
        dll, automatic, 1, "steady", reference_pitch,
        analysis_mode=mode, pitch_seed=reference_pitch,
        pcm_override=signal(shape) if pcm_override is None else pcm_override,
        config_5a0=config_5a0,
        setup_observer=lambda native, context: holder.append((native, context)),
    )
    native, context = holder[0]
    track = P.from_address(context + 0xcd0).value
    if not track:
        raise AssertionError("DRS returned no feature track")
    regions = P.from_address(track + 0x148).value
    counts = {bit: 0 for bit in ARRAYS}
    nonzero = {bit: 0 for bit in ARRAYS}
    maxima = {bit: 0.0 for bit in ARRAYS}
    hashes = {bit: hashlib.sha256() for bit in ARRAYS}
    flags = {}
    feature_frames = 0
    bit17_nonzero = 0
    bit2_nonzero = 0
    bit2_maximum = 0.0
    for region_index in range(I.from_address(track + 0x140).value):
        region = P.from_address(regions + 8 * region_index).value
        frames = P.from_address(region + 0x20).value
        for frame_index in range(I.from_address(region + 0x18).value):
            frame = P.from_address(frames + 8 * frame_index).value
            feature = P.from_address(frame + 0x128).value
            if not feature:
                continue
            feature_frames += 1
            mask = I.from_address(feature).value
            flags[hex(mask)] = flags.get(hex(mask), 0) + 1
            if mask & (1 << 2):
                energy = F.from_address(feature + 0x0c).value
                if not math.isfinite(energy):
                    raise ValueError("DRS feature energy is nonfinite")
                bit2_nonzero += energy != 0
                bit2_maximum = max(bit2_maximum, abs(energy))
            if mask & (1 << 17) and F.from_address(feature + 0x21c).value != 0:
                bit17_nonzero += 1
            for bit, (offset, length) in ARRAYS.items():
                if not mask & (1 << bit):
                    continue
                for index in range(length):
                    value = F.from_address(feature + offset + 4 * index).value
                    if not math.isfinite(value):
                        raise ValueError("DRS feature array has nonfinite element")
                    counts[bit] += 1
                    nonzero[bit] += value != 0
                    maxima[bit] = max(maxima[bit], abs(value))
                    hashes[bit].update(struct.pack("<f", value))
    if feature_frames != summary["frames"]:
        raise AssertionError("feature frame count differs from batch")
    del native
    return {
        "analysis_mode": mode,
        "automatic_pitch": automatic,
        "pcm_shape": shape,
        "config_5a0": config_5a0,
        "frames": summary["frames"],
        "feature_flags": flags,
        "array_elements": {str(bit): counts[bit] for bit in ARRAYS},
        "array_nonzero": {str(bit): nonzero[bit] for bit in ARRAYS},
        "array_max_absolute": {str(bit): maxima[bit] for bit in ARRAYS},
        "array_sha256": {str(bit): hashes[bit].hexdigest()
                         for bit in ARRAYS},
        "bit17_nonzero_frames": bit17_nonzero,
        "bit2_nonzero_frames": bit2_nonzero,
        "bit2_max_absolute": bit2_maximum,
    }


def run(dll: Path, manifest: Path, voice_root: Path,
        parser_root: Path) -> dict:
    cases = [inspect(dll, mode, automatic, shape)
             for mode in range(6)
             for automatic in (0, 1)
             for shape in ("harmonic", "noise")]
    for bit in ARRAYS:
        key = str(bit)
        for shape in ("harmonic", "noise"):
            group = [row for row in cases if row["pcm_shape"] == shape]
            if (any(row["array_elements"][key] != row["frames"] * ARRAYS[bit][1]
                    or row["array_nonzero"][key] == 0 for row in group)
                    or len({row["array_sha256"][key] for row in group}) != 1):
                raise AssertionError("DRS feature array coverage differs")
        harmonic = next(row for row in cases if row["pcm_shape"] == "harmonic")
        noise = next(row for row in cases if row["pcm_shape"] == "noise")
        if harmonic["array_sha256"][key] == noise["array_sha256"][key]:
            raise AssertionError("DRS feature array ignored PCM shape")
    parser_hash, ddi_hash, sequences = load_sequences(
        manifest, voice_root, parser_root)
    saved_pcm_cases = [inspect(dll, 4, 0, f"saved_vqm_{index}",
                               sequence["pcm"], sequence["pitch1"])
                       for index, sequence in enumerate(sequences)]
    alternate_branch_cases = [inspect(
        dll, 4, 0, f"saved_vqm_{index}",
        sequence["pcm"], sequence["pitch1"], config_5a0=1)
        for index, sequence in enumerate(sequences)]
    if ([row["frames"] for row in saved_pcm_cases] != [72, 87, 87]
            or any(row["array_nonzero"][str(bit)] == 0
                   for row in saved_pcm_cases for bit in ARRAYS)):
        raise AssertionError("saved VQM PCM no longer produces DRS arrays")
    return {
        "dse_sha256": SHA256,
        "ddi_sha256": ddi_hash,
        "parser_sha256": parser_hash,
        "owned_pcm_cases": cases,
        "saved_vqm_pcm_cases": saved_pcm_cases,
        "alternate_branch_cases": alternate_branch_cases,
        "scope": "Owned harmonic/noise PCM through DRS modes 0..5 and external/automatic pitch, plus three read-only installed VQM PCM sequences through default mode 4 and its config +0x5a0 alternate branch. Arrays are observed at mapped DSE feature object offsets; this does not identify the VQM authoring transformation.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--voice-root", type=Path, required=True)
    parser.add_argument("--ddb-tools", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll, args.manifest, args.voice_root, args.ddb_tools)
    args.output.write_text(json.dumps(report, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({"owned_cases": len(report["owned_pcm_cases"]),
                      "saved_pcm_cases": len(report["saved_vqm_pcm_cases"]),
                      "alternate_branch_cases": len(report["alternate_branch_cases"]),
                      "frames": sum(row["frames"]
                                    for row in [*report["owned_pcm_cases"],
                                                *report["saved_vqm_pcm_cases"],
                                                *report["alternate_branch_cases"]])}))


if __name__ == "__main__":
    main()
