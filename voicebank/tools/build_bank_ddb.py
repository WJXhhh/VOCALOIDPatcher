#!/usr/bin/env python3
"""Concatenate validated one-unit DDB files and emit adjusted unit metadata."""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

import finalize_stationary_ddi


def write_atomic(path: Path, chunks: list[bytes]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    try:
        with temporary.open("wb") as output:
            for chunk in chunks:
                output.write(chunk)
            output.flush()
            os.fsync(output.fileno())
        temporary.replace(path)
    finally:
        if temporary.exists():
            temporary.unlink()


def build(
    output: Path,
    inputs: list[Path],
    manifest: Path | None = None,
) -> dict[str, object]:
    chunks: list[bytes] = []
    units: list[dict[str, object]] = []
    base = 0
    for index, path in enumerate(inputs):
        info = finalize_stationary_ddi.inspect_ddb(path)
        raw = path.read_bytes()
        chunks.append(raw)
        units.append(
            {
                "index": index,
                "source": str(path.resolve()),
                "base_offset": base,
                "frame_offsets": [base + value for value in info.frame_offsets],
                "snd_offset": base + info.snd_offset,
                "snd_size": info.snd_size,
                "sample_rate": info.sample_rate,
                "channels": info.channels,
                "pcm_count": info.pcm_count,
                "snd_payload_pointer": base
                + info.snd_offset
                + finalize_stationary_ddi.SND_HEADER.size,
                "snd_core_pointer": base
                + info.snd_offset
                + finalize_stationary_ddi.SND_HEADER.size
                + finalize_stationary_ddi.ANALYSIS_MARGIN_SAMPLES * 2,
            }
        )
        base += len(raw)
    write_atomic(output, chunks)
    report: dict[str, object] = {
        "output": str(output.resolve()),
        "output_bytes": base,
        "unit_count": len(units),
        "units": units,
    }
    if manifest is not None:
        encoded = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
        write_atomic(manifest, [encoded.encode("utf-8")])
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("inputs", nargs="+", type=Path, help="one-unit DDB files in output order")
    parser.add_argument("--manifest", type=Path, help="optional JSON manifest output")
    args = parser.parse_args()

    try:
        report = build(args.output, args.inputs, args.manifest)
        print(json.dumps(report, ensure_ascii=False, indent=2))
    except (OSError, ValueError, finalize_stationary_ddi.FinalizeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
