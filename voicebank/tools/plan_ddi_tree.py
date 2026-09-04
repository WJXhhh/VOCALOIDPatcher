#!/usr/bin/env python3
"""Bind a validated multi-unit DDB to an explicit PHDC/STA/ART tree order."""

from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path
from typing import Any

import analyze_recording_units as analysis_tools
import assemble_recording_units_ddb as ddb_tools


class PlanError(Exception):
    pass


def string_list(value: Any, context: str) -> list[str]:
    if (
        not isinstance(value, list)
        or any(not isinstance(item, str) or not item for item in value)
        or len(value) != len(set(value))
    ):
        raise PlanError(f"{context} must be a unique non-empty string list")
    return value


def edge_list(value: Any, context: str) -> list[tuple[str, str]]:
    if not isinstance(value, list):
        raise PlanError(f"{context} must be a list")
    result: list[tuple[str, str]] = []
    for item in value:
        if (
            not isinstance(item, list)
            or len(item) != 2
            or any(not isinstance(token, str) or not token for token in item)
        ):
            raise PlanError(f"{context} contains an invalid edge")
        result.append((item[0], item[1]))
    if len(result) != len(set(result)):
        raise PlanError(f"{context} contains duplicate edges")
    return result


def load_graph(path: Path) -> dict[str, Any]:
    value = analysis_tools.read_json(path)
    if not isinstance(value, dict):
        raise PlanError("reference graph is not an object")
    aggregate = value.get("aggregate")
    if not isinstance(aggregate, dict):
        raise PlanError("reference graph has no aggregate")
    phonemes = aggregate.get("phonemes")
    stationary = aggregate.get("stationary")
    art = aggregate.get("art")
    if not all(isinstance(item, dict) for item in (phonemes, stationary, art)):
        raise PlanError("reference graph lacks phoneme/STA/ART aggregates")
    inventory = string_list(phonemes.get("intersection"), "phoneme intersection")
    voiced = set(
        string_list(phonemes.get("voiced_intersection"), "voiced intersection")
    )
    unvoiced = set(
        string_list(phonemes.get("unvoiced_intersection"), "unvoiced intersection")
    )
    if voiced & unvoiced or voiced | unvoiced != set(inventory):
        raise PlanError("voiced/unvoiced sets do not partition the phoneme inventory")
    stationary_inventory = string_list(
        stationary.get("intersection"), "stationary intersection"
    )
    edges = edge_list(art.get("intersection"), "ART intersection")
    if not set(stationary_inventory) <= set(inventory):
        raise PlanError("stationary inventory is outside PHDC")
    if any(source not in inventory or target not in inventory for source, target in edges):
        raise PlanError("ART intersection contains a phoneme outside PHDC")
    return {
        "phonemes": inventory,
        "voiced": voiced,
        "unvoiced": unvoiced,
        "stationary": stationary_inventory,
        "edges": edges,
    }


def load_ddb_manifest(path: Path) -> tuple[dict[str, Any], list[dict[str, Any]], bool]:
    value = analysis_tools.read_json(path)
    if not isinstance(value, dict) or value.get("format") != (
        "vocaloid-recording-units-ddb-v1"
    ):
        raise PlanError("unsupported DDB manifest format")
    output = value.get("output")
    summary = value.get("summary")
    units = value.get("units")
    if (
        not isinstance(output, dict)
        or not isinstance(summary, dict)
        or not isinstance(units, list)
        or any(not isinstance(item, dict) for item in units)
    ):
        raise PlanError("DDB manifest lacks output, summary, or units")
    ddb_sha = output.get("ddb_sha256")
    if not isinstance(ddb_sha, str):
        raise PlanError("DDB manifest has no output SHA-256")
    if (
        summary.get("unit_count") != len(units)
        or summary.get("articulation_units")
        != sum(item.get("kind") == "articulation" for item in units)
        or summary.get("stationary_units")
        != sum(item.get("kind") == "stationary" for item in units)
        or summary.get("approval_complete") is not False
    ):
        raise PlanError("DDB summary counts or approval flag differ")
    if summary.get("ddb_manifest_sha256") != ddb_tools.canonical_manifest_digest(
        ddb_sha, units
    ):
        raise PlanError("DDB manifest canonical SHA-256 differs")
    ids = [item.get("unit_id") for item in units]
    if any(not isinstance(item, str) or not item for item in ids) or len(ids) != len(
        set(ids)
    ):
        raise PlanError("DDB manifest has invalid or duplicate unit IDs")
    return value, units, summary.get("coverage_complete") is True


