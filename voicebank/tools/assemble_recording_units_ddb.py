#!/usr/bin/env python3
"""Stream validated recording units into one deterministic multi-unit DDB."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import struct
import sys
from array import array
from pathlib import Path, PurePosixPath
from typing import Any, BinaryIO

import analyze_recording_units as analysis_tools
import build_unit_ddb
import probe_frm2
import validate_main_sms2


STEM = re.compile(r"[A-Za-z0-9_-]+\Z")


class AssemblyError(Exception):
    pass


def valid_sha256(value: Any, context: str) -> str:
    if not isinstance(value, str) or len(value) != 64:
        raise AssemblyError(f"{context} has no SHA-256")
    try:
        bytes.fromhex(value)
    except ValueError as error:
        raise AssemblyError(f"{context} has an invalid SHA-256") from error
    return value.lower()


def load_inputs(
    unit_manifest_path: Path,
    analysis_manifest_path: Path,
    unit_root: Path,
    analysis_root: Path,
) -> tuple[list[dict[str, Any]], bool, dict[str, Any], dict[str, Any]]:
    unit_manifest = analysis_tools.read_json(unit_manifest_path)
    all_units, input_complete = analysis_tools.validate_unit_list(unit_manifest)
    unit_by_id = {item["unit_id"]: item for item in all_units}

    report = analysis_tools.read_json(analysis_manifest_path)
    if not isinstance(report, dict) or report.get("format") != (
        "vocaloid-recording-unit-drs-analysis-v1"
    ):
        raise AssemblyError("unsupported DRS analysis manifest format")
    source = report.get("source")
    summary = report.get("summary")
    analyzed = report.get("units")
    if (
        not isinstance(source, dict)
        or not isinstance(summary, dict)
        or not isinstance(analyzed, list)
    ):
        raise AssemblyError("DRS analysis manifest lacks source, summary, or units")
    if source.get("unit_manifest_sha256") != analysis_tools.file_sha256(
        unit_manifest_path
    ):
        raise AssemblyError("DRS source unit-manifest file SHA-256 differs")
    if source.get("unit_manifest_canonical_sha256") != unit_manifest["summary"].get(
        "unit_manifest_sha256"
    ):
        raise AssemblyError("DRS source unit-manifest canonical SHA-256 differs")
    if (
        summary.get("input_units") != len(all_units)
        or summary.get("selected_units") != len(analyzed)
        or summary.get("articulation_units")
        != sum(
            isinstance(item, dict) and item.get("kind") == "articulation"
            for item in analyzed
        )
        or summary.get("stationary_units")
        != sum(
            isinstance(item, dict) and item.get("kind") == "stationary"
            for item in analyzed
        )
    ):
        raise AssemblyError("DRS analysis summary counts differ")
    if summary.get("input_coverage_complete") is not input_complete:
        raise AssemblyError("DRS input coverage differs from the unit manifest")
    selection_complete = len(analyzed) == len(all_units)
    if summary.get("selection_complete") is not selection_complete:
        raise AssemblyError("DRS selection_complete is internally inconsistent")
    coverage_complete = input_complete and selection_complete
    if (
        summary.get("analysis_complete") is not True
        or summary.get("coverage_complete") is not coverage_complete
        or summary.get("approval_complete") is not False
    ):
        raise AssemblyError("DRS completion or approval flags are inconsistent")
    if summary.get("analysis_manifest_sha256") != (
        analysis_tools.analysis_manifest_digest(analyzed)
    ):
        raise AssemblyError("DRS analysis canonical SHA-256 differs")

    prepared: list[dict[str, Any]] = []
    seen: set[str] = set()
    for index, item in enumerate(analyzed):
        if not isinstance(item, dict):
            raise AssemblyError(f"DRS unit {index} is not an object")
        unit_id = item.get("unit_id")
        if not isinstance(unit_id, str) or unit_id in seen or unit_id not in unit_by_id:
            raise AssemblyError(f"DRS unit {index} has an invalid or duplicate ID")
        seen.add(unit_id)
        unit = unit_by_id[unit_id]
        if (
            item.get("kind") != unit["kind"]
            or item.get("input_relative_wav") != unit["output_relative_wav"]
            or item.get("input_wav_sha256") != unit["output_wav_sha256"]
            or item.get("validation_status") != "structurally_valid_unapproved"
            or item.get("approval_status") != "unapproved_drs_analysis"
        ):
            raise AssemblyError(f"DRS/unit identity differs for {unit_id}")
        wav_source = analysis_tools.preflight_unit(unit, unit_root)
        sms2_relative = analysis_tools.safe_relative_path(
            item.get("output_relative_sms2"), ".sms2", f"unit {unit_id} SMS2"
        )
        expected_sms2 = (
            PurePosixPath("sms2")
            / PurePosixPath(unit["output_relative_wav"]).with_suffix(".sms2")
        )
        if sms2_relative != expected_sms2:
            raise AssemblyError(f"unit {unit_id} SMS2 path differs")
        sms2_path = analysis_tools.resolve_below(
            analysis_root, sms2_relative, f"unit {unit_id} SMS2"
        )
        if not sms2_path.is_file():
            raise AssemblyError(f"unit SMS2 does not exist: {sms2_path}")
        sms2_sha = valid_sha256(item.get("output_sms2_sha256"), f"unit {unit_id} SMS2")
        if analysis_tools.file_sha256(sms2_path) != sms2_sha:
            raise AssemblyError(f"unit {unit_id} SMS2 SHA-256 differs")
        valid_sha256(item.get("frm2_payload_sha256"), f"unit {unit_id} FRM2 payload")
        frame_count = item.get("drs_frame_count")
        if (
            isinstance(frame_count, bool)
            or not isinstance(frame_count, int)
            or frame_count != wav_source["expected_drs_frames"]
        ):
            raise AssemblyError(f"unit {unit_id} DRS frame count differs")
        prepared.append(
            {
                "unit": unit,
                "analysis": item,
                "wav": wav_source,
                "sms2_path": sms2_path,
                "sms2_sha256": sms2_sha,
            }
        )
    return prepared, coverage_complete, unit_manifest, report


def expected_runs(values: list[str]) -> list[dict[str, Any]]:
    return analysis_tools.voicing_runs(values)


def validate_frames(item: dict[str, Any], frames: list[bytes]) -> None:
    unit = item["unit"]
    analyzed = item["analysis"]
    unit_id = unit["unit_id"]
    if len(frames) != analyzed["drs_frame_count"]:
        raise AssemblyError(f"unit {unit_id} FRM2 count changed")
    if analysis_tools.frame_payload_hash(frames) != analyzed["frm2_payload_sha256"]:
        raise AssemblyError(f"unit {unit_id} FRM2 payload SHA-256 differs")
    observed = [analysis_tools.frame_voicing(raw) for raw in frames]
    if analyzed.get("voicing_runs") != expected_runs(observed):
        raise AssemblyError(f"unit {unit_id} voicing runs differ")
    if unit["kind"] == "stationary":
        if any(value != "voiced" for value in observed):
            raise AssemblyError(f"stationary unit {unit_id} is not fully voiced")
        return
    split = analyzed.get("split_frame")
    voicing = analyzed.get("voicing")
    if (
        isinstance(split, bool)
        or not isinstance(split, int)
        or not isinstance(voicing, dict)
    ):
        raise AssemblyError(f"articulation unit {unit_id} lacks split/voicing")
    build_unit_ddb.validate_voicing_boundary(
        frames,
        split,
        voicing.get("source"),
        voicing.get("target"),
    )
    source_inner = analyzed.get("source_inner_frames")
    target_inner = analyzed.get("target_inner_frames")
    if (
        not isinstance(source_inner, list)
        or len(source_inner) != 2
        or not isinstance(target_inner, list)
        or len(target_inner) != 2
        or any(
            isinstance(value, bool) or not isinstance(value, int)
            for value in [*source_inner, *target_inner]
        )
        or not 0 <= source_inner[0] < source_inner[1] <= split
        or not split <= target_inner[0] < target_inner[1] <= len(frames)
    ):
        raise AssemblyError(f"articulation unit {unit_id} inner ranges differ")


def write_block(
    output: BinaryIO,
    block: bytes,
    bank_digest: Any,
    unit_digest: Any,
) -> None:
    output.write(block)
    bank_digest.update(block)
    unit_digest.update(block)


def assemble_unit(
    output: BinaryIO,
    item: dict[str, Any],
    bank_digest: Any,
) -> dict[str, Any]:
    unit = item["unit"]
    analyzed = item["analysis"]
    unit_id = unit["unit_id"]
    if analysis_tools.file_sha256(item["sms2_path"]) != item["sms2_sha256"]:
        raise AssemblyError(f"unit {unit_id} SMS2 changed after preflight")
    frames = build_unit_ddb.extract_frames(item["sms2_path"])
    validate_frames(item, frames)
    if analysis_tools.file_sha256(item["sms2_path"]) != item["sms2_sha256"]:
        raise AssemblyError(f"unit {unit_id} SMS2 changed while reading")

    wav_path = item["wav"]["path"]
    wav_sha = unit["output_wav_sha256"]
    if analysis_tools.file_sha256(wav_path) != wav_sha:
        raise AssemblyError(f"unit {unit_id} WAV changed after preflight")
    core_pcm: array[int] = build_unit_ddb.read_pcm16_mono(wav_path)
    if analysis_tools.file_sha256(wav_path) != wav_sha:
        raise AssemblyError(f"unit {unit_id} WAV changed while reading")
    snd, pcm_count, padding = build_unit_ddb.build_snd(core_pcm, len(frames))

    base = output.tell()
    frame_offsets: list[int] = []
    unit_digest = hashlib.sha256()
    for raw in frames:
        frame_offsets.append(output.tell())
        write_block(output, raw, bank_digest, unit_digest)
    snd_offset = output.tell()
    write_block(output, snd, bank_digest, unit_digest)
    end = output.tell()
    snd_payload_pointer = snd_offset + build_unit_ddb.SND_HEADER.size
    snd_core_pointer = (
        snd_payload_pointer + build_unit_ddb.ANALYSIS_MARGIN_SAMPLES * 2
    )
    result: dict[str, Any] = {
        "unit_id": unit_id,
        "kind": unit["kind"],
        "layer_id": unit.get("layer_id"),
        "base_offset": base,
        "end_offset": end,
        "unit_bytes": end - base,
        "ddb_unit_sha256": unit_digest.hexdigest(),
        "frame_count": len(frames),
        "frame_offsets": frame_offsets,
        "snd_chunk_offset": snd_offset,
        "snd_chunk_size": len(snd),
        "snd_payload_pointer": snd_payload_pointer,
        "snd_core_pointer": snd_core_pointer,
        "sample_rate": build_unit_ddb.SAMPLE_RATE,
        "channels": 1,
        "pcm_count": pcm_count,
        "input_core_samples": len(core_pcm),
        "core_padding_samples": padding,
        "f0_hz": unit["builder_spec"]["f0_hz"],
        "source_frm2_payload_sha256": analyzed["frm2_payload_sha256"],
        "approval_status": "unapproved_ddb_unit",
    }
    if unit["kind"] == "articulation":
        split = analyzed["split_frame"]
        result.update(
            {
                "edge": analyzed.get("edge"),
                "role": analyzed.get("role"),
                "voicing": analyzed.get("voicing"),
                "frame_alignments": [
                    [0, split, *analyzed["source_inner_frames"]],
                    [split, len(frames), *analyzed["target_inner_frames"]],
                ],
            }
        )
    else:
        result["phoneme"] = analyzed.get("phoneme")
    return result


def hash_range(stream: BinaryIO, start: int, end: int) -> str:
    stream.seek(start)
    remaining = end - start
    digest = hashlib.sha256()
    while remaining:
        block = stream.read(min(1024 * 1024, remaining))
        if not block:
            raise AssemblyError("DDB ended while hashing a unit range")
        digest.update(block)
        remaining -= len(block)
    return digest.hexdigest()


def verify_output(path: Path, units: list[dict[str, Any]], expected_sha: str) -> None:
    if analysis_tools.file_sha256(path) != expected_sha:
        raise AssemblyError("final DDB SHA-256 differs after atomic write")
    file_size = path.stat().st_size
    if not units or units[-1]["end_offset"] != file_size:
        raise AssemblyError("final DDB size differs from its unit map")
    with path.open("rb") as stream:
        expected_base = 0
        for unit in units:
            if unit["base_offset"] != expected_base:
                raise AssemblyError(f"unit {unit['unit_id']} base offset is not contiguous")
            if hash_range(stream, unit["base_offset"], unit["end_offset"]) != unit[
                "ddb_unit_sha256"
            ]:
                raise AssemblyError(f"unit {unit['unit_id']} output SHA-256 differs")
            frame_digest = hashlib.sha256()
            for offset in unit["frame_offsets"]:
                stream.seek(offset)
                header = stream.read(8)
                if len(header) != 8:
                    raise AssemblyError(f"unit {unit['unit_id']} has a truncated FRM2")
                magic, size = struct.unpack("<4sI", header)
                if magic != b"FRM2" or size < 8:
                    raise AssemblyError(f"unit {unit['unit_id']} has an invalid FRM2")
                raw = header + stream.read(size - 8)
                if len(raw) != size:
                    raise AssemblyError(f"unit {unit['unit_id']} has a short FRM2")
                frame = probe_frm2.parse_frame(raw)
                if probe_frm2.serialize_frame(frame) != raw:
                    raise AssemblyError(f"unit {unit['unit_id']} FRM2 does not round-trip")
                frame_digest.update(size.to_bytes(8, "little"))
                frame_digest.update(raw)
            if frame_digest.hexdigest() != unit["source_frm2_payload_sha256"]:
                raise AssemblyError(f"unit {unit['unit_id']} output FRM2 hash differs")
            stream.seek(unit["snd_chunk_offset"])
            header = stream.read(build_unit_ddb.SND_HEADER.size)
            if len(header) != build_unit_ddb.SND_HEADER.size:
                raise AssemblyError(f"unit {unit['unit_id']} has a truncated SND")
            magic, size, rate, channels, pcm_count = build_unit_ddb.SND_HEADER.unpack(
                header
            )
            if (
                magic != b"SND "
                or size != unit["snd_chunk_size"]
                or rate != unit["sample_rate"]
                or channels != unit["channels"]
                or pcm_count != unit["pcm_count"]
                or unit["snd_chunk_offset"] + size != unit["end_offset"]
            ):
                raise AssemblyError(f"unit {unit['unit_id']} SND metadata differs")
            expected_base = unit["end_offset"]


def canonical_manifest_digest(ddb_sha: str, units: list[dict[str, Any]]) -> str:
    return analysis_tools.canonical_json_hash(
        {
            "ddb_sha256": ddb_sha,
            "units": [
                {
                    key: item.get(key)
                    for key in (
                        "unit_id",
                        "kind",
                        "layer_id",
                        "base_offset",
                        "end_offset",
                        "ddb_unit_sha256",
                        "frame_offsets",
                        "snd_chunk_offset",
                        "snd_payload_pointer",
                        "snd_core_pointer",
                        "pcm_count",
                        "frame_alignments",
                        "edge",
                        "phoneme",
                    )
                }
                for item in units
            ],
        }
    )


def build(
    unit_manifest_path: Path,
    analysis_manifest_path: Path,
    unit_root: Path,
    analysis_root: Path,
    output_root: Path,
    stem: str,
) -> dict[str, Any]:
    if STEM.fullmatch(stem) is None:
        raise AssemblyError(
            "stem must contain only ASCII letters, digits, underscore, or hyphen"
        )
    prepared, coverage_complete, unit_manifest, analysis_manifest = load_inputs(
        unit_manifest_path,
        analysis_manifest_path,
        unit_root,
        analysis_root,
    )
    if output_root.exists():
        raise AssemblyError(f"output directory already exists: {output_root}")
    output_root.parent.mkdir(parents=True, exist_ok=True)
    output_root.mkdir()
    marker = output_root / "ASSEMBLY_INCOMPLETE"
    marker.write_text("DDB assembly did not reach the final manifest yet.\n", encoding="utf-8")
    ddb_path = output_root / f"{stem}.ddb"
    temporary = ddb_path.with_name(ddb_path.name + ".tmp")
    bank_digest = hashlib.sha256()
    unit_reports: list[dict[str, Any]] = []
    try:
        with temporary.open("xb") as output:
            for index, item in enumerate(prepared, 1):
                print(f"[{index}/{len(prepared)}] {item['unit']['unit_id']}", flush=True)
                unit_reports.append(assemble_unit(output, item, bank_digest))
            output.flush()
            os.fsync(output.fileno())
        temporary.replace(ddb_path)
    finally:
        if temporary.exists():
            temporary.unlink()

    ddb_sha = bank_digest.hexdigest()
    verify_output(ddb_path, unit_reports, ddb_sha)
    canonical_digest = canonical_manifest_digest(ddb_sha, unit_reports)
    manifest = {
        "format": "vocaloid-recording-units-ddb-v1",
        "source": {
            "unit_manifest_file_sha256": analysis_tools.file_sha256(
                unit_manifest_path
            ),
            "unit_manifest_canonical_sha256": unit_manifest["summary"][
                "unit_manifest_sha256"
            ],
            "analysis_manifest_file_sha256": analysis_tools.file_sha256(
                analysis_manifest_path
            ),
            "analysis_manifest_canonical_sha256": analysis_manifest["summary"][
                "analysis_manifest_sha256"
            ],
        },
        "output": {
            "relative_ddb": ddb_path.name,
            "ddb_bytes": ddb_path.stat().st_size,
            "ddb_sha256": ddb_sha,
        },
        "summary": {
            "unit_count": len(unit_reports),
            "articulation_units": sum(
                item["kind"] == "articulation" for item in unit_reports
            ),
            "stationary_units": sum(
                item["kind"] == "stationary" for item in unit_reports
            ),
            "coverage_complete": coverage_complete,
            "approval_complete": False,
            "ddb_manifest_sha256": canonical_digest,
        },
        "units": unit_reports,
        "limitations": [
            "This DDB contains structurally validated but unapproved self-owned or synthetic units.",
            "DDB assembly does not create the matching full DDI tree or a VOCALOID product license.",
            "Synthetic fixtures do not establish phoneme correctness, boundary quality, or singing quality.",
        ],
    }
    analysis_tools.write_json_atomic(output_root / "ddb_manifest.json", manifest)
    marker.unlink()
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("unit_manifest", type=Path)
    parser.add_argument("analysis_manifest", type=Path)
    parser.add_argument("unit_root", type=Path)
    parser.add_argument("analysis_root", type=Path)
    parser.add_argument("output_root", type=Path)
    parser.add_argument("--stem", default="ResearchVoice")
    args = parser.parse_args()
    try:
        unit_manifest_path = args.unit_manifest.resolve()
        analysis_manifest_path = args.analysis_manifest.resolve()
        unit_root = args.unit_root.resolve()
        analysis_root = args.analysis_root.resolve()
        output_root = args.output_root.resolve()
        if not unit_root.is_dir():
            raise AssemblyError(f"unit root is not a directory: {unit_root}")
        if not analysis_root.is_dir():
            raise AssemblyError(f"analysis root is not a directory: {analysis_root}")
        manifest = build(
            unit_manifest_path,
            analysis_manifest_path,
            unit_root,
            analysis_root,
            output_root,
            args.stem,
        )
        for name, value in manifest["summary"].items():
            print(f"{name}={value}")
        for name, value in manifest["output"].items():
            print(f"{name}={value}")
        print(f"manifest={output_root / 'ddb_manifest.json'}")
        return 0 if manifest["summary"]["coverage_complete"] else 3
    except (
        OSError,
        UnicodeError,
        json.JSONDecodeError,
        OverflowError,
        ValueError,
        struct.error,
        AssemblyError,
        analysis_tools.AnalysisError,
        build_unit_ddb.BuildError,
        probe_frm2.ProbeError,
        validate_main_sms2.ValidationError,
    ) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
