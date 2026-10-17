"""Compare auxiliary VQM saved features with known DRS component formulas.

Reads the installed VQM.vdb and DSE.dll; writes only grouped statistics.
The auxiliary file is not assumed to be paired with any installed V4X bank.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import statistics
import struct

from audit_aux_vqm_vdb import ENCODED_VQM_MASK, PLAIN_VQM_MASK, feature_fields
from audit_frm2_numeric import F, Native, SHA256, f32
from probe_frm2 import VqmFrame, parse_frame
from verify_vqm_bit6_component_ratio import predict as predict_bit6


EXPECTED_VDB_SHA256 = "a629b36e0b4d571c37f947809d34dcfa283a5af42f12d635cd5f75143ee6203c"


def summary(pairs: list[tuple[float, float]]) -> dict:
    saved = [item[0] for item in pairs]
    predicted = [item[1] for item in pairs]
    differences = [a - b for a, b in pairs]
    absolute = [abs(value) for value in differences]
    saved_mean = statistics.fmean(saved)
    predicted_mean = statistics.fmean(predicted)
    saved_sd = statistics.pstdev(saved)
    predicted_sd = statistics.pstdev(predicted)
    covariance = statistics.fmean(
        (a - saved_mean) * (b - predicted_mean)
        for a, b in pairs)
    correlation = (covariance / saved_sd / predicted_sd
                   if saved_sd and predicted_sd else None)
    return {
        "frames": len(pairs),
        "float32_exact": sum(struct.pack("<f", a) == struct.pack("<f", b)
                             for a, b in pairs),
        "within_1e_6": sum(value <= 1e-6 for value in absolute),
        "saved_mean": saved_mean,
        "predicted_mean": predicted_mean,
        "saved_range": [min(saved), max(saved)],
        "predicted_range": [min(predicted), max(predicted)],
        "mean_signed_difference": statistics.fmean(differences),
        "mean_absolute_error": statistics.fmean(absolute),
        "max_absolute_error": max(absolute),
        "pearson_correlation": correlation,
        "centered_mean_controls": [
            {"radius_frames": radius,
             "interior_frames": len(pairs) - 8,
             "mean_absolute_error": statistics.fmean(
                 abs(pairs[index][0] - statistics.fmean(
                     predicted[index - radius:index + radius + 1]))
                 for index in range(4, len(pairs) - 4))}
            for radius in (0, 1, 2, 4)
        ],
    }


def run(vdb: Path, dll: Path) -> dict:
    data = vdb.read_bytes()
    vdb_sha = hashlib.sha256(data).hexdigest()
    if vdb_sha != EXPECTED_VDB_SHA256:
        raise ValueError("Auxiliary VQM source differs from the audited file")
    native = Native(dll)
    linearize = native.fn(0x2af20, F, F)
    sections: list[tuple[str, int, int]] = []
    position = 0
    while (position := data.find(b"VQMs", position)) >= 0:
        size = struct.unpack_from("<I", data, position + 4)[0]
        if size < 8 or position + size > len(data):
            raise ValueError("Invalid VQMs section")
        label = data[position - 34:position - 29].decode("ascii")
        if label not in ("TENSE", "BRWSP"):
            raise ValueError("Unexpected VQM category")
        sections.append((label, position, position + size))
        position += size
    if len(sections) != 2:
        raise ValueError("Expected two auxiliary VQM sections")

    grouped: dict[str, dict[str, list[tuple[float, float]]]] = {
        label: {"bit6_component_band_ratio": [],
                "bit16_first_component_over_ten": []}
        for label, _, _ in sections
    }
    for label, start, end in sections:
        position = start
        while (position := data.find(b"FRM2", position, end)) >= 0:
            size = struct.unpack_from("<I", data, position + 4)[0]
            if size < 28 or position + size > end:
                raise ValueError("Invalid auxiliary FRM2 size")
            mask = struct.unpack_from("<Q", data, position + 20)[0]
            if mask == ENCODED_VQM_MASK:
                raw = bytearray(data[position:position + size])
                struct.pack_into("<Q", raw, 20, PLAIN_VQM_MASK)
                frame = parse_frame(raw)
                if not isinstance(frame, VqmFrame) or not frame.component_amplitudes:
                    raise ValueError("Invalid auxiliary VQM frame")
                features, _ = feature_fields(frame)
                components = tuple(zip(frame.component_frequencies_hz,
                                       frame.component_amplitudes))
                bit6 = predict_bit6(components, linearize)
                bit16 = f32(f32(frame.component_amplitudes[0]) / f32(10))
                if not all(math.isfinite(x) for x in
                           (features[6], features[16], bit6, bit16)):
                    raise ValueError("Nonfinite feature comparison")
                grouped[label]["bit6_component_band_ratio"].append((features[6], bit6))
                grouped[label]["bit16_first_component_over_ten"].append((features[16], bit16))
            elif mask != 0x40:
                raise ValueError("Unexpected auxiliary FRM2 mask")
            position += size

    if [len(grouped[label]["bit6_component_band_ratio"])
            for label, _, _ in sections] != [148, 389]:
        raise AssertionError("Auxiliary VQM frame counts changed")
    return {
        "dse_sha256": SHA256,
        "vdb_sha256": vdb_sha,
        "by_category": {label: {name: summary(pairs)
                                for name, pairs in fields.items()}
                        for label, fields in grouped.items()},
        "scope": "Compares saved auxiliary fields to known DRS component candidates. "
                 "These statistics do not establish the auxiliary file's "
                 "voicebank pairing or the authoring formulas.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vdb", required=True, type=Path)
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    result = run(args.vdb, args.dll)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({label: {name: value["within_1e_6"]
                              for name, value in fields.items()}
                      for label, fields in result["by_category"].items()}))


if __name__ == "__main__":
    main()
