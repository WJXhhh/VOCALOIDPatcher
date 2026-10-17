"""Compare VQM bit-13 bit16 with voiced-window fundamental power.

Installed V4X material is read only. The report contains bounded error
statistics, hashes and synthetic-signal checks, never frame or PCM payloads.
"""

from __future__ import annotations

import argparse
import cmath
import ctypes as c
import hashlib
import json
import math
import mmap
from pathlib import Path
import statistics
import struct

from audit_frm2_numeric import F, I, Native, P, Q, SHA256, f32
from audit_v4x_vqm_fields import feature_scalars
import probe_frm2
from verify_frm2_analysis_windows import window
from verify_reference_ddi import load_public_parser, pointer
from verify_vqm_feature_gain_response import capture


def load_sequences(manifest_path: Path, voice_root: Path,
                   parser_root: Path, *, include_component_phases: bool = False
                   ) -> tuple[str, str, list[dict]]:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    parser_hash = hashlib.sha256(
        (parser_root / "utils" / "ddi_utils.py").read_bytes()).hexdigest()
    if parser_hash != manifest["parser_sha256"]:
        raise ValueError("DDI parser differs from the fixed manifest")
    bank = next(row for row in manifest["banks"]
                if row["bank"] == "MIKU_V4X_Original_EVEC")
    paths = list(voice_root.rglob(bank["bank"] + ".ddi"))
    if len(paths) != 1:
        raise ValueError("expected one Original EVEC DDI")
    ddi_path = paths[0]
    ddi = ddi_path.read_bytes()
    if hashlib.sha256(ddi).hexdigest() != bank["ddi_sha256"]:
        raise ValueError("Original EVEC DDI hash differs")
    model = load_public_parser(parser_root)(ddi)
    model.read()
    parts = list((model.vqm_data or {}).values())
    if len(parts) != 3:
        raise ValueError("expected three independent VQM samples")
    sequences = []
    with ddi_path.with_suffix(".ddb").open("rb") as source:
        with mmap.mmap(source.fileno(), 0, access=mmap.ACCESS_READ) as ddb:
            for part in parts:
                frames = []
                for entry in part["epr"]:
                    start, _ = pointer(entry, ddi)
                    size = struct.unpack_from("<I", ddb, start + 4)[0]
                    frame = probe_frm2.parse_frame(ddb[start:start + size])
                    if not isinstance(frame, probe_frm2.VqmFrame):
                        raise ValueError("VQM frame reference differs")
                    features = feature_scalars(frame)
                    frames.append({
                        "f0": frame.f0_hz,
                        "features": features,
                        "bit6": features[6],
                        "bit16": features[16],
                        "first_component_frequency": (frame.component_frequencies_hz[0]
                                                      if frame.component_frequencies_hz else None),
                        "first_component_amplitude": (frame.component_amplitudes[0]
                                                      if frame.component_amplitudes else None),
                        "first_component_phase": (frame.component_phases_radians[0]
                                                  if frame.component_phases_radians else None),
                        "components": tuple(zip(frame.component_frequencies_hz,
                                                frame.component_amplitudes)),
                    })
                    if include_component_phases:
                        frames[-1]["component_phases"] = frame.component_phases_radians
                snd_start, pcm_count = pointer(part["snd"], ddi)
                if (ddb[snd_start:snd_start + 4] != b"SND "
                        or pcm_count != 256 * len(frames)):
                    raise ValueError("VQM SND reference differs")
                pcm = struct.unpack_from(f"<{pcm_count}h", ddb, snd_start + 18)
                sequences.append({
                    "frames": frames,
                    "pcm": pcm,
                    "pitch1": float(part["pitch1"]),
                })
    return parser_hash, bank["ddi_sha256"], sequences


def predict(pcm: tuple[int, ...] | list[float], index: int,
            frequency: float, weights: list[float]) -> float:
    size = len(weights)
    start = index * 256 - size // 2
    if start < 0 or start + size > len(pcm):
        raise ValueError("spectral window is outside PCM")
    samples = pcm[start:start + size]
    mean = statistics.fmean(samples)
    projection = sum(
        (sample - mean) * weight
        * cmath.exp(-1j * math.tau * frequency * offset / 44100)
        for offset, (sample, weight) in enumerate(zip(samples, weights))
    )
    magnitude = abs(projection)
    if magnitude <= 0 or not math.isfinite(magnitude):
        raise ValueError("fundamental projection is not finite and positive")
    # DSE window() coefficients have sum 2, so the projection has the
    # single-sided sinusoidal amplitude scale without a further factor.
    return 2 * math.log10(magnitude / 32768)


def errors_for(sequences: list[dict], weights: list[float],
               margin: int, use_pitch1: bool = False) -> list[list[float]]:
    rows = []
    for sequence in sequences:
        values = []
        frames = sequence["frames"]
        for index in range(margin, len(frames) - margin):
            frequency = sequence["pitch1"] if use_pitch1 else frames[index]["f0"]
            values.append(predict(sequence["pcm"], index, frequency,
                                  weights) - frames[index]["bit16"])
        rows.append(values)
    return rows


def summarize_errors(values: list[float]) -> dict:
    absolute = [abs(value) for value in values]
    return {
        "count": len(values),
        "median_absolute_error": statistics.median(absolute),
        "mean_absolute_error": statistics.fmean(absolute),
        "max_absolute_error": max(absolute),
    }


