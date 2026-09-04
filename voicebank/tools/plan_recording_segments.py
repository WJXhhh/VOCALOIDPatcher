#!/usr/bin/env python3
"""Turn passed long-prompt takes into reviewable ART/STA unit-cut candidates."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
from collections import Counter, defaultdict
from pathlib import Path, PurePosixPath
from typing import Any, Iterable


class SegmentError(Exception):
    pass


Edge = tuple[str, str]


def read_json(path: Path) -> Any:
    if not path.is_file():
        raise SegmentError(f"file does not exist: {path}")
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


def finite_number(
    value: Any, name: str, *, positive: bool = False, unit_interval: bool = False
) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise SegmentError(f"{name} must be numeric")
    result = float(value)
    if not math.isfinite(result):
        raise SegmentError(f"{name} must be finite")
    if positive and result <= 0.0:
        raise SegmentError(f"{name} must be positive")
    if unit_interval and not 0.0 < result < 1.0:
        raise SegmentError(f"{name} must lie strictly between 0 and 1")
    return result


def positive_integer(value: Any, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        raise SegmentError(f"{name} must be a positive integer")
    return value


def parse_configuration(value: Any) -> dict[str, object]:
    if not isinstance(value, dict) or value.get("schema_version") != 1:
        raise SegmentError("segmentation configuration must use schema_version 1")
    stationary = value.get("stationary_inner_fraction")
    if not isinstance(stationary, list) or len(stationary) != 2:
        raise SegmentError("stationary_inner_fraction must contain [start, end]")
    stationary_start = finite_number(
        stationary[0], "stationary_inner_fraction[0]", unit_interval=True
    )
    stationary_end = finite_number(
        stationary[1], "stationary_inner_fraction[1]", unit_interval=True
    )
    if stationary_start >= stationary_end:
        raise SegmentError("stationary_inner_fraction must be increasing")
    result: dict[str, object] = {
        "schema_version": 1,
        "two_phoneme_onset_fraction": finite_number(
            value.get("two_phoneme_onset_fraction"),
            "two_phoneme_onset_fraction",
            unit_interval=True,
        ),
        "art_context_seconds": finite_number(
            value.get("art_context_seconds"), "art_context_seconds", positive=True
        ),
        "art_inner_margin_seconds": finite_number(
            value.get("art_inner_margin_seconds"),
            "art_inner_margin_seconds",
            positive=True,
        ),
        "art_inner_width_seconds": finite_number(
            value.get("art_inner_width_seconds"),
            "art_inner_width_seconds",
            positive=True,
        ),
        "stationary_inner_fraction": [stationary_start, stationary_end],
        "minimum_boundary_context_samples": positive_integer(
            value.get("minimum_boundary_context_samples"),
            "minimum_boundary_context_samples",
        ),
        "analysis_hop_samples": positive_integer(
            value.get("analysis_hop_samples"), "analysis_hop_samples"
        ),
    }
    context = float(result["art_context_seconds"])
    margin = float(result["art_inner_margin_seconds"])
    width = float(result["art_inner_width_seconds"])
    if margin + width >= context:
        raise SegmentError(
            "art_inner_margin_seconds + art_inner_width_seconds must be less "
            "than art_context_seconds"
        )
    if int(result["minimum_boundary_context_samples"]) < 1024:
        raise SegmentError(
            "minimum_boundary_context_samples must be at least 1024 for the "
            "current analysis window"
        )
    if result["analysis_hop_samples"] != 256:
        raise SegmentError("current DRS/build chain requires a 256-sample hop")
    return result


def edge_list(value: Any, context: str) -> set[Edge]:
    if not isinstance(value, list):
        raise SegmentError(f"{context} must be a list")
    result: set[Edge] = set()
    for index, raw in enumerate(value):
        if (
            not isinstance(raw, list)
            or len(raw) != 2
            or any(not isinstance(token, str) or not token for token in raw)
        ):
            raise SegmentError(f"{context}[{index}] is not a phoneme pair")
        edge = (raw[0], raw[1])
        if edge in result:
            raise SegmentError(f"duplicate edge in {context}: {edge}")
        result.add(edge)
    return result


def graph_metadata(graph: Any, art_set: str) -> tuple[set[Edge], dict[str, str]]:
    if not isinstance(graph, dict):
        raise SegmentError("graph is not an object")
    try:
        aggregate = graph["aggregate"]
        required = edge_list(aggregate["art"][art_set], f"aggregate.art.{art_set}")
        phonemes = aggregate["phonemes"]
        voiced = phonemes["voiced_intersection"]
        unvoiced = phonemes["unvoiced_intersection"]
    except (KeyError, TypeError) as error:
        raise SegmentError("graph lacks ART or phoneme-class metadata") from error
    if not isinstance(voiced, list) or not isinstance(unvoiced, list):
        raise SegmentError("graph voiced/unvoiced inventories are invalid")
    classification: dict[str, str] = {"Sil": "unvoiced"}
    for kind, values in (("voiced", voiced), ("unvoiced", unvoiced)):
        for token in values:
            if not isinstance(token, str) or not token:
                raise SegmentError("graph contains an invalid phoneme")
            previous = classification.setdefault(token, kind)
            if previous != kind:
                raise SegmentError(f"phoneme has conflicting voicing class: {token}")
    missing = {token for edge in required for token in edge} - set(classification)
    if missing:
        raise SegmentError(f"ART phonemes lack voicing class: {sorted(missing)}")
    return required, classification


def canonical_edge_hash(edges: Iterable[Edge]) -> str:
    payload = json.dumps(
        [list(edge) for edge in sorted(edges)],
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def parse_long_plan(value: Any, required: set[Edge]) -> dict[str, dict[str, Any]]:
    if value.get("format") != "vocaloid-chinese-long-prompt-plan-v1":
        raise SegmentError("unsupported long-prompt plan format")
    source = value.get("source")
    prompts = value.get("recording_prompts")
    trace = value.get("required_edge_trace")
    if not isinstance(source, dict) or not isinstance(prompts, list) or not isinstance(trace, list):
        raise SegmentError("long-prompt plan is incomplete")
    if source.get("edge_sha256") != canonical_edge_hash(required):
        raise SegmentError("long-prompt plan does not match graph ART edges")
    by_id: dict[str, dict[str, Any]] = {}
    for index, prompt in enumerate(prompts):
        if not isinstance(prompt, dict):
            raise SegmentError(f"prompt {index} is not an object")
        prompt_id = prompt.get("id")
        if not isinstance(prompt_id, str) or not prompt_id or prompt_id in by_id:
            raise SegmentError(f"invalid or duplicate prompt ID: {prompt_id!r}")
        by_id[prompt_id] = prompt
    traced: set[Edge] = set()
    for item in trace:
        if not isinstance(item, dict):
            raise SegmentError("required_edge_trace contains a non-object")
        raw = item.get("edge")
        edge = (
            (raw[0], raw[1])
            if isinstance(raw, list)
            and len(raw) == 2
            and all(isinstance(token, str) for token in raw)
            else None
        )
        if edge is None or edge not in required or edge in traced:
            raise SegmentError(f"invalid or duplicate required edge trace: {raw!r}")
        traced.add(edge)
    if traced != required:
        raise SegmentError("required_edge_trace does not cover the graph")
    return by_id


def schedule_prompt(
    prompt: dict[str, Any], timing: dict[str, Any], onset_fraction: float
) -> list[dict[str, object]]:
    syllables = prompt.get("phoneme_syllables")
    phoneme_path = prompt.get("phonemes")
    if not isinstance(syllables, list) or not isinstance(phoneme_path, list):
        raise SegmentError(f"prompt {prompt.get('id')} lacks phoneme structure")
    parsed: list[list[str]] = []
    for index, raw in enumerate(syllables):
        if (
            not isinstance(raw, list)
            or len(raw) not in (1, 2)
            or any(not isinstance(token, str) or not token for token in raw)
        ):
            raise SegmentError(
                f"prompt {prompt.get('id')} syllable {index} must have one or two phonemes"
            )
        parsed.append(raw)
    if not parsed:
        raise SegmentError(f"prompt {prompt.get('id')} has no syllables")
    flattened = [token for syllable in parsed for token in syllable]
    expected_path = ["Sil", *flattened, "Sil"]
    if phoneme_path != expected_path:
        raise SegmentError(f"prompt {prompt.get('id')} phoneme expansion differs")
    if prompt.get("syllable_count") != len(parsed):
        raise SegmentError(f"prompt {prompt.get('id')} syllable_count differs")
    leading = float(timing["art_leading_silence_seconds"])
    syllable_seconds = float(timing["art_syllable_seconds"])
    schedule: list[dict[str, object]] = []
    transition_index = 0
    schedule.append(
        {
            "transition_index": transition_index,
            "edge": ["Sil", parsed[0][0]],
            "role": "silence_onset",
            "boundary_seconds": leading,
            "syllable_index": 0,
        }
    )
    transition_index += 1
    for syllable_index, syllable in enumerate(parsed):
        start = leading + syllable_index * syllable_seconds
        if len(syllable) == 2:
            schedule.append(
                {
                    "transition_index": transition_index,
                    "edge": [syllable[0], syllable[1]],
                    "role": "within_syllable",
                    "boundary_seconds": start + onset_fraction * syllable_seconds,
                    "syllable_index": syllable_index,
                }
            )
            transition_index += 1
        end = start + syllable_seconds
        if syllable_index + 1 < len(parsed):
            target = parsed[syllable_index + 1][0]
            role = "cross_syllable"
        else:
            target = "Sil"
            role = "silence_coda"
        schedule.append(
            {
                "transition_index": transition_index,
                "edge": [syllable[-1], target],
                "role": role,
                "boundary_seconds": end,
                "syllable_index": syllable_index,
            }
        )
        transition_index += 1
    realized = [item["edge"] for item in schedule]
    expected_edges = [phoneme_path[index : index + 2] for index in range(len(phoneme_path) - 1)]
    if realized != expected_edges:
        raise SegmentError(f"prompt {prompt.get('id')} transition schedule differs")
    return schedule


def safe_relative_wav(value: Any, context: str) -> str:
    if not isinstance(value, str) or not value:
        raise SegmentError(f"{context} lacks relative_wav")
    path = PurePosixPath(value)
    if (
        "\\" in value
        or ":" in value
        or path.is_absolute()
        or ".." in path.parts
        or path.suffix.lower() != ".wav"
    ):
        raise SegmentError(f"unsafe WAV path in {context}: {value!r}")
    return str(path)


def round_frame(seconds: float, duration: float, frame_count: int) -> int:
    return max(0, min(frame_count, round(seconds / duration * frame_count)))


def art_candidate(
    take: dict[str, Any],
    result: dict[str, Any],
    scheduled: dict[str, object],
    sample_rate: int,
    classification: dict[str, str],
    configuration: dict[str, object],
) -> dict[str, object]:
    wav = result.get("wav")
    if not isinstance(wav, dict):
        raise SegmentError(f"passed take {take.get('id')} has no WAV metadata")
    frame_count = wav.get("frame_count")
    if isinstance(frame_count, bool) or not isinstance(frame_count, int) or frame_count <= 0:
        raise SegmentError(f"passed take {take.get('id')} has invalid frame_count")
    boundary = round(float(scheduled["boundary_seconds"]) * sample_rate)
    context = round(float(configuration["art_context_seconds"]) * sample_rate)
    minimum = int(configuration["minimum_boundary_context_samples"])
    if boundary < context or frame_count - boundary < context:
        raise SegmentError(
            f"take {take.get('id')} lacks declared ART context around transition "
            f"{scheduled['transition_index']}"
        )
    outer_start = boundary - context
    outer_end = boundary + context
    if boundary - outer_start < minimum or outer_end - boundary < minimum:
        raise SegmentError("ART context is shorter than minimum_boundary_context_samples")
    margin = round(float(configuration["art_inner_margin_seconds"]) * sample_rate)
    width = round(float(configuration["art_inner_width_seconds"]) * sample_rate)
    source_inner = [boundary - margin - width, boundary - margin]
    target_inner = [boundary + margin, boundary + margin + width]
    if not (
        outer_start <= source_inner[0] < source_inner[1] <= boundary
        and boundary <= target_inner[0] < target_inner[1] <= outer_end
    ):
        raise SegmentError("ART inner ranges do not fit inside the extraction window")
    unit_samples = outer_end - outer_start
    unit_duration = unit_samples / sample_rate
    boundary_unit = (boundary - outer_start) / sample_rate
    source_inner_unit = [
        (source_inner[0] - outer_start) / sample_rate,
        (source_inner[1] - outer_start) / sample_rate,
    ]
    target_inner_unit = [
        (target_inner[0] - outer_start) / sample_rate,
        (target_inner[1] - outer_start) / sample_rate,
    ]
    hop = int(configuration["analysis_hop_samples"])
    estimated_frames = math.ceil(unit_samples / hop)
    edge = tuple(scheduled["edge"])
    frame_alignment = {
        "frame_count_estimate": estimated_frames,
        "split_frame_estimate": round_frame(
            boundary_unit, unit_duration, estimated_frames
        ),
        "source_inner_frame_estimate": [
            round_frame(value, unit_duration, estimated_frames)
            for value in source_inner_unit
        ],
        "target_inner_frame_estimate": [
            round_frame(value, unit_duration, estimated_frames)
            for value in target_inner_unit
        ],
        "mapping": "round(seconds / extracted_unit_duration * DRS_frame_count)",
        "status": "provisional_until_drs_output",
    }
    occurrence_id = (
        f"{take['id']}_T{int(scheduled['transition_index']):02d}_"
        f"{edge[0]}_TO_{edge[1]}"
    )
    return {
        "id": occurrence_id,
        "kind": "articulation_unit_candidate",
        "take_id": take["id"],
        "prompt_id": take["prompt_id"],
        "layer_id": take["layer_id"],
        "repetition": take["repetition"],
        "transition_index": scheduled["transition_index"],
        "syllable_index": scheduled["syllable_index"],
        "edge": list(edge),
        "role": scheduled["role"],
        "voicing": {
            "source": classification[edge[0]],
            "target": classification[edge[1]],
        },
        "source_wav": {
            "relative_path": safe_relative_wav(take.get("relative_wav"), str(take["id"])),
            "sha256": result.get("sha256"),
            "sample_rate": sample_rate,
            "frame_count": frame_count,
        },
        "nominal_boundary": {
            "seconds_in_source": boundary / sample_rate,
            "sample_in_source": boundary,
        },
        "extraction": {
            "source_sample_range": [outer_start, outer_end],
            "source_seconds_range": [outer_start / sample_rate, outer_end / sample_rate],
            "unit_sample_count": unit_samples,
            "unit_duration_seconds": unit_duration,
        },
        "builder_spec": {
            "boundary_seconds": boundary_unit,
            "source_inner_seconds": source_inner_unit,
            "target_inner_seconds": target_inner_unit,
            "f0_hz": float(take["target_pitch"]["frequency_hz"]),
        },
        "frame_alignment": frame_alignment,
        "confidence": "nominal_schedule",
        "review_status": "needs_manual_boundary_review",
    }


def stationary_candidate(
    take: dict[str, Any],
    result: dict[str, Any],
    sample_rate: int,
    timing: dict[str, Any],
    configuration: dict[str, object],
) -> dict[str, object]:
    wav = result.get("wav")
    if not isinstance(wav, dict):
        raise SegmentError(f"passed take {take.get('id')} has no WAV metadata")
    frame_count = wav.get("frame_count")
    if isinstance(frame_count, bool) or not isinstance(frame_count, int) or frame_count <= 0:
        raise SegmentError(f"passed take {take.get('id')} has invalid frame_count")
    fractions = configuration["stationary_inner_fraction"]
    leading = float(timing["stationary_leading_silence_seconds"])
    sustain = float(timing["stationary_sustain_seconds"])
    start = round((leading + float(fractions[0]) * sustain) * sample_rate)
    end = round((leading + float(fractions[1]) * sustain) * sample_rate)
    if not 0 <= start < end <= frame_count:
        raise SegmentError(f"STA stable range lies outside take {take.get('id')}")
    unit_samples = end - start
    hop = int(configuration["analysis_hop_samples"])
    return {
        "id": f"{take['id']}_{take['target_phoneme']}",
        "kind": "stationary_unit_candidate",
        "take_id": take["id"],
        "layer_id": take["layer_id"],
        "repetition": take["repetition"],
        "phoneme": take["target_phoneme"],
        "carrier": {
            "pinyin": take["carrier_pinyin"],
            "phonemes": take["carrier_phonemes"],
            "target_phoneme_index": take["target_phoneme_index"],
        },
        "source_wav": {
            "relative_path": safe_relative_wav(take.get("relative_wav"), str(take["id"])),
            "sha256": result.get("sha256"),
            "sample_rate": sample_rate,
            "frame_count": frame_count,
        },
        "extraction": {
            "source_sample_range": [start, end],
            "source_seconds_range": [start / sample_rate, end / sample_rate],
            "unit_sample_count": unit_samples,
            "unit_duration_seconds": unit_samples / sample_rate,
        },
        "builder_spec": {
            "f0_hz": float(take["target_pitch"]["frequency_hz"]),
        },
        "frame_alignment": {
            "frame_count_estimate": math.ceil(unit_samples / hop),
            "status": "provisional_until_drs_output",
        },
        "confidence": "nominal_schedule",
        "review_status": "needs_manual_stability_review",
    }


def collect_manifest(value: Any) -> tuple[dict[str, dict[str, Any]], dict[str, Any]]:
    if value.get("format") != "vocaloid-traditional-recording-session-plan-v1":
        raise SegmentError("unsupported recording-session manifest format")
    configuration = value.get("configuration")
    articulation = value.get("articulation_takes")
    stationary = value.get("stationary_takes")
    if not isinstance(configuration, dict) or not isinstance(articulation, list) or not isinstance(stationary, list):
        raise SegmentError("recording-session manifest is incomplete")
    takes: dict[str, dict[str, Any]] = {}
    for take in [*articulation, *stationary]:
        if not isinstance(take, dict):
            raise SegmentError("manifest contains a non-object take")
        take_id = take.get("id")
        if not isinstance(take_id, str) or not take_id or take_id in takes:
            raise SegmentError(f"invalid or duplicate take ID: {take_id!r}")
        takes[take_id] = take
    return takes, configuration


def build_plan(
    manifest: Any,
    long_plan: Any,
    graph: Any,
    validation: Any,
    segmentation: dict[str, object],
    source_hashes: dict[str, str],
) -> dict[str, object]:
    takes, session_configuration = collect_manifest(manifest)
    source = manifest.get("source")
    if not isinstance(source, dict):
        raise SegmentError("manifest has no source object")
    art_set = source.get("art_set")
    if art_set not in ("intersection", "union"):
        raise SegmentError("manifest has invalid art_set")
    required, classification = graph_metadata(graph, art_set)
    prompts = parse_long_plan(long_plan, required)
    if source.get("long_prompt_plan_sha256") != source_hashes["long_prompt_plan"]:
        raise SegmentError("manifest and supplied long-prompt plan hashes differ")
    if source.get("graph_sha256") != source_hashes["graph"]:
        raise SegmentError("manifest and supplied graph hashes differ")
    if validation.get("format") != "vocaloid-recording-capture-validation-v1":
        raise SegmentError("unsupported capture-validation report format")
    validation_source = validation.get("source")
    validation_results = validation.get("takes")
    validation_summary = validation.get("summary")
    if (
        not isinstance(validation_source, dict)
        or not isinstance(validation_results, list)
        or not isinstance(validation_summary, dict)
    ):
        raise SegmentError("capture-validation report is incomplete")
    if validation_source.get("manifest_sha256") != source_hashes["manifest"]:
        raise SegmentError("capture-validation report belongs to another manifest")
    passed: dict[str, dict[str, Any]] = {}
    rejected: list[dict[str, object]] = []
    for item in validation_results:
        if not isinstance(item, dict):
            raise SegmentError("capture-validation report contains a non-object take")
        take_id = item.get("id")
        if not isinstance(take_id, str) or take_id not in takes:
            raise SegmentError(f"validation report contains unknown take: {take_id!r}")
        if take_id in passed or any(value["id"] == take_id for value in rejected):
            raise SegmentError(f"validation report repeats take: {take_id}")
        if item.get("status") == "passed":
            digest = item.get("sha256")
            if not isinstance(digest, str) or len(digest) != 64:
                raise SegmentError(f"passed take {take_id} lacks a SHA-256")
            passed[take_id] = item
        else:
            rejected.append(
                {
                    "id": take_id,
                    "status": item.get("status"),
                    "failures": item.get("failures"),
                }
            )
    timing = session_configuration.get("timing")
    capture = session_configuration.get("capture")
    if not isinstance(timing, dict) or not isinstance(capture, dict):
        raise SegmentError("session configuration lacks timing or capture")
    sample_rate = capture.get("sample_rate")
    if sample_rate != 44100:
        raise SegmentError("current segment planner requires 44.1 kHz capture")

    art_candidates: list[dict[str, object]] = []
    sta_candidates: list[dict[str, object]] = []
    occurrence_counts: Counter[Edge] = Counter()
    for take_id in sorted(passed):
        take = takes[take_id]
        result = passed[take_id]
        kind = take.get("kind")
        if kind == "articulation_prompt":
            prompt_id = take.get("prompt_id")
            if not isinstance(prompt_id, str) or prompt_id not in prompts:
                raise SegmentError(
                    f"ART take {take_id} lacks a prompt_id present in the long plan"
                )
            prompt = prompts[prompt_id]
            if take.get("phonemes") != prompt.get("phonemes") or take.get("pinyin") != prompt.get("pinyin"):
                raise SegmentError(f"ART take {take_id} differs from prompt {prompt_id}")
            for scheduled in schedule_prompt(
                prompt,
                timing,
                float(segmentation["two_phoneme_onset_fraction"]),
            ):
                edge = tuple(scheduled["edge"])
                if edge not in required:
                    raise SegmentError(f"scheduled non-ART edge: {edge}")
                candidate = art_candidate(
                    take,
                    result,
                    scheduled,
                    sample_rate,
                    classification,
                    segmentation,
                )
                art_candidates.append(candidate)
                occurrence_counts[edge] += 1
        elif kind == "stationary_prompt":
            sta_candidates.append(
                stationary_candidate(
                    take, result, sample_rate, timing, segmentation
                )
            )
        else:
            raise SegmentError(f"unsupported take kind in manifest: {kind!r}")

    preferred_art: list[dict[str, object]] = []
    grouped_art: dict[tuple[str, str, str], list[dict[str, object]]] = defaultdict(list)
    for candidate in art_candidates:
        source_phoneme, target_phoneme = candidate["edge"]
        grouped_art[(candidate["layer_id"], source_phoneme, target_phoneme)].append(candidate)
    for key in sorted(grouped_art):
        candidates = sorted(
            grouped_art[key],
            key=lambda item: (
                item["repetition"],
                item["prompt_id"],
                item["transition_index"],
                item["take_id"],
            ),
        )
        preferred_art.append(
            {
                "layer_id": key[0],
                "edge": [key[1], key[2]],
                "candidate_id": candidates[0]["id"],
                "alternative_count": len(candidates) - 1,
                "selection_status": "deterministic_placeholder_pending_manual_qa",
            }
        )

    preferred_sta: list[dict[str, object]] = []
    grouped_sta: dict[tuple[str, str], list[dict[str, object]]] = defaultdict(list)
    for candidate in sta_candidates:
        grouped_sta[(candidate["layer_id"], candidate["phoneme"])].append(candidate)
    for key in sorted(grouped_sta):
        candidates = sorted(
            grouped_sta[key], key=lambda item: (item["repetition"], item["take_id"])
        )
        preferred_sta.append(
            {
                "layer_id": key[0],
                "phoneme": key[1],
                "candidate_id": candidates[0]["id"],
                "alternative_count": len(candidates) - 1,
                "selection_status": "deterministic_placeholder_pending_manual_qa",
            }
        )

    layers = manifest.get("layers")
    if not isinstance(layers, list):
        raise SegmentError("manifest has no layers")
    layer_ids = {
        item.get("id") for item in layers if isinstance(item, dict) and isinstance(item.get("id"), str)
    }
    if len(layer_ids) != len(layers) or not layer_ids:
        raise SegmentError("manifest layers contain an invalid or duplicate ID")
    required_layer_pairs = len(required) * len(layer_ids)
    stationary_total = manifest.get("summary", {}).get("stationary_phonemes")
    expected_sta_layer_pairs = (
        stationary_total * len(layer_ids) if isinstance(stationary_total, int) else None
    )
    validation_complete = (
        validation_source.get("selected_take_ids") is None
        and validation_summary.get("complete") is True
        and len(validation_results) == len(takes)
    )
    source_take_ids = sorted(passed)
    candidate_digest = canonical_json_hash(
        {
            "art": [
                {
                    "id": item["id"],
                    "sha256": item["source_wav"]["sha256"],
                    "range": item["extraction"]["source_sample_range"],
                    "builder": item["builder_spec"],
                }
                for item in art_candidates
            ],
            "sta": [
                {
                    "id": item["id"],
                    "sha256": item["source_wav"]["sha256"],
                    "range": item["extraction"]["source_sample_range"],
                    "builder": item["builder_spec"],
                }
                for item in sta_candidates
            ],
        }
    )
    return {
        "format": "vocaloid-recording-segmentation-plan-v1",
        "source": {
            **source_hashes,
            "art_set": art_set,
            "capture_validation_selected_take_ids": validation_source.get(
                "selected_take_ids"
            ),
            "recording_root": validation_source.get("recording_root"),
            "passed_take_ids": source_take_ids,
        },
        "configuration": segmentation,
        "summary": {
            "manifest_takes": len(takes),
            "validation_results": len(validation_results),
            "passed_source_takes": len(passed),
            "rejected_source_takes": len(rejected),
            "capture_validation_complete": validation_complete,
            "articulation_candidates": len(art_candidates),
            "stationary_candidates": len(sta_candidates),
            "distinct_art_edges_in_candidates": len(occurrence_counts),
            "preferred_art_layer_edges": len(preferred_art),
            "required_art_layer_edges": required_layer_pairs,
            "preferred_stationary_layer_phonemes": len(preferred_sta),
            "required_stationary_layer_phonemes": expected_sta_layer_pairs,
            "coverage_complete": (
                validation_complete
                and len(preferred_art) == required_layer_pairs
                and expected_sta_layer_pairs is not None
                and len(preferred_sta) == expected_sta_layer_pairs
                and not rejected
            ),
            "candidate_plan_sha256": candidate_digest,
        },
        "rejected_validation_takes": rejected,
        "preferred_articulation_units": preferred_art,
        "preferred_stationary_units": preferred_sta,
        "articulation_candidates": art_candidates,
        "stationary_candidates": sta_candidates,
        "limitations": [
            "Every boundary is derived from the declared performance schedule, not forced alignment.",
            "Two-phoneme syllable boundaries use a configurable onset fraction and require manual correction.",
            "Preferred candidates are deterministic placeholders, not automatic acoustic approvals.",
            "Frame indices are estimates until DRS returns the actual frame count; the builder must remap seconds using its existing formula.",
            "STA ranges avoid the carrier onset by schedule only and still require stability and pronunciation review.",
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("long_prompt_plan", type=Path)
    parser.add_argument("graph", type=Path)
    parser.add_argument("capture_validation", type=Path)
    parser.add_argument("configuration", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    try:
        paths = {
            "manifest": args.manifest.resolve(),
            "long_prompt_plan": args.long_prompt_plan.resolve(),
            "graph": args.graph.resolve(),
            "capture_validation": args.capture_validation.resolve(),
            "configuration": args.configuration.resolve(),
        }
        output = args.output.resolve()
        if output.exists():
            raise SegmentError(f"output already exists: {output}")
        values = {name: read_json(path) for name, path in paths.items()}
        configuration = parse_configuration(values["configuration"])
        result = build_plan(
            values["manifest"],
            values["long_prompt_plan"],
            values["graph"],
            values["capture_validation"],
            configuration,
            {name: file_sha256(path) for name, path in paths.items()},
        )
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(
            json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
        )
        for name, value in result["summary"].items():
            print(f"{name}={value}")
        print(f"output={output}")
        return 0 if result["summary"]["coverage_complete"] else 3
    except (OSError, UnicodeError, json.JSONDecodeError, SegmentError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
