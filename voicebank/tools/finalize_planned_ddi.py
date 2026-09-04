#!/usr/bin/env python3
"""Inject a tree plan's DDB offsets into a native multi-unit skeleton."""

from __future__ import annotations

import argparse
import json
import math
import struct
import sys
from pathlib import Path
from typing import Any

import analyze_recording_units as analysis_tools
import assemble_recording_units_ddb as ddb_tools
import finalize_minimal_articulation_ddi as articulation
import finalize_sil_a_ddi
import finalize_stationary_ddi as stationary
import plan_ddi_tree as plan_tools
import probe_frm2


class PlannedFinalizeError(Exception):
    pass


def load_plan(path: Path) -> dict[str, Any]:
    value = analysis_tools.read_json(path)
    if not isinstance(value, dict) or value.get("format") != "vocaloid-ddi-tree-plan-v1":
        raise PlannedFinalizeError("unsupported tree-plan format")
    summary = value.get("summary")
    source = value.get("source")
    if not isinstance(summary, dict) or not isinstance(source, dict):
        raise PlannedFinalizeError("tree plan lacks summary or source")
    content = {
        key: value.get(key)
        for key in (
            "language_id",
            "phonemes",
            "stationary",
            "articulations",
            "part_order",
        )
    }
    if summary.get("tree_plan_sha256") != analysis_tools.canonical_json_hash(content):
        raise PlannedFinalizeError("tree-plan canonical SHA-256 differs")
    if summary.get("approval_complete") is not False:
        raise PlannedFinalizeError("tree plan must remain unapproved")
    return value


def load_ddb_manifest(path: Path, plan: dict[str, Any]) -> tuple[dict[str, Any], dict[str, dict[str, Any]]]:
    value, units, coverage_complete = plan_tools.load_ddb_manifest(path)
    source = plan["source"]
    if source.get("ddb_manifest_file_sha256") != analysis_tools.file_sha256(path):
        raise PlannedFinalizeError("tree plan DDB-manifest file SHA-256 differs")
    if source.get("ddb_manifest_canonical_sha256") != value["summary"].get(
        "ddb_manifest_sha256"
    ):
        raise PlannedFinalizeError("tree plan DDB-manifest canonical SHA-256 differs")
    if source.get("ddb_sha256") != value["output"].get("ddb_sha256"):
        raise PlannedFinalizeError("tree plan DDB SHA-256 differs")
    if plan["summary"].get("coverage_complete") is not coverage_complete:
        raise PlannedFinalizeError("tree plan coverage differs from DDB coverage")
    return value, {item["unit_id"]: item for item in units}


def encoded_name(value: str) -> bytes:
    raw = value.encode("ascii")
    return struct.pack("<I", len(raw)) + raw


def validate_phdc(skeleton: bytes, plan: dict[str, Any]) -> dict[str, Any]:
    phdc = skeleton.find(b"PHDC")
    if phdc < 0 or skeleton.find(b"PHDC", phdc + 4) >= 0 or phdc + 16 > len(skeleton):
        raise PlannedFinalizeError("skeleton must contain exactly one complete PHDC")
    flags, count = struct.unpack_from("<II", skeleton, phdc + 8)
    expected = [(item["name"], item["unvoiced"]) for item in plan["phonemes"]]
    actual = [
        finalize_sil_a_ddi.read_phoneme_entry(
            skeleton,
            phdc + 16 + index * finalize_sil_a_ddi.PHONEME_ENTRY_SIZE,
        )
        for index in range(count)
    ]
    if actual != expected or count != plan["summary"].get("phonemes"):
        raise PlannedFinalizeError("skeleton PHDC differs from the tree plan")
    return {
        "flags": flags,
        "phonemes": count,
        "voiced": sum(not item[1] for item in actual),
        "unvoiced": sum(item[1] for item in actual),
    }


def chunk_positions(data: bytes, magic: bytes) -> list[int]:
    positions: list[int] = []
    cursor = 0
    while True:
        cursor = data.find(magic, cursor)
        if cursor < 0:
            return positions
        positions.append(cursor)
        cursor += len(magic)