def synthetic_checks(dll: Path, weights: list[float]) -> list[dict]:
    results = []
    for frequency in (220, 440, 880):
        for shape in ("sine", "five_harmonics"):
            samples = [
                math.sin(math.tau * frequency * index / 44100)
                if shape == "sine" else
                sum(math.sin(math.tau * frequency * harmonic * index / 44100
                             + harmonic * 0.23) / harmonic
                    for harmonic in range(1, 6))
                for index in range(11025)
            ]
            rms = math.sqrt(statistics.fmean(value * value
                                            for value in samples))
            pcm = [f32(5000 * value / rms) for value in samples]
            observed = capture(dll, pcm, frequency, frequency)
            errors = [
                predict(pcm, index, frequency, weights) - observed[index][16]
                for index in range(8, 36)
            ]
            result = {"frequency_hz": frequency, "shape": shape,
                      **summarize_errors(errors)}
            if result["max_absolute_error"] >= 0.0004:
                raise AssertionError("owned tonal bit16 projection differs")
            results.append(result)
    return results


def run(manifest_path: Path, voice_root: Path, parser_root: Path,
        dll: Path) -> dict:
    parser_hash, ddi_hash, sequences = load_sequences(
        manifest_path, voice_root, parser_root)
    native = Native(dll)
    weights = window(native, 2048, 10)
    native_window = native.array(F, [0.0] * 2048)
    native_window_pointer = P(native_window)
    native.fn(0x4c250, Q, P, I, c.c_ubyte, P, I, I)(
        0, 2048, 10, c.byref(native_window_pointer), 0, 0)
    if native_window_pointer.value != native_window:
        raise AssertionError("native window unexpectedly replaced its buffer")
    native_window_error = max(
        abs(expected - F.from_address(native_window + 4 * index).value)
        for index, expected in enumerate(weights)
    )
    if native_window_error > 1e-6:
        raise AssertionError("2048-point window model differs from DSE")
    per_sample = []
    all_errors = []
    for sample_index, values in enumerate(errors_for(sequences, weights, 4)):
        saved = [frame["bit16"] for frame in
                 sequences[sample_index]["frames"][4:-4]]
        modeled = [actual + error for actual, error in zip(saved, values)]
        row = {"sample_index": sample_index,
               **summarize_errors(values),
               "correlation": statistics.correlation(modeled, saved)}
        per_sample.append(row)
        all_errors.extend(values)
    if len(all_errors) != 222 or any(
            row["mean_absolute_error"] >= 0.003
            for row in per_sample):
        raise AssertionError("saved VQM bit16 projection scope differs")

    family = []
    for kind in range(16):
        candidate = window(native, 2048, kind)
        errors = [item for row in errors_for(sequences, candidate, 4)
                  for item in row]
        family.append({"window_kind": kind,
                       **summarize_errors(errors)})
    if min(family, key=lambda row: row["mean_absolute_error"])["window_kind"] != 10:
        raise AssertionError("tested 2048-point window family differs")

    lengths = []
    for size in (512, 1024, 2048, 4096):
        candidate = window(native, size, 10)
        errors = [item for row in errors_for(sequences, candidate, 8)
                  for item in row]
        lengths.append({"window_size": size,
                        **summarize_errors(errors)})
    if (any(row["count"] != 198 for row in lengths)
            or min(lengths, key=lambda row: row["mean_absolute_error"])["window_size"] != 2048):
        raise AssertionError("tested window-length comparison differs")

    pitch1 = [item for row in errors_for(sequences, weights, 4,
                                         use_pitch1=True) for item in row]
    return {
        "dse_sha256": SHA256,
        "ddi_sha256": ddi_hash,
        "parser_sha256": parser_hash,
        "independent_samples": 3,
        "bit16_projection": "2*log10(abs(sum((pcm-mean)*window*exp(-i*2*pi*f0*n/44100)))/32768)",
        "window_model": "DSE 2048-point kind-10 coefficients normalized to sum 2",
        "native_window_2048_max_error": native_window_error,
        "window_weight_sum": sum(weights),
        "per_sample_saved_f0": per_sample,
        "all_saved_f0": summarize_errors(all_errors),
        "constant_pitch1_control": summarize_errors(pitch1),
        "window_family": family,
        "window_size_controls_common_198_frames": lengths,
        "owned_tonal_checks": synthetic_checks(dll, weights),
        "scope": "Direct projection closely models bit16 on 222 interior VQM frames and owned tonal PCM; nonzero residuals and missing first/last windows prevent claiming the original producer is fully reproduced.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--voice-root", type=Path, required=True)
    parser.add_argument("--ddb-tools", type=Path, required=True)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = run(args.manifest, args.voice_root, args.ddb_tools, args.dll)
    args.output.write_text(json.dumps(result, indent=2) + "\n",
                           encoding="utf-8")
    print(json.dumps({
        "saved_interior_frames": result["all_saved_f0"]["count"],
        "saved_mean_absolute_error": result["all_saved_f0"]["mean_absolute_error"],
        "owned_tonal_cases": len(result["owned_tonal_checks"]),
    }))


if __name__ == "__main__":
    main()
