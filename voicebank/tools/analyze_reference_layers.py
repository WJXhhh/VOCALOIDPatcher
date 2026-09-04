#!/usr/bin/env python3
"""Aggregate pitch-layer and duration metadata from traditional DDI files."""

from __future__ import annotations

import argparse
import hashlib
import importlib
import json
import math
import statistics
import sys
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Iterable, Iterator


class LayerError(Exception):
    pass


def parse_bank(value: str) -> tuple[str, Path]:
    if "=" not in value:
        raise argparse.ArgumentTypeError("bank must be NAME=DDI_PATH")
    name, raw_path = value.split("=", 1)
    if not name or not raw_path:
        raise argparse.ArgumentTypeError("bank must be NAME=DDI_PATH")
    return name, Path(raw_path)


def percentile(values: list[float], fraction: float) -> float:
    if not values:
        raise LayerError("cannot summarize an empty numeric sequence")
    ordered = sorted(values)
    position = (len(ordered) - 1) * fraction
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return ordered[lower]
    weight = position - lower
    return ordered[lower] * (1.0 - weight) + ordered[upper] * weight


def numeric_summary(values: Iterable[float]) -> dict[str, float | int]:
    materialized = [float(value) for value in values]
    if not materialized or any(not math.isfinite(value) for value in materialized):
        raise LayerError("numeric summary received no values or a non-finite value")
    return {
        "count": len(materialized),
        "minimum": min(materialized),
        "p05": percentile(materialized, 0.05),
        "median": statistics.median(materialized),
        "mean": statistics.fmean(materialized),
        "p95": percentile(materialized, 0.95),
        "maximum": max(materialized),
    }


def cents_description(cents: float) -> dict[str, float | int | str]:
    midi = 69.0 + cents / 100.0
    nearest = round(midi)
    note_names = ("C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B")
    return {
        "cents_from_a4": cents,
        "frequency_hz": 440.0 * (2.0 ** (cents / 1200.0)),
        "midi_note": midi,
        "nearest_note": f"{note_names[nearest % 12]}{nearest // 12 - 1}",
        "detune_from_nearest_cent": (midi - nearest) * 100.0,
    }


def finite_sample_number(sample: dict[str, Any], name: str, context: str) -> float:
    value = sample.get(name)
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise LayerError(f"{context}.{name} is not numeric")
    result = float(value)
    if not math.isfinite(result):
        raise LayerError(f"{context}.{name} is not finite")
    return result


def walk_art(
    node: dict[str, Any], prefix: tuple[str, ...] = ()
) -> Iterator[tuple[tuple[str, ...], list[dict[str, Any]]]]:
    phoneme = node.get("phoneme")
    if not isinstance(phoneme, str) or not phoneme:
        raise LayerError("ART node has no phoneme")
    path = (*prefix, phoneme)
    units = node.get("artu", {})
    if not isinstance(units, dict):
        raise LayerError("ARTu mapping is not an object")
    for unit in units.values():
        if not isinstance(unit, dict):
            raise LayerError("ARTu entry is not an object")
        target = unit.get("phoneme")
        parts = unit.get("artp")
        if not isinstance(target, str) or not isinstance(parts, dict):
            raise LayerError("ARTu entry is missing phoneme or ARTp")
        samples = list(parts.values())
        if any(not isinstance(sample, dict) for sample in samples):
            raise LayerError("ARTp entry is not an object")
        yield (*path, target), samples
    children = node.get("art", {})
    if not isinstance(children, dict):
        raise LayerError("nested ART mapping is not an object")
    for child in children.values():
        if not isinstance(child, dict):
            raise LayerError("nested ART entry is not an object")
        yield from walk_art(child, path)


