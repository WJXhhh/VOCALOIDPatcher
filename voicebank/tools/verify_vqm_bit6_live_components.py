"""Replay the DRS bit6 component ratio from its live intermediate records.

The streaming queue retains processed 20-byte component records several steps
before each output frame. Installed V4X material is read only; the report
stores aggregate errors, hashes, and counts without record or PCM payloads.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import random
import statistics

from audit_frm2_numeric import D, F, I, P, SHA256, f32
from probe_frm2_batch_outputs import owned_pcm, run as analyze_batch
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit6_component_ratio import predict
from verify_vqm_feature_shape_response import equal_rms


def linearize(amplitude: float) -> float:
    value = f32(amplitude)
    if value <= -3.4028234663852886e38:
        return 0.0
    return f32(32768 * math.exp(value * 0.1151292546497))


def compare(dll: Path, name: str, pcm: list[float], pitch: float,
            saved: list[float] | None = None) -> dict:
    predictions: dict[int, float] = {}
    actual: dict[int, float] = {}
    record_counts: dict[int, int] = {}

    def before_step(native, context: int) -> None:
        queue = P.from_address(context + 0xc20).value
        for slot in range(I.from_address(context + 0xc18).value - 1):
            link = P.from_address(queue + 8 * slot).value
            frame = P.from_address(link + 0x28).value
            time = D.from_address(frame + 0x48).value
            count = I.from_address(link + 0x18).value
            data = P.from_address(link + 0x20).value
            if time < 0:
                continue
            index = round(time * 44100 / 256)
            if index in predictions:
                continue
            if count == 0:
                predictions[index] = -1.0
                record_counts[index] = 0
                continue
            if not 0 < count < 1000 or not data:
                raise AssertionError("unexpected intermediate component list")
            components = [(F.from_address(data + 20 * item).value,
                           F.from_address(data + 20 * item + 4).value)
                          for item in range(count)]
            predictions[index] = predict(components, linearize)
            record_counts[index] = count

    def after_step(native, context: int, frame: int) -> None:
        index = round(D.from_address(frame + 0x48).value * 44100 / 256)
        feature = P.from_address(frame + 0x128).value
        if not feature or index in actual:
            raise AssertionError("missing or duplicate DRS bit6 output")
        actual[index] = F.from_address(feature + 0x1c).value

    result = analyze_batch(dll, 0, 1, "steady", pitch, pitch_seed=pitch,
                           pcm_override=pcm, streaming=True,
                           before_step_observer=before_step,
                           step_observer=after_step)
    if (sorted(actual) != list(range(result["frames"]))
            or any(index not in predictions for index in actual)):
        raise AssertionError("bit6 intermediate records do not cover output frames")
    replay_errors = [abs(predictions[index] - actual[index])
                     for index in sorted(actual)]
    if max(replay_errors) != 0:
        raise AssertionError("bit6 intermediate-record replay differs from DRS")
    negative_one = sum(value == -1.0 for value in actual.values())
    if name == "silence_440_pitch_seed" and negative_one != result["frames"]:
        raise AssertionError("silence did not take the bit6 fallback branch")
    report = {
        "case": name,
        "frames": result["frames"],
        "float32_exact_to_drs": sum(error == 0 for error in replay_errors),
        "negative_one_outputs": negative_one,
        "intermediate_record_count_min": min(record_counts[index] for index in actual),
        "intermediate_record_count_max": max(record_counts[index] for index in actual),
    }
    if saved is not None:
        if len(saved) != result["frames"]:
            raise AssertionError("saved VQM and DRS frame count differ")
        saved_errors = [abs(predictions[index] - saved[index])
                        for index in sorted(actual)]
        report["saved_vqm_comparison"] = {
            "within_1e_6": sum(error <= 1e-6 for error in saved_errors),
            "mean_absolute_error": statistics.fmean(saved_errors),
            "max_absolute_error": max(saved_errors),
        }
    return report


def run(manifest: Path, voice_root: Path, parser_root: Path, dll: Path) -> dict:
    size = 11025
    sine = [math.sin(math.tau * 440 * index / 44100)
            for index in range(size)]
    generator = random.Random(104)
    noise = [generator.uniform(-1, 1) for _ in range(size)]
    synthetic = [
        compare(dll, "five_harmonics_440", owned_pcm("steady", 440), 440),
        compare(dll, "five_harmonics_880", owned_pcm("steady", 880), 880),
        compare(dll, "sine_440", equal_rms(sine), 440),
        compare(dll, "sine_plus_noise_440",
                equal_rms([tone + 0.3 * random_value
                           for tone, random_value in zip(sine, noise)]), 440),
        compare(dll, "silence_440_pitch_seed", [0.0] * size, 440),
    ]
    parser_hash, ddi_hash, sequences = load_sequences(
        manifest, voice_root, parser_root)
    saved_cases = [
        compare(dll, f"independent_vqm_{index}",
                [float(value) for value in sequence["pcm"]],
                sequence["pitch1"],
                [frame["bit6"] for frame in sequence["frames"]])
        for index, sequence in enumerate(sequences)
    ]
    return {
        "dse_sha256": SHA256,
        "parser_sha256": parser_hash,
        "ddi_sha256": ddi_hash,
        "synthetic_cases": synthetic,
        "saved_pcm_cases": saved_cases,
        "scope": "Exact DRS bit6 replay from live processed component records on valid tested frames. The saved VQM final bit6 values and production configuration remain separate questions.",
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
        "synthetic_exact": sum(row["float32_exact_to_drs"]
                               for row in report["synthetic_cases"]),
        "saved_pcm_exact_to_drs": sum(row["float32_exact_to_drs"]
                                      for row in report["saved_pcm_cases"]),
        "saved_vqm_mean_errors": [
            row["saved_vqm_comparison"]["mean_absolute_error"]
            for row in report["saved_pcm_cases"]],
    }))


if __name__ == "__main__":
    main()