def patch_unit_indexes(skeleton: bytes, plan: dict[str, Any]) -> tuple[bytes, dict[str, int]]:
    result = bytearray(skeleton)
    stau_positions = chunk_positions(skeleton, b"STAu")
    artu_positions = chunk_positions(skeleton, b"ARTu")
    if len(stau_positions) != len(plan["stationary"]):
        raise PlannedFinalizeError("skeleton STAu count differs from the tree plan")
    if len(artu_positions) != len(plan["articulations"]):
        raise PlannedFinalizeError("skeleton ARTu count differs from the tree plan")
    for position, item in zip(stau_positions, plan["stationary"]):
        if result[position + 8 : position + 16] != b"\x01\x00\x00\x00\x00\x00\x00\x00":
            raise PlannedFinalizeError("unexpected STAu header before index patch")
        struct.pack_into("<I", result, position + 16, item["stationary_index"])
    for position, item in zip(artu_positions, plan["articulations"]):
        if result[position + 8 : position + 16] != b"\x00" * 8:
            raise PlannedFinalizeError("unexpected ARTu header before index patch")
        if result[position + 20 : position + 28] != b"\x00" * 8:
            raise PlannedFinalizeError("unexpected ARTu flags before index patch")
        struct.pack_into("<I", result, position + 16, item["target_index"])
    return bytes(result), {
        "stationary_indexes_patched": len(stau_positions),
        "articulation_indexes_patched": len(artu_positions),
    }


def validate_tree_order(
    skeleton: bytes,
    plan: dict[str, Any],
    sta_positions: list[int],
    art_positions: list[int],
) -> None:
    stationary_groups = plan["stationary"]
    articulations = plan["articulations"]
    if len(sta_positions) != plan["summary"].get("stationary_parts"):
        raise PlannedFinalizeError("skeleton STAp count differs from the tree plan")
    if len(art_positions) != plan["summary"].get("articulation_parts"):
        raise PlannedFinalizeError("skeleton ARTp count differs from the tree plan")

    sta_cursor = 0
    first_art = art_positions[0] if art_positions else len(skeleton)
    for group_index, group in enumerate(stationary_groups):
        count = len(group["unit_ids"])
        last = sta_cursor + count - 1
        if last < sta_cursor or last >= len(sta_positions):
            raise PlannedFinalizeError("stationary group has an invalid part count")
        next_start = (
            sta_positions[sta_cursor + count]
            if sta_cursor + count < len(sta_positions)
            else first_art
        )
        if skeleton.find(
            encoded_name(group["phoneme"]), sta_positions[last], next_start
        ) < 0:
            raise PlannedFinalizeError(
                f"skeleton STAp order does not end in phoneme {group['phoneme']}"
            )
        sta_cursor += count

    art_cursor = 0
    source_spans: list[tuple[str, int, int]] = []
    source_start = 0
    current_source: str | None = None
    for edge_index, group in enumerate(articulations):
        count = len(group["unit_ids"])
        if count <= 0 or art_cursor + count > len(art_positions):
            raise PlannedFinalizeError("articulation group has an invalid part count")
        for local_index in range(count):
            part_index = art_cursor + local_index
            part_end = (
                art_positions[part_index + 1]
                if part_index + 1 < len(art_positions)
                else len(skeleton)
            )
            if skeleton.find(
                encoded_name("default"), art_positions[part_index], part_end
            ) < 0:
                raise PlannedFinalizeError(
                    f"ARTp {part_index} does not serialize the default part name"
                )
        next_edge_start = (
            art_positions[art_cursor + count]
            if art_cursor + count < len(art_positions)
            else len(skeleton)
        )
        if skeleton.find(
            encoded_name(group["target"]),
            art_positions[art_cursor + count - 1],
            next_edge_start,
        ) < 0:
            raise PlannedFinalizeError(
                f"skeleton ART order does not end in target {group['target']}"
            )
        if current_source is None:
            current_source = group["source"]
            source_start = art_cursor
        elif group["source"] != current_source:
            source_spans.append((current_source, source_start, art_cursor))
            current_source = group["source"]
            source_start = art_cursor
        art_cursor += count
        if edge_index + 1 == len(articulations) and current_source is not None:
            source_spans.append((current_source, source_start, art_cursor))

    for span_index, (source, start, end) in enumerate(source_spans):
        span_end = (
            art_positions[source_spans[span_index + 1][1]]
            if span_index + 1 < len(source_spans)
            else len(skeleton)
        )
        if skeleton.find(
            encoded_name(source), art_positions[end - 1], span_end
        ) < 0:
            raise PlannedFinalizeError(
                f"skeleton ART source order does not end in {source}"
            )