def layer_summaries(
    values: dict[tuple[str, ...], list[dict[str, Any]]],
    expected_layers: int,
    label: str,
) -> list[dict[str, object]]:
    ranked: list[list[dict[str, Any]]] = [[] for _ in range(expected_layers)]
    full = 0
    for key, samples in values.items():
        if len(samples) != expected_layers:
            continue
        ordered = sorted(
            samples,
            key=lambda sample: finite_sample_number(sample, "pitch1", f"{label}.{key}"),
        )
        for index, sample in enumerate(ordered):
            ranked[index].append(sample)
        full += 1
    if not full:
        raise LayerError(f"{label} has no full-layer keys")
    result: list[dict[str, object]] = []
    for index, samples in enumerate(ranked):
        pitch1 = [
            finite_sample_number(sample, "pitch1", f"{label}.rank{index}")
            for sample in samples
        ]
        pitch2 = [
            finite_sample_number(sample, "pitch2", f"{label}.rank{index}")
            for sample in samples
        ]
        durations = [
            finite_sample_number(sample, "duration", f"{label}.rank{index}")
            for sample in samples
        ]
        pitch1_stats = numeric_summary(pitch1)
        median_pitch = float(pitch1_stats["median"])
        result.append(
            {
                "rank_low_to_high": index,
                "keys": len(samples),
                "pitch1_cents": pitch1_stats,
                "pitch1_median_note": cents_description(median_pitch),
                "pitch2_cents": numeric_summary(pitch2),
                "pitch2_minus_pitch1_cents": numeric_summary(
                    later - earlier for earlier, later in zip(pitch1, pitch2)
                ),
                "duration_seconds": numeric_summary(durations),
            }
        )
    return result


def distinct_floats(values: Iterable[float]) -> list[dict[str, float | int]]:
    rounded = Counter(round(float(value), 7) for value in values)
    return [
        {"value": value, "count": count} for value, count in sorted(rounded.items())
    ]


def is_no_pitch_sentinel(value: float) -> bool:
    return value <= -3.0e38