def plan(graph: dict[str, Any], units: list[dict[str, Any]]) -> dict[str, Any]:
    phoneme_set = set(graph["phonemes"])
    stationary_set = set(graph["stationary"])
    edge_set = set(graph["edges"])
    stationary_units: dict[str, list[dict[str, Any]]] = defaultdict(list)
    articulation_units: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    seen: set[str] = set()
    for item in units:
        unit_id = item["unit_id"]
        if item.get("approval_status") != "unapproved_ddb_unit":
            raise PlanError(f"unit {unit_id} has an unexpected approval status")
        layer = item.get("layer_id")
        if not isinstance(layer, str) or not layer:
            raise PlanError(f"unit {unit_id} has no layer ID")
        if item.get("kind") == "stationary":
            phoneme = item.get("phoneme")
            if phoneme not in stationary_set:
                raise PlanError(f"unit {unit_id} STA phoneme is outside the graph")
            stationary_units[phoneme].append(item)
        elif item.get("kind") == "articulation":
            edge = item.get("edge")
            if not isinstance(edge, list) or len(edge) != 2:
                raise PlanError(f"unit {unit_id} has no ART edge")
            key = edge[0], edge[1]
            if key not in edge_set:
                raise PlanError(f"unit {unit_id} ART edge is outside the graph")
            voicing = item.get("voicing")
            expected = {
                "source": "unvoiced" if key[0] in graph["unvoiced"] else "voiced",
                "target": "unvoiced" if key[1] in graph["unvoiced"] else "voiced",
            }
            if voicing != expected:
                raise PlanError(f"unit {unit_id} voicing differs from PHDC")
            articulation_units[key].append(item)
        else:
            raise PlanError(f"unit {unit_id} has an invalid kind")
        if unit_id in seen:
            raise PlanError(f"duplicate unit ID {unit_id}")
        seen.add(unit_id)

    stationary_plan: list[dict[str, Any]] = []
    stationary_order: list[str] = []
    for stationary_index, phoneme in enumerate(graph["stationary"]):
        grouped = stationary_units.get(phoneme, [])
        if not grouped:
            continue
        unit_ids = [item["unit_id"] for item in grouped]
        stationary_plan.append(
            {
                "phoneme": phoneme,
                "stationary_index": stationary_index,
                "unit_ids": unit_ids,
            }
        )
        stationary_order.extend(unit_ids)

    articulation_plan: list[dict[str, Any]] = []
    articulation_order: list[str] = []
    phoneme_indexes = {name: index for index, name in enumerate(graph["phonemes"])}
    for edge in graph["edges"]:
        grouped = articulation_units.get(edge, [])
        if not grouped:
            continue
        unit_ids = [item["unit_id"] for item in grouped]
        snd_source_offsets: list[int] = []
        epr_source_offsets: list[int] = []
        source_offset = 0x6C
        for item in grouped:
            snd_source_offsets.append(source_offset)
            try:
                frame_bytes = item["snd_chunk_offset"] - item["base_offset"]
                epr_source_offsets.append(source_offset + item["snd_chunk_size"] + 7)
                source_offset += item["snd_chunk_size"] + 7 + frame_bytes
            except (KeyError, TypeError) as error:
                raise PlanError(
                    f"unit {item['unit_id']} lacks virtual source-unit sizes"
                ) from error
        articulation_plan.append(
            {
                "source": edge[0],
                "target": edge[1],
                "target_index": phoneme_indexes[edge[1]],
                "unit_ids": unit_ids,
                "snd_source_offsets": snd_source_offsets,
                "epr_source_offsets": epr_source_offsets,
            }
        )
        articulation_order.extend(unit_ids)

    ordered = [*stationary_order, *articulation_order]
    if len(ordered) != len(units) or set(ordered) != seen:
        raise PlanError("tree order does not account for every DDB unit exactly once")
    phoneme_plan = [
        {"name": name, "unvoiced": name in graph["unvoiced"]}
        for name in graph["phonemes"]
    ]
    for item in phoneme_plan:
        try:
            encoded = item["name"].encode("ascii")
        except UnicodeEncodeError as error:
            raise PlanError(f"phoneme is not ASCII: {item['name']!r}") from error
        if not 1 <= len(encoded) <= 16:
            raise PlanError(f"phoneme exceeds the native PHDC name limit: {item['name']!r}")
    return {
        "language_id": 4,
        "phonemes": phoneme_plan,
        "stationary": stationary_plan,
        "articulations": articulation_plan,
        "part_order": {
            "stationary_unit_ids": stationary_order,
            "articulation_unit_ids": articulation_order,
        },
    }


def build(graph_path: Path, ddb_manifest_path: Path, output_path: Path) -> dict[str, Any]:
    if output_path.exists():
        raise PlanError(f"output already exists: {output_path}")
    graph = load_graph(graph_path)
    ddb_manifest, units, coverage_complete = load_ddb_manifest(ddb_manifest_path)
    content = plan(graph, units)
    plan_digest = analysis_tools.canonical_json_hash(content)
    result = {
        "format": "vocaloid-ddi-tree-plan-v1",
        "source": {
            "reference_graph_sha256": analysis_tools.file_sha256(graph_path),
            "ddb_manifest_file_sha256": analysis_tools.file_sha256(ddb_manifest_path),
            "ddb_manifest_canonical_sha256": ddb_manifest["summary"][
                "ddb_manifest_sha256"
            ],
            "ddb_sha256": ddb_manifest["output"]["ddb_sha256"],
        },
        "summary": {
            "phonemes": len(content["phonemes"]),
            "stationary_phonemes": len(content["stationary"]),
            "stationary_parts": len(content["part_order"]["stationary_unit_ids"]),
            "articulation_edges": len(content["articulations"]),
            "articulation_parts": len(
                content["part_order"]["articulation_unit_ids"]
            ),
            "coverage_complete": coverage_complete,
            "approval_complete": False,
            "tree_plan_sha256": plan_digest,
        },
        **content,
        "limitations": [
            "The plan fixes native tree and finalizer order but does not approve any acoustic unit.",
            "A partial DDB intentionally produces a partial STA/ART tree while retaining the full PHDC inventory.",
            "Tree construction and DDI injection must independently verify this plan and the DDB manifest.",
        ],
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    analysis_tools.write_json_atomic(output_path, result)
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reference_graph", type=Path)
    parser.add_argument("ddb_manifest", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    try:
        result = build(
            args.reference_graph.resolve(),
            args.ddb_manifest.resolve(),
            args.output.resolve(),
        )
        for name, value in result["summary"].items():
            print(f"{name}={value}")
        print(f"output={args.output.resolve()}")
        return 0 if result["summary"]["coverage_complete"] else 3
    except (
        OSError,
        UnicodeError,
        json.JSONDecodeError,
        ValueError,
        PlanError,
        analysis_tools.AnalysisError,
    ) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
