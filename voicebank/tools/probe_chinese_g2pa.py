#!/usr/bin/env python3
"""Probe VOCALOID's installed Chinese G2PA against the repository pinyin map."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
from pathlib import Path


class ProbeError(Exception):
    pass


ENTRY_RE = re.compile(r'^\s*\["([^"]+)"\]\s*=\s*"([^"]*)"')
TOKEN_RE = re.compile(
    r"^token=([^\t]+)\tcan_convert=(True|False)\tcandidates=(\d+)$"
)
CANDIDATE_RE = re.compile(
    r"^candidate=(\d+)\tsyllable=([^\t]*)\tphonemes=(.*)$"
)


def read_pinyin_map(path: Path) -> dict[str, str]:
    result: dict[str, str] = {}
    inside = False
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        if not inside:
            if "Pinyin2Xsampa" in line:
                inside = True
            continue
        if re.match(r"^\s*};", line):
            break
        match = ENTRY_RE.match(line)
        if match:
            token = match.group(1)
            phonemes = match.group(2).replace("\\\\", "\\")
            if token in result:
                raise ProbeError(f"duplicate pinyin token: {token}")
            result[token] = phonemes
    if not inside or not result:
        raise ProbeError(f"Pinyin2Xsampa was not found in {path}")
    return result


def run_harness(
    project: Path,
    tokens: list[str],
    editor_directory: Path,
    no_build: bool,
) -> list[str]:
    command = [
        "dotnet",
        "run",
        "--project",
        str(project),
        "--configuration",
        "Release",
    ]
    if no_build:
        command.append("--no-build")
    command.extend(["--", *tokens])
    environment = os.environ.copy()
    environment["G2PA_HARNESS_EDITOR"] = str(editor_directory)
    completed = subprocess.run(
        command,
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=environment,
        timeout=120,
    )
    if completed.returncode != 0:
        detail = completed.stderr.strip() or completed.stdout.strip()
        raise ProbeError(
            f"G2PA harness exited with {completed.returncode}: {detail}"
        )
    return completed.stdout.splitlines()


def parse_harness(lines: list[str]) -> tuple[dict[str, dict[str, object]], dict[str, str]]:
    records: dict[str, dict[str, object]] = {}
    lifecycle: dict[str, str] = {}
    current_token: str | None = None
    for line in lines:
        token_match = TOKEN_RE.match(line)
        if token_match:
            current_token = token_match.group(1)
            records[current_token] = {
                "can_convert": token_match.group(2) == "True",
                "candidate_count": int(token_match.group(3)),
                "candidates": [],
            }
            continue
        candidate_match = CANDIDATE_RE.match(line)
        if candidate_match and current_token is not None:
            candidates = records[current_token]["candidates"]
            assert isinstance(candidates, list)
            candidates.append(
                {
                    "candidate_index": int(candidate_match.group(1)),
                    "syllable": candidate_match.group(2),
                    "phonemes": candidate_match.group(3),
                }
            )
            continue
        if line.startswith("vsm.") and "=" in line:
            key, value = line.split("=", 1)
            lifecycle[key] = value
    return records, lifecycle


def write_json(path: Path | None, value: object) -> None:
    text = json.dumps(value, ensure_ascii=False, indent=2) + "\n"
    if path is None:
        sys.stdout.write(text)
        return
    if path.exists():
        raise ProbeError(f"output already exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def main() -> int:
    repository = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--map",
        type=Path,
        default=repository
        / "VOCALOIDPatcher"
        / "VOCALOIDPatcher"
        / "Formats"
        / "LibreSvip"
        / "Plugins"
        / "Vsqx"
        / "VsqxPhonemeMaps.cs",
    )
    parser.add_argument(
        "--harness-project",
        type=Path,
        default=Path(__file__).resolve().parent
        / "g2pa_harness"
        / "G2paHarness.csproj",
    )
    parser.add_argument(
        "--editor",
        type=Path,
        default=Path(r"C:\Program Files\VOCALOID6\Editor"),
    )
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    try:
        expected = read_pinyin_map(args.map.resolve())
        tokens = sorted(expected)
        records, lifecycle = parse_harness(
            run_harness(
                args.harness_project.resolve(),
                tokens,
                args.editor.resolve(),
                args.no_build,
            )
        )
        missing = sorted(set(tokens) - set(records))
        extra = sorted(set(records) - set(tokens))
        mismatches: list[dict[str, object]] = []
        entries: list[dict[str, object]] = []
        for token in tokens:
            record = records.get(token)
            candidates = [] if record is None else record["candidates"]
            assert isinstance(candidates, list)
            first = candidates[0] if candidates else None
            native_phonemes = None if first is None else first["phonemes"]
            exact = native_phonemes == expected[token]
            if record is None or not record["can_convert"] or not exact:
                mismatches.append(
                    {
                        "token": token,
                        "expected": expected[token],
                        "native": native_phonemes,
                        "record": record,
                    }
                )
            entries.append(
                {
                    "token": token,
                    "phonemes": native_phonemes,
                    "expected_phonemes": expected[token],
                    "exact_match": exact,
                    "candidate_count": 0
                    if record is None
                    else record["candidate_count"],
                    "candidates": candidates,
                }
            )

        canonical = "".join(
            f"{entry['token']}\t{entry['phonemes']}\n" for entry in entries
        ).encode("utf-8")
        result = {
            "format": "vocaloid-chinese-g2pa-inventory-v1",
            "source": {
                "language_id": 4,
                "editor_directory": str(args.editor.resolve()),
                "map_path": str(args.map.resolve()),
                "harness_project": str(args.harness_project.resolve()),
            },
            "summary": {
                "tokens": len(tokens),
                "native_records": len(records),
                "convertible": sum(
                    1 for record in records.values() if record["can_convert"]
                ),
                "tokens_with_candidates": sum(
                    1 for record in records.values() if record["candidate_count"]
                ),
                "total_candidates": sum(
                    int(record["candidate_count"]) for record in records.values()
                ),
                "exact_first_candidate_matches": sum(
                    1 for entry in entries if entry["exact_match"]
                ),
                "missing_tokens": missing,
                "extra_tokens": extra,
                "mismatches": mismatches,
                "inventory_sha256": hashlib.sha256(canonical).hexdigest(),
                "vsm_sequence_closed": lifecycle.get("vsm.sequence.closed"),
                "vsm_manager_destroyed": lifecycle.get("vsm.manager.destroyed"),
            },
            "entries": entries,
        }
        write_json(args.output.resolve() if args.output else None, result)
        summary = result["summary"]
        assert isinstance(summary, dict)
        valid = (
            len(tokens) == len(records)
            and not missing
            and not extra
            and not mismatches
            and lifecycle.get("vsm.sequence.closed") == "True"
            and lifecycle.get("vsm.manager.destroyed") == "True"
        )
        if args.output:
            print(f"tokens={summary['tokens']}")
            print(f"exact_matches={summary['exact_first_candidate_matches']}")
            print(f"inventory_sha256={summary['inventory_sha256']}")
        return 0 if valid else 3
    except (OSError, UnicodeError, subprocess.SubprocessError, ProbeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
