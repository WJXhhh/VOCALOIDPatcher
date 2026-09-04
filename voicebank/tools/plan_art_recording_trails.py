#!/usr/bin/env python3
"""Plan deterministic recording trails from an ART graph analysis report."""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from collections import Counter, defaultdict, deque
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable


Edge = tuple[str, str]


class PlanError(Exception):
    pass


@dataclass(frozen=True)
class AugmentedEdge:
    source: str
    target: str
    virtual: bool


def parse_graph(data: object, art_set: str) -> tuple[set[Edge], dict[str, Any]]:
    try:
        aggregate = data["aggregate"]
        raw_edges = aggregate["art"][art_set]
    except (KeyError, TypeError) as error:
        raise PlanError(
            f"analysis report does not contain aggregate.art.{art_set}; "
            "run analyze_reference_graph.py with --include-keys"
        ) from error
    if not isinstance(raw_edges, list):
        raise PlanError(f"aggregate.art.{art_set} must be a list")

    edges: set[Edge] = set()
    for index, value in enumerate(raw_edges):
        if (
            not isinstance(value, list)
            or len(value) != 2
            or any(not isinstance(token, str) or not token for token in value)
        ):
            raise PlanError(f"ART edge {index} must be a pair of non-empty strings")
        edge = (value[0], value[1])
        if edge in edges:
            raise PlanError(f"duplicate ART edge: {edge[0]} -> {edge[1]}")
        edges.add(edge)
    if not edges:
        raise PlanError("ART graph is empty")

    phonemes = aggregate.get("phonemes", {})
    stationary = aggregate.get("stationary", {})
    metadata = {
        "bank_count": aggregate.get("bank_count"),
        "phoneme_union": phonemes.get("union", []),
        "phoneme_intersection": phonemes.get("intersection", []),
        "voiced": phonemes.get("voiced_intersection", []),
        "unvoiced": phonemes.get("unvoiced_intersection", []),
        "stationary": stationary.get(art_set, []),
    }
    return edges, metadata


