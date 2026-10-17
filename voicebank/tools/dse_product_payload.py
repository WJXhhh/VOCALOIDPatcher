"""Author/read product payload slots in owned single-phoneme compact DDI fixtures.

This module implements the stationary-slot representation, without native DLL
calls or a commercial payload. It does not create license keys or install banks.
The compact-DDI locator deliberately accepts only the owned diagnostic layout;
the slot codec itself accepts parts already ordered by the native traversal.
"""

from __future__ import annotations

from dataclasses import dataclass
import struct

import finalize_stationary_ddi as stationary


SLOT_ORDER = (2, 3, 1, 0)  # Memory +1f8, +1fc, +1f4, +1f0.
MAX_PAYLOAD = 128
ABSENT = (-1, -1, -1, -1)


class PayloadError(ValueError):
    pass


@dataclass(frozen=True)
class Extraction:
    data: bytes
    helper_success: bool
    declared_length: int | None


def encode_slots(payload: bytes, part_count: int) -> list[tuple[int, int, int, int]]:
    if part_count < 1 or part_count > 4096:
        raise PayloadError('part count must be between 1 and 4096')
    if len(payload) > MAX_PAYLOAD or len(payload) > 4 * part_count - 1:
        raise PayloadError('payload exceeds the output bound or stationary slot capacity')
    if b'\0' in payload:
        raise PayloadError('embedded zero would truncate the product-table lookup')
    rows = [list(ABSENT) for _ in range(part_count)]
    if not payload:
        return [tuple(row) for row in rows]
    rows[0][2] = len(payload)
    destinations = [(part, slot) for part in range(part_count) for slot in SLOT_ORDER
                    if not (part == 0 and slot == 2)]
    for byte, (part, slot) in zip(payload, destinations):
        rows[part][slot] = ((byte << 1) | (byte >> 7)) & 255
    return [tuple(row) for row in rows]


def decode_slots(rows: list[tuple[int, int, int, int]]) -> Extraction:
    if not rows:
        return Extraction(b'', False, None)
    if any(len(row) != 4 or any(not -(1 << 31) <= value < (1 << 31) for value in row)
           for row in rows):
        raise PayloadError('each part requires four signed 32-bit fields')
    length = rows[0][2]
    if length < 1:
        return Extraction(b'', False, length)
    if length > MAX_PAYLOAD:
        raise PayloadError('declared payload exceeds the bounded native output contract')
    output = bytearray()
    for part, row in enumerate(rows):
        for slot in SLOT_ORDER:
            if part == 0 and slot == 2:
                continue
            byte = row[slot] & 255
            output.append(((byte >> 1) | (byte << 7)) & 255)
            if len(output) == length:
                return Extraction(bytes(output), True, length)
    # The native wrapper ignores this failure and retains the copied prefix.
    return Extraction(bytes(output), False, length)


def effective_product_data(extraction: Extraction) -> bytes:
    return extraction.data.split(b'\0', 1)[0]


def owned_compact_slot_offsets(data: bytes) -> list[int]:
    """Locate four-i32 tails in the supported owned compact STAp layout."""
    if data[8:12] != b'DBSe':
        raise PayloadError('the owned fixture must use a compiled DBSe root')
    phdc = data.find(b'PHDC')
    if phdc < 0 or phdc + 16 > len(data):
        raise PayloadError('PHDC is missing or truncated')
    if struct.unpack_from('<I', data, phdc + 12)[0] != 1:
        raise PayloadError('this fixture locator requires exactly one PHDC phoneme')
    if data.count(b'STA ') != 1 or data.count(b'STAu') != 1:
        raise PayloadError('this fixture locator requires one stationary group and one STAu')
    positions = stationary.stationary_positions(data)
    if not 1 <= len(positions) <= 4096:
        raise PayloadError('invalid stationary part count')
    offsets = []
    for part in positions:
        if part + 108 > len(data):
            raise PayloadError('truncated stationary header')
        if data[part + 4:part + 16] != struct.pack('<III', 0, 0, 1):
            raise PayloadError('unsupported stationary compact header')
        if struct.unpack_from('<H', data, part + 24)[0] != 1:
            raise PayloadError('unsupported stationary scalar marker')
        if data[part + 46:part + 54] != struct.pack('<II', 0, 2):
            raise PayloadError('unsupported stationary children')
        if data[part + 62:part + 77] != stationary.EMPTY_SND:
            raise PayloadError('expected compact SND reference')
        if data[part + 85:part + 100] != stationary.EMPTY_EPR:
            raise PayloadError('expected compact EpR reference')
        if struct.unpack_from('<i', data, part + 100)[0] != -1:
            raise PayloadError('unsupported frame-index marker')
        count = struct.unpack_from('<I', data, part + 104)[0]
        offset = part + 126 + 8 * count
        if not 1 <= count <= 1000000 or offset + 20 > len(data):
            raise PayloadError('stationary frame count exceeds the fixture bounds')
        rate, channels, samples = struct.unpack_from('<IHI', data, part + 108 + 8 * count)
        if rate != 44100 or channels != 1 or samples != count * 256 + 2048:
            raise PayloadError('unsupported owned stationary PCM dimensions')
        name_length = struct.unpack_from('<I', data, offset + 16)[0]
        if not 1 <= name_length <= 1024 or offset + 20 + name_length > len(data):
            raise PayloadError('invalid stationary part name')
        offsets.append(offset)
    if any(right < left + 16 for left, right in zip(offsets, offsets[1:])):
        raise PayloadError('overlapping stationary fields')
    return offsets


def read_owned_compact(data: bytes) -> Extraction:
    rows = [struct.unpack_from('<iiii', data, offset)
            for offset in owned_compact_slot_offsets(data)]
    return decode_slots(rows)


def write_owned_compact(data: bytes, payload: bytes) -> bytes:
    offsets = owned_compact_slot_offsets(data)
    rows = encode_slots(payload, len(offsets))
    result = bytearray(data)
    for offset, row in zip(offsets, rows):
        struct.pack_into('<iiii', result, offset, *row)
    return bytes(result)
