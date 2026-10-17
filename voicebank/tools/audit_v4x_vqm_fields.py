"""Audit installed V4X VQM frame metadata without exporting frame or PCM data."""

from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
import math
import mmap
from pathlib import Path
import statistics
import struct

import probe_frm2
from audit_frm2_numeric import f32
from frm2_raw_features import raw_features
from verify_reference_ddi import load_public_parser, pointer


def describe_numbers(values: list[float | int]) -> dict:
    if not values:
        return {"count": 0}
    counts = Counter(values)
    result = {
        "count": len(values),
        "unique": len(counts),
        "min": min(values),
        "max": max(values),
    }
    if len(counts) <= 12:
        result["counts"] = {str(key): count for key, count in sorted(counts.items())}
    else:
        result["mean"] = statistics.fmean(values)
    return result


def feature_scalars(frame: probe_frm2.VqmFrame) -> dict[int, float]:
    """Decode only the fixed scalar fields of the bit-13 payload."""
    flags = frame.feature_block.flags
    payload = frame.feature_block.payload
    offset = 0
    result = {}
    for bit in range(21):
        if not flags & (1 << bit):
            continue
        if bit < 10 or 14 <= bit < 20:
            result[bit] = struct.unpack_from("<f", payload, offset)[0]
            offset += 4
        elif bit in (10, 11):
            offset += 160
        elif bit in (12, 13):
            offset += 80
        elif bit == 20:
            # This flag has no payload in the verified DSE bit-13 reader.
            continue
        else:
            raise ValueError(f"unsupported feature bit {bit}")
    if offset != len(payload):
        raise ValueError("feature payload size differs from decoded fields")
    return result


def feature_array(frame: probe_frm2.VqmFrame, target_bit: int) -> tuple[float, ...]:
    """Read a flagged bit-13 array without assuming all preceding flags exist."""
    flags = frame.feature_block.flags
    payload = frame.feature_block.payload
    offset = 0
    for bit in range(21):
        if not flags & (1 << bit):
            continue
        size = 4 if bit < 10 or 14 <= bit < 20 else (160 if bit in (10, 11) else 80)
        if bit == 20:
            size = 0
        if bit == target_bit:
            if size not in (80, 160):
                raise ValueError("requested feature is not an array")
            return struct.unpack_from(f"<{size // 4}f", payload, offset)
        offset += size
    raise ValueError(f"feature array bit {target_bit} is absent")


def adjacent_array_change(previous: tuple[float, ...], current: tuple[float, ...]) -> float:
    """Candidate d2cc0 bit-14 value from the 19 noninitial array-12 entries."""
    if len(previous) != 20 or len(current) != 20:
        raise ValueError("expected 20 feature-array entries")
    squared = 0.0
    for index in range(1, 20):
        delta = f32(current[index] - previous[index])
        squared = f32(squared + f32(delta * delta))
    return f32(f32(math.sqrt(squared)) / 19.0)


def crossing_distribution(pcm: tuple[int, ...], start: int) -> float:
    """Model the ten-bin crossing-dispersion term written by DSE d92e0."""
    count = 512
    total = 0.0
    for sample in pcm[start:start + count]:
        total = f32(total + sample)
    mean = f32(total / count)
    bins = [0] * 10
    crossings = 0
    for position in range(count - 1):
        current = f32(pcm[start + position] - mean)
        following = f32(pcm[start + position + 1] - mean)
        if f32(current * following) < 0.0 or current == 0.0:
            bin_index = int(f32(f32(position / (count - 1)) * 10.0))
            bins[bin_index] += 1
            crossings += 1
    if crossings == 0:
        return 0.0
    average = f32(crossings / 10.0)
    difference = 0.0
    for amount in bins:
        difference = f32(difference + abs(f32(amount - average)))
    return f32(f32(difference / 10.0) / average)


