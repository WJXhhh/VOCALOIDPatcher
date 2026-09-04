#!/usr/bin/env python3
"""Validate recorded WAV takes against a recording-session manifest."""

from __future__ import annotations

import argparse
import array
import hashlib
import json
import math
import sys
import wave
from pathlib import Path, PurePosixPath
from typing import Any, Iterable


class CaptureError(Exception):
    pass


def read_json(path: Path) -> Any:
    if not path.is_file():
        raise CaptureError(f"file does not exist: {path}")
    return json.loads(path.read_text(encoding="utf-8"))


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def normalized_relative_path(value: Any, context: str) -> PurePosixPath:
    if not isinstance(value, str) or not value:
        raise CaptureError(f"{context} has no relative_wav")
    path = PurePosixPath(value)
    if path.is_absolute() or ".." in path.parts or not path.parts:
        raise CaptureError(f"unsafe relative WAV path in {context}: {value!r}")
    if path.suffix.lower() != ".wav":
        raise CaptureError(f"take path is not a WAV in {context}: {value!r}")
    return path


def resolve_take_path(root: Path, relative: PurePosixPath) -> Path:
    candidate = root.joinpath(*relative.parts).resolve()
    if candidate != root and root not in candidate.parents:
        raise CaptureError(f"take path resolves outside recording root: {relative}")
    return candidate


def rms(samples: Iterable[int]) -> float:
    count = 0
    squares = 0.0
    for sample in samples:
        value = float(sample) / 32768.0
        squares += value * value
        count += 1
    return math.sqrt(squares / count) if count else 0.0


def dbfs(value: float) -> float:
    return 20.0 * math.log10(value) if value > 0.0 else -200.0


def pitch_correlation(frame: list[float], lag: int) -> float:
    if lag <= 0 or lag >= len(frame):
        return -1.0
    count = len(frame) - lag
    numerator = 0.0
    left_energy = 0.0
    right_energy = 0.0
    for index in range(count):
        left = frame[index]
        right = frame[index + lag]
        numerator += left * right
        left_energy += left * left
        right_energy += right * right
    denominator = math.sqrt(left_energy * right_energy)
    return numerator / denominator if denominator > 0.0 else -1.0


def estimate_target_pitch(
    samples: array.array[int],
    center_sample: int,
    sample_rate: int,
    target_hz: float,
    frame_size: int = 2048,
) -> dict[str, float | int]:
    start = center_sample - frame_size // 2
    end = start + frame_size
    if start < 0 or end > len(samples):
        raise CaptureError("pitch-analysis window lies outside the WAV")
    raw = [float(value) for value in samples[start:end]]
    mean = math.fsum(raw) / len(raw)
    frame = [value - mean for value in raw]
    frame_rms = rms(int(value) for value in raw)
    minimum_hz = target_hz * (2.0 ** (-150.0 / 1200.0))
    maximum_hz = target_hz * (2.0 ** (150.0 / 1200.0))
    minimum_lag = max(2, math.floor(sample_rate / maximum_hz) - 1)
    maximum_lag = min(len(frame) - 2, math.ceil(sample_rate / minimum_hz) + 1)
    correlations = {
        lag: pitch_correlation(frame, lag)
        for lag in range(minimum_lag, maximum_lag + 1)
    }
    best_lag = max(correlations, key=correlations.get)
    refined_lag = float(best_lag)
    if best_lag - 1 in correlations and best_lag + 1 in correlations:
        left = correlations[best_lag - 1]
        center = correlations[best_lag]
        right = correlations[best_lag + 1]
        denominator = left - 2.0 * center + right
        if abs(denominator) > 1.0e-12:
            offset = 0.5 * (left - right) / denominator
            if -1.0 <= offset <= 1.0:
                refined_lag += offset
    estimated_hz = sample_rate / refined_lag
    cents_error = 1200.0 * math.log2(estimated_hz / target_hz)
    return {
        "center_sample": center_sample,
        "frame_size": frame_size,
        "estimated_hz": estimated_hz,
        "cents_error": cents_error,
        "correlation": correlations[best_lag],
        "rms_dbfs": dbfs(frame_rms),
    }


