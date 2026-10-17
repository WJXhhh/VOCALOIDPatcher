"""Measure rendered PCM pairs; envelope boundaries are estimates, not phoneme labels.

Usage: python analyze_transitions.py OUTPUT_DIRECTORY
Requires only Python's standard library. Does not normalize the source audio.
"""
import array
import hashlib
import json
import math
from pathlib import Path
import sys
import wave


def read_pcm(path):
    with wave.open(str(path), "rb") as source:
        if source.getnchannels() != 1 or source.getsampwidth() != 2:
            raise ValueError(f"Expected mono PCM16: {path.name}")
        rate = source.getframerate()
        data = source.readframes(source.getnframes())
    samples = array.array("h", data)
    if sys.byteorder != "little":
        samples.byteswap()
    return rate, samples, data


def measure(path):
    rate, samples, data = read_pcm(path)
    window = round(rate * .01)
    envelope = [math.sqrt(sum(x*x for x in samples[i:i+window]) /
                          len(samples[i:i+window])) / 32768
                for i in range(0, len(samples), window)]
    threshold = max(envelope, default=0) * 10 ** (-50 / 20)
    active = [i for i, value in enumerate(envelope) if value > max(threshold, 1/32768)]
    last_nonzero = next((i for i in range(len(samples) - 1, -1, -1) if samples[i]), None)
    return dict(pcm_sha256=hashlib.sha256(data).hexdigest(),
                peak=max(map(abs, samples), default=0)/32768,
                clipped_samples=sum(abs(x) >= 32767 for x in samples),
                active_start_ms=active[0]*10 if active else None,
                active_end_ms=(active[-1]+1)*10 if active else None,
                last_nonzero_ms=(last_nonzero+1)*1000/rate if last_nonzero is not None else None,
                envelope_10ms=envelope)


def main(directory):
    paths = sorted(directory.glob("*_raw.wav")) + sorted(directory.glob("*_split.wav"))
    results = {path.stem: measure(path) for path in paths}
    pairs = []
    for name, metrics in results.items():
        if not name.endswith("_raw"):
            continue
        split_name = name[:-3] + "split"
        split = results[split_name]
        pairs.append(dict(name=name[:-4], identical=metrics["pcm_sha256"] == split["pcm_sha256"],
                          raw_start_ms=metrics["active_start_ms"], split_start_ms=split["active_start_ms"],
                          raw_end_ms=metrics["active_end_ms"], split_end_ms=split["active_end_ms"],
                          raw_last_nonzero_ms=metrics["last_nonzero_ms"],
                          split_last_nonzero_ms=split["last_nonzero_ms"]))
    report = dict(boundary_method="10 ms RMS frames, threshold 50 dB below maximum frame RMS; WAV-relative",
                  boundary_caution="RMS activity excludes quiet breath tails; last nonzero includes noise. Neither is a phoneme boundary. Async WAVs include presend; use timing.json for the time mapping.",
                  listening_validated=False, pairs=pairs, files=results)
    (directory / "audio-analysis.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    # Preserve levels and onset alignment. Each pair is raw, 400 ms silence,
    # split, 800 ms silence; trim both members to the same measured end + 200 ms.
    bank = paths[0].stem.split("_")[0]
    playlist = []
    with wave.open(str(directory / "listening-ab.wav"), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(44100)
        cursor = 0
        for scenario, release in [("isolated", 0), ("isolated", 201),
                                  ("isolated", 202), ("short_125ms", 201),
                                  ("legato", 201), ("tempo_change", 202)]:
            prefix = f"{bank}_{scenario}_{release}"
            end_ms = max(results[prefix + "_" + mode]["last_nonzero_ms"] or 0
                         for mode in ("raw", "split")) + 200
            frames = math.ceil(end_ms * 44100 / 1000)
            for mode in ("raw", "split"):
                rate, _, data = read_pcm(directory / f"{prefix}_{mode}.wav")
                if rate != 44100:
                    raise ValueError("Listening assembly expects 44100 Hz")
                playlist.append(dict(name=f"{prefix}_{mode}", start_seconds=cursor/44100))
                clip = data[:frames*2].ljust(frames*2, b"\0")
                silence = round((.4 if mode == "raw" else .8) * 44100)
                output.writeframes(clip + bytes(silence*2))
                cursor += frames + silence
    (directory / "listening-ab.json").write_text(json.dumps(playlist, indent=2), encoding="utf-8")
    for pair in pairs:
        print(json.dumps(pair))
    print("clipped_samples=", sum(item["clipped_samples"] for item in results.values()))
    for name in results:
        if "_201_" in name:
            other = name.replace("_201_", "_202_")
            if results[name]["pcm_sha256"] == results[other]["pcm_sha256"]:
                print("short_long_identical=", name)


if __name__ == "__main__":
    main(Path(sys.argv[1]))
