#!/usr/bin/env python3
"""Extract preferred ART/STA candidates into provenance-checked PCM16 unit WAVs."""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import wave
from collections import Counter, defaultdict
from pathlib import Path, PurePosixPath
from typing import Any, Iterable


class ExtractionError(Exception):
    pass


def read_json(path: Path) -> Any:
    if not path.is_file():
        raise ExtractionError(f"file does not exist: {path}")
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


def safe_relative_wav(value: Any, context: str) -> PurePosixPath:
    if not isinstance(value, str) or not value:
        raise ExtractionError(f"{context} has no relative WAV path")
    path = PurePosixPath(value)
    if (
        "\\" in value
        or ":" in value
        or path.is_absolute()
        or ".." in path.parts
        or path.suffix.lower() != ".wav"
    ):
        raise ExtractionError(f"unsafe relative WAV path in {context}: {value!r}")
    return path


def resolve_below(root: Path, relative: PurePosixPath, context: str) -> Path:
    path = root.joinpath(*relative.parts).resolve()
    if path != root and root not in path.parents:
        raise ExtractionError(f"{context} resolves outside its root: {relative}")
    return path


def integer_range(value: Any, context: str) -> tuple[int, int]:
    if (
        not isinstance(value, list)
        or len(value) != 2
        or any(isinstance(item, bool) or not isinstance(item, int) for item in value)
    ):
        raise ExtractionError(f"{context} must contain two integer samples")
    start, end = value
    if not 0 <= start < end:
        raise ExtractionError(f"{context} is not an increasing nonnegative range")
    return start, end


def source_contract(candidate: dict[str, Any]) -> tuple[PurePosixPath, str, int, int]:
    candidate_id = candidate.get("id")
    source = candidate.get("source_wav")
    if not isinstance(candidate_id, str) or not candidate_id:
        raise ExtractionError("candidate has no valid ID")
    if not isinstance(source, dict):
        raise ExtractionError(f"candidate {candidate_id} has no source_wav")
    relative = safe_relative_wav(source.get("relative_path"), candidate_id)
    digest = source.get("sha256")
    sample_rate = source.get("sample_rate")
    frame_count = source.get("frame_count")
    if not isinstance(digest, str) or len(digest) != 64:
        raise ExtractionError(f"candidate {candidate_id} has no source SHA-256")
    try:
        bytes.fromhex(digest)
    except ValueError as error:
        raise ExtractionError(
            f"candidate {candidate_id} has an invalid source SHA-256"
        ) from error
    if sample_rate != 44100:
        raise ExtractionError(f"candidate {candidate_id} is not 44.1 kHz")
    if isinstance(frame_count, bool) or not isinstance(frame_count, int) or frame_count <= 0:
        raise ExtractionError(f"candidate {candidate_id} has an invalid frame count")
    return relative, digest.lower(), sample_rate, frame_count


def wav_metadata(path: Path) -> tuple[dict[str, int | str], bytes]:
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
        raise ExtractionError(f"invalid WAV {path}: {error}") from error
    return metadata, frames


def verify_source(
    path: Path,
    expected_digest: str,
    expected_rate: int,
    expected_frames: int,
    ranges: Iterable[tuple[int, int]],
) -> dict[str, int | str]:
    if not path.is_file():
        raise ExtractionError(f"source WAV does not exist: {path}")
    digest = file_sha256(path)
    if digest != expected_digest:
        raise ExtractionError(
            f"source WAV SHA-256 differs from segmentation plan: {path}"
        )
    metadata, frames = wav_metadata(path)
    expected = {
        "channels": 1,
        "sample_width_bytes": 2,
        "sample_rate": expected_rate,
        "frame_count": expected_frames,
        "compression": "NONE",
    }
    for field, value in expected.items():
        if metadata[field] != value:
            raise ExtractionError(
                f"source WAV {field} differs in {path}: "
                f"{metadata[field]!r} != {value!r}"
            )
    if len(frames) != expected_frames * 2:
        raise ExtractionError(f"source WAV PCM byte count differs in {path}")
    for start, end in ranges:
        if end > expected_frames:
            raise ExtractionError(
                f"extraction range [{start},{end}) exceeds source WAV {path}"
            )
    return metadata


