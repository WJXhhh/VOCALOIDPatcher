#!/usr/bin/env python3
"""Run DRS on extracted ART/STA WAVs and validate their final frame contracts."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import re
import struct
import subprocess
import sys
import wave
from pathlib import Path, PurePosixPath
from typing import Any

import build_unit_ddb
import probe_frm2
import validate_main_sms2


TOOLS_DIR = Path(__file__).resolve().parent
DRS_PROJECT = TOOLS_DIR / "drs_harness" / "DrsHarness.csproj"
DRS_DLL = (
    TOOLS_DIR
    / "drs_harness"
    / "bin"
    / "Release"
    / "net8.0-windows"
    / "DrsHarness.dll"
)
SAMPLE_RATE = 44100
HOP_SAMPLES = 256
ART_ID = re.compile(r"ART_[A-Za-z0-9_-]+_[0-9]{4}\Z")
STA_ID = re.compile(r"STA_[A-Za-z0-9_-]+_[0-9]{3}\Z")


class AnalysisError(Exception):
    pass


def read_json(path: Path) -> Any:
    if not path.is_file():
        raise AnalysisError(f"file does not exist: {path}")
    return json.loads(path.read_text(encoding="utf-8"))


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def canonical_json_hash(value: Any) -> str:
    payload = json.dumps(
        value, ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def safe_relative_path(value: Any, suffix: str, context: str) -> PurePosixPath:
    if not isinstance(value, str) or not value:
        raise AnalysisError(f"{context} has no relative path")
    path = PurePosixPath(value)
    if (
        "\\" in value
        or ":" in value
        or path.is_absolute()
        or ".." in path.parts
        or path.suffix.lower() != suffix
    ):
        raise AnalysisError(f"unsafe relative path in {context}: {value!r}")
    return path


def resolve_below(root: Path, relative: PurePosixPath, context: str) -> Path:
    path = root.joinpath(*relative.parts).resolve()
    if path != root and root not in path.parents:
        raise AnalysisError(f"{context} resolves outside its root: {relative}")
    return path


def finite_number(value: Any, context: str) -> float:
    if isinstance(value, bool):
        raise AnalysisError(f"{context} must be a finite number")
    try:
        result = float(value)
    except (TypeError, ValueError) as error:
        raise AnalysisError(f"{context} must be a finite number") from error
    if not math.isfinite(result):
        raise AnalysisError(f"{context} must be a finite number")
    return result


def second_range(value: Any, duration: float, context: str) -> tuple[float, float]:
    if not isinstance(value, list) or len(value) != 2:
        raise AnalysisError(f"{context} must contain two seconds values")
    start = finite_number(value[0], context)
    end = finite_number(value[1], context)
    if not 0.0 <= start < end <= duration:
        raise AnalysisError(f"{context} must satisfy 0 <= start < end <= duration")
    return start, end


def wav_contract(path: Path) -> dict[str, int | str]:
    try:
        with wave.open(str(path), "rb") as source:
            result: dict[str, int | str] = {
                "channels": source.getnchannels(),
                "sample_width_bytes": source.getsampwidth(),
                "sample_rate": source.getframerate(),
                "frame_count": source.getnframes(),
                "compression": source.getcomptype(),
            }
    except (EOFError, wave.Error) as error:
        raise AnalysisError(f"cannot read WAV {path}: {error}") from error
    if (
        result["channels"] != 1
        or result["sample_width_bytes"] != 2
        or result["sample_rate"] != SAMPLE_RATE
        or result["compression"] != "NONE"
    ):
        raise AnalysisError(
            f"WAV must be 44.1 kHz mono PCM16 without compression: {path}"
        )
    return result


def expected_manifest_digest(units: list[dict[str, Any]]) -> str:
    return canonical_json_hash(
        [
            {
                "unit_id": item["unit_id"],
                "relative_wav": item["output_relative_wav"],
                "sha256": item["output_wav_sha256"],
                "builder_spec": item["builder_spec"],
            }
            for item in units
        ]
    )


def validate_unit_list(manifest: Any) -> tuple[list[dict[str, Any]], bool]:
    if not isinstance(manifest, dict) or manifest.get("format") != (
        "vocaloid-extracted-recording-units-v1"
    ):
        raise AnalysisError("unsupported extracted-unit manifest format")
    summary = manifest.get("summary")
    articulation = manifest.get("articulation_units")
    stationary = manifest.get("stationary_units")
    if (
        not isinstance(summary, dict)
        or not isinstance(articulation, list)
        or not isinstance(stationary, list)
    ):
        raise AnalysisError("unit manifest lacks summary or unit lists")
    units = [*articulation, *stationary]
    if (
        summary.get("articulation_units") != len(articulation)
        or summary.get("stationary_units") != len(stationary)
        or summary.get("total_units") != len(units)
    ):
        raise AnalysisError("unit manifest summary counts differ from its lists")
    if summary.get("approval_complete") is not False:
        raise AnalysisError("unit manifest must keep extracted candidates unapproved")
    expected_digest = expected_manifest_digest(units)
    if summary.get("unit_manifest_sha256") != expected_digest:
        raise AnalysisError("unit manifest canonical SHA-256 differs")

    seen_ids: set[str] = set()
    seen_wavs: set[PurePosixPath] = set()
    for index, item in enumerate(units):
        if not isinstance(item, dict):
            raise AnalysisError(f"unit {index} is not an object")
        unit_id = item.get("unit_id")
        kind = item.get("kind")
        valid_id = (
            kind == "articulation"
            and isinstance(unit_id, str)
            and ART_ID.fullmatch(unit_id) is not None
        ) or (
            kind == "stationary"
            and isinstance(unit_id, str)
            and STA_ID.fullmatch(unit_id) is not None
        )
        if not valid_id or unit_id in seen_ids:
            raise AnalysisError(f"unit {index} has an invalid or duplicate ID")
        seen_ids.add(unit_id)
        relative = safe_relative_path(
            item.get("output_relative_wav"), ".wav", f"unit {unit_id}"
        )
        if relative in seen_wavs:
            raise AnalysisError(f"duplicate unit WAV path: {relative}")
        seen_wavs.add(relative)
        digest = item.get("output_wav_sha256")
        if not isinstance(digest, str) or len(digest) != 64:
            raise AnalysisError(f"unit {unit_id} has no WAV SHA-256")
        try:
            bytes.fromhex(digest)
        except ValueError as error:
            raise AnalysisError(f"unit {unit_id} has an invalid WAV SHA-256") from error
        if item.get("approval_status") != "unapproved_extracted_candidate":
            raise AnalysisError(f"unit {unit_id} has an unexpected approval status")
        if not isinstance(item.get("builder_spec"), dict):
            raise AnalysisError(f"unit {unit_id} has no builder spec")
        if not isinstance(item.get("frame_alignment"), dict):
            raise AnalysisError(f"unit {unit_id} has no provisional frame alignment")
    complete = summary.get("input_coverage_complete") is True
    return units, complete


def preflight_unit(unit: dict[str, Any], unit_root: Path) -> dict[str, Any]:
    unit_id = unit["unit_id"]
    relative = safe_relative_path(unit["output_relative_wav"], ".wav", unit_id)
    path = resolve_below(unit_root, relative, f"unit {unit_id} WAV")
    if not path.is_file():
        raise AnalysisError(f"unit WAV does not exist: {path}")
    actual_digest = file_sha256(path)
    if actual_digest != unit["output_wav_sha256"].lower():
        raise AnalysisError(f"unit {unit_id} WAV SHA-256 differs")
    actual = wav_contract(path)
    declared = unit.get("wav")
    if not isinstance(declared, dict):
        raise AnalysisError(f"unit {unit_id} has no WAV metadata")
    frame_count = actual["frame_count"]
    assert isinstance(frame_count, int)
    if (
        declared.get("sample_rate") != SAMPLE_RATE
        or declared.get("channels") != 1
        or declared.get("bit_depth") != 16
        or declared.get("frame_count") != frame_count
    ):
        raise AnalysisError(f"unit {unit_id} WAV metadata differs")
    duration = frame_count / SAMPLE_RATE
    declared_duration = finite_number(
        declared.get("duration_seconds"), f"unit {unit_id} duration"
    )
    if abs(declared_duration - duration) > 1.0e-12:
        raise AnalysisError(f"unit {unit_id} duration differs")
    if not 0.25 < duration <= 30.0:
        raise AnalysisError(f"unit {unit_id} duration is outside the DRS range")
    estimate = unit["frame_alignment"].get("frame_count_estimate")
    expected_frames = math.ceil(frame_count / HOP_SAMPLES)
    if estimate != expected_frames:
        raise AnalysisError(f"unit {unit_id} provisional frame estimate differs")
    return {
        "unit": unit,
        "path": path,
        "relative": relative,
        "wav_frame_count": frame_count,
        "duration": duration,
        "expected_drs_frames": expected_frames,
    }


def frame_voicing(raw: bytes) -> str:
    frame = probe_frm2.parse_frame(raw)
    if isinstance(frame, probe_frm2.MainFrame):
        return "voiced"
    if isinstance(frame, probe_frm2.UnvoicedFrame):
        return "unvoiced"
    raise AnalysisError("DRS produced a frame that is neither main nor unvoiced")


def voicing_runs(values: list[str]) -> list[dict[str, Any]]:
    if not values:
        return []
    result: list[dict[str, Any]] = []
    start = 0
    current = values[0]
    for index, value in enumerate(values[1:], 1):
        if value != current:
            result.append(
                {"start_frame": start, "end_frame": index, "voicing": current}
            )
            start = index
            current = value
    result.append(
        {"start_frame": start, "end_frame": len(values), "voicing": current}
    )
    return result


def frame_payload_hash(frames: list[bytes]) -> str:
    digest = hashlib.sha256()
    for raw in frames:
        digest.update(len(raw).to_bytes(8, "little"))
        digest.update(raw)
    return digest.hexdigest()


def analysis_manifest_digest(units: list[dict[str, Any]]) -> str:
    return canonical_json_hash(
        [
            {
                "unit_id": item["unit_id"],
                "input_wav_sha256": item["input_wav_sha256"],
                "frm2_payload_sha256": item["frm2_payload_sha256"],
                "drs_frame_count": item["drs_frame_count"],
                "voicing_runs": item["voicing_runs"],
                "split_frame": item.get("split_frame"),
                "source_inner_frames": item.get("source_inner_frames"),
                "target_inner_frames": item.get("target_inner_frames"),
            }
            for item in units
        ]
    )


def to_frame(value: float, duration: float, frame_count: int) -> int:
    return max(0, min(frame_count, round(value / duration * frame_count)))


def analysis_environment(
    boundary_seconds: float | None = None, direction: str | None = None
) -> dict[str, str]:
    environment = {
        key: value
        for key, value in os.environ.items()
        if not key.upper().startswith("DRS_HARNESS_")
    }
    environment["DRS_HARNESS_BUILD_MAIN_FIELDS"] = "1"
    if boundary_seconds is not None and direction is not None:
        environment["DRS_HARNESS_F0_BOUNDARY_SECONDS"] = format(
            boundary_seconds, ".17g"
        )
        environment["DRS_HARNESS_F0_BOUNDARY_DIRECTION"] = direction
    return environment


def run_drs(
    dse: Path,
    source: dict[str, Any],
    output_path: Path,
) -> tuple[str, float, float | None, str | None]:
    unit = source["unit"]
    unit_id = unit["unit_id"]
    spec = unit["builder_spec"]
    f0_hz = finite_number(spec.get("f0_hz"), f"unit {unit_id} F0")
    if not 40.0 <= f0_hz <= 1000.0:
        raise AnalysisError(f"unit {unit_id} F0 is outside 40..1000 Hz")

    mode = "external"
    boundary: float | None = None
    direction: str | None = None
    if unit["kind"] == "articulation":
        voicing = unit.get("voicing")
        if not isinstance(voicing, dict):
            raise AnalysisError(f"unit {unit_id} has no voicing annotation")
        source_voicing = voicing.get("source")
        target_voicing = voicing.get("target")
        if source_voicing not in ("voiced", "unvoiced") or target_voicing not in (
            "voiced",
            "unvoiced",
        ):
            raise AnalysisError(f"unit {unit_id} has invalid voicing annotations")
        boundary = finite_number(
            spec.get("boundary_seconds"), f"unit {unit_id} boundary"
        )
        if not 0.0 < boundary < source["duration"]:
            raise AnalysisError(f"unit {unit_id} boundary is outside its WAV")
        if source_voicing == target_voicing == "unvoiced":
            mode = "unvoiced"
        elif source_voicing != target_voicing:
            direction = (
                "sil-to-voiced"
                if source_voicing == "unvoiced"
                else "voiced-to-sil"
            )

    output_path.parent.mkdir(parents=True, exist_ok=True)
    command = [
        "dotnet",
        str(DRS_DLL),
        str(dse),
        str(output_path),
        format(source["duration"], ".17g"),
        format(f0_hz, ".17g"),
        mode,
        format(f0_hz, ".17g"),
        str(source["path"]),
    ]
    result = subprocess.run(
        command,
        env=analysis_environment(boundary if direction else None, direction),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    if result.returncode != 0:
        tail = "\n".join((result.stdout + result.stderr).splitlines()[-20:])
        raise AnalysisError(
            f"DRS failed for {unit_id} with exit {result.returncode}:\n{tail}"
        )
    if not output_path.is_file():
        raise AnalysisError(f"DRS produced no output for {unit_id}")
    return mode, f0_hz, boundary, direction


def validate_analysis(
    source: dict[str, Any],
    sms2_path: Path,
    mode: str,
    f0_hz: float,
) -> dict[str, Any]:
    unit = source["unit"]
    unit_id = unit["unit_id"]
    frames = build_unit_ddb.extract_frames(sms2_path)
    frame_count = len(frames)
    if frame_count != source["expected_drs_frames"]:
        raise AnalysisError(
            f"unit {unit_id} produced {frame_count} frames; "
            f"expected {source['expected_drs_frames']}"
        )
    actual = [frame_voicing(raw) for raw in frames]
    result: dict[str, Any] = {
        "unit_id": unit_id,
        "kind": unit["kind"],
        "input_relative_wav": str(source["relative"]),
        "input_wav_sha256": unit["output_wav_sha256"],
        "output_relative_sms2": str(
            PurePosixPath("sms2") / source["relative"].with_suffix(".sms2")
        ),
        "output_sms2_sha256": file_sha256(sms2_path),
        "frm2_payload_sha256": frame_payload_hash(frames),
        "analysis_mode": mode,
        "f0_hz": f0_hz,
        "wav_frame_count": source["wav_frame_count"],
        "drs_frame_count": frame_count,
        "hop_samples": HOP_SAMPLES,
        "voicing_runs": voicing_runs(actual),
        "validation_status": "structurally_valid_unapproved",
        "approval_status": "unapproved_drs_analysis",
    }
    if unit["kind"] == "stationary":
        if any(value != "voiced" for value in actual):
            raise AnalysisError(f"stationary unit {unit_id} contains unvoiced frames")
        result.update({"phoneme": unit.get("phoneme")})
        return result

    voicing = unit["voicing"]
    source_voicing = voicing["source"]
    target_voicing = voicing["target"]
    duration = source["duration"]
    spec = unit["builder_spec"]
    boundary = finite_number(spec["boundary_seconds"], f"unit {unit_id} boundary")
    split = to_frame(boundary, duration, frame_count)
    if split <= 0 or split >= frame_count:
        raise AnalysisError(f"unit {unit_id} split frame is not internal")
    expected = [source_voicing] * split + [target_voicing] * (frame_count - split)
    mismatches = [
        index for index, (observed, wanted) in enumerate(zip(actual, expected))
        if observed != wanted
    ]
    if mismatches:
        preview = ", ".join(str(value) for value in mismatches[:8])
        raise AnalysisError(
            f"unit {unit_id} has {len(mismatches)} voicing mismatches; first: {preview}"
        )
    source_seconds = second_range(
        spec.get("source_inner_seconds"), duration, f"unit {unit_id} source inner"
    )
    target_seconds = second_range(
        spec.get("target_inner_seconds"), duration, f"unit {unit_id} target inner"
    )
    source_inner = [to_frame(value, duration, frame_count) for value in source_seconds]
    target_inner = [to_frame(value, duration, frame_count) for value in target_seconds]
    if not (
        0 <= source_inner[0] < source_inner[1] <= split
        and split <= target_inner[0] < target_inner[1] <= frame_count
    ):
        raise AnalysisError(f"unit {unit_id} rounded inner ranges cross the split")
    result.update(
        {
            "edge": unit.get("edge"),
            "role": unit.get("role"),
            "voicing": voicing,
            "split_frame": split,
            "source_inner_frames": source_inner,
            "target_inner_frames": target_inner,
        }
    )
    return result


def write_json_atomic(path: Path, value: Any) -> None:
    temporary = path.with_name(path.name + ".tmp")
    try:
        with temporary.open("w", encoding="utf-8", newline="\n") as output:
            json.dump(value, output, ensure_ascii=False, indent=2)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        temporary.replace(path)
    finally:
        if temporary.exists():
            temporary.unlink()


def build_report(
    manifest_path: Path,
    manifest: Any,
    unit_root: Path,
    output_root: Path,
    dse: Path,
    selected_ids: list[str] | None,
    skip_build: bool,
) -> dict[str, Any]:
    units, input_complete = validate_unit_list(manifest)
    by_id = {item["unit_id"]: item for item in units}
    if selected_ids is None:
        selected = units
    else:
        if len(selected_ids) != len(set(selected_ids)):
            raise AnalysisError("--unit-id contains duplicates")
        missing = [unit_id for unit_id in selected_ids if unit_id not in by_id]
        if missing:
            raise AnalysisError(f"unknown unit IDs: {', '.join(missing)}")
        requested = set(selected_ids)
        selected = [item for item in units if item["unit_id"] in requested]
    if not selected:
        raise AnalysisError("no units were selected")

    sources = [preflight_unit(item, unit_root) for item in selected]
    if not dse.is_file():
        raise AnalysisError(f"DSE not found: {dse}")
    if output_root.exists():
        raise AnalysisError(f"output directory already exists: {output_root}")
    if not skip_build:
        result = subprocess.run(
            ["dotnet", "build", str(DRS_PROJECT), "-c", "Release"], check=False
        )
        if result.returncode != 0:
            raise AnalysisError(f"DrsHarness build exited with {result.returncode}")
    if not DRS_DLL.is_file():
        raise AnalysisError(f"DrsHarness DLL does not exist: {DRS_DLL}")

    output_root.parent.mkdir(parents=True, exist_ok=True)
    output_root.mkdir()
    marker = output_root / "ANALYSIS_INCOMPLETE"
    marker.write_text("DRS analysis did not reach the final manifest yet.\n", encoding="utf-8")
    analyzed: list[dict[str, Any]] = []
    for index, source in enumerate(sources, 1):
        unit_id = source["unit"]["unit_id"]
        relative_sms2 = (
            PurePosixPath("sms2") / source["relative"].with_suffix(".sms2")
        )
        sms2_path = resolve_below(output_root, relative_sms2, f"unit {unit_id} SMS2")
        print(f"[{index}/{len(sources)}] {unit_id}", flush=True)
        mode, f0_hz, _, _ = run_drs(dse, source, sms2_path)
        item = validate_analysis(source, sms2_path, mode, f0_hz)
        if item["output_relative_sms2"] != str(relative_sms2):
            raise AnalysisError(f"unit {unit_id} output path differs internally")
        analyzed.append(item)

    analysis_digest = analysis_manifest_digest(analyzed)
    selection_complete = len(selected) == len(units)
    coverage_complete = input_complete and selection_complete
    report = {
        "format": "vocaloid-recording-unit-drs-analysis-v1",
        "source": {
            "unit_manifest_sha256": file_sha256(manifest_path),
            "unit_manifest_canonical_sha256": manifest["summary"][
                "unit_manifest_sha256"
            ],
            "dse_sha256": file_sha256(dse),
            "drs_harness_sha256": file_sha256(DRS_DLL),
            "selected_unit_ids": selected_ids,
        },
        "summary": {
            "input_units": len(units),
            "selected_units": len(selected),
            "articulation_units": sum(
                item["kind"] == "articulation" for item in analyzed
            ),
            "stationary_units": sum(
                item["kind"] == "stationary" for item in analyzed
            ),
            "input_coverage_complete": input_complete,
            "selection_complete": selection_complete,
            "analysis_complete": True,
            "coverage_complete": coverage_complete,
            "approval_complete": False,
            "analysis_manifest_sha256": analysis_digest,
        },
        "units": analyzed,
        "limitations": [
            "DRS frame and voicing contracts passed, but no unit is approved for a voicebank.",
            "The pitch envelope follows the manifest annotation; it does not prove that the PCM contains the annotated phoneme or boundary.",
            "Synthetic fixtures can prove structural determinism only and cannot replace human recording, listening, or manual boundary QA.",
            "Raw SMS2 container hashes are provenance only; reproducibility uses length-delimited FRM2 payload hashes because non-frame wrapper bytes can vary across equivalent DRS runs.",
            "SMS2 files are intermediate analysis artifacts for self-owned PCM and must not be committed as ordinary source files.",
        ],
    }
    write_json_atomic(output_root / "analysis_manifest.json", report)
    marker.unlink()
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("unit_manifest", type=Path)
    parser.add_argument("unit_root", type=Path)
    parser.add_argument("output_root", type=Path)
    parser.add_argument(
        "--dse",
        type=Path,
        default=Path(r"C:\Program Files\VOCALOID6\Editor\DSE.dll"),
    )
    parser.add_argument(
        "--unit-id",
        action="append",
        dest="unit_ids",
        help="analyze only this unit; repeat for a calibration subset",
    )
    parser.add_argument(
        "--skip-build",
        action="store_true",
        help="reuse the existing Release DrsHarness build",
    )
    args = parser.parse_args()
    try:
        manifest_path = args.unit_manifest.resolve()
        unit_root = args.unit_root.resolve()
        output_root = args.output_root.resolve()
        dse = args.dse.resolve()
        if not unit_root.is_dir():
            raise AnalysisError(f"unit root is not a directory: {unit_root}")
        manifest = read_json(manifest_path)
        report = build_report(
            manifest_path,
            manifest,
            unit_root,
            output_root,
            dse,
            args.unit_ids,
            args.skip_build,
        )
        for name, value in report["summary"].items():
            print(f"{name}={value}")
        print(f"manifest={output_root / 'analysis_manifest.json'}")
        return 0 if report["summary"]["coverage_complete"] else 3
    except (
        OSError,
        UnicodeError,
        json.JSONDecodeError,
        OverflowError,
        struct.error,
        ValueError,
        AnalysisError,
        build_unit_ddb.BuildError,
        probe_frm2.ProbeError,
        validate_main_sms2.ValidationError,
    ) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
