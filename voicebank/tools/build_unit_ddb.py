#!/usr/bin/env python3
"""Build a one-unit FRM2/SND DDB and print the offsets needed by a future DDI."""

from __future__ import annotations

import argparse
import json
import mmap
import os
import struct
import sys
import wave
from array import array
from pathlib import Path

import probe_frm2
import probe_sms2
import validate_main_sms2


SAMPLE_RATE = 44100
HOP_SAMPLES = 256
ANALYSIS_MARGIN_SAMPLES = 1024
SND_HEADER = struct.Struct("<4sIIHI")


class BuildError(Exception):
    pass


def read_pcm16_mono(path: Path) -> array[int]:
    try:
        with wave.open(str(path), "rb") as source:
            channels = source.getnchannels()
            sample_width = source.getsampwidth()
            sample_rate = source.getframerate()
            compression = source.getcomptype()
            frame_count = source.getnframes()
            raw = source.readframes(frame_count)
    except (EOFError, wave.Error) as error:
        raise BuildError(f"cannot read input WAV: {error}") from error

    if sample_rate != SAMPLE_RATE:
        raise BuildError(f"input WAV is {sample_rate} Hz; expected {SAMPLE_RATE} Hz")
    if sample_width != 2 or compression != "NONE":
        raise BuildError("input WAV must be uncompressed PCM16")
    if channels < 1 or channels > 8:
        raise BuildError(f"unsupported channel count {channels}")

    values = array("h")
    values.frombytes(raw)
    if sys.byteorder != "little":
        values.byteswap()
    if len(values) != frame_count * channels:
        raise BuildError("WAV data is not aligned to complete sample frames")
    if channels == 1:
        return values

    mono = array("h")
    for offset in range(0, len(values), channels):
        mixed = round(sum(values[offset : offset + channels]) / channels)
        mono.append(max(-32768, min(32767, mixed)))
    return mono


def extract_frames(path: Path) -> list[bytes]:
    file_size = path.stat().st_size
    with path.open("rb") as stream, mmap.mmap(
        stream.fileno(), 0, access=mmap.ACCESS_READ
    ) as data:
        if file_size < 8:
            raise BuildError("SMS2 is shorter than its header")
        magic, declared_size = struct.unpack_from("<4sI", data, 0)
        if magic != b"SMS2" or declared_size != file_size:
            raise BuildError(
                f"invalid SMS2 header: magic={magic!r}, size={declared_size}/{file_size}"
            )
        runs = probe_sms2.find_frame_runs(data, file_size)
        if len(runs) != 1:
            raise BuildError(f"expected one FRM2 run, found {len(runs)}")

        result: list[bytes] = []
        previous_time: float | None = None
        for index, item in enumerate(runs[0]):
            offset = int(item["offset"])
            size = int(item["chunk_size"])
            raw = bytes(data[offset : offset + size])
            frame = probe_frm2.parse_frame(raw)
            if probe_frm2.serialize_frame(frame) != raw:
                raise BuildError(f"FRM2 {index} does not round-trip byte-exactly")
            if isinstance(frame, probe_frm2.MainFrame):
                validate_main_sms2.validate_frame(frame, index)
            elif not isinstance(frame, probe_frm2.UnvoicedFrame):
                raise BuildError(f"FRM2 {index} is neither a main nor unvoiced frame")
            if previous_time is not None:
                expected = HOP_SAMPLES / SAMPLE_RATE
                actual = frame.time_seconds - previous_time
                if abs(actual - expected) > 1.0e-12:
                    raise BuildError(
                        f"FRM2 {index} time step is {actual:.15g}; expected {expected:.15g}"
                    )
            previous_time = frame.time_seconds
            result.append(raw)
    if not result:
        raise BuildError("SMS2 contains no FRM2")
    return result


def build_snd(core_pcm: array[int], frame_count: int) -> tuple[bytes, int, int]:
    expected_core_count = frame_count * HOP_SAMPLES
    difference = expected_core_count - len(core_pcm)
    if abs(difference) >= HOP_SAMPLES:
        raise BuildError(
            f"WAV has {len(core_pcm)} samples but {frame_count} frames imply "
            f"{expected_core_count}; difference {difference} is at least one hop"
        )

    adjusted = array("h", core_pcm[:expected_core_count])
    if len(adjusted) < expected_core_count:
        adjusted.extend([0] * (expected_core_count - len(adjusted)))
    pcm = array("h", [0] * ANALYSIS_MARGIN_SAMPLES)
    pcm.extend(adjusted)
    pcm.extend([0] * ANALYSIS_MARGIN_SAMPLES)
    if sys.byteorder != "little":
        pcm.byteswap()
    payload = pcm.tobytes()
    chunk_size = SND_HEADER.size + len(payload)
    header = SND_HEADER.pack(b"SND ", chunk_size, SAMPLE_RATE, 1, len(pcm))
    return header + payload, len(pcm), difference


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


