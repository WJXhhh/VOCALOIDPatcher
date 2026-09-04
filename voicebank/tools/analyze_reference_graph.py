#!/usr/bin/env python3
"""Aggregate phoneme and ART-key coverage from traditional DDI files."""

from __future__ import annotations

import argparse
import importlib
import json
import sys
from collections import Counter
from pathlib import Path
from typing import Iterable


class GraphError(Exception):
    pass


def parse_bank(value: str) -> tuple[str, Path]:
    if "=" not in value:
        raise argparse.ArgumentTypeError("bank must be NAME=DDI_PATH")
    name, raw_path = value.split("=", 1)
    if not name or not raw_path:
        raise argparse.ArgumentTypeError("bank must be NAME=DDI_PATH")
    return name, Path(raw_path)


def count_by_arity(keys: Iterable[tuple[str, ...]]) -> dict[str, int]:
    counts = Counter(len(key) for key in keys)
    return {str(arity): count for arity, count in sorted(counts.items())}


def strongly_connected_components(
    nodes: set[str], edges: set[tuple[str, str]]
) -> list[list[str]]:
    adjacency: dict[str, list[str]] = {node: [] for node in nodes}
    for source, target in edges:
        adjacency.setdefault(source, []).append(target)

    index = 0
    indexes: dict[str, int] = {}
    lowlinks: dict[str, int] = {}
    stack: list[str] = []
    on_stack: set[str] = set()
    components: list[list[str]] = []

    def visit(node: str) -> None:
        nonlocal index
        indexes[node] = index
        lowlinks[node] = index
        index += 1
        stack.append(node)
        on_stack.add(node)
        for target in adjacency.get(node, []):
            if target not in indexes:
                visit(target)
                lowlinks[node] = min(lowlinks[node], lowlinks[target])
            elif target in on_stack:
                lowlinks[node] = min(lowlinks[node], indexes[target])
        if lowlinks[node] == indexes[node]:
            component: list[str] = []
            while True:
                current = stack.pop()
                on_stack.remove(current)
                component.append(current)
                if current == node:
                    break
            components.append(sorted(component))

    for node in sorted(nodes):
        if node not in indexes:
            visit(node)
    return sorted(components, key=lambda value: (-len(value), value))


