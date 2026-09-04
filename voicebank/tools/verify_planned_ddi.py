#!/usr/bin/env python3
"""Cross-check a planned DDI with the independent public ddb-tools parser."""

from __future__ import annotations

import argparse
import importlib.util
import json
import math
import sys
from pathlib import Path
from typing import Any

import analyze_recording_units as analysis_tools
import plan_ddi_tree as plan_tools


class VerifyError(Exception):
    pass


def parse_pointer(value: Any, context: str) -> int:
    if not isinstance(value, str):
        raise VerifyError(f"{context} pointer is not a string")
    try:
        encoded = value.split("=", 1)[1].split("_", 1)[0]
        return int(encoded, 16)
    except (IndexError, ValueError) as error:
        raise VerifyError(f"{context} pointer has an unexpected form: {value!r}") from error


def parse_offsets(value: Any, context: str) -> list[int]:
    if not isinstance(value, list):
        raise VerifyError(f"{context} offsets are not a list")
    return [parse_pointer(item, context) for item in value]


def require_close(actual: Any, expected: float, context: str) -> None:
    try:
        value = float(actual)
    except (TypeError, ValueError) as error:
        raise VerifyError(f"{context} is not numeric") from error
    if not math.isclose(value, expected, rel_tol=1e-7, abs_tol=1e-7):
        raise VerifyError(f"{context} differs: {value} != {expected}")


def load_public_parser(ddb_tools_root: Path) -> type:
    module_path = ddb_tools_root / "utils" / "ddi_utils.py"
    if not module_path.is_file():
        raise VerifyError(f"ddb-tools parser not found: {module_path}")
    try:
        spec = importlib.util.spec_from_file_location(
            "voicebank_external_ddb_tools_ddi_utils", module_path
        )
        if spec is None or spec.loader is None:
            raise ImportError("cannot create a module spec")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
    except (ImportError, OSError) as error:
        raise VerifyError(f"cannot import public ddb-tools parser: {error}") from error
    model = getattr(module, "DDIModel", None)
    if not isinstance(model, type):
        raise VerifyError("ddb-tools does not expose DDIModel")
    return model


