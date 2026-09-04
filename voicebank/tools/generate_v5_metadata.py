#!/usr/bin/env python3
"""Generate a review-only VOCALOID5 traditional voicebank metadata manifest."""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import uuid
from pathlib import Path, PureWindowsPath
from typing import Any

import compid_codec


DEFAULT_STYLE_ID = "0c29827a-4289-495d-94d2-e23602d346c6"
LANGUAGES = {
    0: "Japanese",
    1: "English",
    2: "Korean",
    3: "Spanish",
    4: "Chinese",
}
MANIFEST_NAME = "v5_metadata_manifest.json"
REGISTRY_REVIEW_NAME = "v5_registry_review.reg.txt"


class MetadataError(Exception):
    pass


def clean_string(value: object, description: str, *, required: bool = True) -> str | None:
    if value is None and not required:
        return None
    if not isinstance(value, str) or (required and not value):
        qualifier = "a non-empty string" if required else "a string or null"
        raise MetadataError(f"{description} must be {qualifier}")
    if "\x00" in value or "\r" in value or "\n" in value:
        raise MetadataError(f"{description} must not contain NUL or line breaks")
    return value


def utf16_units(value: str) -> int:
    return len(value.encode("utf-16-le")) // 2


def fixed_utf16(value: object, description: str, units: int) -> str:
    result = clean_string(value, description)
    assert result is not None
    actual = utf16_units(result)
    if actual != units:
        raise MetadataError(
            f"{description} must contain exactly {units} UTF-16 code units; got {actual}"
        )
    return result