def graph_summary(edges: set[tuple[str, str]]) -> dict[str, object]:
    nodes = {value for edge in edges for value in edge}
    components = strongly_connected_components(nodes, edges)
    indegree = Counter(target for _, target in edges)
    outdegree = Counter(source for source, _ in edges)
    isolated_direction = sorted(
        node for node in nodes if indegree[node] == 0 or outdegree[node] == 0
    )
    reciprocal = sum(1 for edge in edges if (edge[1], edge[0]) in edges)
    positive_imbalance = sum(
        max(0, outdegree[node] - indegree[node]) for node in nodes
    )
    return {
        "nodes": len(nodes),
        "edges": len(edges),
        "strongly_connected_components": len(components),
        "largest_component_nodes": len(components[0]) if components else 0,
        "nodes_without_both_directions": isolated_direction,
        "maximum_indegree": max(indegree.values(), default=0),
        "maximum_outdegree": max(outdegree.values(), default=0),
        "indegree_histogram": {
            str(degree): count
            for degree, count in sorted(Counter(indegree.values()).items())
        },
        "outdegree_histogram": {
            str(degree): count
            for degree, count in sorted(Counter(outdegree.values()).items())
        },
        "self_edges": sum(1 for source, target in edges if source == target),
        "edges_with_reverse_present": reciprocal,
        "minimum_edge_disjoint_trails": max(1, positive_imbalance) if edges else 0,
        "minimum_tokens_across_trails": (
            len(edges) + max(1, positive_imbalance) if edges else 0
        ),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--ddb-tools",
        type=Path,
        required=True,
        help="directory containing utils/ddi_utils.py from ddb-tools",
    )
    parser.add_argument("--bank", type=parse_bank, action="append", required=True)
    parser.add_argument("--include-keys", action="store_true")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    try:
        dependency = args.ddb_tools.resolve()
        if not (dependency / "utils" / "ddi_utils.py").is_file():
            raise GraphError(f"ddb-tools ddi_utils.py not found below {dependency}")
        sys.path.insert(0, str(dependency))
        ddi_model_type = importlib.import_module("utils.ddi_utils").DDIModel

        banks: list[dict[str, object]] = []
        phoneme_sets: list[set[str]] = []
        voiced_sets: list[set[str]] = []
        unvoiced_sets: list[set[str]] = []
        sta_sets: list[set[str]] = []
        art_sets: list[set[tuple[str, ...]]] = []
        seen_names: set[str] = set()
        for name, raw_path in args.bank:
            if name in seen_names:
                raise GraphError(f"duplicate bank name: {name}")
            seen_names.add(name)
            path = raw_path.resolve()
            if not path.is_file():
                raise GraphError(f"DDI not found: {path}")
            model = ddi_model_type(path.read_bytes())
            model.read()
            phoneme_data = model.phdc_data.get("phoneme", {})
            voiced = set(phoneme_data.get("voiced", []))
            unvoiced = set(phoneme_data.get("unvoiced", []))
            phonemes = voiced | unvoiced
            sta = set(model.ddi_data_dict.get("sta", {}))
            art_mapping = model.ddi_data_dict.get("art", {})
            art = {tuple(key.split(" ")) for key in art_mapping}
            sta_samples = sum(len(parts) for parts in model.ddi_data_dict.get("sta", {}).values())
            art_samples = sum(len(parts) for parts in art_mapping.values())
            stationary_layers = Counter(
                len(parts) for parts in model.ddi_data_dict.get("sta", {}).values()
            )
            articulation_layers = Counter(len(parts) for parts in art_mapping.values())
            item: dict[str, object] = {
                "name": name,
                "ddi_bytes": path.stat().st_size,
                "phonemes": len(phonemes),
                "voiced_phonemes": len(voiced),
                "unvoiced_phonemes": len(unvoiced),
                "stationary_keys": len(sta),
                "stationary_samples": sta_samples,
                "stationary_layer_histogram": {
                    str(layers): keys for layers, keys in sorted(stationary_layers.items())
                },
                "art_keys": len(art),
                "art_keys_by_arity": count_by_arity(art),
                "art_samples": art_samples,
                "art_layer_histogram": {
                    str(layers): keys for layers, keys in sorted(articulation_layers.items())
                },
                "single_layer_art_keys": [
                    key.split(" ")
                    for key, parts in sorted(art_mapping.items())
                    if len(parts) == 1
                ],
                "diphone_graph": graph_summary(
                    {(key[0], key[1]) for key in art if len(key) == 2}
                ),
            }
            if args.include_keys:
                item.update(
                    {
                        "phoneme_inventory": sorted(phonemes),
                        "stationary_inventory": sorted(sta),
                        "art_inventory": [list(key) for key in sorted(art)],
                    }
                )
            banks.append(item)
            phoneme_sets.append(phonemes)
            voiced_sets.append(voiced)
            unvoiced_sets.append(unvoiced)
            sta_sets.append(sta)
            art_sets.append(art)

        if not banks:
            raise GraphError("no banks were supplied")
        phoneme_union = set.union(*phoneme_sets)
        phoneme_intersection = set.intersection(*phoneme_sets)
        voiced_union = set.union(*voiced_sets)
        voiced_intersection = set.intersection(*voiced_sets)
        unvoiced_union = set.union(*unvoiced_sets)
        unvoiced_intersection = set.intersection(*unvoiced_sets)
        sta_union = set.union(*sta_sets)
        sta_intersection = set.intersection(*sta_sets)
        art_union = set.union(*art_sets)
        art_intersection = set.intersection(*art_sets)
        art_frequency = Counter(key for keys in art_sets for key in keys)
        presence_histogram = Counter(art_frequency.values())
        diphone_union = {key for key in art_union if len(key) == 2}
        diphone_intersection = {key for key in art_intersection if len(key) == 2}
        diphone_nodes = {value for key in diphone_union for value in key}
        nonuniversal = []
        for key in sorted(art_union):
            present = [
                str(banks[index]["name"])
                for index, keys in enumerate(art_sets)
                if key in keys
            ]
            if len(present) != len(banks):
                nonuniversal.append({"key": list(key), "banks": present})
        sil_incoming = sorted(source for source, target in diphone_union if target == "Sil")
        sil_outgoing = sorted(target for source, target in diphone_union if source == "Sil")

        art_aggregate: dict[str, object] = {
            "union_count": len(art_union),
            "intersection_count": len(art_intersection),
            "union_by_arity": count_by_arity(art_union),
            "intersection_by_arity": count_by_arity(art_intersection),
            "presence_histogram": {
                str(count): keys for count, keys in sorted(presence_histogram.items())
            },
            "diphone_union_graph": graph_summary(diphone_union),
            "diphone_intersection_graph": graph_summary(diphone_intersection),
            "phonemes_without_diphone_edges": sorted(phoneme_union - diphone_nodes),
            "nonuniversal_key_count": len(nonuniversal),
            "nonuniversal_keys": nonuniversal[:100],
            "sil_boundary": {
                "incoming_count": len(sil_incoming),
                "outgoing_count": len(sil_outgoing),
                "incoming_sources": sil_incoming,
                "outgoing_targets": sil_outgoing,
            },
        }
        if args.include_keys:
            art_aggregate.update(
                {
                    "union": [list(key) for key in sorted(art_union)],
                    "intersection": [list(key) for key in sorted(art_intersection)],
                }
            )
        aggregate: dict[str, object] = {
            "bank_count": len(banks),
            "phonemes": {
                "union": sorted(phoneme_union),
                "intersection": sorted(phoneme_intersection),
                "union_count": len(phoneme_union),
                "intersection_count": len(phoneme_intersection),
                "voiced_union": sorted(voiced_union),
                "voiced_intersection": sorted(voiced_intersection),
                "unvoiced_union": sorted(unvoiced_union),
                "unvoiced_intersection": sorted(unvoiced_intersection),
            },
            "stationary": {
                "union": sorted(sta_union),
                "intersection": sorted(sta_intersection),
                "union_count": len(sta_union),
                "intersection_count": len(sta_intersection),
            },
            "art": art_aggregate,
        }
        text = json.dumps(
            {"banks": banks, "aggregate": aggregate},
            ensure_ascii=False,
            indent=2,
        )
        if args.output is None:
            print(text)
        else:
            output = args.output.resolve()
            if output.exists():
                raise GraphError(f"output already exists: {output}")
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(text + "\n", encoding="utf-8")
            print(f"output={output}")
        return 0
    except (OSError, UnicodeError, AssertionError, GraphError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
