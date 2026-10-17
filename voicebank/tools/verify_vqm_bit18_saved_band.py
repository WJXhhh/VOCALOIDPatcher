"""Compare the verified DRS bit18 ratio with three independent V4X VQM samples.

The installed V4X files are read only. The report stores aggregate errors and
hashes, never frame or PCM payloads.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import statistics

from audit_frm2_numeric import D, F, I, P, Q, SHA256
from probe_frm2_batch_outputs import run as analyze_batch
from verify_vqm_bit16_spectral_power import load_sequences
from verify_vqm_bit18_band_ratio import replay


def summary(errors: list[float]) -> dict:
    return {
        "frames": len(errors),
        "within_1e_6": sum(error <= 1e-6 for error in errors),
        "mean_absolute_error": statistics.fmean(errors),
        "max_absolute_error": max(errors),
    }


def compare_one(dll: Path, sample_index: int, sequence: dict,
                series_sink: list[tuple[list[float], list[float], list[float]]] | None = None,
                feature_sink: list[dict[str, float | int]] | None = None,
                config_588: int | None = None) -> dict:
    frames = sequence["frames"]
    pending: dict[int, tuple[list[float], int]] = {}
    native_replay = []
    native_to_saved = []
    saved_pitch_to_saved = []
    native_minus_saved = []
    native_values = []
    saved_values = []
    saved_f0_predictions = []
    observed_indices = []

    def before_step(native, context: int) -> None:
        ring = P.from_address(context + 0xc58).value
        item = P.from_address(ring).value if ring else 0
        if not item:
            return
        frame = P.from_address(item + 0x28).value
        if not frame or D.from_address(frame + 0x48).value < 0:
            return
        count = I.from_address(item).value
        data = P.from_address(item + 8).value
        if count != 2048 or not data:
            raise AssertionError("unexpected VQM PCM analysis spectrum")
        pending[frame] = ([F.from_address(data + 4 * index).value
                           for index in range(count)],
                          I.from_address(context + 0xad8).value)

    def after_step(native, context: int, frame: int) -> None:
        if frame not in pending:
            raise AssertionError("unpaired VQM PCM analysis frame")
        values, sample_rate = pending.pop(frame)
        index = round(D.from_address(frame + 0x48).value * 44100 / 256)
        if not 0 <= index < len(frames):
            raise AssertionError("VQM and DRS frame indices differ")
        observed_indices.append(index)
        stored_pitch = F.from_address(frame + 0xf0).value
        mask = Q.from_address(frame + 0x50).value
        native_pitch = (440 * 2 ** (stored_pitch / 1200)
                        if mask & (1 << 31) else stored_pitch)
        feature = P.from_address(frame + 0x128).value
        native_value = F.from_address(feature + 0x220).value
        saved_value = frames[index]["features"][18]
        if feature_sink is not None:
            feature_sink.append({
                "sample_index": sample_index,
                "frame_index": index,
                "drs_bit6": F.from_address(feature + 0x1c).value,
                "saved_bit6": frames[index]["features"][6],
                "drs_bit16": F.from_address(feature + 0x218).value,
                "saved_bit16": frames[index]["features"][16],
                "drs_bit18": native_value,
                "saved_bit18": saved_value,
                "drs_f0": native_pitch,
                "saved_f0": frames[index]["f0"],
            })
        native_values.append(native_value)
        saved_values.append(saved_value)
        native_replay.append(abs(replay(values, native_pitch, sample_rate)
                                 - native_value))
        native_to_saved.append(abs(native_value - saved_value))
        native_minus_saved.append(native_value - saved_value)
        saved_f0_prediction = replay(values, frames[index]["f0"], sample_rate)
        saved_f0_predictions.append(saved_f0_prediction)
        saved_pitch_to_saved.append(abs(saved_f0_prediction - saved_value))

    result = analyze_batch(dll, 0, 1, "steady", sequence["pitch1"],
                           pitch_seed=sequence["pitch1"],
                           pcm_override=[float(value) for value in sequence["pcm"]],
                           streaming=True, before_step_observer=before_step,
                           step_observer=after_step, config_588=config_588)
    if (result["frames"] != len(frames)
            or observed_indices != list(range(len(frames))) or pending):
        raise AssertionError("VQM PCM streaming frame count changed")
    if max(native_replay) > 1e-5:
        raise AssertionError("DRS bit18 producer replay differs on saved PCM")
    if series_sink is not None:
        series_sink.append((native_values, saved_values, saved_f0_predictions))
    lag_controls = []
    for lag in range(-4, 5):
        aligned = [(native_values[index + lag], saved_values[index])
                   for index in range(len(frames))
                   if 0 <= index + lag < len(frames)]
        lag_controls.append({"lag_frames": lag,
                             "frames": len(aligned),
                             "mean_absolute_error": statistics.fmean(
                                 abs(left - right) for left, right in aligned)})
    smoothing_controls = []
    for radius in (0, 1, 2, 4):
        aligned = [(statistics.fmean(native_values[index - radius:index + radius + 1]),
                    saved_values[index])
                   for index in range(radius, len(frames) - radius)]
        smoothing_controls.append({"radius_frames": radius,
                                   "frames": len(aligned),
                                   "mean_absolute_error": statistics.fmean(
                                       abs(left - right) for left, right in aligned)})
    return {
        "sample_index": sample_index,
        "drs_formula_to_drs": summary(native_replay),
        "drs_value_to_saved_vqm": summary(native_to_saved),
        "drs_minus_saved_signed_mean": statistics.fmean(native_minus_saved),
        "drs_minus_saved_signed_stddev": statistics.pstdev(native_minus_saved),
        "drs_spectrum_with_saved_f0_to_saved_vqm": summary(saved_pitch_to_saved),
        "frame_shift_controls": lag_controls,
        "temporal_mean_controls": smoothing_controls,
    }


def run(manifest: Path, voice_root: Path, parser_root: Path, dll: Path) -> dict:
    parser_hash, ddi_hash, sequences = load_sequences(
        manifest, voice_root, parser_root)
    cases = [compare_one(dll, index, sequence)
             for index, sequence in enumerate(sequences)]
    return {
        "dse_sha256": SHA256,
        "parser_sha256": parser_hash,
        "ddi_sha256": ddi_hash,
        "cases": cases,
        "scope": "Three independent saved V4X VQM sequences, 246 frames; reuses the DRS spectrum on saved PCM and tests saved F0, frame shifts, and simple temporal means. No voicebank payload is saved.",
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
    print(json.dumps({"frames": sum(row["drs_formula_to_drs"]["frames"]
                                    for row in report["cases"]),
                      "drs_exact": sum(row["drs_formula_to_drs"]["within_1e_6"]
                                       for row in report["cases"]),
                      "saved_pitch_mean_errors": [
                          row["drs_spectrum_with_saved_f0_to_saved_vqm"]["mean_absolute_error"]
                          for row in report["cases"]]}))


if __name__ == "__main__":
    main()
