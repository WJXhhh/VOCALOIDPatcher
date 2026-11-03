"""Verify a cached phoneme-boundary burst against old/new production file processing.

The fixture directory contains native-source.wav, host-phoneme.wav,
host-smoothed.wav, old-pair.wav, fixed-pair.wav, and regions.json.
WAVs and figures remain outside the repository. Requires NumPy and Matplotlib.
"""
import hashlib
import json
import sys
import wave
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np


def read(path):
    with wave.open(str(path), "rb") as stream:
        assert stream.getsampwidth() == 2 and stream.getnchannels() == 1
        return np.frombuffer(stream.readframes(stream.getnframes()), dtype="<i2").copy(), stream.getframerate()


def main(root, evidence):
    names = ["native-source.wav", "host-phoneme.wav", "host-smoothed.wav", "old-pair.wav", "fixed-pair.wav"]
    arrays = {}
    rate = None
    for name in names:
        arrays[name], current_rate = read(root / name)
        assert rate is None or rate == current_rate
        rate = current_rate
    native, host, smoothed, before, after = (arrays[name] for name in names)
    assert len(set(map(len, arrays.values()))) == 1
    regions = json.loads((root / "regions.json").read_text())
    assert regions[0]["EndSeconds"] == regions[1]["BeginSeconds"]
    seam = round(regions[0]["EndSeconds"] * rate)
    fade = round(rate * .005)
    lo, hi = seam - fade, seam + fade
    outside = np.ones(len(before), dtype=bool)
    outside[lo:hi] = False
    assert np.array_equal(before[outside], after[outside]), "Changed samples outside the shared boundary"
    assert np.array_equal(before[lo - fade:hi + fade], host[lo - fade:hi + fade]), "Old implementation does not reproduce host PCM"
    assert np.array_equal(host, smoothed), "Host smoother changed this cached part"
    mask = np.abs(native.astype(float)) > 100
    before_gain = np.divide(before.astype(float), native, out=np.full(len(native), np.nan), where=mask)
    after_gain = np.divide(after.astype(float), native, out=np.full(len(native), np.nan), where=mask)
    valid = mask[lo:hi]
    gains = [10 ** (float(np.float32(r["Decibels"])) / 20) for r in regions]
    assert np.nanmax(after_gain[lo:hi]) < max(gains) + .01
    local = host.copy()
    local[lo:hi] = after[lo:hi]
    # Keep the entire captured Part intact except for the verified shared seam.
    with wave.open(str(root / "fixed-local.wav"), "wb") as stream:
        stream.setparams((1, 2, rate, len(local), "NONE", "not compressed"))
        stream.writeframes(local.tobytes())
    for name, audio in [("before", host), ("after", local)]:
        excerpt = audio[seam - round(rate * .22):seam + round(rate * .30)]
        with wave.open(str(root / (name + "-comparison.wav")), "wb") as stream:
            stream.setparams((1, 2, rate, len(excerpt), "NONE", "not compressed"))
            stream.writeframes(excerpt.tobytes())
    axis = np.arange(lo - fade, hi + fade) - seam
    fig, plots = plt.subplots(2, 1, figsize=(12, 6), sharex=True)
    plots[0].plot(axis * 1000 / rate, before[lo - fade:hi + fade] / 32768, label="captured host / old processing", color="#ae443c", lw=1)
    plots[0].plot(axis * 1000 / rate, after[lo - fade:hi + fade] / 32768, label="fixed shared boundary", color="#0077a8", lw=1)
    plots[0].set_ylabel("PCM amplitude")
    plots[1].plot(axis * 1000 / rate, before_gain[lo - fade:hi + fade], color="#ae443c", label="old gain")
    plots[1].plot(axis * 1000 / rate, after_gain[lo - fade:hi + fade], color="#0077a8", label="fixed gain")
    plots[1].set_ylabel("actual sample gain")
    plots[1].set_xlabel("milliseconds from the shared phoneme boundary")
    for plot in plots:
        plot.axvline(0, color="#555", ls="--", lw=.7)
        plot.grid(alpha=.2)
        plot.legend()
    fig.tight_layout()
    fig.savefig(root / "shared-boundary-fix.png", dpi=150)
    plt.close(fig)
    repo = Path(__file__).resolve().parents[2]
    processor = repo / "VOCALOIDPatcher/VOCALOIDPatcher/PhonemeVolume/PhonemeGainWaveProcessor.cs"
    summary = {"date": "2026-10-22", "sampleRate": rate, "sharedBoundarySample": seam,
               "sharedBoundarySecondsInPartWave": seam / rate, "leftDecibels": regions[0]["Decibels"],
               "rightDecibels": regions[1]["Decibels"], "bridgeHalfWidthSamples": fade,
               "oldHostPcmExactAroundSeam": True, "hostSmoothingPcmExactWithPhonemeGainOutput": True,
               "oldPeakGainAtSeam": float(np.nanmax(before_gain[lo:hi])),
               "newMinimumGainAroundSeam": float(np.nanmin(after_gain[lo:hi])),
               "newMaximumGainAroundSeam": float(np.nanmax(after_gain[lo:hi])),
               "oldLocalPeakPcm16": int(np.max(np.abs(before[lo:hi].astype(int)))),
               "newLocalPeakPcm16": int(np.max(np.abs(after[lo:hi].astype(int)))),
               "changedSamples": int(np.count_nonzero(before != after)), "outsideSharedBridgeExact": True,
               "sourceHashes": {name: hashlib.sha256((root / name).read_bytes()).hexdigest() for name in names},
               "processorSha256": hashlib.sha256(processor.read_bytes()).hexdigest(),
               "projectModified": False, "deployed": False, "editorStarted": False, "listeningValidation": False}
    evidence.write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main(Path(sys.argv[1]), Path(sys.argv[2]))
