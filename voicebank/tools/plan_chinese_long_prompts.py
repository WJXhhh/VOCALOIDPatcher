#!/usr/bin/env python3
"""Pack a verified Chinese ART graph into bounded, pronounceable pinyin clips."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import random
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

from plan_chinese_g2pa_prompts import (
    Edge,
    PromptError,
    canonical_edge_hash,
    classify_edges,
    read_g2pa_inventory,
    read_graph,
    transitions,
)


class LongPromptError(Exception):
    pass


@dataclass
class FlowEdge:
    target: int
    reverse: int
    capacity: int
    initial_capacity: int


class Dinic:
    def __init__(self, node_count: int) -> None:
        self.graph: list[list[FlowEdge]] = [[] for _ in range(node_count)]

    def add_edge(self, source: int, target: int, capacity: int) -> tuple[int, int]:
        if capacity < 0:
            raise LongPromptError("negative flow capacity")
        forward = FlowEdge(target, len(self.graph[target]), capacity, capacity)
        reverse = FlowEdge(source, len(self.graph[source]), 0, 0)
        self.graph[source].append(forward)
        self.graph[target].append(reverse)
        return source, len(self.graph[source]) - 1

    def flow_for(self, reference: tuple[int, int]) -> int:
        source, index = reference
        edge = self.graph[source][index]
        return edge.initial_capacity - edge.capacity

    def max_flow(self, source: int, target: int) -> int:
        total = 0
        node_count = len(self.graph)
        while True:
            level = [-1] * node_count
            level[source] = 0
            queue = [source]
            for node in queue:
                for edge in self.graph[node]:
                    if edge.capacity and level[edge.target] < 0:
                        level[edge.target] = level[node] + 1
                        queue.append(edge.target)
            if level[target] < 0:
                return total
            cursor = [0] * node_count

            def send(node: int, amount: int) -> int:
                if node == target:
                    return amount
                while cursor[node] < len(self.graph[node]):
                    edge = self.graph[node][cursor[node]]
                    if edge.capacity and level[edge.target] == level[node] + 1:
                        sent = send(edge.target, min(amount, edge.capacity))
                        if sent:
                            edge.capacity -= sent
                            self.graph[edge.target][edge.reverse].capacity += sent
                            return sent
                    cursor[node] += 1
                return 0

            while True:
                sent = send(source, 1 << 60)
                if not sent:
                    break
                total += sent


def shuffled(values: Iterable[object], rng: random.Random) -> list[object]:
    result = sorted(values)
    rng.shuffle(result)
    return result


def connector_circulation(
    cross_edges: set[Edge],
    syllables: dict[tuple[str, ...], str],
    total_links: int,
    seed: int,
    onset_capacities: Counter[str] | None = None,
    coda_capacities: Counter[str] | None = None,
) -> Counter[Edge] | None:
    """Choose internal syllable links with one lower-bound copy of every syllable."""

    rng = random.Random(seed)
    onsets = sorted({target for _, target in cross_edges})
    codas = sorted({source for source, _ in cross_edges})
    indegree = Counter(target for _, target in cross_edges)
    outdegree = Counter(source for source, _ in cross_edges)
    onset_caps = onset_capacities or indegree
    coda_caps = coda_capacities or outdegree
    allowed = {(phonemes[0], phonemes[-1]) for phonemes in syllables}
    if len(allowed) != len(syllables):
        raise LongPromptError("canonical syllables do not have unique onset/coda pairs")

    names = ["source", *[f"onset:{value}" for value in onsets]]
    names.extend(f"coda:{value}" for value in codas)
    names.extend(("sink", "super_source", "super_sink"))
    node = {name: index for index, name in enumerate(names)}
    source = node["source"]
    sink = node["sink"]
    super_source = node["super_source"]
    super_sink = node["super_sink"]
    flow = Dinic(len(names))
    balance = [0] * len(names)
    references: dict[Edge, tuple[tuple[int, int], int]] = {}

    def add_bounded(
        source_node: int, target_node: int, lower: int, upper: int
    ) -> tuple[int, int]:
        if not 0 <= lower <= upper:
            raise LongPromptError(f"invalid bounded edge {lower}..{upper}")
        reference = flow.add_edge(source_node, target_node, upper - lower)
        balance[source_node] -= lower
        balance[target_node] += lower
        return reference

    for onset in shuffled(onsets, rng):
        add_bounded(source, node[f"onset:{onset}"], 0, onset_caps[onset])
    for onset, coda in shuffled(allowed, rng):
        reference = add_bounded(
            node[f"onset:{onset}"],
            node[f"coda:{coda}"],
            1,
            min(onset_caps[onset], coda_caps[coda]),
        )
        references[(onset, coda)] = (reference, 1)
    for coda in shuffled(codas, rng):
        add_bounded(node[f"coda:{coda}"], sink, 0, coda_caps[coda])
    add_bounded(sink, source, total_links, total_links)

    required = 0
    for index, value in enumerate(balance):
        if value > 0:
            flow.add_edge(super_source, index, value)
            required += value
        elif value < 0:
            flow.add_edge(index, super_sink, -value)
    if flow.max_flow(super_source, super_sink) != required:
        return None

    counts: Counter[Edge] = Counter()
    for pair, (reference, lower) in references.items():
        counts[pair] = lower + flow.flow_for(reference)
    if sum(counts.values()) != total_links:
        raise LongPromptError("connector circulation has the wrong total")
    for onset in onsets:
        if sum(count for (value, _), count in counts.items() if value == onset) > onset_caps[onset]:
            raise LongPromptError("connector circulation exceeds an onset capacity")
    for coda in codas:
        if sum(count for (_, value), count in counts.items() if value == coda) > coda_caps[coda]:
            raise LongPromptError("connector circulation exceeds a coda capacity")
    return counts


def boundary_reservations(
    required_values: set[str],
    allowed_pairs: set[Edge],
    cover_onsets: bool,
    seed: int,
) -> Counter[str]:
    """Reserve one compatible boundary slot for every required value."""

    rng = random.Random(seed)
    options: dict[str, list[str]] = defaultdict(list)
    for onset, coda in sorted(allowed_pairs):
        required = onset if cover_onsets else coda
        slot = coda if cover_onsets else onset
        if required in required_values:
            options[required].append(slot)
    missing = required_values - set(options)
    if missing:
        raise LongPromptError(f"boundary values have no syllable witness: {sorted(missing)}")
    loads: Counter[str] = Counter()
    ordered = sorted(required_values, key=lambda value: (len(options[value]), value))
    for value in ordered:
        candidates = list(options[value])
        rng.shuffle(candidates)
        chosen = min(candidates, key=lambda slot: (loads[slot], slot))
        loads[chosen] += 1
    return loads


def cover_boundary_values(
    required_values: set[str],
    slot_counts: Counter[str],
    allowed_pairs: set[Edge],
    cover_onsets: bool,
) -> dict[str, list[Edge]] | None:
    """Assign boundary syllables while covering every required onset or coda once."""

    left = sorted(required_values)
    slots = sorted(value for value, count in slot_counts.items() if count)
    names = ["source", *[f"left:{value}" for value in left]]
    names.extend(f"slot:{value}" for value in slots)
    names.append("sink")
    node = {name: index for index, name in enumerate(names)}
    source = node["source"]
    sink = node["sink"]
    flow = Dinic(len(names))
    references: dict[Edge, tuple[int, int]] = {}
    for value in left:
        flow.add_edge(source, node[f"left:{value}"], 1)
    for onset, coda in sorted(allowed_pairs):
        required_value = onset if cover_onsets else coda
        slot_value = coda if cover_onsets else onset
        if required_value not in required_values or slot_counts[slot_value] <= 0:
            continue
        references[(onset, coda)] = flow.add_edge(
            node[f"left:{required_value}"], node[f"slot:{slot_value}"], 1
        )
    for value in slots:
        flow.add_edge(node[f"slot:{value}"], sink, slot_counts[value])
    if flow.max_flow(source, sink) != len(required_values):
        return None

    assignments: dict[str, list[Edge]] = defaultdict(list)
    used = Counter()
    for pair, reference in references.items():
        if flow.flow_for(reference):
            slot = pair[1] if cover_onsets else pair[0]
            assignments[slot].append(pair)
            used[slot] += 1
    candidates: dict[str, list[Edge]] = defaultdict(list)
    for pair in sorted(allowed_pairs):
        slot = pair[1] if cover_onsets else pair[0]
        candidates[slot].append(pair)
    for slot, count in sorted(slot_counts.items()):
        options = candidates.get(slot, [])
        if not options:
            return None
        for index in range(count - used[slot]):
            assignments[slot].append(options[index % len(options)])
    return assignments


def build_fixed_length_paths(
    cross_edges: set[Edge],
    link_counts: Counter[Edge],
    path_lengths: list[int],
    seed: int,
) -> list[list[Edge]] | None:
    rng = random.Random(seed)
    remaining_edges: dict[str, set[str]] = defaultdict(set)
    for source, target in cross_edges:
        remaining_edges[source].add(target)
    remaining_links = Counter(link_counts)
    incoming_links = Counter()
    outgoing_links = Counter()
    for (onset, coda), count in remaining_links.items():
        outgoing_links[onset] += count
        incoming_links[coda] += count
    starts = Counter(
        {
            coda: len(targets) - incoming_links[coda]
            for coda, targets in remaining_edges.items()
        }
    )
    target_counts = Counter(target for _, target in cross_edges)
    ends = Counter(
        {
            onset: target_counts[onset] - outgoing_links[onset]
            for onset in target_counts
        }
    )
    if min(starts.values(), default=0) < 0 or min(ends.values(), default=0) < 0:
        raise LongPromptError("negative path boundary count")
    if sum(starts.values()) != len(path_lengths) or sum(ends.values()) != len(path_lengths):
        raise LongPromptError("path boundary counts do not match the target clip count")

    def jitter() -> float:
        return rng.random()

    paths: list[list[Edge]] = []
    ordered_lengths = sorted(path_lengths, reverse=True)
    for length in ordered_lengths:
        start_candidates = [
            (coda, onset)
            for coda in sorted(starts)
            if starts[coda]
            for onset in sorted(remaining_edges[coda])
            if outgoing_links[onset]
        ]
        start_candidates.sort(
            key=lambda value: (
                len(remaining_edges[value[0]]) / starts[value[0]],
                outgoing_links[value[1]],
                jitter(),
            )
        )
        built: list[Edge] | None = None
        for start_coda, start_onset in start_candidates[:80]:
            starts[start_coda] -= 1
            remaining_edges[start_coda].remove(start_onset)
            candidate_path: list[Edge] = [(start_coda, start_onset)]

            def extend() -> bool:
                current_onset = candidate_path[-1][1]
                if len(candidate_path) == length:
                    if ends[current_onset] <= 0:
                        return False
                    ends[current_onset] -= 1
                    return True
                final_step = len(candidate_path) + 1 == length
                options: list[tuple[str, str]] = []
                for (onset, next_coda), count in sorted(remaining_links.items()):
                    if onset != current_onset or count <= 0:
                        continue
                    for next_onset in sorted(remaining_edges[next_coda]):
                        if final_step:
                            if ends[next_onset] <= 0:
                                continue
                        elif outgoing_links[next_onset] <= 0:
                            continue
                        options.append((next_coda, next_onset))
                options.sort(
                    key=lambda value: (
                        0 if final_step else outgoing_links[value[1]],
                        len(remaining_edges[value[0]]),
                        -remaining_links[(current_onset, value[0])],
                        jitter(),
                    )
                )
                for next_coda, next_onset in options[:120]:
                    pair = (current_onset, next_coda)
                    remaining_links[pair] -= 1
                    outgoing_links[current_onset] -= 1
                    incoming_links[next_coda] -= 1
                    remaining_edges[next_coda].remove(next_onset)
                    candidate_path.append((next_coda, next_onset))
                    if extend():
                        return True
                    candidate_path.pop()
                    remaining_edges[next_coda].add(next_onset)
                    incoming_links[next_coda] += 1
                    outgoing_links[current_onset] += 1
                    remaining_links[pair] += 1
                return False

            if extend():
                built = candidate_path
                break
            remaining_edges[start_coda].add(start_onset)
            starts[start_coda] += 1
        if built is None:
            return None
        paths.append(built)

    if any(remaining_edges.values()) or any(remaining_links.values()):
        return None
    if any(starts.values()) or any(ends.values()):
        return None
    return paths


def allocate_boundary_pairs(
    paths: list[list[Edge]],
    first_assignments: dict[str, list[Edge]],
    last_assignments: dict[str, list[Edge]],
) -> tuple[list[Edge], list[Edge]]:
    first_pool = {key: list(value) for key, value in first_assignments.items()}
    last_pool = {key: list(value) for key, value in last_assignments.items()}
    first_pairs: list[Edge] = []
    last_pairs: list[Edge] = []
    for path in paths:
        first_coda = path[0][0]
        last_onset = path[-1][1]
        try:
            first_pairs.append(first_pool[first_coda].pop())
            last_pairs.append(last_pool[last_onset].pop())
        except (KeyError, IndexError) as error:
            raise LongPromptError("boundary assignment does not match path counts") from error
    if any(first_pool.values()) or any(last_pool.values()):
        raise LongPromptError("unused boundary assignments remain")
    return first_pairs, last_pairs


def flatten_syllables(values: Iterable[tuple[str, ...]]) -> tuple[str, ...]:
    return tuple(phoneme for syllable in values for phoneme in syllable)


def build_output(
    graph_path: Path,
    inventory_path: Path,
    art_set: str,
    max_syllables: int,
    search_seeds: int,
) -> dict[str, object]:
    if max_syllables < 2:
        raise LongPromptError("max_syllables must be at least 2")
    if search_seeds < 1:
        raise LongPromptError("search_seeds must be positive")
    required, bank_count = read_graph(graph_path, art_set)
    syllables, g2pa_summary = read_g2pa_inventory(inventory_path)
    roles, role_histogram = classify_edges(required, syllables)
    if any(len(edge_roles) != 1 for edge_roles in roles.values()):
        raise LongPromptError("ART roles are not a disjoint, exhaustive partition")
    cross_edges = {
        edge for edge, edge_roles in roles.items() if edge_roles == ["cross_syllable"]
    }
    silence_onsets = {
        target
        for (source, target), edge_roles in roles.items()
        if edge_roles == ["silence_onset"] and source == "Sil"
    }
    silence_codas = {
        source
        for (source, target), edge_roles in roles.items()
        if edge_roles == ["silence_coda"] and target == "Sil"
    }
    allowed_pairs = {(value[0], value[-1]) for value in syllables}
    if len(allowed_pairs) != len(syllables):
        raise LongPromptError("syllable onset/coda pairs are not unique")
    missing_syllable_edges = {
        edge for value in syllables for edge in transitions(value) if edge not in required
    }
    if missing_syllable_edges:
        raise LongPromptError(
            f"canonical syllables contain non-ART transitions: {sorted(missing_syllable_edges)[:10]}"
        )
    cross_per_clip = max_syllables - 1
    lower_bound = math.ceil(len(cross_edges) / cross_per_clip)
    path_lengths = [cross_per_clip] * (len(cross_edges) // cross_per_clip)
    remainder = len(cross_edges) % cross_per_clip
    if remainder:
        path_lengths.append(remainder)
    if len(path_lengths) != lower_bound:
        raise LongPromptError("path length construction disagrees with its lower bound")
    total_links = len(cross_edges) - len(path_lengths)
    if total_links < len(syllables):
        raise LongPromptError(
            "clip limit leaves too few internal slots to witness every canonical syllable"
        )

    chosen: tuple[
        int,
        Counter[Edge],
        list[list[Edge]],
        dict[str, list[Edge]],
        dict[str, list[Edge]],
    ] | None = None
    cross_indegree = Counter(target for _, target in cross_edges)
    cross_outdegree = Counter(source for source, _ in cross_edges)
    for seed in range(search_seeds):
        first_reserved = boundary_reservations(
            silence_onsets, allowed_pairs, True, seed ^ 0x19A4D36B
        )
        last_reserved = boundary_reservations(
            silence_codas, allowed_pairs, False, seed ^ 0x73C81E25
        )
        onset_capacities = Counter(
            {
                onset: cross_indegree[onset] - last_reserved[onset]
                for onset in cross_indegree
            }
        )
        coda_capacities = Counter(
            {
                coda: cross_outdegree[coda] - first_reserved[coda]
                for coda in cross_outdegree
            }
        )
        links = connector_circulation(
            cross_edges,
            syllables,
            total_links,
            seed,
            onset_capacities,
            coda_capacities,
        )
        if links is None:
            continue
        starts = Counter(source for source, _ in cross_edges)
        ends = Counter(target for _, target in cross_edges)
        for (onset, coda), count in links.items():
            starts[coda] -= count
            ends[onset] -= count
        first_assignments = cover_boundary_values(
            silence_onsets, starts, allowed_pairs, True
        )
        last_assignments = cover_boundary_values(
            silence_codas, ends, allowed_pairs, False
        )
        if first_assignments is None or last_assignments is None:
            continue
        paths = build_fixed_length_paths(
            cross_edges, links, path_lengths, seed ^ 0x5A17C9E3
        )
        if paths is None:
            continue
        chosen = seed, links, paths, first_assignments, last_assignments
        break
    if chosen is None:
        raise LongPromptError(
            f"no exact bounded path decomposition found in {search_seeds} deterministic seeds"
        )

    seed, links, paths, first_assignments, last_assignments = chosen
    first_pairs, last_pairs = allocate_boundary_pairs(
        paths, first_assignments, last_assignments
    )
    pair_to_phonemes = {(value[0], value[-1]): value for value in syllables}
    prompts: list[dict[str, object]] = []
    occurrences: dict[Edge, list[dict[str, object]]] = defaultdict(list)
    used_syllables: Counter[tuple[str, ...]] = Counter()
    cross_seen: Counter[Edge] = Counter()
    for prompt_index, path in enumerate(paths):
        connector_pairs = [first_pairs[prompt_index]]
        connector_pairs.extend(
            (path[index][1], path[index + 1][0])
            for index in range(len(path) - 1)
        )
        connector_pairs.append(last_pairs[prompt_index])
        phoneme_syllables = [pair_to_phonemes[pair] for pair in connector_pairs]
        pinyin = [syllables[value] for value in phoneme_syllables]
        for value in phoneme_syllables:
            used_syllables[value] += 1
        phoneme_path = ("Sil", *flatten_syllables(phoneme_syllables), "Sil")
        realized = transitions(phoneme_path)
        if any(edge not in required for edge in realized):
            raise LongPromptError("constructed prompt contains a non-ART transition")
        for edge in path:
            cross_seen[edge] += 1
        prompt_id = f"prompt_{prompt_index + 1:04d}"
        prompts.append(
            {
                "id": prompt_id,
                "pinyin": ["<sil>", *pinyin, "<sil>"],
                "phoneme_syllables": [list(value) for value in phoneme_syllables],
                "phonemes": list(phoneme_path),
                "cross_edges": [list(edge) for edge in path],
                "syllable_count": len(phoneme_syllables),
                "phoneme_count_excluding_silence": len(phoneme_path) - 2,
            }
        )
        for transition_index, edge in enumerate(realized):
            occurrences[edge].append(
                {"prompt": prompt_id, "transition_index": transition_index}
            )

    if set(cross_seen) != cross_edges or any(count != 1 for count in cross_seen.values()):
        raise LongPromptError("cross-syllable edge coverage is not exactly once")
    uncovered = required - set(occurrences)
    extra = set(occurrences) - required
    if uncovered or extra:
        raise LongPromptError(
            f"prompt coverage mismatch: uncovered={len(uncovered)}, extra={len(extra)}"
        )
    missing_syllables = set(syllables) - set(used_syllables)
    if missing_syllables:
        raise LongPromptError(
            f"canonical syllables were not witnessed: {sorted(missing_syllables)[:10]}"
        )
    if len(prompts) != lower_bound:
        raise LongPromptError("constructed prompt count does not attain the lower bound")

    trace = [
        {
            "edge": list(edge),
            "role": roles[edge][0],
            "occurrences": occurrences[edge],
        }
        for edge in sorted(required)
    ]
    prompt_payload = json.dumps(
        [item["pinyin"] for item in prompts],
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    prompt_plan_sha256 = hashlib.sha256(prompt_payload).hexdigest()
    return {
        "format": "vocaloid-chinese-long-prompt-plan-v1",
        "source": {
            "graph": str(graph_path),
            "g2pa_inventory": str(inventory_path),
            "art_set": art_set,
            "bank_count": bank_count,
            "edge_sha256": canonical_edge_hash(required),
            "g2pa_inventory_sha256": g2pa_summary.get("inventory_sha256"),
        },
        "model": {
            "max_syllables_per_prompt": max_syllables,
            "explicit_leading_and_trailing_silence": True,
            "each_cross_edge_used_exactly_once": True,
            "every_canonical_phoneme_syllable_used": True,
            "tones_assigned": False,
            "durations_assigned": False,
        },
        "summary": {
            "required_edges": len(required),
            "edge_role_histogram": dict(sorted(role_histogram.items())),
            "cross_syllable_edges": len(cross_edges),
            "canonical_phoneme_syllables": len(syllables),
            "internal_syllable_links": sum(links.values()),
            "prompt_lower_bound": lower_bound,
            "recording_prompts": len(prompts),
            "model_optimal": len(prompts) == lower_bound,
            "search_seed": seed,
            "maximum_syllables_in_prompt": max(
                item["syllable_count"] for item in prompts
            ),
            "maximum_phonemes_excluding_silence": max(
                item["phoneme_count_excluding_silence"] for item in prompts
            ),
            "cross_edges_covered_exactly_once": len(cross_seen),
            "all_required_edges_covered": len(occurrences),
            "uncovered_edges": 0,
            "non_art_edges": 0,
            "canonical_syllables_witnessed": len(used_syllables),
            "prompt_plan_sha256": prompt_plan_sha256,
        },
        "recording_prompts": prompts,
        "required_edge_trace": trace,
        "limitations": [
            "The clips are legal pinyin syllable sequences, not natural Mandarin sentences or assigned Chinese characters.",
            "Tone, duration, dynamics, pitch layers, breath timing, and singer comfort are not assigned.",
            "The optimum is for the bounded syllable-count/explicit-silence model, not an acoustic workload optimum.",
            "A complete ART-edge witness does not replace forced alignment, manual boundary QA, or host rendering tests.",
        ],
    }


def write_json(path: Path | None, value: object) -> None:
    text = json.dumps(value, ensure_ascii=False, indent=2) + "\n"
    if path is None:
        sys.stdout.write(text)
        return
    if path.exists():
        raise LongPromptError(f"output already exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("graph", type=Path)
    parser.add_argument("g2pa_inventory", type=Path)
    parser.add_argument("--art-set", choices=("intersection", "union"), default="intersection")
    parser.add_argument("--max-syllables", type=int, default=12)
    parser.add_argument("--search-seeds", type=int, default=256)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    try:
        result = build_output(
            args.graph.resolve(),
            args.g2pa_inventory.resolve(),
            args.art_set,
            args.max_syllables,
            args.search_seeds,
        )
        write_json(args.output.resolve() if args.output else None, result)
        if args.output:
            for name, value in result["summary"].items():
                if not isinstance(value, (dict, list)):
                    print(f"{name}={value}")
        return 0
    except (
        OSError,
        UnicodeError,
        json.JSONDecodeError,
        PromptError,
        LongPromptError,
    ) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
