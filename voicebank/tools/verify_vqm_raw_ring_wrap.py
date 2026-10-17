"""Test a circular-buffer explanation for saved VQM raw-feature exceptions.

Installed PCM and frames are read only. This constructs a representative
physical ring from the saved PCM and one owned lookahead sample; the report
contains only match counts and candidate index ranges.
"""

from __future__ import annotations

import argparse
import ctypes as c
import json
from pathlib import Path

from audit_frm2_numeric import F, I, Native, P, SHA256
from frm2_raw_features import raw_features
from verify_vqm_bit16_spectral_power import load_sequences


FIELDS = ((0, "feature4", 4), (1, "feature8", 8),
          (2, "feature12", 12))
TOLERANCE = 1e-6


def physical_ring(pcm: tuple[int, ...], wrap: int,
                  lookahead: int) -> tuple[int, ...]:
    if not 0 < wrap < len(pcm) or not -32768 <= lookahead <= 32767:
        raise ValueError("invalid owned ring geometry")
    return pcm[wrap:] + (lookahead,) + pcm[:wrap]


def physical_start(frame_index: int, ring_size: int, wrap: int) -> int:
    return ((frame_index - 1) * 256 + ring_size - wrap) % ring_size


def mismatches(sequence: dict, ring: tuple[int, ...],
               wrap: int) -> tuple[dict[int, list[int]], dict[int, float]]:
    failures = {bit: [] for bit, _, _ in FIELDS}
    maxima = {bit: 0.0 for bit, _, _ in FIELDS}
    for index in range(1, len(sequence["frames"])):
        prediction = raw_features(ring, physical_start(index, len(ring), wrap), 256)
        saved = sequence["frames"][index]["features"]
        for bit, key, _ in FIELDS:
            error = abs(prediction[key] - saved[bit])
            maxima[bit] = max(maxima[bit], error)
            if error > TOLERANCE:
                failures[bit].append(index)
    return failures, maxima


def native_matches(native: Native, sequences: list[dict],
                   cases: list[tuple[int, int]]) -> list[dict]:
    config = native.alloc(0x35f0)
    native.fn(0x90de0, P, P)(config)
    F.from_address(config + 0x18).value = 44100 / 256
    for time in (0.0, 1.0):
        native.add(config + 0x5d0 + 0x17 * 0xa0, time, 1.0)
    construct = native.fn(0xcdde0, P, P, I, P)
    frame = native.alloc(0x1a0)
    feature = native.alloc(0x228)
    P.from_address(frame + 0x128).value = feature
    analyze = native.fn(0xd92e0, I, P, P)
    results = []
    for sequence, (wrap, lookahead) in zip(sequences, cases):
        context = native.alloc(0xce0)
        if construct(context, 44100, config) != context:
            raise AssertionError("DRS context construction differs")
        capacity = I.from_address(context + 0xaf0).value
        buffer = P.from_address(context + 0xae0).value
        start_offset = capacity - wrap
        if capacity != 45124 or not buffer or not 0 < start_offset < capacity:
            raise AssertionError("constructed DRS ring geometry differs")
        c.memset(buffer, 0, capacity * 4)
        ring = [0] * capacity
        for index, value in enumerate(sequence["pcm"]):
            physical = (start_offset + index) % capacity
            ring[physical] = value
            F.from_address(buffer + 4 * physical).value = value
        physical = (start_offset + len(sequence["pcm"])) % capacity
        ring[physical] = lookahead
        F.from_address(buffer + 4 * physical).value = lookahead
        matches = {bit: 0 for bit, _, _ in FIELDS}
        maxima = {bit: 0.0 for bit, _, _ in FIELDS}
        for index in range(1, len(sequence["frames"])):
            start = (start_offset + (index - 1) * 256) % capacity
            I.from_address(context + 0xb00).value = (start + 256) % capacity
            analyze(context, frame)
            saved = sequence["frames"][index]["features"]
            predicted = raw_features(ring, start, 256)
            for bit, key, offset in FIELDS:
                actual = F.from_address(feature + offset).value
                if actual != predicted[key]:
                    raise AssertionError("native ring feature differs from model")
                error = abs(actual - saved[bit])
                matches[bit] += error <= TOLERANCE
                maxima[bit] = max(maxima[bit], error)
        results.append({
            "constructed_ring_capacity": capacity,
            "representative_material_start_index": start_offset,
            "compared_frames": len(sequence["frames"]) - 1,
            "saved_matches": {str(bit): matches[bit] for bit, _, _ in FIELDS},
            "maximum_absolute_error": {
                str(bit): maxima[bit] for bit, _, _ in FIELDS},
        })
    return results


