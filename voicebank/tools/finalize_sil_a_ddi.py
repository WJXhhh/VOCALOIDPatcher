#!/usr/bin/env python3
"""Finalize a diagnostic Sil/a bank with one STA and both boundary transitions."""

from __future__ import annotations

import argparse
import json
import math
import struct
import sys
from pathlib import Path

import finalize_minimal_articulation_ddi as articulation
import finalize_stationary_ddi as stationary


PHONEME_ENTRY_SIZE = 31


def frame_range(value: str) -> tuple[int, int]:
    try:
        start_text, end_text = value.split(":", 1)
        start = int(start_text)
        end = int(end_text)
    except (ValueError, TypeError) as error:
        raise argparse.ArgumentTypeError("frame range must be START:END") from error
    if start < 0 or end < start:
        raise argparse.ArgumentTypeError("frame range must satisfy 0 <= START <= END")
    return start, end


def alignment_groups(
    frame_count: int,
    split_frame: int | None,
    source_inner: tuple[int, int] | None,
    target_inner: tuple[int, int] | None,
) -> tuple[tuple[int, int, int, int], ...]:
    boundary = frame_count // 2 if split_frame is None else split_frame
    source = source_inner or (0, boundary)
    target = target_inner or (boundary, frame_count)
    return (
        (0, boundary, source[0], source[1]),
        (boundary, frame_count, target[0], target[1]),
    )


def read_phoneme_entry(data: bytes, offset: int) -> tuple[str, bool]:
    entry = data[offset : offset + PHONEME_ENTRY_SIZE]
    if len(entry) != PHONEME_ENTRY_SIZE:
        raise stationary.FinalizeError("truncated PHDC phoneme entry")
    raw_name = entry[:18].split(b"\x00", 1)[0]
    try:
        name = raw_name.decode("ascii")
    except UnicodeDecodeError as error:
        raise stationary.FinalizeError("PHDC phoneme name is not ASCII") from error
    return name, entry[30] != 0


def validate_sil_a_phdc(skeleton: bytes) -> dict[str, object]:
    phdc = skeleton.find(b"PHDC")
    if phdc < 0 or phdc + 16 > len(skeleton):
        raise stationary.FinalizeError("skeleton has no complete PHDC header")
    flags, count = struct.unpack_from("<II", skeleton, phdc + 8)
    if count != 2:
        raise stationary.FinalizeError(f"PHDC must contain two phonemes, got {count}")
    entries = [
        read_phoneme_entry(skeleton, phdc + 16 + index * PHONEME_ENTRY_SIZE)
        for index in range(count)
    ]
    expected = [("Sil", True), ("a", False)]
    if entries != expected:
        raise stationary.FinalizeError(
            f"PHDC phonemes must be {expected!r}, got {entries!r}"
        )
    return {
        "flags": flags,
        "phonemes": [
            {"name": name, "unvoiced": unvoiced} for name, unvoiced in entries
        ],
    }


def transition_tail(source: str, target: str) -> bytes:
    def encoded_name(value: str) -> bytes:
        raw = value.encode("ascii")
        return struct.pack("<I", len(raw)) + raw

    return encoded_name("default") + encoded_name(target) + encoded_name(source)


def validate_transition_order(skeleton: bytes, positions: list[int]) -> None:
    expected = [("Sil", "a"), ("a", "Sil")]
    for index, ((source, target), position) in enumerate(zip(expected, positions)):
        limit = positions[index + 1] if index + 1 < len(positions) else len(skeleton)
        tail = transition_tail(source, target)
        found = skeleton.find(tail, position + len(articulation.ARTP_MAGIC), limit)
        if found < 0:
            raise stationary.FinalizeError(
                f"ARTp {index} is not the expected {source}->{target} transition"
            )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("skeleton", type=Path)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--pitch-hz", type=float, required=True)
    parser.add_argument("--sta-unit", type=int, default=0)
    parser.add_argument("--sil-to-a-unit", type=int, default=1)
    parser.add_argument("--a-to-sil-unit", type=int, default=2)
    parser.add_argument("--sil-to-a-split-frame", type=int)
    parser.add_argument("--a-to-sil-split-frame", type=int)
    parser.add_argument("--sil-to-a-source-inner", type=frame_range)
    parser.add_argument("--sil-to-a-target-inner", type=frame_range)
    parser.add_argument("--a-to-sil-source-inner", type=frame_range)
    parser.add_argument("--a-to-sil-target-inner", type=frame_range)
    parser.add_argument("--singer-name")
    parser.add_argument("--unknown2", type=float, default=0.0)
    parser.add_argument("--dynamics", type=float, default=0.6)
    parser.add_argument("--tempo", type=float, default=90.0)
    args = parser.parse_args()

    if not math.isfinite(args.pitch_hz) or args.pitch_hz <= 0:
        parser.error("--pitch-hz must be finite and positive")
    unit_indexes = (args.sta_unit, args.sil_to_a_unit, args.a_to_sil_unit)
    if len(set(unit_indexes)) != len(unit_indexes):
        parser.error("STA, Sil->a, and a->Sil must use distinct manifest units")

    try:
        manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
        units = [articulation.unit_info(manifest, index) for index in unit_indexes]
        skeleton = args.skeleton.read_bytes()
        phdc_report = validate_sil_a_phdc(skeleton)
        positions = articulation.articulation_positions(skeleton)
        if len(positions) != 2:
            raise stationary.FinalizeError(
                f"skeleton must contain exactly two ARTp chunks, got {len(positions)}"
            )
        validate_transition_order(skeleton, positions)

        transitions = [
            (
                "Sil",
                "a",
                units[1],
                args.sil_to_a_split_frame,
                args.sil_to_a_source_inner,
                args.sil_to_a_target_inner,
            ),
            (
                "a",
                "Sil",
                units[2],
                args.a_to_sil_split_frame,
                args.a_to_sil_source_inner,
                args.a_to_sil_target_inner,
            ),
        ]
        reports: list[dict[str, object] | None] = [None] * len(transitions)
        with_articulations = skeleton
        for index in range(len(transitions) - 1, -1, -1):
            source, target, unit, split_frame, source_inner, target_inner = transitions[index]
            with_articulations, report = articulation.insert_articulation_at(
                with_articulations,
                positions[index],
                unit,
                args.pitch_hz,
                split_frame,
                args.unknown2,
                args.dynamics,
                args.tempo,
                alignment_groups(
                    len(unit.frame_offsets),
                    split_frame,
                    source_inner,
                    target_inner,
                ),
            )
            report["source_phoneme"] = source
            report["target_phoneme"] = target
            report["manifest_unit"] = unit_indexes[index + 1]
            reports[index] = report

        ddi, stationary_report = stationary.finalize_skeleton(
            with_articulations,
            units[0],
            args.pitch_hz,
            args.unknown2,
            args.dynamics,
            args.tempo,
            args.singer_name or args.output.stem,
        )
        stationary.write_atomic(args.output, ddi)
    except (
        OSError,
        ValueError,
        UnicodeEncodeError,
        json.JSONDecodeError,
        struct.error,
        stationary.FinalizeError,
    ) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2

    print(
        json.dumps(
            {
                "output": str(args.output.resolve()),
                "output_bytes": len(ddi),
                "phonetic_dictionary": phdc_report,
                "stationary": stationary_report,
                "articulations": reports,
            },
            ensure_ascii=False,
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
