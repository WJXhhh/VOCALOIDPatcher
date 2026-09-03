#!/usr/bin/env python3
"""Decode and create VDM-compatible 16-character component IDs.

The codec tables are read from the user's own VDM.dll at runtime.  The script
does not contain or redistribute those tables.  The layout offsets below were
verified against VOCALOID 6.13.0.1; all table shapes are validated before use.
"""

from __future__ import annotations

import argparse
import dataclasses
import struct
from pathlib import Path


SCRAMBLED_ALPHABET = b"23456789ABCDEFGHKLMNPRSTWXYZ"
PAYLOAD_ALPHABET = "0123456789ABCDEFGHIJKLMNOPQR"
CHECKSUM_ALPHABET = "KL23456789ABCDEF"


@dataclasses.dataclass(frozen=True)
class CodecTables:
    substitution: bytes
    high_scramble: tuple[tuple[int, ...], ...]
    low_scramble: tuple[tuple[int, ...], ...]


def _read_scramble_table(data: bytes, offset: int) -> tuple[tuple[int, ...], ...]:
    values = struct.unpack_from("<" + "i" * (16 * 13), data, offset)
    rows = tuple(tuple(values[row * 13 : (row + 1) * 13]) for row in range(16))
    if any(value < 0 or value >= len(SCRAMBLED_ALPHABET) for row in rows for value in row):
        raise ValueError("VDM scramble table values are outside the expected base-28 range")
    return rows


def load_tables(vdm_path: Path) -> CodecTables:
    data = vdm_path.read_bytes()
    alphabet_offset = data.find(SCRAMBLED_ALPHABET)
    if alphabet_offset < 0:
        raise ValueError(f"base-28 alphabet was not found in {vdm_path}")
    if data.find(SCRAMBLED_ALPHABET, alphabet_offset + 1) >= 0:
        raise ValueError(f"base-28 alphabet is not unique in {vdm_path}")

    # In 6.13.0.1 these tables share one read-only section with the alphabet,
    # so RVA and raw-file differences are identical.
    substitution_offset = alphabet_offset - 0x8F8
    high_scramble_offset = alphabet_offset - 0x768
    low_scramble_offset = alphabet_offset - 0x3A8
    if substitution_offset < 0:
        raise ValueError("VDM table layout is incompatible with the known 6.13.0.1 layout")

    substitution = data[substitution_offset : substitution_offset + 28 * 14]
    if len(substitution) != 28 * 14:
        raise ValueError("VDM substitution table is truncated")
    if any(value not in SCRAMBLED_ALPHABET and value != 0x20 for value in substitution):
        raise ValueError("VDM substitution table contains an unexpected character")
    for position in range(14):
        column = [value for value in substitution[position::14] if value != 0x20]
        expected = 28 if position < 8 else 10
        if len(column) != expected or len(set(column)) != expected:
            raise ValueError(
                f"VDM substitution column {position} has an unexpected valid-digit set"
            )

    return CodecTables(
        substitution=substitution,
        high_scramble=_read_scramble_table(data, high_scramble_offset),
        low_scramble=_read_scramble_table(data, low_scramble_offset),
    )


def _decode_checksum_digit(value: str) -> int:
    try:
        return CHECKSUM_ALPHABET.index(value)
    except ValueError as exc:
        raise ValueError(f"invalid component-ID checksum character: {value!r}") from exc


def decode_component_id(component_id: str, tables: CodecTables) -> str:
    component_id = component_id.upper()
    if len(component_id) != 16:
        raise ValueError("component ID must contain exactly 16 characters")

    high = _decode_checksum_digit(component_id[14])
    low = _decode_checksum_digit(component_id[15])
    intermediate = bytearray(component_id[:14].encode("ascii"))

    for position in range(1, 14):
        try:
            index = SCRAMBLED_ALPHABET.index(intermediate[position])
        except ValueError as exc:
            raise ValueError(
                f"invalid component-ID character at position {position}: "
                f"{chr(intermediate[position])!r}"
            ) from exc
        index = (index - tables.low_scramble[low][position - 1]) % 28
        intermediate[position] = SCRAMBLED_ALPHABET[index]

    for position in range(1, 14):
        index = SCRAMBLED_ALPHABET.index(intermediate[position])
        index = (index - tables.high_scramble[high][position - 1]) % 28
        intermediate[position] = SCRAMBLED_ALPHABET[index]

    checksum = sum(intermediate) & 0xFF
    if checksum >> 4 != high or checksum & 0x0F != low:
        raise ValueError(
            f"component-ID checksum mismatch: stored={high:X}{low:X}, calculated={checksum:02X}"
        )

    payload: list[str] = []
    for position, value in enumerate(intermediate):
        matches = [
            digit
            for digit in range(28)
            if tables.substitution[digit * 14 + position] == value
        ]
        if len(matches) != 1:
            raise ValueError(f"component-ID substitution at position {position} is ambiguous")
        payload.append(PAYLOAD_ALPHABET[matches[0]])
    return "".join(payload)


def encode_component_id(payload: str, tables: CodecTables) -> str:
    payload = payload.upper()
    if len(payload) != 14:
        raise ValueError("component-ID payload must contain exactly 14 base-28 digits")
    if any(value not in PAYLOAD_ALPHABET for value in payload):
        raise ValueError(f"payload digits must be drawn from {PAYLOAD_ALPHABET}")

    intermediate = bytearray(
        tables.substitution[PAYLOAD_ALPHABET.index(value) * 14 + position]
        for position, value in enumerate(payload)
    )
    if 0x20 in intermediate:
        raise ValueError(
            "payload positions 8 through 13 accept decimal digits only in this VDM format"
        )
    checksum = sum(intermediate) & 0xFF
    high = checksum >> 4
    low = checksum & 0x0F

    encoded = bytearray(intermediate)
    for position in range(1, 14):
        index = SCRAMBLED_ALPHABET.index(encoded[position])
        index = (index + tables.high_scramble[high][position - 1]) % 28
        encoded[position] = SCRAMBLED_ALPHABET[index]

    for position in range(1, 14):
        index = SCRAMBLED_ALPHABET.index(encoded[position])
        index = (index + tables.low_scramble[low][position - 1]) % 28
        encoded[position] = SCRAMBLED_ALPHABET[index]

    return encoded.decode("ascii") + CHECKSUM_ALPHABET[high] + CHECKSUM_ALPHABET[low]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--vdm",
        type=Path,
        default=Path(r"C:\Program Files\VOCALOID6\Editor\VDM.dll"),
        help="VDM.dll whose codec tables should be used",
    )
    operation = parser.add_mutually_exclusive_group(required=True)
    operation.add_argument("--decode", metavar="COMPONENT_ID")
    operation.add_argument("--encode", metavar="PAYLOAD")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    tables = load_tables(args.vdm)
    if args.decode is not None:
        payload = decode_component_id(args.decode, tables)
        print(f"component_id={args.decode.upper()}")
        print(f"payload={payload}")
        print(f"language_digit={payload[3]}")
        print(f"roundtrip={encode_component_id(payload, tables)}")
    else:
        component_id = encode_component_id(args.encode, tables)
        print(f"payload={args.encode.upper()}")
        print(f"language_digit={args.encode.upper()[3]}")
        print(f"component_id={component_id}")
        print(f"roundtrip={decode_component_id(component_id, tables)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