def verify(
    plan_path: Path,
    ddb_manifest_path: Path,
    ddi_path: Path,
    ddb_path: Path,
    ddb_tools_root: Path,
) -> dict[str, Any]:
    plan = analysis_tools.read_json(plan_path)
    if not isinstance(plan, dict) or plan.get("format") != "vocaloid-ddi-tree-plan-v1":
        raise VerifyError("unsupported tree-plan format")
    plan_content = {
        key: plan.get(key)
        for key in ("language_id", "phonemes", "stationary", "articulations", "part_order")
    }
    if plan.get("summary", {}).get("tree_plan_sha256") != analysis_tools.canonical_json_hash(
        plan_content
    ):
        raise VerifyError("tree-plan canonical SHA-256 differs")

    ddb_manifest, units, coverage_complete = plan_tools.load_ddb_manifest(ddb_manifest_path)
    units_by_id = {item["unit_id"]: item for item in units}
    if analysis_tools.file_sha256(ddb_path) != ddb_manifest["output"].get("ddb_sha256"):
        raise VerifyError("DDB file SHA-256 differs from its manifest")
    source = plan.get("source", {})
    if (
        source.get("ddb_manifest_file_sha256") != analysis_tools.file_sha256(ddb_manifest_path)
        or source.get("ddb_manifest_canonical_sha256")
        != ddb_manifest["summary"].get("ddb_manifest_sha256")
        or source.get("ddb_sha256") != ddb_manifest["output"].get("ddb_sha256")
        or plan.get("summary", {}).get("coverage_complete") is not coverage_complete
    ):
        raise VerifyError("tree plan is not bound to this DDB manifest")

    model_type = load_public_parser(ddb_tools_root)
    try:
        model = model_type(ddi_path.read_bytes())
        model.read()
    except (AssertionError, KeyError, UnicodeError, ValueError) as error:
        raise VerifyError(f"public ddb-tools parser rejected the DDI: {error}") from error

    voiced = [item["name"] for item in plan["phonemes"] if not item["unvoiced"]]
    unvoiced = [item["name"] for item in plan["phonemes"] if item["unvoiced"]]
    if model.phdc_data.get("phoneme") != {"voiced": voiced, "unvoiced": unvoiced}:
        raise VerifyError("public parser PHDC differs from the tree plan")
    phoneme_indexes = {item["name"]: index for index, item in enumerate(plan["phonemes"])}

    parsed_stationary_parts = 0
    expected_stationary_indexes = {item["stationary_index"] for item in plan["stationary"]}
    if set(model.sta_data) != expected_stationary_indexes:
        raise VerifyError("public parser STA indexes differ from the tree plan")
    for group in plan["stationary"]:
        parsed = model.sta_data[group["stationary_index"]]
        if parsed.get("phoneme") != group["phoneme"]:
            raise VerifyError(f"STA phoneme differs for {group['phoneme']}")
        parts = parsed.get("stap")
        expected_keys = [str(index) for index in range(len(group["unit_ids"]))]
        if not isinstance(parts, dict) or list(parts) != expected_keys:
            raise VerifyError(f"STA part order differs for {group['phoneme']}")
        for part_key, unit_id in zip(expected_keys, group["unit_ids"]):
            item = units_by_id[unit_id]
            if item.get("kind") != "stationary" or item.get("phoneme") != group["phoneme"]:
                raise VerifyError(f"STA plan binding differs for {unit_id}")
            part = parts[part_key]
            if parse_offsets(part.get("epr"), f"STA {unit_id}") != item["frame_offsets"]:
                raise VerifyError(f"STA {unit_id} frame offsets differ")
            if parse_pointer(part.get("snd"), f"STA {unit_id}") != item["snd_core_pointer"]:
                raise VerifyError(f"STA {unit_id} SND core pointer differs")
            if part.get("fs") != item["sample_rate"]:
                raise VerifyError(f"STA {unit_id} sample rate differs")
            require_close(
                part.get("duration"),
                item["pcm_count"] / item["sample_rate"],
                f"STA {unit_id} duration",
            )
            parsed_stationary_parts += 1

    expected_edges = {(item["source"], item["target"]) for item in plan["articulations"]}
    parsed_edges: set[tuple[str, str]] = set()
    parsed_articulation_parts = 0
    for source_index, source in model.art_data.items():
        source_name = source.get("phoneme")
        if source_index != phoneme_indexes.get(source_name):
            raise VerifyError(f"ART source index differs for {source_name}")
        for target_index, target in source.get("artu", {}).items():
            target_name = target.get("phoneme")
            if target_index != phoneme_indexes.get(target_name):
                raise VerifyError(f"ART target index differs for {source_name}->{target_name}")
            parsed_edges.add((source_name, target_name))
    if parsed_edges != expected_edges:
        raise VerifyError("public parser ART edges differ from the tree plan")

    for group in plan["articulations"]:
        source_index = phoneme_indexes[group["source"]]
        target_index = group["target_index"]
        target = model.art_data[source_index]["artu"][target_index]
        if target.get("phoneme") != group["target"]:
            raise VerifyError(f"ART target differs for {group['source']}->{group['target']}")
        parts = target.get("artp")
        if not isinstance(parts, dict) or list(parts) != group["snd_source_offsets"]:
            raise VerifyError(f"ART part order differs for {group['source']}->{group['target']}")
        for source_offset, unit_id in zip(group["snd_source_offsets"], group["unit_ids"]):
            item = units_by_id[unit_id]
            if item.get("kind") != "articulation" or item.get("edge") != [
                group["source"],
                group["target"],
            ]:
                raise VerifyError(f"ART plan binding differs for {unit_id}")
            part = parts[source_offset]
            if parse_offsets(part.get("epr"), f"ART {unit_id}") != item["frame_offsets"]:
                raise VerifyError(f"ART {unit_id} frame offsets differ")
            # ddb-tools subtracts the 18-byte SND header when formatting ART pointers.
            if parse_pointer(part.get("snd"), f"ART {unit_id}") + 18 != item["snd_payload_pointer"]:
                raise VerifyError(f"ART {unit_id} SND payload pointer differs")
            if parse_pointer(part.get("snd_start"), f"ART {unit_id}") + 18 != item["snd_core_pointer"]:
                raise VerifyError(f"ART {unit_id} SND core pointer differs")
            expected_alignment = [
                {"start": values[0], "end": values[1], "start2": values[2], "end2": values[3]}
                for values in item["frame_alignments"]
            ]
            if part.get("frame_align") != expected_alignment:
                raise VerifyError(f"ART {unit_id} frame alignment differs")
            if part.get("fs") != item["sample_rate"]:
                raise VerifyError(f"ART {unit_id} sample rate differs")
            require_close(
                part.get("duration"),
                item["pcm_count"] / item["sample_rate"],
                f"ART {unit_id} duration",
            )
            parsed_articulation_parts += 1

    expected_unit_ids = set(plan["part_order"]["stationary_unit_ids"]) | set(
        plan["part_order"]["articulation_unit_ids"]
    )
    if expected_unit_ids != set(units_by_id):
        raise VerifyError("tree plan does not cover every DDB unit")
    return {
        "format": "vocaloid-planned-ddi-public-verification-v1",
        "source": {
            "tree_plan_sha256": analysis_tools.file_sha256(plan_path),
            "ddb_manifest_sha256": analysis_tools.file_sha256(ddb_manifest_path),
            "ddi_sha256": analysis_tools.file_sha256(ddi_path),
            "ddb_sha256": analysis_tools.file_sha256(ddb_path),
            "ddb_tools_parser_sha256": analysis_tools.file_sha256(
                ddb_tools_root / "utils" / "ddi_utils.py"
            ),
        },
        "summary": {
            "phonemes": len(plan["phonemes"]),
            "voiced_phonemes": len(voiced),
            "unvoiced_phonemes": len(unvoiced),
            "stationary_parts": parsed_stationary_parts,
            "articulation_edges": len(parsed_edges),
            "articulation_parts": parsed_articulation_parts,
            "coverage_complete": coverage_complete,
            "approval_complete": False,
            "public_parser_valid": True,
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("tree_plan", type=Path)
    parser.add_argument("ddb_manifest", type=Path)
    parser.add_argument("ddi", type=Path)
    parser.add_argument("ddb", type=Path)
    parser.add_argument("ddb_tools", type=Path)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    try:
        result = verify(
            args.tree_plan.resolve(),
            args.ddb_manifest.resolve(),
            args.ddi.resolve(),
            args.ddb.resolve(),
            args.ddb_tools.resolve(),
        )
        if args.report is not None:
            report = args.report.resolve()
            if report.exists():
                raise VerifyError(f"report already exists: {report}")
            analysis_tools.write_json_atomic(report, result)
            print(f"report={report}")
        for name, value in result["summary"].items():
            print(f"{name}={value}")
        return 0 if result["summary"]["coverage_complete"] else 3
    except (
        OSError,
        UnicodeError,
        json.JSONDecodeError,
        KeyError,
        TypeError,
        ValueError,
        VerifyError,
        plan_tools.PlanError,
        analysis_tools.AnalysisError,
    ) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
