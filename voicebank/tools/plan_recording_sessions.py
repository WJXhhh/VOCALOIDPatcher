#!/usr/bin/env python3
"""Expand a Chinese long-prompt plan into pitch-layer recording takes."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import sys
from pathlib import Path
from typing import Any


class SessionError(Exception):
    pass


def read_json(path: Path) -> Any:
    if not path.is_file():
        raise SessionError(f"file does not exist: {path}")
    return json.loads(path.read_text(encoding="utf-8"))


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def finite_number(
    value: Any, name: str, *, positive: bool = False, nonnegative: bool = False
) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise SessionError(f"{name} must be numeric")
    result = float(value)
    if not math.isfinite(result):
        raise SessionError(f"{name} must be finite")
    if positive and result <= 0.0:
        raise SessionError(f"{name} must be positive")
    if nonnegative and result < 0.0:
        raise SessionError(f"{name} must be nonnegative")
    return result


def positive_integer(value: Any, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        raise SessionError(f"{name} must be a positive integer")
    return value


def midi_description(midi: float) -> dict[str, float | int | str]:
    nearest = round(midi)
    names = ("C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B")
    cents = (midi - 69.0) * 100.0
    return {
        "midi_note": midi,
        "frequency_hz": 440.0 * (2.0 ** ((midi - 69.0) / 12.0)),
        "cents_from_a4": cents,
        "nearest_note": f"{names[nearest % 12]}{nearest // 12 - 1}",
        "detune_from_nearest_cent": (midi - nearest) * 100.0,
    }


def canonical_json_hash(value: Any) -> str:
    payload = json.dumps(
        value, ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def carrier_score(token: str, phonemes: tuple[str, ...]) -> tuple[object, ...]:
    conventional_zero_initial = token.startswith(("y", "w")) or token in {
        "a",
        "o",
        "e",
        "ai",
        "ei",
        "ao",
        "ou",
        "an",
        "en",
        "ang",
        "eng",
        "er",
    }
    return (
        len(phonemes),
        int(not conventional_zero_initial),
        int(":" in token),
        len(token),
        token,
    )


def inventory_carriers(inventory: Any) -> tuple[dict[str, dict[str, object]], str]:
    if inventory.get("format") != "vocaloid-chinese-g2pa-inventory-v1":
        raise SessionError("unsupported G2PA inventory format")
    entries = inventory.get("entries")
    summary = inventory.get("summary")
    if not isinstance(entries, list) or not isinstance(summary, dict):
        raise SessionError("invalid G2PA inventory structure")
    by_target: dict[str, list[tuple[str, tuple[str, ...]]]] = {}
    for entry in entries:
        if not isinstance(entry, dict) or not entry.get("exact_match"):
            raise SessionError("G2PA inventory contains an unverified entry")
        token = entry.get("token")
        phoneme_text = entry.get("phonemes")
        if not isinstance(token, str) or not isinstance(phoneme_text, str):
            raise SessionError("invalid G2PA token or phonemes")
        phonemes = tuple(phoneme_text.split())
        if not phonemes:
            raise SessionError(f"empty G2PA phoneme sequence for {token}")
        by_target.setdefault(phonemes[-1], []).append((token, phonemes))
    carriers: dict[str, dict[str, object]] = {}
    for target, options in by_target.items():
        token, phonemes = min(options, key=lambda value: carrier_score(*value))
        carriers[target] = {
            "pinyin": token,
            "phonemes": list(phonemes),
            "target_phoneme_index": len(phonemes) - 1,
        }
    inventory_hash = summary.get("inventory_sha256")
    if not isinstance(inventory_hash, str) or not inventory_hash:
        raise SessionError("G2PA inventory has no inventory_sha256")
    return carriers, inventory_hash


def stationary_inventory(graph: Any, art_set: str) -> list[str]:
    try:
        values = graph["aggregate"]["stationary"][art_set]
    except (KeyError, TypeError) as error:
        raise SessionError(
            f"graph has no aggregate.stationary.{art_set}; use --include-keys"
        ) from error
    if not isinstance(values, list) or any(
        not isinstance(value, str) or not value for value in values
    ):
        raise SessionError("invalid stationary inventory")
    if len(values) != len(set(values)):
        raise SessionError("duplicate stationary phoneme")
    return sorted(values)


def graph_edge_sha256(graph: Any, art_set: str) -> str:
    try:
        values = graph["aggregate"]["art"][art_set]
    except (KeyError, TypeError) as error:
        raise SessionError(
            f"graph has no aggregate.art.{art_set}; use --include-keys"
        ) from error
    edges: set[tuple[str, str]] = set()
    if not isinstance(values, list):
        raise SessionError("invalid ART inventory")
    for value in values:
        if (
            not isinstance(value, list)
            or len(value) != 2
            or any(not isinstance(token, str) for token in value)
        ):
            raise SessionError("invalid ART edge")
        edges.add((value[0], value[1]))
    if len(edges) != len(values):
        raise SessionError("duplicate ART edge")
    payload = json.dumps(
        [list(edge) for edge in sorted(edges)],
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def parse_configuration(config: Any) -> dict[str, object]:
    if not isinstance(config, dict) or config.get("schema_version") != 1:
        raise SessionError("configuration must be an object with schema_version 1")
    session_id = config.get("session_id")
    if not isinstance(session_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]+", session_id):
        raise SessionError("session_id must contain only ASCII letters, digits, _ or -")
    layer_count = positive_integer(config.get("layer_count"), "layer_count")
    if layer_count not in (2, 3, 4):
        raise SessionError("layer_count must be 2, 3 or 4")
    center_midi = finite_number(config.get("center_midi"), "center_midi")
    comfortable = config.get("comfortable_midi_range")
    if (
        not isinstance(comfortable, list)
        or len(comfortable) != 2
    ):
        raise SessionError("comfortable_midi_range must contain [minimum, maximum]")
    comfortable_min = finite_number(comfortable[0], "comfortable_midi_range[0]")
    comfortable_max = finite_number(comfortable[1], "comfortable_midi_range[1]")
    if comfortable_min >= comfortable_max:
        raise SessionError("comfortable_midi_range must be increasing")
    timing = config.get("timing")
    capture = config.get("capture")
    qa = config.get("qa")
    if not isinstance(timing, dict) or not isinstance(capture, dict) or not isinstance(qa, dict):
        raise SessionError("configuration requires timing, capture and qa objects")
    parsed_timing = {
        "art_syllable_seconds": finite_number(
            timing.get("art_syllable_seconds"),
            "timing.art_syllable_seconds",
            positive=True,
        ),
        "art_leading_silence_seconds": finite_number(
            timing.get("art_leading_silence_seconds"),
            "timing.art_leading_silence_seconds",
            positive=True,
        ),
        "art_trailing_silence_seconds": finite_number(
            timing.get("art_trailing_silence_seconds"),
            "timing.art_trailing_silence_seconds",
            positive=True,
        ),
        "stationary_sustain_seconds": finite_number(
            timing.get("stationary_sustain_seconds"),
            "timing.stationary_sustain_seconds",
            positive=True,
        ),
        "stationary_leading_silence_seconds": finite_number(
            timing.get("stationary_leading_silence_seconds"),
            "timing.stationary_leading_silence_seconds",
            positive=True,
        ),
        "stationary_trailing_silence_seconds": finite_number(
            timing.get("stationary_trailing_silence_seconds"),
            "timing.stationary_trailing_silence_seconds",
            positive=True,
        ),
        "maximum_prompt_seconds": finite_number(
            timing.get("maximum_prompt_seconds"),
            "timing.maximum_prompt_seconds",
            positive=True,
        ),
    }
    sample_rate = positive_integer(capture.get("sample_rate"), "capture.sample_rate")
    channels = positive_integer(capture.get("channels"), "capture.channels")
    bit_depth = positive_integer(capture.get("bit_depth"), "capture.bit_depth")
    if sample_rate != 44100 or channels != 1 or bit_depth != 16:
        raise SessionError(
            "current DRS/build chain requires 44100 Hz, mono, 16-bit PCM capture"
        )
    repetitions = positive_integer(config.get("repetitions"), "repetitions")
    pitch_tolerance = finite_number(
        qa.get("pitch_tolerance_cents"), "qa.pitch_tolerance_cents", positive=True
    )
    minimum_snr = finite_number(
        qa.get("minimum_snr_db"), "qa.minimum_snr_db", positive=True
    )
    minimum_pitch_correlation = finite_number(
        qa.get("minimum_pitch_correlation"),
        "qa.minimum_pitch_correlation",
        positive=True,
    )
    if minimum_pitch_correlation > 1.0:
        raise SessionError("qa.minimum_pitch_correlation must not exceed 1")
    duration_tolerance = finite_number(
        qa.get("duration_tolerance_seconds"),
        "qa.duration_tolerance_seconds",
        nonnegative=True,
    )
    maximum_peak_dbfs = finite_number(
        qa.get("maximum_peak_dbfs"), "qa.maximum_peak_dbfs"
    )
    if maximum_peak_dbfs > 0.0:
        raise SessionError("qa.maximum_peak_dbfs must not exceed 0")
    minimum_signal_rms_dbfs = finite_number(
        qa.get("minimum_signal_rms_dbfs"), "qa.minimum_signal_rms_dbfs"
    )
    maximum_dc_offset = finite_number(
        qa.get("maximum_dc_offset"), "qa.maximum_dc_offset", positive=True
    )
    if maximum_dc_offset > 1.0:
        raise SessionError("qa.maximum_dc_offset must not exceed 1")
    return {
        "schema_version": 1,
        "session_id": session_id,
        "layer_count": layer_count,
        "center_midi": center_midi,
        "comfortable_midi_range": [comfortable_min, comfortable_max],
        "repetitions": repetitions,
        "timing": parsed_timing,
        "capture": {
            "sample_rate": sample_rate,
            "channels": channels,
            "bit_depth": bit_depth,
        },
        "qa": {
            "pitch_tolerance_cents": pitch_tolerance,
            "minimum_pitch_correlation": minimum_pitch_correlation,
            "minimum_snr_db": minimum_snr,
            "duration_tolerance_seconds": duration_tolerance,
            "maximum_peak_dbfs": maximum_peak_dbfs,
            "minimum_signal_rms_dbfs": minimum_signal_rms_dbfs,
            "maximum_dc_offset": maximum_dc_offset,
            "clipping_allowed": False,
        },
    }


def resolve_layers(
    analysis: Any, configuration: dict[str, object]
) -> tuple[list[dict[str, object]], dict[str, object]]:
    if analysis.get("format") != "vocaloid-traditional-layer-analysis-v1":
        raise SessionError("unsupported reference layer analysis format")
    try:
        template = analysis["summary"]["layer_templates"][
            str(configuration["layer_count"])
        ]
        offsets = template["median_offsets_from_bank_center_semitones"]
    except (KeyError, TypeError) as error:
        raise SessionError("reference analysis has no requested layer template") from error
    if not isinstance(offsets, list) or len(offsets) != configuration["layer_count"]:
        raise SessionError("reference layer template has the wrong length")
    center = float(configuration["center_midi"])
    minimum, maximum = configuration["comfortable_midi_range"]
    layers: list[dict[str, object]] = []
    for index, raw_offset in enumerate(offsets):
        offset = finite_number(raw_offset, f"layer offset {index}")
        midi = center + offset
        if midi < minimum or midi > maximum:
            raise SessionError(
                f"derived layer {index + 1} MIDI {midi:.3f} lies outside "
                f"comfortable range {minimum:.3f}..{maximum:.3f}"
            )
        layers.append(
            {
                "id": f"L{index + 1:02d}",
                "rank_low_to_high": index,
                "offset_from_center_semitones": offset,
                **midi_description(midi),
            }
        )
    return layers, template


def build_plan(
    long_plan: Any,
    graph: Any,
    inventory: Any,
    layer_analysis: Any,
    configuration: dict[str, object],
    source_hashes: dict[str, str],
) -> dict[str, object]:
    if long_plan.get("format") != "vocaloid-chinese-long-prompt-plan-v1":
        raise SessionError("unsupported long-prompt plan format")
    source = long_plan.get("source")
    prompts = long_plan.get("recording_prompts")
    if not isinstance(source, dict) or not isinstance(prompts, list) or not prompts:
        raise SessionError("long-prompt plan is incomplete")
    art_set = source.get("art_set")
    if art_set not in ("intersection", "union"):
        raise SessionError("long-prompt plan has an invalid art_set")
    if source.get("edge_sha256") != graph_edge_sha256(graph, art_set):
        raise SessionError("long-prompt plan and graph ART hashes differ")
    carriers, inventory_hash = inventory_carriers(inventory)
    if source.get("g2pa_inventory_sha256") != inventory_hash:
        raise SessionError("long-prompt plan and G2PA inventory hashes differ")
    stationary = stationary_inventory(graph, art_set)
    missing_carriers = set(stationary) - set(carriers)
    if missing_carriers:
        raise SessionError(f"stationary phonemes lack pinyin carriers: {sorted(missing_carriers)}")
    layers, reference_template = resolve_layers(layer_analysis, configuration)
    timing = configuration["timing"]
    repetitions = int(configuration["repetitions"])
    session_id = str(configuration["session_id"])

    articulation_takes: list[dict[str, object]] = []
    maximum_art_seconds = 0.0
    for layer in layers:
        for prompt in prompts:
            if not isinstance(prompt, dict):
                raise SessionError("long-prompt entry is not an object")
            prompt_id = prompt.get("id")
            pinyin = prompt.get("pinyin")
            phonemes = prompt.get("phonemes")
            syllable_count = prompt.get("syllable_count")
            cross_edges = prompt.get("cross_edges")
            if (
                not isinstance(prompt_id, str)
                or not isinstance(pinyin, list)
                or not isinstance(phonemes, list)
                or isinstance(syllable_count, bool)
                or not isinstance(syllable_count, int)
                or not isinstance(cross_edges, list)
            ):
                raise SessionError("long-prompt entry has an invalid field")
            expected_seconds = (
                float(timing["art_leading_silence_seconds"])
                + syllable_count * float(timing["art_syllable_seconds"])
                + float(timing["art_trailing_silence_seconds"])
            )
            if expected_seconds > float(timing["maximum_prompt_seconds"]) + 1e-12:
                raise SessionError(
                    f"{prompt_id} estimated duration {expected_seconds:.3f}s exceeds "
                    f"maximum_prompt_seconds"
                )
            maximum_art_seconds = max(maximum_art_seconds, expected_seconds)
            for repetition in range(1, repetitions + 1):
                take_id = f"ART_{layer['id']}_{prompt_id}_R{repetition:02d}"
                articulation_takes.append(
                    {
                        "id": take_id,
                        "kind": "articulation_prompt",
                        "layer_id": layer["id"],
                        "target_pitch": {
                            key: layer[key]
                            for key in (
                                "midi_note",
                                "frequency_hz",
                                "cents_from_a4",
                                "nearest_note",
                                "detune_from_nearest_cent",
                            )
                        },
                        "repetition": repetition,
                        "prompt_id": prompt_id,
                        "relative_wav": f"art/{layer['id']}/{prompt_id}_R{repetition:02d}.wav",
                        "pinyin": pinyin,
                        "phonemes": phonemes,
                        "required_cross_edges": cross_edges,
                        "syllable_count": syllable_count,
                        "expected_seconds": expected_seconds,
                        "instruction": "Sing every syllable at the target note with an even, untoned pitch contour.",
                        "provenance": {
                            "status": "pending",
                            "wav_sha256": None,
                            "recorded_utc": None,
                            "performer_id": None,
                            "microphone_chain_id": None,
                            "qa_status": "pending",
                        },
                    }
                )

    stationary_seconds = (
        float(timing["stationary_leading_silence_seconds"])
        + float(timing["stationary_sustain_seconds"])
        + float(timing["stationary_trailing_silence_seconds"])
    )
    stationary_takes: list[dict[str, object]] = []
    for layer in layers:
        for index, phoneme in enumerate(stationary):
            carrier = carriers[phoneme]
            for repetition in range(1, repetitions + 1):
                item_id = f"STA_{layer['id']}_{index + 1:03d}_R{repetition:02d}"
                stationary_takes.append(
                    {
                        "id": item_id,
                        "kind": "stationary_prompt",
                        "layer_id": layer["id"],
                        "target_pitch": {
                            key: layer[key]
                            for key in (
                                "midi_note",
                                "frequency_hz",
                                "cents_from_a4",
                                "nearest_note",
                                "detune_from_nearest_cent",
                            )
                        },
                        "repetition": repetition,
                        "relative_wav": f"sta/{layer['id']}/sta_{index + 1:03d}_R{repetition:02d}.wav",
                        "target_phoneme": phoneme,
                        "carrier_pinyin": carrier["pinyin"],
                        "carrier_phonemes": carrier["phonemes"],
                        "target_phoneme_index": carrier["target_phoneme_index"],
                        "expected_seconds": stationary_seconds,
                        "instruction": "Sustain the target phoneme at the target note; keep the carrier onset short.",
                        "provenance": {
                            "status": "pending",
                            "wav_sha256": None,
                            "recorded_utc": None,
                            "performer_id": None,
                            "microphone_chain_id": None,
                            "qa_status": "pending",
                        },
                    }
                )

    all_takes = [*articulation_takes, *stationary_takes]
    relative_paths = [str(item["relative_wav"]) for item in all_takes]
    if len(relative_paths) != len(set(relative_paths)):
        raise SessionError("recording plan generated duplicate WAV paths")
    total_seconds = sum(float(item["expected_seconds"]) for item in all_takes)
    take_digest = canonical_json_hash(
        [
            {
                "id": item["id"],
                "relative_wav": item["relative_wav"],
                "layer_id": item["layer_id"],
                "expected_seconds": item["expected_seconds"],
            }
            for item in all_takes
        ]
    )
    return {
        "format": "vocaloid-traditional-recording-session-plan-v1",
        "source": {
            "art_set": art_set,
            "long_prompt_plan_sha256": source_hashes["long_prompt_plan"],
            "graph_sha256": source_hashes["graph"],
            "g2pa_inventory_sha256": source_hashes["g2pa_inventory"],
            "reference_layer_analysis_sha256": source_hashes["layer_analysis"],
            "configuration_sha256": source_hashes["configuration"],
            "prompt_plan_sha256": long_plan.get("summary", {}).get(
                "prompt_plan_sha256"
            ),
        },
        "configuration": configuration,
        "layer_template_evidence": reference_template,
        "layers": layers,
        "summary": {
            "session_id": session_id,
            "layers": len(layers),
            "articulation_prompt_templates": len(prompts),
            "stationary_phonemes": len(stationary),
            "repetitions": repetitions,
            "articulation_takes": len(articulation_takes),
            "stationary_takes": len(stationary_takes),
            "total_takes": len(all_takes),
            "maximum_articulation_prompt_seconds": maximum_art_seconds,
            "stationary_prompt_seconds": stationary_seconds,
            "planned_audio_seconds": total_seconds,
            "planned_audio_minutes": total_seconds / 60.0,
            "take_plan_sha256": take_digest,
        },
        "stationary_takes": stationary_takes,
        "articulation_takes": articulation_takes,
        "qa_contract": {
            "wav_format": configuration["capture"],
            "pitch_tolerance_cents": configuration["qa"]["pitch_tolerance_cents"],
            "minimum_pitch_correlation": configuration["qa"][
                "minimum_pitch_correlation"
            ],
            "minimum_snr_db": configuration["qa"]["minimum_snr_db"],
            "duration_tolerance_seconds": configuration["qa"][
                "duration_tolerance_seconds"
            ],
            "maximum_peak_dbfs": configuration["qa"]["maximum_peak_dbfs"],
            "minimum_signal_rms_dbfs": configuration["qa"][
                "minimum_signal_rms_dbfs"
            ],
            "maximum_dc_offset": configuration["qa"]["maximum_dc_offset"],
            "clipping_allowed": False,
            "required_manual_checks": [
                "pronunciation and pinyin order",
                "stable target pitch outside intended consonant/noise intervals",
                "no clipping, dropout, room interruption, or overlapping take",
                "audible breath only in declared boundary silence",
                "outer and inner phoneme boundaries before DRS analysis",
            ],
        },
        "layer_policy": {
            "full_layer_recording": "Record every ART and STA prompt at every selected layer.",
            "silence_to_unvoiced": (
                "Reference products sometimes serialize Sil-to-unvoiced ART as one pitchless "
                "layer. Record all layers conservatively; choose/discard candidates only after QA."
            ),
            "vqm": "Growl/VQM recording is not included in this session plan.",
        },
        "limitations": [
            "The manifest schedules takes but does not create, align, or approve any recording.",
            "The layer template is transposed reference metadata; singer comfort must be established before recording.",
            "Timing values are explicit session choices, not DSE format constants or recovered original prompt timing.",
            "Pinyin chains remain phonologically legal coverage prompts, not natural Mandarin sentences.",
            "The planned duration excludes count-in, retakes beyond configured repetitions, breaks, slate, and engineering time.",
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("long_prompt_plan", type=Path)
    parser.add_argument("graph", type=Path)
    parser.add_argument("g2pa_inventory", type=Path)
    parser.add_argument("layer_analysis", type=Path)
    parser.add_argument("configuration", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    try:
        paths = {
            "long_prompt_plan": args.long_prompt_plan.resolve(),
            "graph": args.graph.resolve(),
            "g2pa_inventory": args.g2pa_inventory.resolve(),
            "layer_analysis": args.layer_analysis.resolve(),
            "configuration": args.configuration.resolve(),
        }
        output = args.output.resolve()
        if output.exists():
            raise SessionError(f"output already exists: {output}")
        values = {name: read_json(path) for name, path in paths.items()}
        configuration = parse_configuration(values["configuration"])
        result = build_plan(
            values["long_prompt_plan"],
            values["graph"],
            values["g2pa_inventory"],
            values["layer_analysis"],
            configuration,
            {name: file_sha256(path) for name, path in paths.items()},
        )
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(
            json.dumps(result, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        for name, value in result["summary"].items():
            print(f"{name}={value}")
        print(f"output={output}")
        return 0
    except (OSError, UnicodeError, json.JSONDecodeError, SessionError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