def read_pcm16(path: Path) -> tuple[dict[str, int | str], array.array[int]]:
    try:
        with wave.open(str(path), "rb") as stream:
            metadata: dict[str, int | str] = {
                "channels": stream.getnchannels(),
                "sample_width_bytes": stream.getsampwidth(),
                "sample_rate": stream.getframerate(),
                "frame_count": stream.getnframes(),
                "compression": stream.getcomptype(),
            }
            frames = stream.readframes(stream.getnframes())
    except (wave.Error, EOFError) as error:
        raise CaptureError(f"invalid WAV {path}: {error}") from error
    samples = array.array("h")
    samples.frombytes(frames)
    if sys.byteorder != "little":
        samples.byteswap()
    return metadata, samples


def pitch_centers(take: dict[str, Any], timing: dict[str, Any], sample_rate: int) -> list[int]:
    kind = take.get("kind")
    if kind == "articulation_prompt":
        syllable_count = take.get("syllable_count")
        if isinstance(syllable_count, bool) or not isinstance(syllable_count, int):
            raise CaptureError(f"take {take.get('id')} has invalid syllable_count")
        leading = float(timing["art_leading_silence_seconds"])
        syllable = float(timing["art_syllable_seconds"])
        return [
            round((leading + (index + 0.72) * syllable) * sample_rate)
            for index in range(syllable_count)
        ]
    if kind == "stationary_prompt":
        leading = float(timing["stationary_leading_silence_seconds"])
        sustain = float(timing["stationary_sustain_seconds"])
        return [round((leading + 0.65 * sustain) * sample_rate)]
    raise CaptureError(f"take {take.get('id')} has unsupported kind {kind!r}")