def run(manifest: Path, voice_root: Path, parser_root: Path,
        dll: Path) -> dict:
    parser_hash, ddi_hash, sequences = load_sequences(
        manifest, voice_root, parser_root)
    if [len(sequence["frames"]) for sequence in sequences] != [72, 87, 87]:
        raise AssertionError("independent VQM sequence lengths differ")
    cases = []
    selected = []
    for sample_index, sequence in enumerate(sequences):
        baseline, _ = mismatches(sequence, sequence["pcm"], len(sequence["pcm"]))
        tail_only, _ = mismatches(
            sequence, sequence["pcm"] + (sequence["pcm"][-1],),
            len(sequence["pcm"]) + 1)
        crossing_exceptions = baseline[0]
        if len(crossing_exceptions) != 2 or crossing_exceptions[1] != crossing_exceptions[0] + 1:
            raise AssertionError("expected one adjacent crossing-exception pair")
        first = crossing_exceptions[0]
        admissible = []
        for wrap in range(first * 256, first * 256 + 257):
            ring = physical_ring(sequence["pcm"], wrap, 0)
            if all(abs(raw_features(
                    ring, physical_start(index, len(ring), wrap), 256
                    )["feature4"] - sequence["frames"][index]["features"][0])
                   <= TOLERANCE for index in crossing_exceptions):
                admissible.append(wrap)
        if not admissible:
            raise AssertionError("no physical wrap explains the crossing pair")
        wrap = min(admissible)
        # The last frame's derivative reads one sample beyond the saved SND.
        # Repeating the last saved value demonstrates one possible boundary;
        # it does not identify the original unsaved lookahead value.
        lookahead = sequence["pcm"][-1]
        ring = physical_ring(sequence["pcm"], wrap, lookahead)
        reconstructed, maximum = mismatches(sequence, ring, wrap)
        if any(reconstructed.values()):
            raise AssertionError("owned ring does not reconstruct all interior features")
        selected.append((wrap, lookahead))
        cases.append({
            "sample_index": sample_index,
            "frame_count": len(sequence["frames"]),
            "baseline_mismatch_indices": {
                str(bit): baseline[bit] for bit, _, _ in FIELDS},
            "tail_repeat_only_mismatch_indices": {
                str(bit): tail_only[bit] for bit, _, _ in FIELDS},
            "admissible_wrap_count": len(admissible),
            "admissible_wrap_min": min(admissible),
            "admissible_wrap_max": max(admissible),
            "representative_wrap": wrap,
            "owned_lookahead_rule": "repeat_last_saved_pcm_sample",
            "synthetic_ring_size": len(ring),
            "model_matches": {
                str(bit): len(sequence["frames"]) - 1 - len(reconstructed[bit])
                for bit, _, _ in FIELDS},
            "model_maximum_absolute_error": {
                str(bit): maximum[bit] for bit, _, _ in FIELDS},
        })
    native = Native(dll)
    native_rows = native_matches(native, sequences, selected)
    for case, result in zip(cases, native_rows):
        case["native"] = result
        if any(count != result["compared_frames"]
               for count in result["saved_matches"].values()):
            raise AssertionError("native raw-feature reconstruction is incomplete")
    return {
        "dse_sha256": SHA256,
        "ddi_sha256": ddi_hash,
        "parser_sha256": parser_hash,
        "independent_samples": len(cases),
        "total_interior_frames": sum(row["native"]["compared_frames"] for row in cases),
        "cases": cases,
        "scope": "Existence test for a physical ring-index crossing guard. Native checks use a DRS-constructed default 45124-sample ring seeded with read-only saved VQM PCM at representative physical positions and one owned unsaved lookahead sample. This does not identify the original authoring ring capacity, write pointer, or lookahead sample, nor reconstruct first-frame prehistory.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--voice-root", type=Path, required=True)
    parser.add_argument("--ddb-tools", type=Path, required=True)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.manifest, args.voice_root, args.ddb_tools, args.dll)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "independent_samples": report["independent_samples"],
        "total_interior_frames": report["total_interior_frames"],
        "native_matches": {
            str(bit): sum(case["native"]["saved_matches"][str(bit)]
                          for case in report["cases"])
            for bit, _, _ in FIELDS},
    }))


if __name__ == "__main__":
    main()
