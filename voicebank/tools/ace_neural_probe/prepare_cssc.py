"""Prepare an offline CSSC/BAIB control experiment; never claims model inference.

Only the standard library and an external FFmpeg are required. The CSSC source
checkout and its tokenizer/config are read in place, never imported or modified.
"""
import argparse
import ast
import bisect
import hashlib
import json
import math
import shutil
import subprocess
import wave
from pathlib import Path


TECHNIQUES = ("mix", "falsetto", "breathy", "pharyngeal", "glissando", "vibrato")
RATE, HOP = 24000, 256


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def finite(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError("Expected a finite number")
    return float(value)


def intervals(items, duration):
    if not isinstance(items, list) or not items or len(items) > 50000:
        raise ValueError("Expected 1..50000 ordered annotation intervals")
    previous_end = 0.0
    for item in items:
        start, end = finite(item["start"]), finite(item["end"])
        if not previous_end <= start < end <= duration:
            raise ValueError("Annotations overlap or fall outside source audio")
        previous_end = end


def curve_points(request):
    if request.get("air_units") != "ace-signed":
        raise ValueError("air_units must be ace-signed: (panel value - 50) / 50")
    points = request["air"]
    if not isinstance(points, list) or not points or len(points) > 50000:
        raise ValueError("Expected 1..50000 air points")
    result = []
    for point in points:
        t, value = finite(point["seconds"]), finite(point["value"])
        # This model's breathy condition has no demonstrated inverse operation.
        if not 0 <= value <= 1:
            raise ValueError("This probe supports positive Air only, not de-breathing")
        if result and t <= result[-1][0]:
            raise ValueError("Air points must have strictly increasing times")
        result.append((t, value))
    return result


def interpolate(points, times, t):
    index = bisect.bisect_right(times, t) - 1
    if index < 0:
        return 0.0
    if index == len(points) - 1:
        return points[index][1]
    a, b = points[index], points[index + 1]
    return a[1] + (b[1] - a[1]) * ((t - a[0]) / (b[0] - a[0]))


def prepare(request_path, cssc_root, output, ffmpeg="ffmpeg"):
    request_path, cssc_root, output = map(lambda p: Path(p).resolve(), (request_path, cssc_root, output))
    if output.exists():
        raise ValueError("Output directory must be new; existing files are never overwritten")
    if request_path.stat().st_size > 8 * 1024 * 1024:
        raise ValueError("Request exceeds 8 MiB")
    request = read_json(request_path)
    if request.get("schema_version") != 1:
        raise ValueError("Unsupported request schema")
    config_path, tokenizer_path = cssc_root / "configs/config.json", cssc_root / "phoneme_tokenizer.json"
    config, tokenizer = read_json(config_path), read_json(tokenizer_path)
    loader_tree = ast.parse((cssc_root / "data_utils.py").read_text(encoding="utf-8-sig"))
    upstream_order = next((ast.literal_eval(node.value) for node in loader_tree.body
                           if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == "TECHNIQUES"
                                                                   for t in node.targets)), None)
    if tuple(upstream_order or ()) != TECHNIQUES:
        raise ValueError("Upstream technique channel order changed")
    data, model = config["data"], config["model"]
    if data["sampling_rate"] != RATE or data["hop_length"] != HOP or data["filter_length"] != 1024:
        raise ValueError("This probe targets the inspected 24 kHz / 256-hop / 1024-FFT CSSC configuration")
    if math.prod(model["upsample_rates"]) != HOP:
        raise ValueError("Decoder hop mismatch")
    if sorted(tokenizer.values()) != list(range(263)):
        raise ValueError("Expected the inspected 263-entry model tokenizer")
    source = (request_path.parent / request["source_wave"]).resolve()
    if source.stat().st_size > 128 * 1024 * 1024:
        raise ValueError("Input exceeds 128 MiB")
    with wave.open(str(source), "rb") as reader:
        channels, bits, rate, samples = reader.getnchannels(), reader.getsampwidth(), reader.getframerate(), reader.getnframes()
        if channels != 1 or bits != 2 or not 16000 <= rate <= 192000:
            raise ValueError("First probe supports mono PCM16 input only")
        if not 0 < samples <= 16000000:
            raise ValueError("Invalid source sample count")
    duration = samples / rate
    if duration > 60:
        raise ValueError("Use a <=60 second offline probe segment")
    notes, phonemes = request["notes"], request["phonemes"]
    intervals(notes, duration)
    intervals(phonemes, duration)
    for note in notes:
        if type(note["midi"]) is not int or not 0 <= note["midi"] <= 127:
            raise ValueError("MIDI notes must be integers in [0, 127]")
    for phoneme in phonemes:
        # model_token must come from an explicit language-aware mapping. Never
        # silently feed VOCALOID labels into the model's multilingual vocabulary.
        token = phoneme["model_token"]
        if token not in tokenizer or token in ("<UNK>", "<PAD>"):
            raise ValueError(f"Unmapped model phoneme: {token!r}")
    points = curve_points(request)
    if any(t > duration for t, _ in points):
        raise ValueError("Air points exceed source duration")
    executable = shutil.which(ffmpeg)
    if executable is None:
        raise ValueError("FFmpeg is required for band-limited audio resampling")
    source_hash = sha256(source)
    output.mkdir(parents=True)
    destination = output / "sample.wav"
    subprocess.run([executable, "-nostdin", "-v", "error", "-i", str(source), "-ar", str(RATE),
                    "-ac", "1", "-c:a", "pcm_s16le", str(destination)], check=True, timeout=60)
    if sha256(source) != source_hash:
        raise ValueError("Source changed during preparation")
    with wave.open(str(destination), "rb") as reader:
        resampled_count = reader.getnframes()
    # Upstream reflect padding plus center=False STFT yields floor(N / hop).
    frames = resampled_count // HOP
    if frames < 4:
        raise ValueError("Source is too short for the inspected model loader")
    mask = [False] * frames
    for phoneme in phonemes:
        if phoneme["model_token"] in ("<SP>", "<AP>"):
            continue
        a, b = (min(frames, round(phoneme[k] * RATE / HOP)) for k in ("start", "end"))
        for index in range(a, b):
            mask[index] = True
    note_mask = [False] * frames
    for note in notes:
        a, b = (min(frames, round(note[k] * RATE / HOP)) for k in ("start", "end"))
        for index in range(a, b):
            note_mask[index] = True
    times = [p[0] for p in points]
    values = [interpolate(points, times, i * HOP / RATE) if mask[i] and note_mask[i] else 0.0 for i in range(frames)]
    annotation = {
        "word": "ace-offline-probe",
        "note": [n["midi"] for n in notes],
        "note_start": [n["start"] for n in notes], "note_end": [n["end"] for n in notes],
        "ph": [p["model_token"] for p in phonemes],
        "ph_start": [p["start"] for p in phonemes], "ph_end": [p["end"] for p in phonemes],
        **{name: [0] for name in TECHNIQUES},
        "breathy": values,
        "breathy_start": [i * HOP / RATE for i in range(frames)],
        "breathy_end": [(i + 1) * HOP / RATE for i in range(frames)],
    }
    (output / "sample.json").write_text(json.dumps([annotation], ensure_ascii=False, indent=2), encoding="utf-8")
    # A worker must load these external features and a matching learned model.
    # Preparation must never substitute random weights or the previous DSP.
    report = {
        "stage": "prepared_not_inferred", "schema_version": 1,
        "source_sha256": source_hash, "config_sha256": sha256(config_path),
        "tokenizer_sha256": sha256(tokenizer_path), "model_rate": RATE, "model_hop": HOP,
        "source_samples": samples, "source_rate": rate, "resampled_samples": resampled_count,
        "model_frames": frames, "model_output_samples": frames * HOP,
        "tail_samples_not_covered_by_model": resampled_count - frames * HOP,
        "breathy_min": min(values), "breathy_max": max(values),
        "technique_order": TECHNIQUES, "continuous_control_perceptually_validated": False,
        "required_before_inference": ["matched trained checkpoint and singer map", "sample.wav.f0.npy with measured F0/UV",
                                      "sample.wav.wsp.pt from the checkpoint's semantic encoder"],
        "required_before_host_integration": ["source voice reconstruction passes", "language-aware phoneme mapping verified",
                                             "duration and neutral-region preservation", "continuous Air listening comparison"],
        "upstream_cli_technique": "original", "upstream_predict_f0": False,
    }
    (output / "preparation.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--request", required=True)
    parser.add_argument("--cssc-root", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    args = parser.parse_args()
    try:
        report = prepare(args.request, args.cssc_root, args.output, args.ffmpeg)
    except (ValueError, KeyError, TypeError, OSError, wave.Error, subprocess.SubprocessError) as error:
        parser.exit(2, f"Preparation failed: {error}\n")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
