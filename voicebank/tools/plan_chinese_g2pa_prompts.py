#!/usr/bin/env python3
"""Build pronounceable pinyin witnesses and a greedy ART-edge prompt cover."""

from __future__ import annotations

import argparse
import hashlib
import heapq
import json
import math
import sys
from collections import Counter
from pathlib import Path
from typing import Iterable


Edge = tuple[str, str]


class PromptError(Exception):
    pass


def canonical_edge_hash(edges: set[Edge]) -> str:
    payload = json.dumps(
        [list(edge) for edge in sorted(edges)],
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def token_score(token: str, phonemes: tuple[str, ...]) -> tuple[object, ...]:
    punctuation_penalty = int(":" in token)
    zero_initial = len(phonemes) == 1
    conventional_zero_initial = (
        token.startswith(("y", "w"))
        or token
        in {
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
    )
    orthography_penalty = int(zero_initial and not conventional_zero_initial)
    return (punctuation_penalty, orthography_penalty, len(token), token)


def read_g2pa_inventory(path: Path) -> tuple[dict[tuple[str, ...], str], dict[str, object]]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if value.get("format") != "vocaloid-chinese-g2pa-inventory-v1":
        raise PromptError("unsupported G2PA inventory format")
    summary = value.get("summary")
    entries = value.get("entries")
    if not isinstance(summary, dict) or not isinstance(entries, list):
        raise PromptError("invalid G2PA inventory structure")
    if summary.get("mismatches") or summary.get("missing_tokens"):
        raise PromptError("G2PA inventory contains mismatches or missing tokens")

    canonical: dict[tuple[str, ...], str] = {}
    for entry in entries:
        if not isinstance(entry, dict) or not entry.get("exact_match"):
            raise PromptError("G2PA inventory contains an unverified entry")
        token = entry.get("token")
        phoneme_text = entry.get("phonemes")
        if not isinstance(token, str) or not isinstance(phoneme_text, str):
            raise PromptError("G2PA token or phoneme value is not a string")
        phonemes = tuple(phoneme_text.split())
        if not 1 <= len(phonemes) <= 2:
            raise PromptError(
                f"expected one or two phonemes for {token}, got {phonemes}"
            )
        previous = canonical.get(phonemes)
        if previous is None or token_score(token, phonemes) < token_score(
            previous, phonemes
        ):
            canonical[phonemes] = token
    if not canonical:
        raise PromptError("G2PA inventory is empty")
    return canonical, summary


def read_graph(path: Path, art_set: str) -> tuple[set[Edge], int]:
    value = json.loads(path.read_text(encoding="utf-8"))
    aggregate = value.get("aggregate")
    if not isinstance(aggregate, dict):
        raise PromptError("graph JSON has no aggregate object")
    art = aggregate.get("art")
    if not isinstance(art, dict) or not isinstance(art.get(art_set), list):
        raise PromptError(
            f"graph JSON has no aggregate.art.{art_set}; use --include-keys"
        )
    edges: set[Edge] = set()
    for raw in art[art_set]:
        if not isinstance(raw, list) or len(raw) != 2:
            raise PromptError(f"non-diphone ART key in {art_set}: {raw!r}")
        source, target = raw
        if not isinstance(source, str) or not isinstance(target, str):
            raise PromptError(f"non-string ART key: {raw!r}")
        edges.add((source, target))
    return edges, int(aggregate.get("bank_count", 0))


def transitions(phonemes: tuple[str, ...]) -> tuple[Edge, ...]:
    return tuple(zip(phonemes, phonemes[1:]))


def prompt_score(
    pinyin: tuple[str, ...],
    phoneme_path: tuple[str, ...],
) -> tuple[object, ...]:
    spoken = tuple(token for token in pinyin if token != "<sil>")
    return (
        len(spoken),
        sum(len(token) for token in spoken),
        spoken,
        phoneme_path,
    )


def add_candidate(
    candidates: dict[frozenset[Edge], dict[str, object]],
    required: set[Edge],
    pinyin: tuple[str, ...],
    phoneme_path: tuple[str, ...],
    kind: str,
) -> None:
    realized = transitions(phoneme_path)
    if not realized or any(edge not in required for edge in realized):
        return
    coverage = frozenset(realized)
    item = {
        "kind": kind,
        "pinyin": list(pinyin),
        "phonemes": list(phoneme_path),
        "covered_edges": [list(edge) for edge in realized],
    }
    previous = candidates.get(coverage)
    if previous is None:
        candidates[coverage] = item
        return
    previous_pinyin = tuple(previous["pinyin"])
    previous_phonemes = tuple(previous["phonemes"])
    if prompt_score(pinyin, phoneme_path) < prompt_score(
        previous_pinyin, previous_phonemes
    ):
        candidates[coverage] = item


def build_candidates(
    required: set[Edge],
    syllables: dict[tuple[str, ...], str],
) -> dict[frozenset[Edge], dict[str, object]]:
    candidates: dict[frozenset[Edge], dict[str, object]] = {}
    ordered = sorted(
        syllables.items(),
        key=lambda item: (token_score(item[1], item[0]), item[0]),
    )
    for phonemes, token in ordered:
        add_candidate(
            candidates,
            required,
            ("<sil>", token, "<sil>"),
            ("Sil", *phonemes, "Sil"),
            "one_syllable_clip",
        )
    for left_phonemes, left_token in ordered:
        for right_phonemes, right_token in ordered:
            add_candidate(
                candidates,
                required,
                ("<sil>", left_token, right_token, "<sil>"),
                ("Sil", *left_phonemes, *right_phonemes, "Sil"),
                "two_syllable_clip",
            )
    return candidates


def classify_edges(
    required: set[Edge],
    syllables: dict[tuple[str, ...], str],
) -> tuple[dict[Edge, list[str]], Counter[str]]:
    within = {
        edge for phonemes in syllables for edge in transitions(phonemes)
    }
    cross = {
        (left[-1], right[0])
        for left in syllables
        for right in syllables
    }
    onset = {("Sil", phonemes[0]) for phonemes in syllables}
    coda = {(phonemes[-1], "Sil") for phonemes in syllables}
    categories = {
        "within_syllable": within,
        "cross_syllable": cross,
        "silence_onset": onset,
        "silence_coda": coda,
    }
    roles: dict[Edge, list[str]] = {}
    histogram: Counter[str] = Counter()
    for edge in required:
        edge_roles = sorted(
            name for name, values in categories.items() if edge in values
        )
        roles[edge] = edge_roles
        if len(edge_roles) == 1:
            histogram[edge_roles[0]] += 1
        elif not edge_roles:
            histogram["unclassified"] += 1
        else:
            histogram["multiple_roles"] += 1
    return roles, histogram


def greedy_cover(
    required: set[Edge],
    candidates: dict[frozenset[Edge], dict[str, object]],
) -> tuple[list[dict[str, object]], set[Edge]]:
    items = list(candidates.items())
    heap: list[tuple[int, tuple[object, ...], int]] = []
    for index, (coverage, item) in enumerate(items):
        heapq.heappush(
            heap,
            (
                -len(coverage),
                prompt_score(tuple(item["pinyin"]), tuple(item["phonemes"])),
                index,
            ),
        )

    uncovered = set(required)
    selected: list[dict[str, object]] = []
    while uncovered and heap:
        negative_estimate, score, index = heapq.heappop(heap)
        coverage, item = items[index]
        new_edges = coverage & uncovered
        gain = len(new_edges)
        if gain == 0:
            continue
        if gain != -negative_estimate:
            heapq.heappush(heap, (-gain, score, index))
            continue
        selected_item = dict(item)
        selected_item["new_edges"] = [list(edge) for edge in sorted(new_edges)]
        selected.append(selected_item)
        uncovered.difference_update(new_edges)
    return selected, uncovered


def edge_witnesses(
    required: set[Edge],
    candidates: dict[frozenset[Edge], dict[str, object]],
) -> tuple[list[dict[str, object]], set[Edge]]:
    by_edge: dict[Edge, list[dict[str, object]]] = {}
    for coverage, item in candidates.items():
        for edge in coverage:
            by_edge.setdefault(edge, []).append(item)
    witnesses: list[dict[str, object]] = []
    missing: set[Edge] = set()
    for edge in sorted(required):
        options = by_edge.get(edge, [])
        if not options:
            missing.add(edge)
            continue
        best = min(
            options,
            key=lambda item: prompt_score(
                tuple(item["pinyin"]), tuple(item["phonemes"])
            ),
        )
        target_index = [tuple(value) for value in best["covered_edges"]].index(edge)
        witnesses.append(
            {
                "edge": list(edge),
                "kind": best["kind"],
                "pinyin": best["pinyin"],
                "phonemes": best["phonemes"],
                "target_transition_index": target_index,
            }
        )
    return witnesses, missing


def flatten_edges(items: Iterable[dict[str, object]], key: str) -> set[Edge]:
    result: set[Edge] = set()
    for item in items:
        for edge in item[key]:
            result.add(tuple(edge))
    return result


def write_json(path: Path | None, value: object) -> None:
    text = json.dumps(value, ensure_ascii=False, indent=2) + "\n"
    if path is None:
        sys.stdout.write(text)
        return
    if path.exists():
        raise PromptError(f"output already exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("graph", type=Path)
    parser.add_argument("g2pa_inventory", type=Path)
    parser.add_argument("--art-set", choices=("intersection", "union"), default="intersection")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--allow-uncovered", action="store_true")
    args = parser.parse_args()

    try:
        edges, bank_count = read_graph(args.graph.resolve(), args.art_set)
        syllables, g2pa_summary = read_g2pa_inventory(args.g2pa_inventory.resolve())
        roles, role_histogram = classify_edges(edges, syllables)
        candidates = build_candidates(edges, syllables)
        witnesses, witness_missing = edge_witnesses(edges, candidates)
        prompts, uncovered = greedy_cover(edges, candidates)
        realized = flatten_edges(prompts, "covered_edges")
        if realized - edges:
            raise PromptError("selected prompts contain edges outside the ART graph")
        if (witness_missing or uncovered) and not args.allow_uncovered:
            sample = sorted(witness_missing | uncovered)[:20]
            raise PromptError(f"ART edges lack a valid pinyin prompt: {sample!r}")

        max_edges_per_prompt = max(
            (len(item["covered_edges"]) for item in candidates.values()),
            default=0,
        )
        kind_counts = Counter(item["kind"] for item in prompts)
        cross_only_edges = {
            edge for edge, edge_roles in roles.items() if edge_roles == ["cross_syllable"]
        }
        multiple_role_edges = {
            edge for edge, edge_roles in roles.items() if len(edge_roles) > 1
        }
        unclassified_edges = {
            edge for edge, edge_roles in roles.items() if not edge_roles
        }
        model_lower_bound = len(cross_only_edges)
        result = {
            "format": "vocaloid-chinese-art-prompt-plan-v1",
            "source": {
                "art_set": args.art_set,
                "bank_count": bank_count,
                "edge_sha256": canonical_edge_hash(edges),
                "g2pa_inventory_sha256": g2pa_summary.get("inventory_sha256"),
            },
            "summary": {
                "required_edges": len(edges),
                "canonical_phoneme_syllables": len(syllables),
                "verified_g2pa_spellings": g2pa_summary.get("tokens"),
                "candidate_coverage_sets": len(candidates),
                "edge_role_histogram": dict(sorted(role_histogram.items())),
                "multiple_role_edges": [
                    list(edge) for edge in sorted(multiple_role_edges)
                ],
                "unclassified_role_edges": [
                    list(edge) for edge in sorted(unclassified_edges)
                ],
                "maximum_edges_per_prompt": max_edges_per_prompt,
                "simple_prompt_lower_bound": math.ceil(
                    len(edges) / max_edges_per_prompt
                )
                if max_edges_per_prompt
                else 0,
                "two_syllable_model_lower_bound": model_lower_bound,
                "selected_prompts": len(prompts),
                "model_optimal": (
                    len(prompts) == model_lower_bound
                    and not uncovered
                    and not multiple_role_edges
                    and not unclassified_edges
                ),
                "selected_prompt_kind_histogram": dict(sorted(kind_counts.items())),
                "covered_edges": len(realized),
                "uncovered_edges": [list(edge) for edge in sorted(uncovered)],
                "witnessed_edges": len(witnesses),
                "witness_missing_edges": [
                    list(edge) for edge in sorted(witness_missing)
                ],
            },
            "recording_prompts": prompts,
            "edge_witnesses": witnesses,
            "limitations": [
                "Prompts are silence-bounded one- or two-syllable pinyin clips, not natural sentences.",
                "Optimality applies only to this at-most-two-syllable, explicit-silence clip model.",
                "Tone, duration, breath grouping, pitch layers, and acoustic segmentation are not assigned.",
                "A native G2PA match proves phoneme spelling, not that a trained bank will synthesize well.",
            ],
        }
        write_json(args.output.resolve() if args.output else None, result)
        if args.output:
            print(f"required_edges={len(edges)}")
            print(f"canonical_phoneme_syllables={len(syllables)}")
            print(f"candidate_coverage_sets={len(candidates)}")
            print(f"selected_prompts={len(prompts)}")
            print(f"covered_edges={len(realized)}")
            print(f"uncovered_edges={len(uncovered)}")
            print(f"edge_sha256={canonical_edge_hash(edges)}")
        return 0 if not witness_missing and not uncovered else 3
    except (OSError, UnicodeError, json.JSONDecodeError, PromptError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