def validate_voicing_boundary(
    frames: list[bytes],
    split_frame: int,
    source_voicing: str,
    target_voicing: str,
) -> dict[str, object]:
    if split_frame <= 0 or split_frame >= len(frames):
        raise BuildError(
            f"split frame must be inside 1..{len(frames) - 1}, got {split_frame}"
        )
    actual: list[str] = []
    for index, raw in enumerate(frames):
        frame = probe_frm2.parse_frame(raw)
        if isinstance(frame, probe_frm2.MainFrame):
            actual.append("voiced")
        elif isinstance(frame, probe_frm2.UnvoicedFrame):
            actual.append("unvoiced")
        else:
            raise BuildError(f"frame {index} has unsupported voicing type")
    expected = [source_voicing] * split_frame + [target_voicing] * (
        len(frames) - split_frame
    )
    mismatches = [index for index, pair in enumerate(zip(actual, expected)) if pair[0] != pair[1]]
    if mismatches:
        preview = ", ".join(str(index) for index in mismatches[:8])
        raise BuildError(
            f"{len(mismatches)} frames disagree with the annotated voicing boundary; "
            f"first mismatches: {preview}"
        )
    return {
        "split_frame": split_frame,
        "source_voicing": source_voicing,
        "target_voicing": target_voicing,
        "source_frame_count": split_frame,
        "target_frame_count": len(frames) - split_frame,
    }


def build(
    sms2_path: Path,
    wave_path: Path,
    output_path: Path,
    unit_kind: str,
    split_frame: int | None = None,
    source_voicing: str | None = None,
    target_voicing: str | None = None,
) -> dict[str, object]:
    frames = extract_frames(sms2_path)
    voicing_boundary = None
    if split_frame is not None and source_voicing is not None and target_voicing is not None:
        voicing_boundary = validate_voicing_boundary(
            frames,
            split_frame,
            source_voicing,
            target_voicing,
        )
    core_pcm = read_pcm16_mono(wave_path)
    snd, pcm_count, padded_core_samples = build_snd(core_pcm, len(frames))

    frame_offsets: list[int] = []
    offset = 0
    for frame in frames:
        frame_offsets.append(offset)
        offset += len(frame)
    snd_offset = offset
    write_atomic(output_path, [*frames, snd])
    ddi_snd_pointer = (
        snd_offset + SND_HEADER.size + ANALYSIS_MARGIN_SAMPLES * 2
        if unit_kind == "sta"
        else snd_offset
    )
    return {
        "output": str(output_path.resolve()),
        "unit_kind": unit_kind,
        "file_size": output_path.stat().st_size,
        "frame_count": len(frames),
        "frame_offsets": frame_offsets,
        "snd_chunk_offset": snd_offset,
        "ddi_snd_pointer": ddi_snd_pointer,
        "pcm_value_count": pcm_count,
        "sample_rate": SAMPLE_RATE,
        "channels": 1,
        "input_core_samples": len(core_pcm),
        "core_padding_samples": padded_core_samples,
        "voicing_boundary": voicing_boundary,
        "invariants": {
            "pcm_count_equals_frames_times_256_plus_2048": (
                pcm_count == len(frames) * HOP_SAMPLES + 2 * ANALYSIS_MARGIN_SAMPLES
            ),
            "sta_pointer_delta": ddi_snd_pointer - snd_offset if unit_kind == "sta" else None,
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sms2", type=Path, help="final main/unvoiced DRS SMS2")
    parser.add_argument("wav", type=Path, help="the matching 44.1 kHz PCM16 recording")
    parser.add_argument("output", type=Path, help="output one-unit DDB")
    parser.add_argument("--kind", choices=("sta", "art"), required=True)
    parser.add_argument("--split-frame", type=int)
    parser.add_argument("--source-voicing", choices=("voiced", "unvoiced"))
    parser.add_argument("--target-voicing", choices=("voiced", "unvoiced"))
    args = parser.parse_args()
    boundary_options = (args.split_frame, args.source_voicing, args.target_voicing)
    if any(value is not None for value in boundary_options):
        if args.kind != "art" or any(value is None for value in boundary_options):
            parser.error(
                "--split-frame, --source-voicing, and --target-voicing must be "
                "used together with --kind art"
            )
    try:
        result = build(
            args.sms2,
            args.wav,
            args.output,
            args.kind,
            args.split_frame,
            args.source_voicing,
            args.target_voicing,
        )
    except (OSError, OverflowError, ValueError, struct.error, BuildError,
            probe_frm2.ProbeError, validate_main_sms2.ValidationError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
