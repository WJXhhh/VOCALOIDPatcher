#!/usr/bin/env python3
"""Build and natively validate a minimal Sil/a bank from three annotated WAV files."""

from __future__ import annotations

import argparse
import json
import math
import os
import subprocess
import sys
import wave
from pathlib import Path

import build_bank_ddb
import build_unit_ddb
import finalize_stationary_ddi
import probe_frm2


TOOLS_DIR = Path(__file__).resolve().parent
DRS_PROJECT = TOOLS_DIR / "drs_harness" / "DrsHarness.csproj"
TREE_PROJECT = TOOLS_DIR / "tree_harness" / "TreeHarness.csproj"
DRS_DLL = TOOLS_DIR / "drs_harness" / "bin" / "Release" / "net8.0-windows" / "DrsHarness.dll"
TREE_DLL = TOOLS_DIR / "tree_harness" / "bin" / "Release" / "net8.0-windows" / "TreeHarness.dll"


class TrainingError(Exception):
    pass


def run(command: list[str], environment: dict[str, str] | None = None) -> None:
    print("+ " + subprocess.list2cmdline(command), flush=True)
    result = subprocess.run(command, env=environment, check=False)
    if result.returncode != 0:
        raise TrainingError(
            f"command exited with {result.returncode}: {subprocess.list2cmdline(command)}"
        )


def resolved_input(base: Path, value: object, description: str) -> Path:
    if not isinstance(value, str) or not value:
        raise TrainingError(f"{description} must be a non-empty path string")
    path = Path(value)
    if not path.is_absolute():
        path = base / path
    path = path.resolve()
    if not path.is_file():
        raise TrainingError(f"{description} does not exist: {path}")
    return path


def finite_number(value: object, description: str, positive: bool = False) -> float:
    try:
        result = float(value)
    except (TypeError, ValueError) as error:
        raise TrainingError(f"{description} must be a number") from error
    if not math.isfinite(result) or (positive and result <= 0.0):
        qualifier = "finite and positive" if positive else "finite"
        raise TrainingError(f"{description} must be {qualifier}")
    return result


def wav_duration(path: Path) -> float:
    try:
        with wave.open(str(path), "rb") as source:
            if (
                source.getframerate() != build_unit_ddb.SAMPLE_RATE
                or source.getnchannels() != 1
                or source.getsampwidth() != 2
                or source.getcomptype() != "NONE"
            ):
                raise TrainingError(
                    f"WAV must be 44.1 kHz mono PCM16 without compression: {path}"
                )
            duration = source.getnframes() / source.getframerate()
    except (EOFError, wave.Error) as error:
        raise TrainingError(f"cannot read WAV {path}: {error}") from error
    if duration <= 0.25 or duration > 30.0:
        raise TrainingError(f"WAV duration must be >0.25 and <=30 seconds: {path}")
    return duration


def second_range(value: object, description: str) -> tuple[float, float]:
    if not isinstance(value, list) or len(value) != 2:
        raise TrainingError(f"{description} must be [start_seconds, end_seconds]")
    start = finite_number(value[0], description)
    end = finite_number(value[1], description)
    if start < 0.0 or end < start:
        raise TrainingError(f"{description} must satisfy 0 <= start <= end")
    return start, end


def to_frame_range(
    seconds: tuple[float, float], duration: float, frame_count: int
) -> tuple[int, int]:
    start = max(0, min(frame_count, round(seconds[0] / duration * frame_count)))
    end = max(0, min(frame_count, round(seconds[1] / duration * frame_count)))
    return start, end


def frame_voicing(raw: bytes) -> str:
    frame = probe_frm2.parse_frame(raw)
    if isinstance(frame, probe_frm2.MainFrame):
        return "voiced"
    if isinstance(frame, probe_frm2.UnvoicedFrame):
        return "unvoiced"
    raise TrainingError("analysis produced a frame that is neither main nor unvoiced")


def detect_boundary(
    frames: list[bytes], source_voicing: str, target_voicing: str
) -> int:
    voicing = [frame_voicing(raw) for raw in frames]
    split = 0
    while split < len(voicing) and voicing[split] == source_voicing:
        split += 1
    if split == 0 or split == len(voicing):
        raise TrainingError("analysis did not produce two non-empty voicing regions")
    if any(value != target_voicing for value in voicing[split:]):
        raise TrainingError("analysis voicing changes more than once")
    return split