def audit_bank(path: Path, expected_hash: str, model_type: type) -> dict:
    ddi = path.read_bytes()
    actual_hash = hashlib.sha256(ddi).hexdigest()
    if actual_hash != expected_hash:
        raise ValueError(f"DDI hash differs: {path}")
    model = model_type(ddi)
    model.read()
    samples = list((model.vqm_data or {}).values())
    if len(samples) != 3:
        raise ValueError(f"expected three VQM samples in {path}")

    rows = []
    with path.with_suffix(".ddb").open("rb") as stream:
        with mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as ddb:
            for sample_index, sample in enumerate(samples):
                frames = []
                frame_hash = hashlib.sha256()
                controls = {name: [] for name in ("trailer", "bit18", "bit19")}
                scalar_values = {bit: [] for bit in (*range(10), *range(14, 20))}
                flags = Counter()
                masks = Counter()
                f0_values = []
                spectrum_bins = []
                for item in sample["epr"]:
                    start, _ = pointer(item, ddi)
                    if start + 8 > len(ddb):
                        raise ValueError("VQM frame pointer outside DDB")
                    size = struct.unpack_from("<I", ddb, start + 4)[0]
                    if size < 28 or start + size > len(ddb):
                        raise ValueError("VQM frame extent outside DDB")
                    raw = ddb[start:start + size]
                    frame = probe_frm2.parse_frame(raw)
                    if not isinstance(frame, probe_frm2.VqmFrame):
                        raise ValueError("VQM part points to a non-VQM frame")
                    frames.append(frame)
                    frame_hash.update(raw)
                    masks[f"0x{frame.mask:x}"] += 1
                    flags[f"0x{frame.feature_block.flags:x}"] += 1
                    f0_values.append(frame.f0_hz)
                    spectrum_bins.append(len(frame.spectrum_amplitudes))
                    controls["trailer"].append(frame.feature_trailer)
                    controls["bit18"].append(frame.bit18_value)
                    controls["bit19"].append(frame.bit19_value)
                    decoded = feature_scalars(frame)
                    for bit in scalar_values:
                        if bit in decoded:
                            scalar_values[bit].append(decoded[bit])

                if not all(math.isfinite(value) and value > 0 for value in f0_values):
                    raise ValueError("VQM contains a nonpositive or nonfinite F0")
                pitch2_prediction = struct.unpack(
                    "<f", struct.pack(
                        "<f", statistics.fmean(
                            1200.0 * math.log2(value / 440.0) for value in f0_values
                        )
                    )
                )[0]
                pitch2_saved = float(sample["pitch2"])
                pitch2_error = abs(pitch2_prediction - pitch2_saved)
                pitch2_exact = struct.pack("<f", pitch2_prediction) == struct.pack(
                    "<f", pitch2_saved
                )
                snd_start, pcm_count = pointer(sample["snd"], ddi)
                if ddb[snd_start:snd_start + 4] != b"SND ":
                    raise ValueError("VQM SND pointer does not identify a sound chunk")
                if struct.unpack_from("<I", ddb, snd_start + 4)[0] != 18 + 2 * pcm_count:
                    raise ValueError("VQM SND chunk size differs from PCM count")
                if struct.unpack_from("<I", ddb, snd_start + 8)[0] != 44100 or sample["fs"] != 44100:
                    raise ValueError("VQM sample rate differs from feature model")
                if struct.unpack_from("<H", ddb, snd_start + 12)[0] != 1:
                    raise ValueError("VQM PCM is not mono")
                if struct.unpack_from("<I", ddb, snd_start + 14)[0] != pcm_count:
                    raise ValueError("VQM PCM count differs from SND header")
                if pcm_count != len(frames) * 256:
                    raise ValueError("VQM PCM/frame hop count differs")
                pcm = struct.unpack_from(f"<{pcm_count}h", ddb, snd_start + 18)
                feature2 = scalar_values[2]
                if any(len(scalar_values[bit]) != len(frames) for bit in (0, 1, 2)):
                    raise ValueError("VQM raw-feature flags differ between frames")
                raw_feature_errors = {bit: [] for bit in (0, 1, 2)}
                raw_feature_mismatches = {bit: [] for bit in (0, 1, 2)}
                crossing_distribution_errors = []
                array12 = [feature_array(frame, 12) for frame in frames]
                adjacent_array_errors = []
                for frame_index in range(1, len(frames)):
                    start = (frame_index - 1) * 256
                    if start + 512 > pcm_count:
                        raise ValueError("VQM raw-feature window exceeds PCM")
                    predicted = raw_features(pcm, start, 256)
                    crossing_distribution_errors.append(
                        abs(crossing_distribution(pcm, start) - scalar_values[15][frame_index])
                    )
                    adjacent_array_errors.append(abs(
                        adjacent_array_change(array12[frame_index - 1], array12[frame_index])
                        - scalar_values[14][frame_index]
                    ))
                    for bit, key in ((0, "feature4"), (1, "feature8"), (2, "feature12")):
                        error = abs(predicted[key] - scalar_values[bit][frame_index])
                        raw_feature_errors[bit].append(error)
                        if error > 1e-6 and len(raw_feature_mismatches[bit]) < 8:
                            raw_feature_mismatches[bit].append({
                                "frame_index": frame_index,
                                "absolute_error": error,
                            })
                raw_feature_pcm_match = {
                    str(bit): {
                        "compared": len(errors),
                        "within_1e_6": sum(error <= 1e-6 for error in errors),
                        "max_error": max(errors),
                        "mismatches": raw_feature_mismatches[bit],
                    }
                    for bit, errors in raw_feature_errors.items()
                }
                crossing_distribution_match = {
                    "compared": len(crossing_distribution_errors),
                    "within_1e_6": sum(error <= 1e-6 for error in crossing_distribution_errors),
                    "max_error": max(crossing_distribution_errors),
                }
                adjacent_array_match = {
                    "compared": len(adjacent_array_errors),
                    "within_1e_6": sum(error <= 1e-6 for error in adjacent_array_errors),
                    "max_error": max(adjacent_array_errors),
                }
                log_energy_errors = [
                    abs(f32(10.0 * f32(math.log10(energy))) - saved)
                    for energy, saved in zip(scalar_values[2], scalar_values[5])
                ]
                pcm_energy_candidates = {}
                for shift in (-512, -256, 0, 256, 512):
                    errors = []
                    for frame_index, saved in enumerate(feature2):
                        start = frame_index * 256 + shift
                        if start < 0 or start + 512 > pcm_count:
                            continue
                        predicted = raw_features(pcm, start, 256)["feature12"]
                        errors.append(abs(predicted - saved))
                    pcm_energy_candidates[str(shift)] = {
                        "compared": len(errors),
                        "within_1e_6": sum(error <= 1e-6 for error in errors),
                        "mean_error": statistics.fmean(errors),
                        "max_error": max(errors),
                    }
                first_prefixes = {
                    "zeros": [0] * 256,
                    "wrap_last_hop": list(pcm[-256:]),
                    "repeat_first_sample": [pcm[0]] * 256,
                    "mirror_first_hop": list(reversed(pcm[:256])),
                    "repeat_first_hop": list(pcm[:256]),
                }
                first_edge_candidates = {}
                for name, prefix in first_prefixes.items():
                    window = prefix + list(pcm[:256]) + [0]
                    predicted = raw_features(window, 0, 256)["feature12"]
                    first_edge_candidates[name] = {
                        "predicted": predicted,
                        "absolute_error": abs(predicted - feature2[0]),
                    }
                rows.append({
                    "sample_index": sample_index,
                    "frame_count": len(frames),
                    "frame_sequence_sha256": frame_hash.hexdigest(),
                    "masks": dict(sorted(masks.items())),
                    "feature_flags": dict(sorted(flags.items())),
                    "spectrum_bins": describe_numbers(spectrum_bins),
                    "f0_hz": describe_numbers(f0_values),
                    "pitch1_saved": float(sample["pitch1"]),
                    "pitch2_saved_cent": pitch2_saved,
                    "pitch2_frame_mean_cent": pitch2_prediction,
                    "pitch2_python_mean_exact_float32": pitch2_exact,
                    "pitch2_error_cent": pitch2_error,
                    "pitch2_within_0_0001_cent": pitch2_error <= 0.0001,
                    "controls": {name: describe_numbers(values) for name, values in controls.items()},
                    "feature_scalars": {
                        str(bit): describe_numbers(values)
                        for bit, values in scalar_values.items()
                    },
                    "feature2_pcm_energy_candidates": pcm_energy_candidates,
                    "feature2_first_edge_candidates": first_edge_candidates,
                    "raw_feature_pcm_match": raw_feature_pcm_match,
                    "crossing_distribution_match_bit15": crossing_distribution_match,
                    "adjacent_array12_change_match_bit14": adjacent_array_match,
                    "feature5_from_feature2": {
                        "compared": len(log_energy_errors),
                        "within_1e_5": sum(error <= 1e-5 for error in log_energy_errors),
                        "max_error": max(log_energy_errors),
                    },
                })
    return {"bank": path.stem, "ddi_sha256": actual_hash, "samples": rows}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--voice-root", type=Path, required=True)
    parser.add_argument("--ddb-tools", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()

    manifest_data = args.manifest.read_bytes()
    manifest = json.loads(manifest_data)
    parser_hash = hashlib.sha256(
        (args.ddb_tools / "utils" / "ddi_utils.py").read_bytes()
    ).hexdigest()
    if parser_hash != manifest["parser_sha256"]:
        raise ValueError("DDI parser hash differs from reference manifest")
    model_type = load_public_parser(args.ddb_tools)
    banks = []
    for row in manifest["banks"]:
        matches = list(args.voice_root.rglob(row["bank"] + ".ddi"))
        if len(matches) != 1:
            raise ValueError(f"expected one DDI for {row['bank']}, found {len(matches)}")
        banks.append(audit_bank(matches[0], row["ddi_sha256"], model_type))

    signatures = [row["frame_sequence_sha256"]
                  for bank in banks for row in bank["samples"]]
    if len(banks) != 8 or len(signatures) != 24 or len(set(signatures)) != 3:
        raise ValueError("V4X VQM sample coverage or independence differs")
    for bank in banks:
        for row in bank["samples"]:
            frame_count = row["frame_count"]
            if row["masks"] != {"0xe2287": frame_count}:
                raise ValueError("V4X VQM frame mask differs")
            if row["feature_flags"] != {"0x1fffff": frame_count}:
                raise ValueError("V4X VQM feature flags differ")
            if row["spectrum_bins"]["max"] != 0:
                raise ValueError("V4X VQM unexpectedly stores spectral bins")
            for name, expected in (("trailer", 0), ("bit18", 10), ("bit19", 2049)):
                values = row["controls"][name]
                if values["unique"] != 1 or values["min"] != expected:
                    raise ValueError(f"V4X VQM {name} differs")
            if not row["pitch2_within_0_0001_cent"]:
                raise ValueError("V4X VQM pitch2 differs from frame mean")
            for bit in ("0", "1", "2"):
                if row["raw_feature_pcm_match"][bit]["compared"] != frame_count - 1:
                    raise ValueError("V4X VQM raw-feature window coverage differs")
            if row["raw_feature_pcm_match"]["2"]["within_1e_6"] != frame_count - 1:
                raise ValueError("V4X VQM feature2 PCM calculation differs")
            if row["feature5_from_feature2"]["max_error"] != 0:
                raise ValueError("V4X VQM log-energy feature differs")
    independent_rows = {}
    for bank in banks:
        for row in bank["samples"]:
            independent_rows.setdefault(row["frame_sequence_sha256"], row)
    result = {
        "manifest_sha256": hashlib.sha256(manifest_data).hexdigest(),
        "parser_sha256": parser_hash,
        "bank_count": len(banks),
        "sample_count": len(signatures),
        "independent_frame_sequences": len(set(signatures)),
        "frame_count": sum(row["frame_count"]
                           for bank in banks for row in bank["samples"]),
        "feature2_pcm_interior_matches": sum(
            row["raw_feature_pcm_match"]["2"]["within_1e_6"]
            for bank in banks for row in bank["samples"]
        ),
        "feature5_log_energy_exact": sum(
            row["feature5_from_feature2"]["compared"]
            for bank in banks for row in bank["samples"]
        ),
        "raw_crossing_candidate_bit15_independent": {
            "compared": sum(row["crossing_distribution_match_bit15"]["compared"]
                            for row in independent_rows.values()),
            "within_1e_6": sum(row["crossing_distribution_match_bit15"]["within_1e_6"]
                                for row in independent_rows.values()),
        },
        "adjacent_array_candidate_bit14_independent": {
            "compared": sum(row["adjacent_array12_change_match_bit14"]["compared"]
                            for row in independent_rows.values()),
            "within_1e_6": sum(row["adjacent_array12_change_match_bit14"]["within_1e_6"]
                                for row in independent_rows.values()),
        },
        "pitch2_python_mean_exact_float32": sum(
            row["pitch2_python_mean_exact_float32"]
            for bank in banks for row in bank["samples"]
        ),
        "pitch2_within_0_0001_cent": sum(
            row["pitch2_within_0_0001_cent"] for bank in banks for row in bank["samples"]
        ),
        "pitch2_max_error_cent": max(
            row["pitch2_error_cent"] for bank in banks for row in bank["samples"]
        ),
        "banks": banks,
        "scope": "Installed V4X VQMp frames only; aggregated metadata and hashes, no frame or PCM bytes.",
    }
    args.evidence.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n",
                             encoding="utf-8")
    print(json.dumps({key: result[key] for key in (
        "bank_count", "sample_count", "frame_count", "independent_frame_sequences",
        "feature2_pcm_interior_matches", "feature5_log_energy_exact",
        "pitch2_python_mean_exact_float32", "pitch2_within_0_0001_cent",
        "pitch2_max_error_cent"
    )}))


if __name__ == "__main__":
    main()
