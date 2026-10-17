"""Replay the DSE bit18 band-ratio write on owned streaming PCM.

The input spectrum is copied just before each native streaming step. The
resulting frame's saved pitch is decoded after that step, then paired by native
frame pointer with the spectrum. No voicebank PCM is used.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import random
import statistics

from audit_frm2_numeric import D, F, I, P, Q, SHA256, f32
from probe_frm2_batch_outputs import owned_pcm, run as analyze_batch
from verify_vqm_feature_shape_response import equal_rms


def replay(values: list[float], frequency: float, sample_rate: int) -> float:
    count = len(values)
    if count != 2048 or frequency <= 0 or sample_rate != 44100:
        raise AssertionError("unexpected spectrum or frequency")
    denominator = f32(f32(sample_rate) * f32(0.5))
    first_end = f32(f32(f32(frequency * f32(1.5)) / denominator) * f32(count))
    second_start = f32(frequency * 2.5 / denominator * count)
    second_end = f32(frequency * 6.5 / denominator * count)
    if not 0 < first_end < second_start < second_end < count:
        raise AssertionError("band limits are outside the observed spectrum")

    first = 0.0
    second = 0.0
    for index in range(math.ceil(first_end)):
        first = f32(first + values[index])
    for index in range(int(second_start), math.ceil(second_end)):
        second = f32(second + values[index])
    ratio = f32(max(f32(1e-5), f32(second * f32(0.25)))
                / max(f32(1e-5), first))
    decibels = f32(20 * math.log10(ratio / 32768))
    return f32(f32(decibels / f32(100)) + f32(5))


def case(dll: Path, name: str, pcm: list[float], pitch: float) -> dict:
    pending: dict[int, tuple[list[float], int]] = {}
    residuals = []
    exact = 0
    observed = 0

    def before_step_observer(native, context: int) -> None:
        ring = P.from_address(context + 0xc58).value
        item = P.from_address(ring).value if ring else 0
        if not item:
            return
        frame = P.from_address(item + 0x28).value
        if not frame or D.from_address(frame + 0x48).value < 0:
            return
        count = I.from_address(item).value
        data = P.from_address(item + 8).value
        sample_rate = I.from_address(context + 0xad8).value
        if count != 2048 or not data or sample_rate != 44100:
            raise AssertionError("unexpected DRS input-spectrum layout")
        pending[frame] = ([F.from_address(data + index * 4).value
                           for index in range(count)], sample_rate)

    def step_observer(native, context: int, frame: int) -> None:
        nonlocal exact, observed
        observed += 1
        if frame not in pending:
            raise AssertionError("output frame has no captured input spectrum")
        values, sample_rate = pending.pop(frame)
        stored_pitch = F.from_address(frame + 0xf0).value
        mask = Q.from_address(frame + 0x50).value
        frequency = (440 * 2 ** (stored_pitch / 1200)
                     if mask & (1 << 31) else stored_pitch)
        predicted = replay(values, frequency, sample_rate)
        feature = P.from_address(frame + 0x128).value
        actual = F.from_address(feature + 0x220).value
        residuals.append(abs(predicted - actual))
        exact += predicted == actual

    result = analyze_batch(dll, 0, 1, "steady", pitch, pitch_seed=pitch,
                           pcm_override=pcm, streaming=True,
                           before_step_observer=before_step_observer,
                           step_observer=step_observer)
    if observed != result["frames"] or len(residuals) != observed:
        raise AssertionError("streaming frame count changed")
    if pending:
        raise AssertionError("unpaired DRS spectrum observations remain")
    if max(residuals) > 1e-5:
        raise AssertionError(f"bit18 frame replay exceeded bound: {name}, "
                             f"max={max(residuals)}, frame={residuals.index(max(residuals))}")
    return {
        "case": name,
        "frames": observed,
        "paired_frames": len(residuals),
        "float32_exact": exact,
        "mean_absolute_error": statistics.fmean(residuals),
        "max_absolute_error": max(residuals),
    }


def run(dll: Path) -> dict:
    size = 11025
    sine = [math.sin(math.tau * 440 * index / 44100) for index in range(size)]
    generator = random.Random(104)
    noise = [generator.uniform(-1, 1) for _ in range(size)]
    cases = [
        case(dll, "five_harmonics_440", owned_pcm("steady", 440), 440),
        case(dll, "five_harmonics_880", owned_pcm("steady", 880), 880),
        case(dll, "sine_440", equal_rms(sine), 440),
        case(dll, "sine_plus_noise_440",
             equal_rms([tone + 0.3 * random_value
                        for tone, random_value in zip(sine, noise)]), 440),
    ]
    return {
        "dse_sha256": SHA256,
        "sample_rate": 44100,
        "source": "owned PCM; DSE streaming input spectrum and same output frame",
        "cases": cases,
        "scope": "Validates the DRS internal bit18 band-ratio producer on four controlled signals; does not establish VQM authoring inputs or saved-frame equality.",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.dll)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"paired_frames": sum(row["paired_frames"] for row in report["cases"]),
                      "float32_exact": sum(row["float32_exact"] for row in report["cases"]),
                      "max_absolute_error": max(row["max_absolute_error"] for row in report["cases"])}))


if __name__ == "__main__":
    main()
