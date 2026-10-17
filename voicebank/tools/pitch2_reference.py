"""Derive ordinary DDI reference pitch from final-frame F0 and PHDC regions."""

import math
import struct
from typing import BinaryIO

import probe_frm2


NO_PITCH = -3.4028234663852886e38


def read_final_f0(stream: BinaryIO, offsets: list[int]) -> list[float]:
    values = []
    for offset in offsets:
        stream.seek(offset)
        header = stream.read(8)
        if len(header) != 8 or header[:4] != b'FRM2':
            raise ValueError(f'no FRM2 at DDB offset {offset}')
        size = struct.unpack_from('<I', header, 4)[0]
        if size < 32 or size > 16 * 1024 * 1024:
            raise ValueError(f'invalid FRM2 size at DDB offset {offset}')
        frame = probe_frm2.parse_frame(header + stream.read(size - 8))
        if isinstance(frame, probe_frm2.VqmFrame):
            raise ValueError('VQM frame cannot provide an ordinary reference pitch')
        if not math.isfinite(frame.f0_hz):
            raise ValueError('nonfinite final-frame F0')
        values.append(frame.f0_hz)
    return values


def reference_pitch_cents(
    f0_hz: list[float],
    alignments: tuple[tuple[int, int, int, int], ...] | None = None,
    tokens: tuple[str, ...] | None = None,
    voiced_phonemes: set[str] | None = None,
) -> float:
    if not f0_hz or any(not math.isfinite(value) for value in f0_hz):
        raise ValueError('final-frame F0 list must be nonempty and finite')
    if alignments is None:
        indices = range(len(f0_hz))
    else:
        if tokens is None or voiced_phonemes is None or len(tokens) != len(alignments):
            raise ValueError('one PHDC token is required for each ART outer region')
        previous_end = 0
        chosen = []
        for token, group in zip(tokens, alignments):
            start, end, inner_start, inner_end = group
            if not (start == previous_end and start <= inner_start <= inner_end <= end <= len(f0_hz)):
                raise ValueError('ART outer regions must partition the final frames')
            if token in voiced_phonemes:
                chosen.extend(range(start, end))
            previous_end = end
        if previous_end != len(f0_hz):
            raise ValueError('ART outer regions do not reach the last frame')
        indices = chosen
    cents = [1200. * math.log2(f0_hz[index] / 440.)
             for index in indices if f0_hz[index] > 0.]
    return math.fsum(cents) / len(cents) if cents else NO_PITCH
