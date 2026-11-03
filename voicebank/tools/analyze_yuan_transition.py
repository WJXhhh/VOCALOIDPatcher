"""Inspect the one-second yuan/left-transition-End reproduction without modifying PCM."""
import json
import sys
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

from analyze_transition_repair import pcm, metrics


def main(root):
    records = json.loads((root / "transition-artifacts.json").read_text(encoding="utf-8-sig"))
    domains = [r for r in records if r.get("kind") == "yuan-domain"]
    report = []
    fig, axes = plt.subplots(len(domains), 3, figsize=(17, 2.7 * len(domains)), squeeze=False)
    for axis, domain in zip(axes, domains):
        rows = [r for r in records if r.get("fixture") == domain["fixture"] and "file" in r]
        baseline = next(r for r in rows if r["variant"] == "baseline")
        extreme = next(r for r in rows if r["variant"] == f"left-End{domain['Maximum']}")
        x, rate = pcm(root / baseline["file"])
        y, _ = pcm(root / extreme["file"])
        note_begin = 1.5  # one-second note position + 0.5-second presend
        note_end = 2.5
        variants = []
        for row in rows:
            audio, _ = pcm(root / row["file"])
            begin, end = round(note_begin * rate), round(note_end * rate)
            body = audio[begin:end]
            window = round(.02 * rate)
            frames = body[:len(body) // window * window].reshape(-1, window)
            rms = np.sqrt(np.mean(frames ** 2, axis=1))
            dc = np.mean(frames, axis=1)
            active = rms > max(1, rms.max() * .1)
            variants.append({"variant": row["variant"], **metrics(audio),
                             "max20msDcOverRms": float(np.max(np.abs(dc[active]) / rms[active])),
                             "active20msRmsMaxMinRatio": float(rms[active].max() / rms[active].min()),
                             "exactConsumedSourceMap": row["exactConsumedSourceMap"]})
        for audio, label, color in [(x, "baseline", "#777777"), (y, "dragged End", "#0576bc")]:
            lo, hi = round(1.3 * rate), min(len(audio), round(2.7 * rate))
            hop = 44
            peaks = np.abs(audio[lo:hi][:((hi - lo) // hop) * hop]).reshape(-1, hop).max(axis=1) / 32768
            axis[0].plot(lo / rate + np.arange(len(peaks)) * hop / rate, peaks, color=color, label=label)
            # Fixed time inside the stretched onset, not selected from peaks.
            lo, hi = round(1.9 * rate), round(1.94 * rate)
            axis[1].plot(np.arange(lo, hi) / rate, audio[lo:hi] / 32768, color=color, alpha=.8)
        for endpoint, color in [(domain["current"], "#777777"), (domain["Maximum"], "#0576bc")]:
            axis[0].axvline((endpoint - 3087) * 256 / rate, color=color, linestyle="--", alpha=.6)
        sources = extreme["rows"][0]
        frames = np.arange(sources["Begin"], sources["End"])
        axis[2].plot((frames - 3087) * 256 / rate, sources["sources"], color="#0576bc")
        axis[0].set_title(f"{domain['fixture']} / 1-second yuan / End {domain['current']} -> {domain['Maximum']}")
        axis[1].set_title("40 ms PCM inside extended transition")
        axis[2].set_title("Consumed ART source progress")
        for item in axis:
            item.grid(alpha=.2)
            item.set_xlabel("seconds from WAV start")
        axis[0].set_ylabel("peak amplitude / 1 ms")
        axis[1].set_ylabel("PCM amplitude")
        axis[2].set_ylabel("source frame")
        report.append({"fixture": domain["fixture"], "phonemes": domain["Phones"], "pitch": domain["pitch"],
                       "durationTicks": domain["Duration"], "durationSeconds": 1,
                       "endCurrent": domain["current"], "endMinimum": domain["Minimum"], "endMaximum": domain["Maximum"],
                       "maximumEndDragMs": (domain["Maximum"] - domain["current"]) * 256 / rate * 1000,
                       "extremeFile": extreme["file"], "variants": variants})
    axes[0, 0].legend()
    fig.tight_layout()
    fig.savefig(root / "yuan-waveforms.png", dpi=125)
    plt.close(fig)
    (root / "yuan-analysis.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps([{k: v for k, v in r.items() if k != "variants"} for r in report], indent=2))


if __name__ == "__main__":
    main(Path(sys.argv[1]))
