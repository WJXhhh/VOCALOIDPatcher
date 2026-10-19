"""Verify production onset audio against the reviewed same-source experiment.

Uses raw PCM on a fixed score axis. No alignment or per-file normalization.
Python standard library only; fixtures and proprietary audio stay outside Git.
"""
import argparse
import json
import math
import struct
import wave
from pathlib import Path


def pcm(path):
    with wave.open(str(path), "rb") as wav:
        assert (wav.getnchannels(), wav.getsampwidth(), wav.getframerate()) == (1, 2, 44100)
        raw = wav.readframes(wav.getnframes())
    return raw, [v[0] / 32768 for v in struct.iter_unpack("<h", raw)]


def envelope(values):
    prefix = [0.0]
    for value in values:
        prefix.append(prefix[-1] + value * value)
    return [math.sqrt(max(0, (prefix[min(len(values), i + 221)] - prefix[max(0, i - 220)]) / 441))
            for i in range(len(values))]


def verify(old, new, reviewed, output):
    rows = json.loads((old / "onset-process.json").read_text(encoding="utf-8"))
    current = json.loads((new / "onset-process.json").read_text(encoding="utf-8"))
    by_name = {r["name"]: r for r in rows}
    other = {r["name"]: r for r in current}
    assert by_name.keys() == other.keys()
    cross_version = all(pcm(old / (name + ".wav"))[0] == pcm(new / (name + ".wav"))[0] for name in by_name)
    assert cross_version
    baseline, base = pcm(old / "baseline.wav")
    origin = 1.5
    lo, hi, split = [round((origin + offset) * 44100) for offset in (-.35, .2, 0)]
    records = []
    for amount in (8, 24, 48):
        raw, actual = pcm(old / f"source-progress-{amount}.wav")
        _, reference = pcm(old / f"phoneme-early-{amount}.wav")
        expected, _ = pcm(reviewed / f"source-progress-{amount}.wav")
        a, b = envelope(actual), envelope(reference)
        ai, bi = max(range(lo, hi), key=a.__getitem__), max(range(lo, hi), key=b.__getitem__)
        energy = lambda y: sum(v*v for v in y[lo:split]) / sum(v*v for v in y[lo:hi])
        native = by_name[f"source-progress-{amount}"]
        head = next(s for s in native["segments"] if s["From"] == "Sil" and s["To"] == "a")
        own = next(s for s in by_name[f"phoneme-early-{amount}"]["segments"] if s["From"] == "Sil" and s["To"] == "a")
        attack_lo, attack_hi = (own["Begin"] - 3087 + 3) * 256, (own["End"] - 3087) * 256
        record = dict(offsetFrames=-amount, sameAsReviewedPrototype=raw == expected,
                      samePeakTime=ai == bi, peakMilliseconds=round((ai / 44100 - origin) * 1000, 3),
                      samePeakRms=abs(a[ai] - b[bi]) < 1e-12,
                      normalizedEnvelopeError=math.sqrt(sum((a[i]-b[i])**2 for i in range(lo,hi))/(hi-lo))/b[bi],
                      referencePreNoteEnergy=energy(reference), productionPreNoteEnergy=energy(actual),
                      sourceProgressExact=[f["SourceBits"] for f in head["Frames"]] == [f["SourceBits"] for f in own["Frames"]],
                      mainOnsetPcmExact=actual[attack_lo:attack_hi] == reference[attack_lo:attack_hi],
                      phonemeBoundariesFixed=native["nativePhonePositions"] == by_name["baseline"]["nativePhonePositions"],
                      restoredPcmExact=pcm(old / f"restore-{amount}.wav")[0] == baseline)
        assert all(record[k] for k in ("sameAsReviewedPrototype", "samePeakTime", "samePeakRms", "sourceProgressExact",
                                       "mainOnsetPcmExact", "phonemeBoundariesFixed", "restoredPcmExact")), record
        assert record["normalizedEnvelopeError"] < .01
        records.append(record)
    result = dict(scope="same Chinese a, MIDI 60, VEL 64, 500 ms; fixed musical and phoneme timing",
                  crossVersionPcmExact=cross_version, versions=["6.13.1.1", "6.13.3.1"], records=records,
                  minorDifferences="first three output blocks and later sustain/release differ from the full phoneme-timing reference")
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for name in ("old", "new", "reviewed", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    verify(args.old, args.new, args.reviewed, args.output)