def analysis_environment(
    boundary_seconds: float | None = None,
    direction: str | None = None,
) -> dict[str, str]:
    environment = os.environ.copy()
    environment["DRS_HARNESS_BUILD_MAIN_FIELDS"] = "1"
    environment.pop("DRS_HARNESS_REGION_ANALYSIS", None)
    environment.pop("DRS_HARNESS_F0_BOUNDARY_SECONDS", None)
    environment.pop("DRS_HARNESS_F0_BOUNDARY_DIRECTION", None)
    if boundary_seconds is not None and direction is not None:
        environment["DRS_HARNESS_F0_BOUNDARY_SECONDS"] = format(
            boundary_seconds, ".17g"
        )
        environment["DRS_HARNESS_F0_BOUNDARY_DIRECTION"] = direction
    return environment


def analyze(
    dse: Path,
    wav: Path,
    output: Path,
    f0_hz: float,
    duration: float,
    boundary_seconds: float | None = None,
    direction: str | None = None,
) -> None:
    run(
        [
            "dotnet",
            str(DRS_DLL),
            str(dse),
            str(output),
            format(duration, ".17g"),
            format(f0_hz, ".17g"),
            "external",
            format(f0_hz, ".17g"),
            str(wav),
        ],
        analysis_environment(boundary_seconds, direction),
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("spec", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument(
        "--dse",
        type=Path,
        default=Path(r"C:\Program Files\VOCALOID6\Editor\DSE.dll"),
    )
    args = parser.parse_args()

    try:
        spec_path = args.spec.resolve()
        spec = json.loads(spec_path.read_text(encoding="utf-8"))
        if not isinstance(spec, dict) or spec.get("schema_version") != 1:
            raise TrainingError("spec must be an object with schema_version 1")
        singer_name = spec.get("singer_name")
        if (
            not isinstance(singer_name, str)
            or not singer_name
            or not singer_name.isascii()
            or any(not (value.isalnum() or value in "_-") for value in singer_name)
        ):
            raise TrainingError(
                "singer_name must contain only ASCII letters, digits, underscore, or hyphen"
            )
        pitch_hz = finite_number(spec.get("pitch_hz"), "pitch_hz", positive=True)
        dse = args.dse.resolve()
        if not dse.is_file():
            raise TrainingError(f"DSE not found: {dse}")
        stationary = spec.get("stationary")
        articulations = spec.get("articulations")
        if not isinstance(stationary, dict):
            raise TrainingError("stationary must be an object")
        if not isinstance(articulations, list) or len(articulations) != 2:
            raise TrainingError("articulations must contain exactly two entries")

        base = spec_path.parent
        sta_wav = resolved_input(base, stationary.get("wav"), "stationary.wav")
        sta_f0 = finite_number(
            stationary.get("f0_hz", pitch_hz), "stationary.f0_hz", positive=True
        )
        sta_duration = wav_duration(sta_wav)
        transitions: dict[tuple[str, str], dict[str, object]] = {}
        for item in articulations:
            if not isinstance(item, dict):
                raise TrainingError("each articulation must be an object")
            source = item.get("source")
            target = item.get("target")
            if not isinstance(source, str) or not isinstance(target, str):
                raise TrainingError("articulation source/target must be strings")
            key = source, target
            if key in transitions:
                raise TrainingError(f"duplicate articulation {source}->{target}")
            transitions[key] = item
        required = {("Sil", "a"), ("a", "Sil")}
        if set(transitions) != required:
            raise TrainingError("articulations must be exactly Sil->a and a->Sil")

        output = args.output.resolve()
        work = output / "work"
        skeleton_directory = work / "skeleton"
        output.mkdir(parents=True, exist_ok=True)
        work.mkdir(parents=True, exist_ok=True)

        run(["dotnet", "build", str(DRS_PROJECT), "-c", "Release"])
        run(["dotnet", "build", str(TREE_PROJECT), "-c", "Release"])

        sta_sms2 = work / "stationary.sms2"
        sta_unit = work / "stationary.ddb"
        analyze(dse, sta_wav, sta_sms2, sta_f0, sta_duration)
        sta_frames = build_unit_ddb.extract_frames(sta_sms2)
        if any(frame_voicing(raw) != "voiced" for raw in sta_frames):
            raise TrainingError("stationary analysis contains unvoiced frames")
        reports: dict[str, object] = {
            "stationary": build_unit_ddb.build(
                sta_sms2, sta_wav, sta_unit, "sta"
            )
        }

        unit_paths = [sta_unit]
        finalizer_options: list[str] = []
        transition_report: dict[str, object] = {}
        transition_settings = [
            (("Sil", "a"), "sil_to_a", "sil-to-voiced", "unvoiced", "voiced"),
            (("a", "Sil"), "a_to_sil", "voiced-to-sil", "voiced", "unvoiced"),
        ]
        for key, label, direction, source_voicing, target_voicing in transition_settings:
            item = transitions[key]
            wav = resolved_input(base, item.get("wav"), f"{label}.wav")
            duration = wav_duration(wav)
            boundary = finite_number(item.get("boundary_seconds"), f"{label}.boundary_seconds")
            if boundary <= 0.0 or boundary >= duration:
                raise TrainingError(f"{label} boundary must be inside its WAV")
            f0_hz = finite_number(item.get("f0_hz", pitch_hz), f"{label}.f0_hz", positive=True)
            source_inner_seconds = second_range(
                item.get("source_inner_seconds"), f"{label}.source_inner_seconds"
            )
            target_inner_seconds = second_range(
                item.get("target_inner_seconds"), f"{label}.target_inner_seconds"
            )
            if not (
                source_inner_seconds[1] <= boundary
                and target_inner_seconds[0] >= boundary
                and target_inner_seconds[1] <= duration
            ):
                raise TrainingError(f"{label} inner ranges are outside their outer sides")

            sms2 = work / f"{label}.sms2"
            unit = work / f"{label}.ddb"
            analyze(dse, wav, sms2, f0_hz, duration, boundary, direction)
            frames = build_unit_ddb.extract_frames(sms2)
            split = detect_boundary(frames, source_voicing, target_voicing)
            expected_split = round(boundary / duration * len(frames))
            if split != expected_split:
                raise TrainingError(
                    f"{label} analyzed split {split} differs from annotated frame {expected_split}"
                )
            source_inner = to_frame_range(source_inner_seconds, duration, len(frames))
            target_inner = to_frame_range(target_inner_seconds, duration, len(frames))
            if not (
                0 <= source_inner[0] <= source_inner[1] <= split
                and split <= target_inner[0] <= target_inner[1] <= len(frames)
            ):
                raise TrainingError(f"{label} rounded inner ranges cross the outer boundary")
            unit_report = build_unit_ddb.build(
                sms2,
                wav,
                unit,
                "art",
                split,
                source_voicing,
                target_voicing,
            )
            unit_paths.append(unit)
            transition_report[label] = {
                "duration_seconds": duration,
                "boundary_seconds": boundary,
                "split_frame": split,
                "source_inner_frames": list(source_inner),
                "target_inner_frames": list(target_inner),
                "unit": unit_report,
            }
            option_label = label.replace("_", "-")
            finalizer_options.extend(
                [
                    f"--{option_label}-split-frame",
                    str(split),
                    f"--{option_label}-source-inner",
                    f"{source_inner[0]}:{source_inner[1]}",
                    f"--{option_label}-target-inner",
                    f"{target_inner[0]}:{target_inner[1]}",
                ]
            )
        reports["articulations"] = transition_report

        tree_environment = os.environ.copy()
        tree_environment.update(
            {
                "TREE_HARNESS_ADD_SIL_A": "1",
                "TREE_HARNESS_ADD_EMPTY_STAP": "1",
                "TREE_HARNESS_ADD_EMPTY_REFS": "1",
                "TREE_HARNESS_ADD_EMPTY_ARTP": "1",
            }
        )
        run(
            [
                "dotnet",
                str(TREE_DLL),
                str(dse),
                str(skeleton_directory),
                singer_name,
            ],
            tree_environment,
        )

        ddb = output / f"{singer_name}.ddb"
        ddi = output / f"{singer_name}.ddi"
        manifest = output / f"{singer_name}.manifest.json"
        reports["bank"] = build_bank_ddb.build(ddb, unit_paths, manifest)
        run(
            [
                sys.executable,
                str(TOOLS_DIR / "finalize_sil_a_ddi.py"),
                str(skeleton_directory / f"{singer_name}.tree"),
                str(manifest),
                str(ddi),
                "--pitch-hz",
                format(pitch_hz, ".17g"),
                "--singer-name",
                singer_name,
                *finalizer_options,
            ]
        )

        load_environment = os.environ.copy()
        load_environment["TREE_HARNESS_LOAD_EXISTING"] = "1"
        load_environment["TREE_HARNESS_EXPECT_SIL_A"] = "1"
        run(
            ["dotnet", str(TREE_DLL), str(dse), str(output), singer_name],
            load_environment,
        )
        reports["output"] = {
            "ddi": str(ddi),
            "ddi_bytes": ddi.stat().st_size,
            "ddb": str(ddb),
            "ddb_bytes": ddb.stat().st_size,
            "native_load_valid": True,
        }
        build_bank_ddb.write_atomic(
            output / "build_report.json",
            [(json.dumps(reports, ensure_ascii=False, indent=2) + "\n").encode("utf-8")],
        )
        print(json.dumps(reports["output"], ensure_ascii=False, indent=2))
        return 0
    except (
        OSError,
        ValueError,
        json.JSONDecodeError,
        subprocess.SubprocessError,
        TrainingError,
        build_unit_ddb.BuildError,
        finalize_stationary_ddi.FinalizeError,
        probe_frm2.ProbeError,
    ) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