def validate_take(
    take: dict[str, Any],
    path: Path,
    capture: dict[str, Any],
    timing: dict[str, Any],
    qa: dict[str, Any],
) -> dict[str, object]:
    metadata, samples = read_pcm16(path)
    failures: list[str] = []
    expected_channels = int(capture["channels"])
    expected_width = int(capture["bit_depth"]) // 8
    expected_rate = int(capture["sample_rate"])
    if metadata["channels"] != expected_channels:
        failures.append("channel_count")
    if metadata["sample_width_bytes"] != expected_width:
        failures.append("sample_width")
    if metadata["sample_rate"] != expected_rate:
        failures.append("sample_rate")
    if metadata["compression"] != "NONE":
        failures.append("compression")
    if failures:
        return {
            "id": take.get("id"),
            "relative_wav": take.get("relative_wav"),
            "status": "failed",
            "failures": failures,
            "wav": metadata,
            "sha256": file_sha256(path),
        }
    if len(samples) != metadata["frame_count"]:
        raise CaptureError(f"WAV sample count differs from frame count: {path}")

    duration = len(samples) / expected_rate
    expected_duration = float(take["expected_seconds"])
    duration_error = duration - expected_duration
    if abs(duration_error) > float(qa["duration_tolerance_seconds"]):
        failures.append("duration")
    peak_value = max((abs(value) for value in samples), default=0)
    peak_linear = peak_value / 32768.0
    peak_dbfs = dbfs(peak_linear)
    clipping_samples = sum(value in (-32768, 32767) for value in samples)
    if clipping_samples:
        failures.append("clipping")
    if peak_dbfs > float(qa["maximum_peak_dbfs"]):
        failures.append("peak_level")
    dc_offset = (
        math.fsum(float(value) for value in samples) / (len(samples) * 32768.0)
        if samples
        else 0.0
    )
    if abs(dc_offset) > float(qa["maximum_dc_offset"]):
        failures.append("dc_offset")

    if take.get("kind") == "articulation_prompt":
        leading_seconds = float(timing["art_leading_silence_seconds"])
        trailing_seconds = float(timing["art_trailing_silence_seconds"])
    else:
        leading_seconds = float(timing["stationary_leading_silence_seconds"])
        trailing_seconds = float(timing["stationary_trailing_silence_seconds"])
    leading_count = round(leading_seconds * expected_rate)
    trailing_count = round(trailing_seconds * expected_rate)
    signal_end = len(samples) - trailing_count if trailing_count else len(samples)
    if leading_count + trailing_count >= len(samples):
        raise CaptureError(f"boundary silence consumes the whole WAV: {path}")
    boundary_values = list(samples[:leading_count])
    if trailing_count:
        boundary_values.extend(samples[-trailing_count:])
    silence_rms = rms(boundary_values)
    signal_rms = rms(samples[leading_count:signal_end])
    signal_dbfs = dbfs(signal_rms)
    snr_db = (
        20.0 * math.log10(signal_rms / silence_rms)
        if silence_rms > 0.0 and signal_rms > 0.0
        else (200.0 if signal_rms > 0.0 else -200.0)
    )
    if signal_dbfs < float(qa["minimum_signal_rms_dbfs"]):
        failures.append("signal_level")
    if snr_db < float(qa["minimum_snr_db"]):
        failures.append("snr")

    target_pitch = take.get("target_pitch")
    if not isinstance(target_pitch, dict):
        raise CaptureError(f"take {take.get('id')} has no target_pitch")
    target_hz = float(target_pitch["frequency_hz"])
    pitch_results: list[dict[str, float | int]] = []
    for center in pitch_centers(take, timing, expected_rate):
        result = estimate_target_pitch(samples, center, expected_rate, target_hz)
        pitch_results.append(result)
        if abs(float(result["cents_error"])) > float(qa["pitch_tolerance_cents"]):
            failures.append("pitch_tolerance")
        if float(result["correlation"]) < float(qa["minimum_pitch_correlation"]):
            failures.append("pitch_correlation")
    failures = sorted(set(failures))
    digest = file_sha256(path)
    provenance = take.get("provenance")
    if isinstance(provenance, dict):
        expected_hash = provenance.get("wav_sha256")
        if expected_hash is not None and expected_hash != digest:
            failures.append("provenance_hash")
    return {
        "id": take.get("id"),
        "relative_wav": take.get("relative_wav"),
        "status": "passed" if not failures else "failed",
        "failures": sorted(set(failures)),
        "sha256": digest,
        "wav": {
            **metadata,
            "duration_seconds": duration,
            "expected_duration_seconds": expected_duration,
            "duration_error_seconds": duration_error,
        },
        "level": {
            "peak_dbfs": peak_dbfs,
            "clipping_samples": clipping_samples,
            "dc_offset_normalized": dc_offset,
            "signal_rms_dbfs": signal_dbfs,
            "boundary_silence_rms_dbfs": dbfs(silence_rms),
            "boundary_snr_db": snr_db,
        },
        "pitch_windows": pitch_results,
    }