def analyze_bank(name: str, path: Path, model_type: type[Any]) -> dict[str, object]:
    resolved = path.resolve()
    if not resolved.is_file():
        raise LayerError(f"DDI not found: {resolved}")
    ddi_bytes = resolved.read_bytes()
    model = model_type(ddi_bytes)
    model.read()
    phoneme_data = model.phdc_data.get("phoneme", {})
    voiced = set(phoneme_data.get("voiced", []))
    unvoiced = set(phoneme_data.get("unvoiced", []))

    stationary: dict[tuple[str, ...], list[dict[str, Any]]] = {}
    for unit in model.sta_data.values():
        phoneme = unit.get("phoneme")
        parts = unit.get("stap")
        if not isinstance(phoneme, str) or not isinstance(parts, dict):
            raise LayerError("invalid STAu entry")
        stationary[(phoneme,)] = list(parts.values())
    if not stationary:
        raise LayerError(f"{name} has no stationary data")
    sta_layer_histogram = Counter(len(samples) for samples in stationary.values())
    if len(sta_layer_histogram) != 1:
        raise LayerError(f"{name} STA layer counts are not uniform")
    main_layers = next(iter(sta_layer_histogram))

    articulation: dict[tuple[str, ...], list[dict[str, Any]]] = {}
    for root in model.art_data.values():
        if not isinstance(root, dict):
            raise LayerError("invalid ART root")
        for key, samples in walk_art(root):
            if key in articulation:
                raise LayerError(f"duplicate ART key in {name}: {key}")
            articulation[key] = samples
    art_layer_histogram = Counter(len(samples) for samples in articulation.values())
    full_art = {
        key: samples
        for key, samples in articulation.items()
        if len(samples) == main_layers
    }
    exceptional_art = {
        key: samples
        for key, samples in articulation.items()
        if len(samples) != main_layers
    }

    all_sta_samples = [sample for values in stationary.values() for sample in values]
    all_art_samples = [sample for values in articulation.values() for sample in values]
    exceptional_art_samples = [
        sample for values in exceptional_art.values() for sample in values
    ]
    exceptional_pitch1 = [
        finite_sample_number(sample, "pitch1", "exceptional_ART")
        for sample in exceptional_art_samples
    ]
    exceptional_pitch2 = [
        finite_sample_number(sample, "pitch2", "exceptional_ART")
        for sample in exceptional_art_samples
    ]
    vqm_samples = (
        list(model.vqm_data.values()) if isinstance(model.vqm_data, dict) else []
    )
    art_duration_by_voice: dict[str, list[float]] = defaultdict(list)
    for key, samples in articulation.items():
        if len(key) != 2:
            role = f"arity_{len(key)}"
        else:
            source = "voiced" if key[0] in voiced else "unvoiced"
            target = "voiced" if key[1] in voiced else "unvoiced"
            role = f"{source}_to_{target}"
        art_duration_by_voice[role].extend(
            finite_sample_number(sample, "duration", f"ART.{key}")
            for sample in samples
        )

    sta_layers = layer_summaries(stationary, main_layers, "STA")
    art_layers = layer_summaries(full_art, main_layers, "ART")
    sta_medians = [
        float(value["pitch1_cents"]["median"]) for value in sta_layers
    ]
    art_medians = [
        float(value["pitch1_cents"]["median"]) for value in art_layers
    ]
    center = statistics.fmean(sta_medians)
    return {
        "name": name,
        "ddi_bytes": resolved.stat().st_size,
        "ddi_sha256": hashlib.sha256(ddi_bytes).hexdigest(),
        "phonemes": len(voiced | unvoiced),
        "main_layers": main_layers,
        "stationary_keys": len(stationary),
        "articulation_keys": len(articulation),
        "full_articulation_keys": len(full_art),
        "exceptional_art_key_count": len(exceptional_art),
        "stationary_layer_histogram": {
            str(key): value for key, value in sorted(sta_layer_histogram.items())
        },
        "articulation_layer_histogram": {
            str(key): value for key, value in sorted(art_layer_histogram.items())
        },
        "exceptional_art_keys": [list(key) for key in sorted(exceptional_art)],
        "exceptional_art_all_silence_to_unvoiced": all(
            len(key) == 2 and key[0] == "Sil" and key[1] in unvoiced
            for key in exceptional_art
        ),
        "exceptional_art_pitch": {
            "pitch1_no_pitch_sentinel_count": sum(
                is_no_pitch_sentinel(value) for value in exceptional_pitch1
            ),
            "pitch2_no_pitch_sentinel_count": sum(
                is_no_pitch_sentinel(value) for value in exceptional_pitch2
            ),
            "all_pitch1_no_pitch_sentinel": all(
                is_no_pitch_sentinel(value) for value in exceptional_pitch1
            ),
            "all_pitch2_no_pitch_sentinel": all(
                is_no_pitch_sentinel(value) for value in exceptional_pitch2
            ),
            "pitch1_raw_values": distinct_floats(exceptional_pitch1),
            "pitch2_raw_values": distinct_floats(exceptional_pitch2),
        },
        "exceptional_art_duration_seconds": numeric_summary(
            finite_sample_number(sample, "duration", "exceptional_ART")
            for sample in exceptional_art_samples
        ),
        "exceptional_art_duration_values": distinct_floats(
            finite_sample_number(sample, "duration", "exceptional_ART")
            for sample in exceptional_art_samples
        ),
        "stationary_layers": sta_layers,
        "full_articulation_layers": art_layers,
        "art_minus_sta_median_pitch_cents": [
            art_value - sta_value
            for sta_value, art_value in zip(sta_medians, art_medians)
        ],
        "stationary_template_offsets_cents": [
            value - center for value in sta_medians
        ],
        "stationary_adjacent_intervals_cents": [
            later - earlier for earlier, later in zip(sta_medians, sta_medians[1:])
        ],
        "stationary_duration_seconds": numeric_summary(
            finite_sample_number(sample, "duration", "STA")
            for sample in all_sta_samples
        ),
        "articulation_duration_seconds": numeric_summary(
            finite_sample_number(sample, "duration", "ART")
            for sample in all_art_samples
        ),
        "articulation_duration_by_voice": {
            role: numeric_summary(values)
            for role, values in sorted(art_duration_by_voice.items())
        },
        "dynamics": distinct_floats(
            finite_sample_number(sample, "dynamics", "sample")
            for sample in (*all_sta_samples, *all_art_samples)
        ),
        "tempo": distinct_floats(
            finite_sample_number(sample, "tempo", "sample")
            for sample in (*all_sta_samples, *all_art_samples)
        ),
        "unknown2": distinct_floats(
            finite_sample_number(sample, "unknown2", "sample")
            for sample in (*all_sta_samples, *all_art_samples)
        ),
        "vqm_samples": len(vqm_samples),
        "vqm_pitch1_cents": numeric_summary(
            finite_sample_number(sample, "pitch1", "VQM") for sample in vqm_samples
        )
        if vqm_samples
        else None,
        "vqm_duration_seconds": numeric_summary(
            finite_sample_number(sample, "duration", "VQM") for sample in vqm_samples
        )
        if vqm_samples
        else None,
    }