def canonical_edge_hash(edges: Iterable[Edge]) -> str:
    payload = json.dumps(
        [list(edge) for edge in sorted(edges)],
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def minimum_edge_disjoint_trails(edges: set[Edge]) -> list[list[str]]:
    nodes = sorted({token for edge in edges for token in edge})
    indegree = Counter(target for _, target in edges)
    outdegree = Counter(source for source, _ in edges)
    starts = [
        node
        for node in nodes
        for _ in range(max(0, outdegree[node] - indegree[node]))
    ]
    ends = [
        node
        for node in nodes
        for _ in range(max(0, indegree[node] - outdegree[node]))
    ]
    if len(starts) != len(ends):
        raise PlanError("directed degree imbalance does not balance")

    augmented = [AugmentedEdge(source, target, False) for source, target in sorted(edges)]
    augmented.extend(
        AugmentedEdge(end, start, True) for end, start in zip(ends, starts)
    )
    adjacency: dict[str, list[int]] = {node: [] for node in nodes}
    for edge_id, edge in enumerate(augmented):
        adjacency[edge.source].append(edge_id)
    for edge_ids in adjacency.values():
        edge_ids.sort(
            key=lambda edge_id: (
                augmented[edge_id].target,
                augmented[edge_id].virtual,
                edge_id,
            ),
            reverse=True,
        )

    stack_nodes = [nodes[0]]
    stack_edges: list[int] = []
    circuit: list[int] = []
    while stack_nodes:
        node = stack_nodes[-1]
        if adjacency[node]:
            edge_id = adjacency[node].pop()
            stack_nodes.append(augmented[edge_id].target)
            stack_edges.append(edge_id)
        else:
            stack_nodes.pop()
            if stack_edges:
                circuit.append(stack_edges.pop())
    circuit.reverse()
    if len(circuit) != len(augmented):
        raise PlanError("ART graph is not one connected Eulerian component")

    virtual_positions = [
        index for index, edge_id in enumerate(circuit) if augmented[edge_id].virtual
    ]
    if virtual_positions:
        pivot = virtual_positions[0]
        circuit = circuit[pivot + 1 :] + circuit[: pivot + 1]

    trails: list[list[str]] = []
    trail: list[str] = []
    for edge_id in circuit:
        edge = augmented[edge_id]
        if edge.virtual:
            if len(trail) < 2:
                raise PlanError("virtual balancing produced an empty trail")
            trails.append(trail)
            trail = []
            continue
        if not trail:
            trail = [edge.source, edge.target]
        else:
            if trail[-1] != edge.source:
                raise PlanError("Euler traversal lost edge continuity")
            trail.append(edge.target)
    if trail:
        trails.append(trail)

    expected_trails = max(1, len(starts))
    if len(trails) != expected_trails:
        raise PlanError(
            f"expected {expected_trails} trails from degree imbalance, got {len(trails)}"
        )
    observed = Counter(
        (tokens[index], tokens[index + 1])
        for tokens in trails
        for index in range(len(tokens) - 1)
    )
    if set(observed) != edges or any(count != 1 for count in observed.values()):
        raise PlanError("minimum trails do not cover every required edge exactly once")
    return trails


def shortest_paths(
    source: str, adjacency: dict[str, list[str]]
) -> tuple[dict[str, int], dict[str, str]]:
    distances = {source: 0}
    previous: dict[str, str] = {}
    pending = deque([source])
    while pending:
        node = pending.popleft()
        for target in adjacency[node]:
            if target not in distances:
                distances[target] = distances[node] + 1
                previous[target] = node
                pending.append(target)
    return distances, previous


def reconstruct_path(source: str, target: str, previous: dict[str, str]) -> list[str]:
    if source == target:
        return [source]
    path = [target]
    while path[-1] != source:
        try:
            path.append(previous[path[-1]])
        except KeyError as error:
            raise PlanError(f"no directed connector from {source} to {target}") from error
    path.reverse()
    return path


def join_trails(
    trails: list[list[str]], edges: set[Edge]
) -> tuple[list[str], list[str], list[int]]:
    adjacency: dict[str, list[str]] = defaultdict(list)
    for source, target in sorted(edges):
        adjacency[source].append(target)

    first = min(
        range(len(trails)),
        key=lambda index: (-len(trails[index]), trails[index], index),
    )
    remaining = set(range(len(trails)))
    remaining.remove(first)
    order = [first]
    route = list(trails[first])
    roles = ["required"] * (len(route) - 1)

    while remaining:
        distances, previous = shortest_paths(route[-1], adjacency)
        next_index = min(
            remaining,
            key=lambda index: (
                distances.get(trails[index][0], sys.maxsize),
                -len(trails[index]),
                trails[index],
                index,
            ),
        )
        connector = reconstruct_path(route[-1], trails[next_index][0], previous)
        route.extend(connector[1:])
        roles.extend(["connector_repeat"] * (len(connector) - 1))
        route.extend(trails[next_index][1:])
        roles.extend(["required"] * (len(trails[next_index]) - 1))
        order.append(next_index)
        remaining.remove(next_index)

    if len(roles) != len(route) - 1:
        raise PlanError("joined route role count is inconsistent")
    if any((route[index], route[index + 1]) not in edges for index in range(len(roles))):
        raise PlanError("joined route contains a transition outside the ART graph")
    required_roles = sum(role == "required" for role in roles)
    if required_roles != len(edges):
        raise PlanError("joined route lost a required edge")
    return route, roles, order


def split_recording_clips(
    route: list[str], roles: list[str], max_tokens: int
) -> list[dict[str, Any]]:
    if max_tokens < 2:
        raise PlanError("max_tokens must be at least 2")
    clips: list[dict[str, Any]] = []
    start = 0
    while start < len(route) - 1:
        end = min(start + max_tokens, len(route))
        clip_roles = roles[start : end - 1]
        clips.append(
            {
                "id": f"clip_{len(clips) + 1:04d}",
                "route_transition_start": start,
                "tokens": route[start:end],
                "transition_roles": clip_roles,
                "required_transitions": sum(role == "required" for role in clip_roles),
                "connector_repeats": sum(
                    role == "connector_repeat" for role in clip_roles
                ),
            }
        )
        start = end - 1
    return clips


def edge_trace(edges: set[Edge], clips: list[dict[str, Any]]) -> list[dict[str, Any]]:
    occurrences: dict[Edge, list[dict[str, Any]]] = defaultdict(list)
    for clip in clips:
        tokens = clip["tokens"]
        roles = clip["transition_roles"]
        for index, role in enumerate(roles):
            occurrences[(tokens[index], tokens[index + 1])].append(
                {
                    "clip": clip["id"],
                    "transition_index": index,
                    "role": role,
                }
            )
    missing = edges - set(occurrences)
    extra = set(occurrences) - edges
    if missing or extra:
        raise PlanError(
            f"clip trace mismatch: missing={len(missing)}, extra={len(extra)}"
        )
    return [
        {
            "source": source,
            "target": target,
            "occurrences": occurrences[(source, target)],
        }
        for source, target in sorted(edges)
    ]


def build_plan(
    edges: set[Edge], metadata: dict[str, Any], art_set: str, max_tokens: int
) -> dict[str, Any]:
    trails = minimum_edge_disjoint_trails(edges)
    route, roles, order = join_trails(trails, edges)
    clips = split_recording_clips(route, roles, max_tokens)
    trace = edge_trace(edges, clips)
    nodes = sorted({token for edge in edges for token in edge})
    connector_repeats = sum(role == "connector_repeat" for role in roles)
    total_recorded_tokens = sum(len(clip["tokens"]) for clip in clips)
    return {
        "schema_version": 1,
        "source": {
            "art_set": art_set,
            "bank_count": metadata["bank_count"],
            "edge_sha256": canonical_edge_hash(edges),
        },
        "inventory": {
            "graph_nodes": nodes,
            "voiced": metadata["voiced"],
            "unvoiced": metadata["unvoiced"],
            "stationary_prompts": metadata["stationary"],
        },
        "summary": {
            "required_edges": len(edges),
            "minimum_edge_disjoint_trails": len(trails),
            "minimum_tokens_across_disjoint_trails": sum(len(value) for value in trails),
            "joined_route_tokens": len(route),
            "joined_route_transitions": len(roles),
            "connector_repeat_transitions": connector_repeats,
            "max_tokens_per_clip": max_tokens,
            "recording_clips": len(clips),
            "recorded_tokens_with_clip_boundary_overlap": total_recorded_tokens,
            "unique_required_edges_covered": len(trace),
        },
        "minimum_trails": [
            {"id": f"trail_{index + 1:04d}", "tokens": tokens}
            for index, tokens in enumerate(trails)
        ],
        "joined_trail_order": [f"trail_{index + 1:04d}" for index in order],
        "recording_clips": clips,
        "required_edge_trace": trace,
        "limitations": [
            "Every adjacent pair is an observed ART edge, but longer token chains are not proven Mandarin syllables or natural lyrics.",
            "Clip length is counted in phoneme tokens, not seconds, breaths, or syllables.",
            "The plan does not choose pitch layers, dynamics, timing, or outer/inner alignment.",
            "Stationary prompts are listed separately and are not embedded automatically in every ART clip.",
        ],
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("analysis", help="analysis JSON path, or - for stdin")
    parser.add_argument("output", type=Path)
    parser.add_argument(
        "--art-set",
        choices=("intersection", "union"),
        default="intersection",
    )
    parser.add_argument("--max-tokens", type=int, default=12)
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="validate and print the summary without writing the output file",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        output = args.output.resolve()
        if args.analysis == "-":
            data = json.load(sys.stdin)
        else:
            analysis = Path(args.analysis).resolve()
            if not analysis.is_file():
                raise PlanError(f"analysis report does not exist: {analysis}")
            data = json.loads(analysis.read_text(encoding="utf-8"))
        if not args.dry_run and output.exists():
            raise PlanError(f"refusing to overwrite existing output: {output}")
        edges, metadata = parse_graph(data, args.art_set)
        plan = build_plan(edges, metadata, args.art_set, args.max_tokens)
        if not args.dry_run:
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(
                json.dumps(plan, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
        for name, value in plan["summary"].items():
            print(f"{name}={value}")
        print(f"output={output if not args.dry_run else '(dry-run)'}")
        return 0
    except (OSError, UnicodeError, json.JSONDecodeError, PlanError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