def collect_takes(manifest: Any) -> tuple[list[dict[str, Any]], dict[str, Any], dict[str, Any], dict[str, Any]]:
    if manifest.get("format") != "vocaloid-traditional-recording-session-plan-v1":
        raise CaptureError("unsupported recording-session manifest format")
    stationary = manifest.get("stationary_takes")
    articulation = manifest.get("articulation_takes")
    configuration = manifest.get("configuration")
    if not isinstance(stationary, list) or not isinstance(articulation, list):
        raise CaptureError("manifest has no take lists")
    if not isinstance(configuration, dict):
        raise CaptureError("manifest has no configuration")
    capture = configuration.get("capture")
    timing = configuration.get("timing")
    qa = configuration.get("qa")
    if not isinstance(capture, dict) or not isinstance(timing, dict) or not isinstance(qa, dict):
        raise CaptureError("manifest configuration is incomplete")
    takes = [*stationary, *articulation]
    ids: set[str] = set()
    paths: set[PurePosixPath] = set()
    for index, take in enumerate(takes):
        if not isinstance(take, dict):
            raise CaptureError(f"take {index} is not an object")
        take_id = take.get("id")
        if not isinstance(take_id, str) or not take_id or take_id in ids:
            raise CaptureError(f"invalid or duplicate take ID: {take_id!r}")
        ids.add(take_id)
        relative = normalized_relative_path(take.get("relative_wav"), take_id)
        if relative in paths:
            raise CaptureError(f"duplicate take path: {relative}")
        paths.add(relative)
    return takes, capture, timing, qa


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("recording_root", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument(
        "--take-id",
        action="append",
        help="validate only this take ID; may be supplied more than once",
    )
    args = parser.parse_args()
    try:
        manifest_path = args.manifest.resolve()
        root = args.recording_root.resolve()
        output = args.output.resolve()
        if output.exists():
            raise CaptureError(f"output already exists: {output}")
        if not root.is_dir():
            raise CaptureError(f"recording root is not a directory: {root}")
        manifest = read_json(manifest_path)
        takes, capture, timing, qa = collect_takes(manifest)
        by_id = {str(take["id"]): take for take in takes}
        if args.take_id:
            requested = list(dict.fromkeys(args.take_id))
            missing_ids = set(requested) - set(by_id)
            if missing_ids:
                raise CaptureError(f"unknown take IDs: {sorted(missing_ids)}")
            selected = [by_id[value] for value in requested]
        else:
            selected = takes

        results: list[dict[str, object]] = []
        expected_paths: set[PurePosixPath] = set()
        for take in selected:
            relative = normalized_relative_path(take.get("relative_wav"), str(take["id"]))
            expected_paths.add(relative)
            path = resolve_take_path(root, relative)
            if not path.is_file():
                results.append(
                    {
                        "id": take["id"],
                        "relative_wav": str(relative),
                        "status": "missing",
                        "failures": ["missing_file"],
                    }
                )
                continue
            results.append(validate_take(take, path, capture, timing, qa))

        unexpected: list[str] = []
        if not args.take_id:
            for path in root.rglob("*.wav"):
                if not path.is_file():
                    continue
                relative = PurePosixPath(path.relative_to(root).as_posix())
                if relative not in expected_paths:
                    unexpected.append(str(relative))
        status_counts = {
            status: sum(result["status"] == status for result in results)
            for status in ("passed", "failed", "missing")
        }
        complete = (
            status_counts["passed"] == len(selected)
            and not unexpected
        )
        report = {
            "format": "vocaloid-recording-capture-validation-v1",
            "source": {
                "manifest_sha256": file_sha256(manifest_path),
                "recording_root": str(root),
                "selected_take_ids": args.take_id,
            },
            "summary": {
                "manifest_takes": len(takes),
                "selected_takes": len(selected),
                **status_counts,
                "unexpected_wav_files": len(unexpected),
                "complete": complete,
            },
            "unexpected_wav_files": sorted(unexpected),
            "takes": results,
            "limitations": [
                "Automatic signal checks do not approve pronunciation, timbre, emotion, or phoneme boundaries.",
                "Pitch is sampled in expected stable vowel regions and does not replace a full F0 contour review.",
                "SNR uses declared leading/trailing silence and is invalid if those regions contain slate or speech.",
                "A passed report is a machine preflight; manual QA remains required before DRS analysis.",
            ],
        }
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(
            json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
        )
        for name, value in report["summary"].items():
            print(f"{name}={value}")
        print(f"output={output}")
        return 0 if complete else 3
    except (OSError, UnicodeError, json.JSONDecodeError, CaptureError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