def ddb_info(item: dict[str, Any]) -> stationary.DdbInfo:
    try:
        info = stationary.DdbInfo(
            frame_offsets=[int(value) for value in item["frame_offsets"]],
            snd_offset=int(item["snd_chunk_offset"]),
            snd_size=int(item["snd_chunk_size"]),
            sample_rate=int(item["sample_rate"]),
            channels=int(item["channels"]),
            pcm_count=int(item["pcm_count"]),
        )
    except (KeyError, TypeError, ValueError) as error:
        raise PlannedFinalizeError(
            f"invalid DDB unit {item.get('unit_id')}: {error}"
        ) from error
    if (
        not info.frame_offsets
        or info.sample_rate != stationary.SAMPLE_RATE
        or info.channels != 1
        or info.pcm_count
        != len(info.frame_offsets) * stationary.HOP_SAMPLES
        + 2 * stationary.ANALYSIS_MARGIN_SAMPLES
        or info.frame_offsets[0] < item.get("base_offset", -1)
        or info.frame_offsets[-1] >= info.snd_offset
        or info.snd_offset + info.snd_size != item.get("end_offset")
    ):
        raise PlannedFinalizeError(f"DDB unit {item.get('unit_id')} offsets differ")
    return info


def finite_unit_f0(item: dict[str, Any]) -> float:
    try:
        value = float(item["f0_hz"])
    except (KeyError, TypeError, ValueError) as error:
        raise PlannedFinalizeError(f"unit {item.get('unit_id')} has no F0") from error
    if not math.isfinite(value) or value <= 0:
        raise PlannedFinalizeError(f"unit {item.get('unit_id')} has invalid F0")
    return value


def art_alignments(item: dict[str, Any]) -> tuple[tuple[int, int, int, int], ...]:
    raw = item.get("frame_alignments")
    if (
        not isinstance(raw, list)
        or len(raw) != 2
        or any(
            not isinstance(group, list)
            or len(group) != 4
            or any(isinstance(value, bool) or not isinstance(value, int) for value in group)
            for group in raw
        )
    ):
        raise PlannedFinalizeError(f"unit {item.get('unit_id')} has invalid alignments")
    return tuple(tuple(group) for group in raw)


def validate_plan_bindings(
    plan: dict[str, Any],
    units: dict[str, dict[str, Any]],
) -> dict[str, tuple[int, int]]:
    phoneme_indexes = {
        item["name"]: index for index, item in enumerate(plan["phonemes"])
    }
    stationary_order: list[str] = []
    for group in plan["stationary"]:
        if group.get("phoneme") not in phoneme_indexes:
            raise PlannedFinalizeError("planned STA phoneme is outside PHDC")
        unit_ids = group.get("unit_ids")
        if not isinstance(unit_ids, list) or not unit_ids:
            raise PlannedFinalizeError("planned STA group has no units")
        for unit_id in unit_ids:
            item = units.get(unit_id)
            if (
                item is None
                or item.get("kind") != "stationary"
                or item.get("phoneme") != group["phoneme"]
            ):
                raise PlannedFinalizeError(f"STA plan binding differs for {unit_id}")
        stationary_order.extend(unit_ids)

    articulation_order: list[str] = []
    source_offsets: dict[str, tuple[int, int]] = {}
    for group in plan["articulations"]:
        source = group.get("source")
        target = group.get("target")
        unit_ids = group.get("unit_ids")
        snd_offsets = group.get("snd_source_offsets")
        epr_offsets = group.get("epr_source_offsets")
        if (
            source not in phoneme_indexes
            or target not in phoneme_indexes
            or group.get("target_index") != phoneme_indexes[target]
            or not isinstance(unit_ids, list)
            or not unit_ids
            or not isinstance(snd_offsets, list)
            or not isinstance(epr_offsets, list)
            or len(unit_ids) != len(snd_offsets)
            or len(unit_ids) != len(epr_offsets)
        ):
            raise PlannedFinalizeError(f"invalid ART plan binding for {source}->{target}")
        previous_end = 0
        for index, (unit_id, snd_offset, epr_offset) in enumerate(
            zip(unit_ids, snd_offsets, epr_offsets)
        ):
            item = units.get(unit_id)
            if (
                item is None
                or item.get("kind") != "articulation"
                or item.get("edge") != [source, target]
                or isinstance(snd_offset, bool)
                or not isinstance(snd_offset, int)
                or isinstance(epr_offset, bool)
                or not isinstance(epr_offset, int)
                or snd_offset <= 0
                or epr_offset != snd_offset + item.get("snd_chunk_size", -1) + 7
                or (index > 0 and snd_offset != previous_end)
            ):
                raise PlannedFinalizeError(f"ART plan binding differs for {unit_id}")
            frame_bytes = item["snd_chunk_offset"] - item["base_offset"]
            previous_end = epr_offset + frame_bytes
            source_offsets[unit_id] = snd_offset, epr_offset
        articulation_order.extend(unit_ids)

    if stationary_order != plan["part_order"].get("stationary_unit_ids"):
        raise PlannedFinalizeError("planned STA part order differs")
    if articulation_order != plan["part_order"].get("articulation_unit_ids"):
        raise PlannedFinalizeError("planned ART part order differs")
    if len(stationary_order) + len(articulation_order) != len(units):
        raise PlannedFinalizeError("tree plan does not bind every DDB unit")
    if len(set(stationary_order + articulation_order)) != len(units):
        raise PlannedFinalizeError("tree plan binds a DDB unit more than once")
    return source_offsets