def aggregate_templates(banks: list[dict[str, object]]) -> dict[str, object]:
    by_count: dict[int, list[dict[str, object]]] = defaultdict(list)
    for bank in banks:
        by_count[int(bank["main_layers"])].append(bank)
    templates: dict[str, object] = {}
    for layer_count, values in sorted(by_count.items()):
        offsets_by_rank = [
            [float(bank["stationary_template_offsets_cents"][rank]) for bank in values]
            for rank in range(layer_count)
        ]
        intervals_by_rank = [
            [float(bank["stationary_adjacent_intervals_cents"][rank]) for bank in values]
            for rank in range(layer_count - 1)
        ]
        median_offsets = [statistics.median(rank_values) for rank_values in offsets_by_rank]
        templates[str(layer_count)] = {
            "bank_count": len(values),
            "bank_names": [str(bank["name"]) for bank in values],
            "median_offsets_from_bank_center_cents": median_offsets,
            "median_offsets_from_bank_center_semitones": [
                value / 100.0 for value in median_offsets
            ],
            "offset_distributions_cents": [
                numeric_summary(rank_values) for rank_values in offsets_by_rank
            ],
            "adjacent_interval_distributions_cents": [
                numeric_summary(rank_values) for rank_values in intervals_by_rank
            ],
        }
    return templates


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--ddb-tools",
        type=Path,
        required=True,
        help="directory containing utils/ddi_utils.py from ddb-tools",
    )
    parser.add_argument("--bank", type=parse_bank, action="append", required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    try:
        dependency = args.ddb_tools.resolve()
        if not (dependency / "utils" / "ddi_utils.py").is_file():
            raise LayerError(f"ddb-tools ddi_utils.py not found below {dependency}")
        sys.path.insert(0, str(dependency))
        model_type = importlib.import_module("utils.ddi_utils").DDIModel
        seen: set[str] = set()
        banks: list[dict[str, object]] = []
        for name, path in args.bank:
            if name in seen:
                raise LayerError(f"duplicate bank name: {name}")
            seen.add(name)
            banks.append(analyze_bank(name, path, model_type))
        result = {
            "format": "vocaloid-traditional-layer-analysis-v1",
            "source": {
                "ddb_tools_required_file": "utils/ddi_utils.py",
                "bank_count": len(banks),
                "reads_ddb": False,
            },
            "summary": {
                "main_layer_histogram": {
                    str(key): value
                    for key, value in sorted(
                        Counter(int(bank["main_layers"]) for bank in banks).items()
                    )
                },
                "total_stationary_samples": sum(
                    int(bank["stationary_duration_seconds"]["count"])
                    for bank in banks
                ),
                "total_articulation_samples": sum(
                    int(bank["articulation_duration_seconds"]["count"])
                    for bank in banks
                ),
                "layer_templates": aggregate_templates(banks),
            },
            "banks": banks,
            "limitations": [
                "This reads DDI metadata only; it does not read or export commercial DDB audio or FRM2 data.",
                "Pitch templates describe installed reference products and must be transposed to a new singer's comfortable range.",
                "Serialized sample duration is not the duration of an original recording prompt or session.",
                "Layer-count evidence does not prove that every singer should record four layers.",
            ],
        }
        text = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
        if args.output:
            output = args.output.resolve()
            if output.exists():
                raise LayerError(f"output already exists: {output}")
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(text, encoding="utf-8")
            print(f"banks={len(banks)}")
            print(f"sta_samples={result['summary']['total_stationary_samples']}")
            print(f"art_samples={result['summary']['total_articulation_samples']}")
            print(f"output={output}")
        else:
            sys.stdout.write(text)
        return 0
    except (OSError, UnicodeError, json.JSONDecodeError, LayerError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