def write_pcm16_mono(path: Path, sample_rate: int, pcm: bytes) -> None:
    if len(pcm) % 2:
        raise ExtractionError(f"odd PCM byte count for output {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as stream:
        stream.setnchannels(1)
        stream.setsampwidth(2)
        stream.setframerate(sample_rate)
        stream.setcomptype("NONE", "not compressed")
        stream.writeframes(pcm)


def indexed_candidates(
    raw: Any, expected_kind: str, context: str
) -> dict[str, dict[str, Any]]:
    if not isinstance(raw, list):
        raise ExtractionError(f"{context} must be a list")
    result: dict[str, dict[str, Any]] = {}
    for index, candidate in enumerate(raw):
        if not isinstance(candidate, dict):
            raise ExtractionError(f"{context}[{index}] is not an object")
        candidate_id = candidate.get("id")
        if (
            not isinstance(candidate_id, str)
            or not candidate_id
            or candidate_id in result
        ):
            raise ExtractionError(f"invalid or duplicate candidate ID: {candidate_id!r}")
        if candidate.get("kind") != expected_kind:
            raise ExtractionError(f"candidate {candidate_id} has the wrong kind")
        source_contract(candidate)
        integer_range(
            candidate.get("extraction", {}).get("source_sample_range"),
            f"candidate {candidate_id} extraction range",
        )
        if not isinstance(candidate.get("builder_spec"), dict):
            raise ExtractionError(f"candidate {candidate_id} has no builder_spec")
        result[candidate_id] = candidate
    return result


def preferred_candidates(
    raw: Any,
    candidates: dict[str, dict[str, Any]],
    kind: str,
) -> list[tuple[dict[str, Any], dict[str, Any]]]:
    if not isinstance(raw, list):
        raise ExtractionError(f"preferred {kind} list is missing")
    selected: list[tuple[dict[str, Any], dict[str, Any]]] = []
    seen: set[str] = set()
    for index, item in enumerate(raw):
        if not isinstance(item, dict):
            raise ExtractionError(f"preferred {kind} item {index} is not an object")
        candidate_id = item.get("candidate_id")
        if (
            not isinstance(candidate_id, str)
            or candidate_id not in candidates
            or candidate_id in seen
        ):
            raise ExtractionError(
                f"preferred {kind} item has an invalid candidate: {candidate_id!r}"
            )
        seen.add(candidate_id)
        selected.append((item, candidates[candidate_id]))
    return selected


def unit_plan(
    segmentation: Any,
) -> tuple[list[dict[str, Any]], bool, dict[str, Any]]:
    if segmentation.get("format") != "vocaloid-recording-segmentation-plan-v1":
        raise ExtractionError("unsupported segmentation-plan format")
    source = segmentation.get("source")
    summary = segmentation.get("summary")
    if not isinstance(source, dict) or not isinstance(summary, dict):
        raise ExtractionError("segmentation plan lacks source or summary")
    art_candidates = indexed_candidates(
        segmentation.get("articulation_candidates"),
        "articulation_unit_candidate",
        "articulation_candidates",
    )
    sta_candidates = indexed_candidates(
        segmentation.get("stationary_candidates"),
        "stationary_unit_candidate",
        "stationary_candidates",
    )
    preferred_art = preferred_candidates(
        segmentation.get("preferred_articulation_units"), art_candidates, "ART"
    )
    preferred_sta = preferred_candidates(
        segmentation.get("preferred_stationary_units"), sta_candidates, "STA"
    )
    candidate_digest = canonical_json_hash(
        {
            "art": [
                {
                    "id": item["id"],
                    "sha256": item["source_wav"]["sha256"],
                    "range": item["extraction"]["source_sample_range"],
                    "builder": item["builder_spec"],
                }
                for item in segmentation["articulation_candidates"]
            ],
            "sta": [
                {
                    "id": item["id"],
                    "sha256": item["source_wav"]["sha256"],
                    "range": item["extraction"]["source_sample_range"],
                    "builder": item["builder_spec"],
                }
                for item in segmentation["stationary_candidates"]
            ],
        }
    )
    if candidate_digest != summary.get("candidate_plan_sha256"):
        raise ExtractionError("segmentation candidate-plan SHA-256 differs")
    count_contract = {
        "articulation_candidates": len(art_candidates),
        "stationary_candidates": len(sta_candidates),
        "preferred_art_layer_edges": len(preferred_art),
        "preferred_stationary_layer_phonemes": len(preferred_sta),
    }
    for field, actual in count_contract.items():
        if summary.get(field) != actual:
            raise ExtractionError(
                f"segmentation summary {field} differs: {summary.get(field)!r} != {actual}"
            )
    art_counts: Counter[str] = Counter()
    sta_counts: Counter[str] = Counter()
    units: list[dict[str, Any]] = []
    for preferred, candidate in preferred_art:
        layer = candidate.get("layer_id")
        edge = candidate.get("edge")
        if (
            not isinstance(layer, str)
            or not layer
            or not isinstance(edge, list)
            or len(edge) != 2
            or any(not isinstance(token, str) for token in edge)
            or preferred.get("layer_id") != layer
            or preferred.get("edge") != edge
        ):
            raise ExtractionError(f"preferred ART identity differs for {candidate['id']}")
        art_counts[layer] += 1
        ordinal = art_counts[layer]
        units.append(
            {
                "unit_id": f"ART_{layer}_{ordinal:04d}",
                "kind": "articulation",
                "output_relative_wav": f"art/{layer}/edge_{ordinal:04d}.wav",
                "candidate": candidate,
                "selection": preferred,
            }
        )
    for preferred, candidate in preferred_sta:
        layer = candidate.get("layer_id")
        phoneme = candidate.get("phoneme")
        if (
            not isinstance(layer, str)
            or not layer
            or not isinstance(phoneme, str)
            or not phoneme
            or preferred.get("layer_id") != layer
            or preferred.get("phoneme") != phoneme
        ):
            raise ExtractionError(f"preferred STA identity differs for {candidate['id']}")
        sta_counts[layer] += 1
        ordinal = sta_counts[layer]
        units.append(
            {
                "unit_id": f"STA_{layer}_{ordinal:03d}",
                "kind": "stationary",
                "output_relative_wav": f"sta/{layer}/phoneme_{ordinal:03d}.wav",
                "candidate": candidate,
                "selection": preferred,
            }
        )
    unit_ids = [item["unit_id"] for item in units]
    output_paths = [item["output_relative_wav"] for item in units]
    if len(unit_ids) != len(set(unit_ids)) or len(output_paths) != len(set(output_paths)):
        raise ExtractionError("unit plan contains a duplicate ID or output path")
    required_art = summary.get("required_art_layer_edges")
    required_sta = summary.get("required_stationary_layer_phonemes")
    computed_complete = (
        summary.get("capture_validation_complete") is True
        and isinstance(required_art, int)
        and len(preferred_art) == required_art
        and isinstance(required_sta, int)
        and len(preferred_sta) == required_sta
        and summary.get("rejected_source_takes") == 0
    )
    if (summary.get("coverage_complete") is True) != computed_complete:
        raise ExtractionError("segmentation coverage_complete is internally inconsistent")
    complete = computed_complete
    return units, complete, source


def build_manifest(
    segmentation_path: Path,
    segmentation: Any,
    recording_root: Path,
    output_root: Path,
) -> dict[str, Any]:
    units, input_complete, source = unit_plan(segmentation)
    declared_root = source.get("recording_root")
    if not isinstance(declared_root, str) or Path(declared_root).resolve() != recording_root:
        raise ExtractionError(
            "recording root differs from the root recorded by capture validation"
        )

    grouped: dict[PurePosixPath, list[dict[str, Any]]] = defaultdict(list)
    source_contracts: dict[PurePosixPath, tuple[str, int, int]] = {}
    for unit in units:
        candidate = unit["candidate"]
        relative, digest, rate, frames = source_contract(candidate)
        contract = (digest, rate, frames)
        previous = source_contracts.setdefault(relative, contract)
        if previous != contract:
            raise ExtractionError(f"source contract differs across candidates: {relative}")
        start, end = integer_range(
            candidate["extraction"]["source_sample_range"],
            f"candidate {candidate['id']} extraction range",
        )
        if candidate["extraction"].get("unit_sample_count") != end - start:
            raise ExtractionError(f"candidate {candidate['id']} unit sample count differs")
        grouped[relative].append(unit)

    # Complete all read-only preflight checks before creating the output directory.
    for relative in sorted(grouped, key=str):
        digest, rate, frames = source_contracts[relative]
        source_path = resolve_below(recording_root, relative, "source WAV")
        ranges = [
            integer_range(
                unit["candidate"]["extraction"]["source_sample_range"],
                f"candidate {unit['candidate']['id']} extraction range",
            )
            for unit in grouped[relative]
        ]
        verify_source(source_path, digest, rate, frames, ranges)

    if output_root.exists():
        raise ExtractionError(f"output directory already exists: {output_root}")
    output_root.parent.mkdir(parents=True, exist_ok=True)
    output_root.mkdir()
    incomplete_marker = output_root / "EXTRACTION_INCOMPLETE"
    incomplete_marker.write_text(
        "Extraction did not reach the final manifest yet.\n", encoding="utf-8"
    )

    extracted: list[dict[str, Any]] = []
    for relative in sorted(grouped, key=str):
        digest, rate, frames = source_contracts[relative]
        source_path = resolve_below(recording_root, relative, "source WAV")
        metadata, pcm = wav_metadata(source_path)
        if metadata["frame_count"] != frames or file_sha256(source_path) != digest:
            raise ExtractionError(f"source WAV changed after preflight: {source_path}")
        for unit in grouped[relative]:
            candidate = unit["candidate"]
            start, end = integer_range(
                candidate["extraction"]["source_sample_range"],
                f"candidate {candidate['id']} extraction range",
            )
            output_relative = safe_relative_wav(
                unit["output_relative_wav"], unit["unit_id"]
            )
            output_path = resolve_below(output_root, output_relative, "output WAV")
            write_pcm16_mono(output_path, rate, pcm[start * 2 : end * 2])
            output_metadata, output_pcm = wav_metadata(output_path)
            if (
                output_metadata["channels"] != 1
                or output_metadata["sample_width_bytes"] != 2
                or output_metadata["sample_rate"] != rate
                or output_metadata["frame_count"] != end - start
                or output_metadata["compression"] != "NONE"
                or output_pcm != pcm[start * 2 : end * 2]
            ):
                raise ExtractionError(f"output WAV round-trip differs: {output_path}")
            item: dict[str, Any] = {
                "unit_id": unit["unit_id"],
                "kind": unit["kind"],
                "output_relative_wav": str(output_relative),
                "output_wav_sha256": file_sha256(output_path),
                "wav": {
                    "sample_rate": rate,
                    "channels": 1,
                    "bit_depth": 16,
                    "frame_count": end - start,
                    "duration_seconds": (end - start) / rate,
                },
                "source": {
                    "candidate_id": candidate["id"],
                    "take_id": candidate["take_id"],
                    "relative_wav": str(relative),
                    "wav_sha256": digest,
                    "sample_range": [start, end],
                },
                "layer_id": candidate["layer_id"],
                "builder_spec": candidate["builder_spec"],
                "frame_alignment": candidate["frame_alignment"],
                "selection": unit["selection"],
                "approval_status": "unapproved_extracted_candidate",
            }
            if unit["kind"] == "articulation":
                item.update(
                    {
                        "edge": candidate["edge"],
                        "role": candidate["role"],
                        "voicing": candidate["voicing"],
                    }
                )
            else:
                item.update(
                    {
                        "phoneme": candidate["phoneme"],
                        "carrier": candidate["carrier"],
                    }
                )
            extracted.append(item)

    articulation = sorted(
        (item for item in extracted if item["kind"] == "articulation"),
        key=lambda item: item["unit_id"],
    )
    stationary = sorted(
        (item for item in extracted if item["kind"] == "stationary"),
        key=lambda item: item["unit_id"],
    )
    output_digest = canonical_json_hash(
        [
            {
                "unit_id": item["unit_id"],
                "relative_wav": item["output_relative_wav"],
                "sha256": item["output_wav_sha256"],
                "builder_spec": item["builder_spec"],
            }
            for item in [*articulation, *stationary]
        ]
    )
    manifest = {
        "format": "vocaloid-extracted-recording-units-v1",
        "source": {
            "segmentation_plan_sha256": file_sha256(segmentation_path),
            "recording_root": str(recording_root),
            "segmentation_candidate_plan_sha256": segmentation.get("summary", {}).get(
                "candidate_plan_sha256"
            ),
        },
        "summary": {
            "source_wav_files": len(grouped),
            "articulation_units": len(articulation),
            "stationary_units": len(stationary),
            "total_units": len(extracted),
            "input_coverage_complete": input_complete,
            "approval_complete": False,
            "unit_manifest_sha256": output_digest,
        },
        "articulation_units": articulation,
        "stationary_units": stationary,
        "limitations": [
            "Extraction preserves selected PCM sample ranges but does not refine phoneme boundaries.",
            "Preferred candidates are deterministic placeholders and every extracted unit remains unapproved.",
            "Frame alignment is provisional until DRS analysis returns the actual frame count and voicing sequence.",
            "The output contains self-owned recordings or synthetic fixtures and must not be committed as ordinary source artifacts.",
        ],
    }
    manifest_path = output_root / "unit_manifest.json"
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    incomplete_marker.unlink()
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("segmentation_plan", type=Path)
    parser.add_argument("recording_root", type=Path)
    parser.add_argument("output_root", type=Path)
    args = parser.parse_args()
    try:
        segmentation_path = args.segmentation_plan.resolve()
        recording_root = args.recording_root.resolve()
        output_root = args.output_root.resolve()
        if not recording_root.is_dir():
            raise ExtractionError(
                f"recording root is not a directory: {recording_root}"
            )
        segmentation = read_json(segmentation_path)
        manifest = build_manifest(
            segmentation_path, segmentation, recording_root, output_root
        )
        for name, value in manifest["summary"].items():
            print(f"{name}={value}")
        print(f"manifest={output_root / 'unit_manifest.json'}")
        return 0 if manifest["summary"]["input_coverage_complete"] else 3
    except (OSError, UnicodeError, json.JSONDecodeError, ExtractionError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
