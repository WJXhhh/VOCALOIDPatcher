#!/usr/bin/env python3
"""Independently verify a bounded Chinese pinyin ART recording plan."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any


Edge = tuple[str, str]


class VerificationError(Exception):
    pass


def read_json(path: Path) -> Any:
    if not path.is_file():
        raise VerificationError(f"file does not exist: {path}")
    return json.loads(path.read_text(encoding="utf-8"))


def edge_hash(edges: set[Edge]) -> str:
    payload = json.dumps(
        [list(edge) for edge in sorted(edges)],
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def parse_required_edges(graph: Any, art_set: str) -> tuple[set[Edge], int]:
    try:
        aggregate = graph["aggregate"]
        values = aggregate["art"][art_set]
    except (KeyError, TypeError) as error:
        raise VerificationError(
            f"graph has no aggregate.art.{art_set}; use --include-keys"
        ) from error
    edges: set[Edge] = set()
    for index, value in enumerate(values):
        if (
            not isinstance(value, list)
            or len(value) != 2
            or any(not isinstance(token, str) or not token for token in value)
        ):
            raise VerificationError(f"invalid graph edge at index {index}")
        edge = value[0], value[1]
        if edge in edges:
            raise VerificationError(f"duplicate graph edge: {edge}")
        edges.add(edge)
    return edges, int(aggregate.get("bank_count", 0))


def parse_inventory(
    inventory: Any,
) -> tuple[dict[str, tuple[str, ...]], set[tuple[str, ...]], str | None]:
    if inventory.get("format") != "vocaloid-chinese-g2pa-inventory-v1":
        raise VerificationError("unsupported G2PA inventory format")
    entries = inventory.get("entries")
    summary = inventory.get("summary")
    if not isinstance(entries, list) or not isinstance(summary, dict):
        raise VerificationError("invalid G2PA inventory structure")
    token_map: dict[str, tuple[str, ...]] = {}
    canonical: set[tuple[str, ...]] = set()
    for index, entry in enumerate(entries):
        if not isinstance(entry, dict) or not entry.get("exact_match"):
            raise VerificationError(f"unverified G2PA entry at index {index}")
        token = entry.get("token")
        phoneme_text = entry.get("phonemes")
        if not isinstance(token, str) or not isinstance(phoneme_text, str):
            raise VerificationError(f"invalid G2PA entry at index {index}")
        phonemes = tuple(phoneme_text.split())
        if not 1 <= len(phonemes) <= 2:
            raise VerificationError(f"invalid phoneme syllable for {token}: {phonemes}")
        previous = token_map.setdefault(token, phonemes)
        if previous != phonemes:
            raise VerificationError(f"ambiguous G2PA token: {token}")
        canonical.add(phonemes)
    return token_map, canonical, summary.get("inventory_sha256")


def classify_roles(
    required: set[Edge], canonical: set[tuple[str, ...]]
) -> tuple[dict[Edge, str], set[Edge]]:
    within = {
        (phonemes[index], phonemes[index + 1])
        for phonemes in canonical
        for index in range(len(phonemes) - 1)
    }
    cross = {
        (left[-1], right[0]) for left in canonical for right in canonical
    }
    onset = {("Sil", phonemes[0]) for phonemes in canonical}
    coda = {(phonemes[-1], "Sil") for phonemes in canonical}
    categories = {
        "within_syllable": within,
        "cross_syllable": cross,
        "silence_onset": onset,
        "silence_coda": coda,
    }
    roles: dict[Edge, str] = {}
    for edge in required:
        matches = [name for name, values in categories.items() if edge in values]
        if len(matches) != 1:
            raise VerificationError(f"edge does not have exactly one role: {edge} -> {matches}")
        roles[edge] = matches[0]
    return roles, {edge for edge, role in roles.items() if role == "cross_syllable"}


def parse_edge(value: Any, context: str) -> Edge:
    if (
        not isinstance(value, list)
        or len(value) != 2
        or any(not isinstance(token, str) for token in value)
    ):
        raise VerificationError(f"invalid edge in {context}: {value!r}")
    return value[0], value[1]


def verify(
    plan: Any,
    required: set[Edge],
    bank_count: int,
    token_map: dict[str, tuple[str, ...]],
    canonical: set[tuple[str, ...]],
    inventory_hash: str | None,
) -> dict[str, object]:
    if plan.get("format") != "vocaloid-chinese-long-prompt-plan-v1":
        raise VerificationError("unsupported long-prompt plan format")
    source = plan.get("source")
    model = plan.get("model")
    summary = plan.get("summary")
    prompts = plan.get("recording_prompts")
    trace = plan.get("required_edge_trace")
    if not all(
        isinstance(value, expected)
        for value, expected in (
            (source, dict),
            (model, dict),
            (summary, dict),
            (prompts, list),
            (trace, list),
        )
    ):
        raise VerificationError("plan is missing a required object or list")
    if source.get("bank_count") != bank_count:
        raise VerificationError("plan bank count differs from graph")
    if source.get("edge_sha256") != edge_hash(required):
        raise VerificationError("plan ART edge hash differs from graph")
    if source.get("g2pa_inventory_sha256") != inventory_hash:
        raise VerificationError("plan G2PA inventory hash differs from inventory")
    max_syllables = model.get("max_syllables_per_prompt")
    if isinstance(max_syllables, bool) or not isinstance(max_syllables, int):
        raise VerificationError("invalid max_syllables_per_prompt")
    if max_syllables < 2:
        raise VerificationError("max_syllables_per_prompt must be at least 2")

    roles, required_cross = classify_roles(required, canonical)
    transitions_seen: dict[Edge, list[dict[str, object]]] = defaultdict(list)
    cross_seen: Counter[Edge] = Counter()
    syllables_seen: Counter[tuple[str, ...]] = Counter()
    prompt_ids: set[str] = set()
    pinyin_payload: list[list[str]] = []
    maximum_syllables = 0
    maximum_phonemes = 0
    for prompt_index, prompt in enumerate(prompts):
        if not isinstance(prompt, dict):
            raise VerificationError(f"prompt {prompt_index} is not an object")
        prompt_id = prompt.get("id")
        pinyin = prompt.get("pinyin")
        raw_syllables = prompt.get("phoneme_syllables")
        phonemes = prompt.get("phonemes")
        raw_cross = prompt.get("cross_edges")
        if not isinstance(prompt_id, str) or not prompt_id:
            raise VerificationError(f"prompt {prompt_index} has no ID")
        if prompt_id in prompt_ids:
            raise VerificationError(f"duplicate prompt ID: {prompt_id}")
        prompt_ids.add(prompt_id)
        if not all(
            isinstance(value, list)
            for value in (pinyin, raw_syllables, phonemes, raw_cross)
        ):
            raise VerificationError(f"prompt {prompt_id} has an invalid list field")
        if len(pinyin) != len(raw_syllables) + 2:
            raise VerificationError(f"prompt {prompt_id} pinyin/syllable lengths differ")
        if pinyin[:1] != ["<sil>"] or pinyin[-1:] != ["<sil>"]:
            raise VerificationError(f"prompt {prompt_id} lacks explicit boundary silence")
        if not 1 <= len(raw_syllables) <= max_syllables:
            raise VerificationError(f"prompt {prompt_id} exceeds its syllable limit")
        syllable_values: list[tuple[str, ...]] = []
        for token, raw in zip(pinyin[1:-1], raw_syllables):
            if not isinstance(token, str) or not isinstance(raw, list):
                raise VerificationError(f"prompt {prompt_id} has an invalid syllable")
            value = tuple(raw)
            if any(not isinstance(phoneme, str) for phoneme in value):
                raise VerificationError(f"prompt {prompt_id} has a non-string phoneme")
            if token_map.get(token) != value:
                raise VerificationError(
                    f"prompt {prompt_id} G2PA mismatch: {token} -> {value}"
                )
            syllable_values.append(value)
            syllables_seen[value] += 1
        expected_phonemes = [
            "Sil",
            *(phoneme for value in syllable_values for phoneme in value),
            "Sil",
        ]
        if phonemes != expected_phonemes:
            raise VerificationError(f"prompt {prompt_id} flattened phonemes differ")
        expected_cross = [
            (syllable_values[index][-1], syllable_values[index + 1][0])
            for index in range(len(syllable_values) - 1)
        ]
        declared_cross = [
            parse_edge(value, f"prompt {prompt_id} cross_edges") for value in raw_cross
        ]
        if declared_cross != expected_cross:
            raise VerificationError(f"prompt {prompt_id} cross-edge list differs")
        cross_seen.update(declared_cross)
        for transition_index in range(len(phonemes) - 1):
            edge = phonemes[transition_index], phonemes[transition_index + 1]
            if edge not in required:
                raise VerificationError(f"prompt {prompt_id} contains non-ART edge {edge}")
            transitions_seen[edge].append(
                {"prompt": prompt_id, "transition_index": transition_index}
            )
        if prompt.get("syllable_count") != len(syllable_values):
            raise VerificationError(f"prompt {prompt_id} syllable_count differs")
        if prompt.get("phoneme_count_excluding_silence") != len(phonemes) - 2:
            raise VerificationError(f"prompt {prompt_id} phoneme count differs")
        maximum_syllables = max(maximum_syllables, len(syllable_values))
        maximum_phonemes = max(maximum_phonemes, len(phonemes) - 2)
        pinyin_payload.append(pinyin)

    if set(transitions_seen) != required:
        missing = required - set(transitions_seen)
        extra = set(transitions_seen) - required
        raise VerificationError(
            f"ART coverage mismatch: missing={len(missing)}, extra={len(extra)}"
        )
    if set(cross_seen) != required_cross or any(
        count != 1 for count in cross_seen.values()
    ):
        raise VerificationError("cross-syllable edges are not covered exactly once")
    if set(syllables_seen) != canonical:
        missing = canonical - set(syllables_seen)
        raise VerificationError(f"canonical syllables are missing: {sorted(missing)[:10]}")

    expected_trace = {
        edge: occurrences for edge, occurrences in sorted(transitions_seen.items())
    }
    actual_trace: dict[Edge, list[dict[str, object]]] = {}
    for index, entry in enumerate(trace):
        if not isinstance(entry, dict):
            raise VerificationError(f"trace entry {index} is not an object")
        edge = parse_edge(entry.get("edge"), f"trace entry {index}")
        if edge in actual_trace:
            raise VerificationError(f"duplicate trace edge: {edge}")
        if entry.get("role") != roles.get(edge):
            raise VerificationError(f"trace role differs for edge {edge}")
        occurrences = entry.get("occurrences")
        if not isinstance(occurrences, list):
            raise VerificationError(f"trace occurrences differ for edge {edge}")
        actual_trace[edge] = occurrences
    if actual_trace != expected_trace:
        raise VerificationError("required_edge_trace differs from prompt transitions")

    prompt_hash = hashlib.sha256(
        json.dumps(
            pinyin_payload, ensure_ascii=False, separators=(",", ":")
        ).encode("utf-8")
    ).hexdigest()
    lower_bound = math.ceil(len(required_cross) / (max_syllables - 1))
    expected_summary = {
        "required_edges": len(required),
        "cross_syllable_edges": len(required_cross),
        "canonical_phoneme_syllables": len(canonical),
        "prompt_lower_bound": lower_bound,
        "recording_prompts": len(prompts),
        "model_optimal": len(prompts) == lower_bound,
        "maximum_syllables_in_prompt": maximum_syllables,
        "maximum_phonemes_excluding_silence": maximum_phonemes,
        "cross_edges_covered_exactly_once": len(cross_seen),
        "all_required_edges_covered": len(transitions_seen),
        "uncovered_edges": 0,
        "non_art_edges": 0,
        "canonical_syllables_witnessed": len(syllables_seen),
        "prompt_plan_sha256": prompt_hash,
    }
    for name, expected in expected_summary.items():
        if summary.get(name) != expected:
            raise VerificationError(
                f"summary.{name}={summary.get(name)!r}, expected {expected!r}"
            )
    if not expected_summary["model_optimal"]:
        raise VerificationError("plan does not attain the prompt-count lower bound")
    return expected_summary


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("plan", type=Path)
    parser.add_argument("graph", type=Path)
    parser.add_argument("g2pa_inventory", type=Path)
    args = parser.parse_args()
    try:
        plan = read_json(args.plan.resolve())
        graph = read_json(args.graph.resolve())
        inventory = read_json(args.g2pa_inventory.resolve())
        source = plan.get("source")
        art_set = source.get("art_set") if isinstance(source, dict) else None
        if art_set not in ("intersection", "union"):
            raise VerificationError("plan has an invalid art_set")
        required, bank_count = parse_required_edges(graph, art_set)
        token_map, canonical, inventory_hash = parse_inventory(inventory)
        summary = verify(
            plan,
            required,
            bank_count,
            token_map,
            canonical,
            inventory_hash,
        )
        for name, value in summary.items():
            print(f"{name}={value}")
        return 0
    except (OSError, UnicodeError, json.JSONDecodeError, VerificationError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