def build(
    plan_path: Path,
    ddb_manifest_path: Path,
    skeleton_path: Path,
    ddb_path: Path,
    output_path: Path,
    report_path: Path,
    unknown2: float,
    dynamics: float,
    tempo: float,
) -> dict[str, Any]:
    if output_path.exists() or report_path.exists():
        raise PlannedFinalizeError("DDI output or report already exists")
    if output_path.stem != ddb_path.stem:
        raise PlannedFinalizeError("DDI and DDB must use the same stem")
    plan = load_plan(plan_path)
    ddb_manifest, units = load_ddb_manifest(ddb_manifest_path, plan)
    planned_art_offsets = validate_plan_bindings(plan, units)
    if not ddb_path.is_file() or analysis_tools.file_sha256(ddb_path) != (
        ddb_manifest["output"]["ddb_sha256"]
    ):
        raise PlannedFinalizeError("DDB file SHA-256 differs from its manifest")
    original_skeleton = skeleton_path.read_bytes()
    skeleton, index_patches = patch_unit_indexes(original_skeleton, plan)
    phdc_report = validate_phdc(skeleton, plan)
    sta_positions = stationary.stationary_positions(skeleton)
    art_positions = articulation.articulation_positions(skeleton)
    validate_tree_order(skeleton, plan, sta_positions, art_positions)

    sta_ids = plan["part_order"]["stationary_unit_ids"]
    art_ids = plan["part_order"]["articulation_unit_ids"]
    insertion: list[tuple[int, str, str, int]] = [
        (position, "stationary", unit_id, index)
        for index, (position, unit_id) in enumerate(zip(sta_positions, sta_ids))
    ] + [
        (position, "articulation", unit_id, index)
        for index, (position, unit_id) in enumerate(zip(art_positions, art_ids))
    ]
    if len(insertion) != len(units) or {item[2] for item in insertion} != set(units):
        raise PlannedFinalizeError("tree insertion order does not cover every DDB unit")

    sta_reports: list[dict[str, Any] | None] = [None] * len(sta_ids)
    art_reports: list[dict[str, Any] | None] = [None] * len(art_ids)
    ddi = skeleton
    for position, kind, unit_id, order_index in sorted(insertion, reverse=True):
        item = units[unit_id]
        info = ddb_info(item)
        f0_hz = finite_unit_f0(item)
        if kind == "stationary":
            ddi, report = stationary.insert_stationary_at(
                ddi,
                position,
                info,
                f0_hz,
                unknown2,
                dynamics,
                tempo,
            )
            if report["snd_pointer"] != item["snd_core_pointer"]:
                raise PlannedFinalizeError(f"STA unit {unit_id} pointer differs")
            report.update({"unit_id": unit_id, "phoneme": item.get("phoneme")})
            sta_reports[order_index] = report
        else:
            alignments = art_alignments(item)
            split = alignments[0][1]
            ddi, report = articulation.insert_articulation_at(
                ddi,
                position,
                info,
                f0_hz,
                split,
                unknown2,
                dynamics,
                tempo,
                alignments,
            )
            if (
                report["snd_payload_pointer"] != item["snd_payload_pointer"]
                or report["snd_core_pointer"] != item["snd_core_pointer"]
                or (
                    report["snd_source_offset"],
                    report["epr_source_offset"],
                )
                != planned_art_offsets[unit_id]
            ):
                raise PlannedFinalizeError(
                    f"ART unit {unit_id} pointers or source offsets differ"
                )
            report.update({"unit_id": unit_id, "edge": item.get("edge")})
            art_reports[order_index] = report

    normalized = bytearray(ddi)
    authentication = stationary.insert_dbse_authentication(normalized, output_path.stem)
    normalization = stationary.normalize_compact_ddi(normalized)
    final = bytes(normalized)
    stationary.write_atomic(output_path, final)
    output_sha = analysis_tools.file_sha256(output_path)
    result = {
        "format": "vocaloid-planned-ddi-build-v1",
        "source": {
            "tree_plan_file_sha256": analysis_tools.file_sha256(plan_path),
            "tree_plan_canonical_sha256": plan["summary"]["tree_plan_sha256"],
            "ddb_manifest_file_sha256": analysis_tools.file_sha256(ddb_manifest_path),
            "ddb_manifest_canonical_sha256": ddb_manifest["summary"][
                "ddb_manifest_sha256"
            ],
            "skeleton_sha256": analysis_tools.file_sha256(skeleton_path),
            "ddb_sha256": ddb_manifest["output"]["ddb_sha256"],
        },
        "output": {
            "ddi": output_path.name,
            "ddi_bytes": len(final),
            "ddi_sha256": output_sha,
        },
        "summary": {
            "phonemes": phdc_report["phonemes"],
            "stationary_parts": len(sta_ids),
            "articulation_parts": len(art_ids),
            "coverage_complete": plan["summary"]["coverage_complete"],
            "approval_complete": False,
            "native_loader_valid": False,
        },
        "phonetic_dictionary": phdc_report,
        "stationary_units": sta_reports,
        "articulation_units": art_reports,
        "normalization": normalization,
        "index_patches": index_patches,
        "authentication": authentication,
        "limitations": [
            "The DDI is structurally finalized from unapproved units and is not a licensed product.",
            "native_loader_valid remains false until an independent DSE load validates this exact DDI/DDB pair.",
            "Partial input intentionally creates a partial STA/ART tree with the full PHDC inventory.",
        ],
    }
    analysis_tools.write_json_atomic(report_path, result)
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("tree_plan", type=Path)
    parser.add_argument("ddb_manifest", type=Path)
    parser.add_argument("skeleton", type=Path)
    parser.add_argument("ddb", type=Path)
    parser.add_argument("output_ddi", type=Path)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--unknown2", type=float, default=0.0)
    parser.add_argument("--dynamics", type=float, default=0.6)
    parser.add_argument("--tempo", type=float, default=90.0)
    args = parser.parse_args()
    for name in ("unknown2", "dynamics", "tempo"):
        if not math.isfinite(getattr(args, name)):
            parser.error(f"--{name} must be finite")
    try:
        output = args.output_ddi.resolve()
        report_path = (
            args.report.resolve()
            if args.report is not None
            else output.with_name(output.stem + ".ddi_manifest.json")
        )
        result = build(
            args.tree_plan.resolve(),
            args.ddb_manifest.resolve(),
            args.skeleton.resolve(),
            args.ddb.resolve(),
            output,
            report_path,
            args.unknown2,
            args.dynamics,
            args.tempo,
        )
        for name, value in result["summary"].items():
            print(f"{name}={value}")
        for name, value in result["output"].items():
            print(f"{name}={value}")
        print(f"report={report_path}")
        return 0 if result["summary"]["coverage_complete"] else 3
    except (
        OSError,
        UnicodeError,
        json.JSONDecodeError,
        OverflowError,
        ValueError,
        struct.error,
        PlannedFinalizeError,
        plan_tools.PlanError,
        analysis_tools.AnalysisError,
        stationary.FinalizeError,
        probe_frm2.ProbeError,
    ) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