def uint31(value: object, description: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise MetadataError(f"{description} must be an integer")
    if value < 0 or value > 0x7FFFFFFF:
        raise MetadataError(f"{description} must be between 0 and 2147483647")
    return value


def singer_stem(value: object) -> str:
    result = clean_string(value, "singer_stem")
    assert result is not None
    if not result.isascii() or any(
        not (character.isalnum() or character in "_-") for character in result
    ):
        raise MetadataError(
            "singer_stem must contain only ASCII letters, digits, underscore, or hyphen"
        )
    return result


def windows_base_path(value: object) -> str:
    result = clean_string(value, "path")
    assert result is not None
    path = PureWindowsPath(result)
    if not path.is_absolute():
        raise MetadataError("path must be an absolute Windows path")
    return str(path)


def optional_style_id(value: object) -> str | None:
    result = clean_string(value, "default_style_id", required=False)
    if result is None or result == "":
        return None
    try:
        return str(uuid.UUID(result))
    except ValueError as error:
        raise MetadataError("default_style_id must be a UUID or null") from error


def dbse_digest(stem: str) -> str:
    return hashlib.md5(b"K2ho" + stem.upper().encode("ascii") + b"nF").hexdigest()


def registry_escape(value: str) -> str:
    return value.replace("\\", "\\\\").replace('"', '\\"')


def registry_review(manifest: dict[str, Any]) -> str:
    registry = manifest["registry"]
    values = registry["values"]
    version = registry["version"]
    lines = [
        "Windows Registry Editor Version 5.00",
        "",
        "; REVIEW ONLY. This file deliberately uses .reg.txt and is not installed by the tool.",
        "; It does not create or establish a valid VOCALOID license.",
        f"[{registry['component_key']}]",
    ]
    for name, value in values.items():
        lines.append(f'"{name}"="{registry_escape(value)}"')
    lines.extend(
        [
            "",
            f"[{registry['version_key']}]",
            f'"Major"=dword:{version["major"]:08x}',
            f'"Minor"=dword:{version["minor"]:08x}',
            f'"Revision"=dword:{version["revision"]:08x}',
            "",
        ]
    )
    return "\n".join(lines)


def build_manifest(spec: object, vdm_path: Path) -> dict[str, Any]:
    if not isinstance(spec, dict) or spec.get("schema_version") != 1:
        raise MetadataError("spec must be an object with schema_version 1")

    payload_value = clean_string(spec.get("component_payload"), "component_payload")
    assert payload_value is not None
    payload = payload_value.upper()
    if len(payload) != 14 or any(
        character not in compid_codec.PAYLOAD_ALPHABET for character in payload
    ):
        raise MetadataError(
            "component_payload must contain exactly 14 base-28 digits from "
            f"{compid_codec.PAYLOAD_ALPHABET}"
        )
    if any(character not in "0123456789" for character in payload[8:]):
        raise MetadataError("component_payload positions 8 through 13 must be decimal")

    language = spec.get("native_language")
    if isinstance(language, bool) or not isinstance(language, int) or language not in LANGUAGES:
        raise MetadataError("native_language must be an integer from 0 through 4")
    if payload[3] != str(language):
        raise MetadataError(
            f"component_payload language digit {payload[3]!r} does not match "
            f"native_language {language}"
        )

    component_name = clean_string(spec.get("component_name"), "component_name")
    bank_name = clean_string(spec.get("bank_name"), "bank_name")
    group_name = clean_string(spec.get("group_name"), "group_name", required=False)
    assert component_name is not None and bank_name is not None
    if group_name == "":
        group_name = None
    stem = singer_stem(spec.get("singer_stem"))
    drp = fixed_utf16(spec.get("drp"), "drp", 6)
    date = fixed_utf16(spec.get("date"), "date", 16)
    base_path = windows_base_path(spec.get("path"))
    style_id = optional_style_id(spec.get("default_style_id"))

    version = spec.get("version")
    if not isinstance(version, dict):
        raise MetadataError("version must be an object")
    version_values = {
        "major": uint31(version.get("major"), "version.major"),
        "minor": uint31(version.get("minor"), "version.minor"),
        "revision": uint31(version.get("revision"), "version.revision"),
    }

    if not vdm_path.is_file():
        raise MetadataError(f"VDM.dll does not exist: {vdm_path}")
    tables = compid_codec.load_tables(vdm_path)
    component_id = compid_codec.encode_component_id(payload, tables)
    if compid_codec.decode_component_id(component_id, tables) != payload:
        raise MetadataError("generated component ID failed the codec round-trip")

    reserved = spec.get("reserved_component_ids", [])
    if not isinstance(reserved, list) or any(not isinstance(value, str) for value in reserved):
        raise MetadataError("reserved_component_ids must be a list of strings")
    reserved_ids: list[str] = []
    for value in reserved:
        candidate = value.upper()
        try:
            compid_codec.decode_component_id(candidate, tables)
        except ValueError as error:
            raise MetadataError(f"reserved component ID is invalid: {value}") from error
        reserved_ids.append(candidate)
    if component_id in reserved_ids:
        raise MetadataError(f"generated component ID collides with reserved ID {component_id}")

    component_directory = str(PureWindowsPath(base_path) / component_id)
    component_key = (
        "HKEY_LOCAL_MACHINE\\SOFTWARE\\VOCALOID5\\Voice\\Components\\"
        + component_id
    )
    registry_values = {
        "Path": base_path,
        "DRP": drp,
        "Name": component_name,
        "Date": date,
        "BankName": bank_name,
    }
    if group_name is not None:
        registry_values["GroupName"] = group_name
    if style_id is not None:
        registry_values["DefaultStyleID"] = style_id

    return {
        "schema_version": 1,
        "component": {
            "component_id": component_id,
            "payload": payload,
            "native_language": language,
            "native_language_name": LANGUAGES[language],
            "reserved_ids_checked": reserved_ids,
            "global_uniqueness_proven": False,
        },
        "voicebank": {
            "component_name": component_name,
            "bank_name": bank_name,
            "group_name_declared": group_name,
            "group_name_effective": group_name or bank_name,
            "singer_stem": stem,
            "version": version_values,
            "drp": drp,
            "date": date,
            "default_style_id_declared": style_id,
            "default_style_id_effective": style_id or DEFAULT_STYLE_ID,
        },
        "files": {
            "registry_base_path": base_path,
            "component_directory": component_directory,
            "ddi": str(PureWindowsPath(component_directory) / f"{stem}.ddi"),
            "ddb": str(PureWindowsPath(component_directory) / f"{stem}.ddb"),
            "dbse_digest": dbse_digest(stem),
        },
        "registry": {
            "component_key": component_key,
            "version_key": component_key + "\\Version",
            "values": registry_values,
            "version": version_values,
            "omitted_values": ["IsInstalled", "Key", "IceProductName", "IceValue"],
        },
        "safety": {
            "writes_registry": False,
            "creates_license": False,
            "license_status": "unresolved",
            "notes": [
                "A valid component ID is an identifier, not a license.",
                "VDM appends the component ID to the registry Path value.",
                "The component directory should contain one same-stem DDI/DDB pair.",
                "Check the candidate ID against installed and distributed components before use.",
            ],
        },
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("spec", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument(
        "--vdm",
        type=Path,
        default=Path(r"C:\Program Files\VOCALOID6\Editor\VDM.dll"),
        help="VDM.dll whose 6.13-compatible CompID tables should be used",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        spec_path = args.spec.resolve()
        if not spec_path.is_file():
            raise MetadataError(f"spec does not exist: {spec_path}")
        spec = json.loads(spec_path.read_text(encoding="utf-8"))
        manifest = build_manifest(spec, args.vdm.resolve())
        output = args.output.resolve()
        manifest_path = output / MANIFEST_NAME
        registry_path = output / REGISTRY_REVIEW_NAME
        for path in (manifest_path, registry_path):
            if path.exists():
                raise MetadataError(f"refusing to overwrite existing output: {path}")
        output.mkdir(parents=True, exist_ok=True)
        manifest_path.write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
        )
        registry_path.write_text(registry_review(manifest), encoding="utf-8")
        print(f"component_id={manifest['component']['component_id']}")
        print(f"payload={manifest['component']['payload']}")
        print(f"language={manifest['component']['native_language_name']}")
        print(f"dbse_digest={manifest['files']['dbse_digest']}")
        print(f"manifest={manifest_path}")
        print(f"registry_review={registry_path}")
        return 0
    except (MetadataError, ValueError, json.JSONDecodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
