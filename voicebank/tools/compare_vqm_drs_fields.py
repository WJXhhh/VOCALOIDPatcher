"""Compare DRS analysis of installed VQM PCM with saved VQM feature scalars."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import mmap
from pathlib import Path
import statistics
import struct

from audit_frm2_numeric import B, D, F, I, P, Q, SHA256
from audit_v4x_vqm_fields import feature_scalars
import probe_frm2
from probe_frm2_batch_outputs import run as analyze_batch
from verify_reference_ddi import load_public_parser, pointer


OFFSETS = {0: 4, 1: 8, 2: 12, 4: 0x14, 6: 0x1c, 9: 0x28,
           14: 0x20c, 15: 0x214, 16: 0x218, 17: 0x21c,
           18: 0x220, 19: 0x224}


def analyze(manifest_path: Path, voice_root: Path, parser_root: Path,
            dll: Path, sample_index: int, mode: int | None, automatic: int,
            config_5a0: int | None, estimator_variant: int | None,
            pitch_track: str | None = None) -> dict:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    parser_hash = hashlib.sha256(
        (parser_root / "utils" / "ddi_utils.py").read_bytes()
    ).hexdigest()
    if parser_hash != manifest["parser_sha256"]:
        raise ValueError("DDI parser hash differs from fixed manifest")
    bank = next(row for row in manifest["banks"]
                if row["bank"] == "MIKU_V4X_Original_EVEC")
    paths = list(voice_root.rglob(bank["bank"] + ".ddi"))
    if len(paths) != 1:
        raise ValueError("expected one V4X Original DDI")
    ddi_path = paths[0]
    ddi = ddi_path.read_bytes()
    if hashlib.sha256(ddi).hexdigest() != bank["ddi_sha256"]:
        raise ValueError("V4X Original DDI hash differs")
    model = load_public_parser(parser_root)(ddi)
    model.read()
    samples = list((model.vqm_data or {}).values())
    if len(samples) != 3 or sample_index not in range(3):
        raise ValueError("VQM sample index or coverage differs")
    sample = samples[sample_index]
    saved = []
    saved_f0 = []
    with ddi_path.with_suffix(".ddb").open("rb") as source:
        with mmap.mmap(source.fileno(), 0, access=mmap.ACCESS_READ) as ddb:
            for item in sample["epr"]:
                start, _ = pointer(item, ddi)
                size = struct.unpack_from("<I", ddb, start + 4)[0]
                frame = probe_frm2.parse_frame(ddb[start:start + size])
                if not isinstance(frame, probe_frm2.VqmFrame):
                    raise ValueError("VQM frame reference differs")
                saved.append(feature_scalars(frame))
                saved_f0.append(frame.f0_hz)
            snd_start, pcm_count = pointer(sample["snd"], ddi)
            if pcm_count != 256 * len(saved) or ddb[snd_start:snd_start + 4] != b"SND ":
                raise ValueError("VQM sound reference differs")
            pcm = list(struct.unpack_from(f"<{pcm_count}h", ddb, snd_start + 18))
    holder = []
    if pitch_track not in (None, "saved_f0"):
        raise ValueError("unsupported pitch track")
    curve = None
    if pitch_track == "saved_f0":
        if not all(math.isfinite(value) and value > 0 for value in saved_f0):
            raise ValueError("saved VQM F0 is not a positive finite track")
        curve = [(index / len(saved_f0), value)
                 for index, value in enumerate(saved_f0)]
        curve.append((1.0, saved_f0[-1]))
    summary = analyze_batch(
        dll, automatic, 1, "steady", float(sample["pitch1"]),
        analysis_mode=mode, pitch_seed=float(sample["pitch1"]),
        pitch_curve=curve,
        pcm_override=pcm,
        config_5a0=config_5a0,
        estimator_variant=estimator_variant,
        setup_observer=lambda native, context: holder.append((native, context)),
    )
    native, context = holder[0]
    live_config = P.from_address(context + 0xad0).value
    effective_mode = B.from_address(live_config + 0x558).value
    effective_5a0 = I.from_address(live_config + 0x5a0).value
    track = P.from_address(context + 0xcd0).value
    region_count = I.from_address(track + 0x140).value
    regions = P.from_address(track + 0x148).value
    produced = []
    flags_histogram = {}
    for region_index in range(region_count):
        region = P.from_address(regions + 8 * region_index).value
        count = I.from_address(region + 0x18).value
        pointers = P.from_address(region + 0x20).value
        for index in range(count):
            frame = P.from_address(pointers + 8 * index).value
            mask = Q.from_address(frame + 0x50).value
            feature = P.from_address(frame + 0x128).value
            if not mask & (1 << 13) or not feature:
                continue
            flags = I.from_address(feature).value
            flags_histogram[f"0x{flags:x}"] = flags_histogram.get(f"0x{flags:x}", 0) + 1
            values = {bit: F.from_address(feature + offset).value
                      for bit, offset in OFFSETS.items() if flags & (1 << bit)}
            time = D.from_address(frame + 0x48).value
            if math.isfinite(time):
                produced.append((round(time * 44100 / 256), values))
    feature_hash = hashlib.sha256()
    for frame_index, values in sorted(produced):
        for bit, value in sorted(values.items()):
            feature_hash.update(struct.pack("<iIf", frame_index, bit, value))
    produced_ranges = {}
    for bit in OFFSETS:
        values = [features[bit] for _, features in produced if bit in features]
        produced_ranges[str(bit)] = {
            "count": len(values),
            "min": min(values) if values else None,
            "max": max(values) if values else None,
            "nonzero_over_1e_6": sum(abs(value) > 1e-6 for value in values),
        }
    comparison = {}
    for shift in range(-4, 5):
        errors = {bit: [] for bit in OFFSETS}
        pairs = {bit: [] for bit in OFFSETS}
        for source_index, values in produced:
            target_index = source_index + shift
            if target_index not in range(len(saved)):
                continue
            for bit, value in values.items():
                errors[bit].append(abs(value - saved[target_index][bit]))
                pairs[bit].append((value, saved[target_index][bit]))
        offsets = {
            bit: statistics.median(saved - produced
                                   for produced, saved in values)
            if values else None
            for bit, values in pairs.items()
        }
        comparison[str(shift)] = {
            str(bit): {
                "compared": len(items),
                "mean_absolute_error": statistics.fmean(items) if items else None,
                "max_absolute_error": max(items) if items else None,
                "within_1e_5": sum(item <= 1e-5 for item in items),
                "median_saved_minus_produced": offsets[bit],
                "within_1e_5_after_median_offset": (
                    sum(abs((saved - produced) - offsets[bit]) <= 1e-5
                        for produced, saved in pairs[bit])
                    if items else 0
                ),
                "correlation": (
                    statistics.correlation(*zip(*pairs[bit]))
                    if len(items) >= 3
                    and len({pair[0] for pair in pairs[bit]}) > 1
                    and len({pair[1] for pair in pairs[bit]}) > 1
                    else None
                ),
            }
            for bit, items in errors.items()
        }
    return {
        "dse_sha256": SHA256,
        "ddi_sha256": bank["ddi_sha256"],
        "sample_index": sample_index,
        "saved_frames": len(saved),
        "analysis_mode": mode,
        "effective_analysis_mode": effective_mode,
        "config_5a0": config_5a0,
        "effective_config_5a0": effective_5a0,
        "estimator_variant": estimator_variant,
        "automatic_pitch": automatic,
        "pitch_track": pitch_track,
        "produced_frames": summary["frames"],
        "produced_feature_frames": len(produced),
        "produced_feature_sha256": feature_hash.hexdigest(),
        "produced_feature_ranges": produced_ranges,
        "feature_flags": flags_histogram,
        "comparison_by_frame_shift": comparison,
        "scope": "Read-only installed VQM PCM; disposable DRS analysis, aggregate feature errors only.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--voice-root", type=Path, required=True)
    parser.add_argument("--ddb-tools", type=Path, required=True)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--sample-index", type=int, default=0)
    parser.add_argument("--mode", type=int)
    parser.add_argument("--automatic", type=int, choices=(0, 1), default=0)
    parser.add_argument("--config-5a0", type=int, choices=(0, 1))
    parser.add_argument("--estimator-variant", type=int, choices=(0, 4))
    parser.add_argument("--pitch-track", choices=("saved_f0",))
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = analyze(args.manifest, args.voice_root, args.ddb_tools,
                     args.dll, args.sample_index, args.mode, args.automatic,
                     args.config_5a0, args.estimator_variant, args.pitch_track)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"produced_frames": result["produced_frames"],
                      "feature_frames": result["produced_feature_frames"],
                      "flags": result["feature_flags"]}))


if __name__ == "__main__":
    main()
