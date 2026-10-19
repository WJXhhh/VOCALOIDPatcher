"""Compare same-source onset controls on a common score/audio time axis.

Requires numpy, scipy and matplotlib in the analysis environment only.
No per-clip normalization, time alignment or WAV postprocessing is used when
judging similarity. Listening copies share one crop and one common gain.
"""
import argparse
import json
import struct
import wave
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from scipy.ndimage import uniform_filter1d


def read_wave(path):
    with wave.open(str(path), "rb") as wav:
        assert wav.getnchannels() == 1 and wav.getsampwidth() == 2
        rate = wav.getframerate()
        pcm = np.frombuffer(wav.readframes(wav.getnframes()), dtype="<i2")
    return rate, pcm.astype(float) / 32768


def source_position(bits):
    return struct.unpack("<f", struct.pack("<i", bits))[0]


def analyze(directory, output):
    fixture = json.loads((directory / "fixture.json").read_text(encoding="utf-8"))
    rows = json.loads((directory / "onset-process.json").read_text(encoding="utf-8"))
    sr = fixture["samplingRate"]
    origin = fixture["presendSeconds"] + fixture["position"] / 960  # 120 BPM
    signals, envelopes = {}, {}
    for row in rows:
        rate, samples = read_wave(directory / (row["name"] + ".wav"))
        assert rate == sr
        signals[row["name"]] = samples
        envelopes[row["name"]] = np.sqrt(np.maximum(0, uniform_filter1d(samples ** 2, 441, mode="constant")))
    time = np.arange(len(signals["baseline"])) / sr - origin
    window = (time >= -.35) & (time <= .2)
    sustain = (time >= .2) & (time <= .35)
    head = (time >= -.35) & (time < 0)
    baseline_peak = float(envelopes["baseline"][window].max())
    reference = envelopes["phoneme-early-24"]
    records = []
    for row in rows:
        name = row["name"]; y = signals[name]; rms = envelopes[name]
        art = next(s for s in row["segments"] if s["From"] == "Sil" and s["To"] == "a")
        frames = art["Frames"]
        position = np.array([source_position(f["SourceBits"]) for f in frames])
        after = np.array([f["Frame"] >= art["Split"] for f in frames])
        neutral_art = next(s for s in rows[0]["segments"] if s["From"] == "Sil" and s["To"] == "a")
        neutral_frames = {f["Frame"]: f for f in neutral_art["Frames"] if f["Frame"] >= neutral_art["Split"]}
        actual_frames = {f["Frame"]: f for f in frames}
        post_map_equal = all(actual_frames.get(k) == v for k, v in neutral_frames.items())
        def crossing(fraction):
            indices = np.flatnonzero(window & (rms >= baseline_peak * fraction))
            return None if not len(indices) else round(float(time[indices[0]] * 1000), 3)
        peak_index = np.flatnonzero(window)[int(np.argmax(rms[window]))]
        energy = float(np.sum(y[window] ** 2))
        baseline_art_end = (neutral_art["End"] - fixture["nativeFrameOrigin"]) * 256 / sr
        unchanged_window = (time + origin >= (neutral_art["Split"] - fixture["nativeFrameOrigin"]) * 256 / sr + 256 / sr) & (time + origin < baseline_art_end)
        difference = y[unchanged_window] - signals["baseline"][unchanged_window]
        own_name = "phoneme-early-" + str(row["requested"] or 24)
        own_row = next(r for r in rows if r["name"] == own_name)
        own_art = next(s for s in own_row["segments"] if s["From"] == "Sil" and s["To"] == "a")
        attack_start = (own_art["Begin"] - fixture["nativeFrameOrigin"]) * 256
        attack_end = (own_art["End"] - fixture["nativeFrameOrigin"]) * 256
        own_difference = y[attack_start:attack_end] - signals[own_name][attack_start:attack_end]
        interior_start = attack_start + 3 * 256
        interior_difference = y[interior_start:attack_end] - signals[own_name][interior_start:attack_end]
        def input_frames(r):
            return {(g["Time"], g["Lane"]): (g["InputHash"], g["OriginalGain"]) for g in r["gainTrace"]
                    if own_art["Begin"] <= g["Time"] < own_art["End"]}
        reference_inputs = input_frames(own_row); actual_inputs = input_frames(row)
        neutral_gains = {(g["Time"], g["Lane"]): g["OriginalGain"] for g in rows[0]["gainTrace"]}
        current_gains = {(g["Time"], g["Lane"]): g["OriginalGain"] for g in row["gainTrace"]}
        common_gains = neutral_gains.keys() & current_gains.keys()
        records.append(dict(name=name, family=row["family"], requested=row["requested"],
            onsetFrames=[art["Begin"], art["Split"], art["End"]],
            t20Milliseconds=crossing(.2), t50Milliseconds=crossing(.5),
            peakMilliseconds=round(float(time[peak_index] * 1000), 3),
            peakRms=float(rms[peak_index]), sustainRms=float(np.sqrt(np.mean(y[sustain] ** 2))),
            preNoteEnergyFraction=float(np.sum(y[head] ** 2) / energy),
            rmsErrorToReference=float(np.sqrt(np.mean((rms[window] - reference[window]) ** 2)) / baseline_peak),
            peakErrorToReference=float(np.max(np.abs(rms[window] - reference[window])) / baseline_peak),
            preSplitSourceRange=[float(position[~after].min()), float(position[~after].max())] if (~after).any() else [],
            postSplitSourceRange=[float(position[after].min()), float(position[after].max())] if after.any() else [],
            baselinePostSplitSourceMapEqual=post_map_equal,
            baselinePostSplitPcmMaxError=float(np.max(np.abs(difference))),
            ownReference=own_name, ownReferenceAttackPcmMaxError=float(np.max(np.abs(own_difference))),
            ownReferenceAttackPcmRmsError=float(np.sqrt(np.mean(own_difference ** 2))),
            ownReferenceInteriorPcmExact=bool(np.all(interior_difference == 0)),
            ownReferenceInteriorSamples=int(len(interior_difference)),
            ownReferenceAttackInputCount=len(reference_inputs),
            ownReferenceAttackInputMatches=sum(actual_inputs.get(k) == v for k, v in reference_inputs.items()),
            nativeGainCommonFrames=len(common_gains),
            nativeGainSameOnCommonFrames=all(neutral_gains[k] == current_gains[k] for k in common_gains),
            nativePhonePositions=row["nativePhonePositions"],
            exactBaselinePcm=bool(np.array_equal(y, signals["baseline"]))))
    output.mkdir(parents=True, exist_ok=True)
    (output / "metrics.json").write_text(json.dumps(dict(fixture={k: fixture[k] for k in ["bank", "pitch", "duration", "velocity", "sdk", "compatibility"]},
        axis="ms relative to fixed musical note; WAV includes 0.5s presend", rmsWindowSamples=441,
        reference="phoneme-early-24", amplitudeNormalization=False, timeAlignment=False, rows=records), indent=2), encoding="utf-8")
    by_name = {r["name"]: r for r in records}
    if "source-progress-24" in by_name:
        prototype, target = by_name["source-progress-24"], by_name["phoneme-early-24"]
        checks = dict(
            fixedNoteAndPhonemeBoundaries=prototype["nativePhonePositions"] == by_name["baseline"]["nativePhonePositions"],
            sameT50=prototype["t50Milliseconds"] == target["t50Milliseconds"],
            samePeakTime=prototype["peakMilliseconds"] == target["peakMilliseconds"],
            samePeakRms=prototype["peakRms"] == target["peakRms"],
            mainOnsetPcmExact=prototype["ownReferenceInteriorPcmExact"],
            currentLeftDoesNotChangeMainPcm=by_name["transition-left-24"]["baselinePostSplitPcmMaxError"] == 0,
            matchedAllocationStillStartsAfterNote=by_name["matched-allocation-24"]["t50Milliseconds"] > 0,
            nativeGainPreserved=all(r["nativeGainSameOnCommonFrames"] for r in records),
            restoredPcmExact=all(r["exactBaselinePcm"] for r in records if r["family"] == "restore"),
            positionOnlySufficient=np.array_equal(signals["source-progress-24"], signals["position-only-24"]))
        assert all(checks.values()), checks
        (output / "verification.json").write_text(json.dumps(dict(checks=checks,
            scope="same isolated vowel fixture; not whole-audio identity or subjective listening acceptance",
            unresolved="first two primary synthesis-frame inputs and following sustain/release allocation differ"), indent=2), encoding="utf-8")
    families = [("baseline", "Baseline"), ("phoneme-early-24", "Phoneme onset -139 ms"),
                ("transition-left-24", "Transition LEFT -139 ms"), ("transition-right-24", "Transition RIGHT +139 ms"),
                ("transition-both-24", "Both transition ends")]
    if "source-progress-24" in signals:
        families.append(("source-progress-24", "Same ART source-progress experiment"))
    fig, axes = plt.subplots(3, 1, figsize=(11, 10), sharex=True, constrained_layout=True)
    colors = ["#7c8797", "#e08c22", "#168dc1", "#ae5ad4", "#3a9c5e", "#d04856"]
    for (name, label), color in zip(families, colors):
        axes[0].plot(time[::64] * 1000, envelopes[name][::64], color=color, label=label)
        row = next(r for r in rows if r["name"] == name)
        art = next(s for s in row["segments"] if s["From"] == "Sil" and s["To"] == "a")
        x = [(f["Frame"] - fixture["nativeFrameOrigin"]) * 256000 / sr - origin * 1000 for f in art["Frames"]]
        axes[1].plot(x, [source_position(f["SourceBits"]) for f in art["Frames"]], color=color, label=label)
        gain = [g for g in row["gainTrace"] if g["Lane"] == "primary" and g["Selected"]]
        axes[2].plot([(g["Time"] - fixture["nativeFrameOrigin"]) * 256000 / sr - origin * 1000 for g in gain],
                     [g["OriginalGain"] for g in gain], color=color, label=label)
    axes[0].set_ylabel("10 ms RMS (same gain)"); axes[0].legend(fontsize=9, loc="upper right")
    axes[1].set_ylabel("ART source frame position")
    axes[2].set_ylabel("Native synthesis gain"); axes[2].set_xlabel("Time relative to musical note (ms)")
    for ax in axes:
        ax.axvline(0, color="#424a55", linestyle="--", linewidth=.8); ax.grid(alpha=.2); ax.set_xlim(-320, 240)
    fig.suptitle("Same vowel a / C4 / VEL 64 / 500 ms — independent native renders")
    fig.savefig(output / "onset-comparison.png", dpi=160); fig.savefig(output / "onset-comparison.svg"); plt.close(fig)
    from matplotlib.font_manager import FontProperties
    font_path = Path("C:/Windows/Fonts/msyh.ttc")
    if font_path.exists():
        chinese = FontProperties(fname=str(font_path))
        fig, ax = plt.subplots(figsize=(10, 4.1), constrained_layout=True)
        summary = [("baseline", "原始", "#7c8797"), ("phoneme-early-24", "音素起点提前 139 ms", "#e08c22"),
                   ("transition-left-24", "过渡左端提前 139 ms", "#168dc1"), ("transition-right-24", "过渡右端延长 139 ms", "#ae5ad4")]
        if "source-progress-24" in signals: summary.append(("source-progress-24", "源帧进度研究原型", "#d04856"))
        for name, label, color in summary:
            ax.plot(time[::64] * 1000, envelopes[name][::64], color=color, label=label,
                    linestyle="--" if name == "source-progress-24" else "-", linewidth=1.4)
        ax.axvline(0, color="#424a55", linestyle=":", linewidth=1)
        ax.set_xlim(-200, 200); ax.grid(alpha=.2)
        ax.set_xlabel("相对音符起点（毫秒）；0 为固定音符起点", fontproperties=chinese)
        ax.set_ylabel("10 毫秒 RMS（统一音量比例）", fontproperties=chinese)
        ax.set_title("同一中文 a / MIDI 60 / VEL 64 / 500 毫秒", fontproperties=chinese)
        ax.legend(prop=chinese, fontsize=8)
        fig.savefig(output / "onset-envelope-summary.png", dpi=160); plt.close(fig)
    fig, axes = plt.subplots(len(families), 1, figsize=(11, 8), sharex=True, sharey=True, constrained_layout=True)
    for ax, (name, label), color in zip(axes, families, colors):
        ax.plot(time[::8] * 1000, signals[name][::8], color=color, linewidth=.5)
        ax.axvline(0, color="#424a55", linestyle="--", linewidth=.8); ax.set_ylabel(label, fontsize=8); ax.set_xlim(-320, 400); ax.grid(alpha=.15)
    axes[-1].set_xlabel("Time relative to fixed musical note (ms)")
    fig.savefig(output / "waveform-comparison.png", dpi=160); plt.close(fig)
    # Audition assets use exactly the same crop and fixed multiplier for every
    # variant, so onset timing and level differences remain audible.
    lo, hi = int((origin - .4) * sr), int((origin + .7) * sr)
    common_gain = min(4, .95 / max(float(np.max(np.abs(signals[n][lo:hi]))) for n, _ in families))
    montage = []
    for name, _ in families:
        pcm = np.round(signals[name][lo:hi] * common_gain * 32767).astype("<i2")
        with wave.open(str(output / (name + ".wav")), "wb") as wav:
            wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(sr); wav.writeframes(pcm.tobytes())
        montage.extend([pcm, np.zeros(int(sr * .45), dtype="<i2")])
    with wave.open(str(output / "comparison-in-order.wav"), "wb") as wav:
        wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(sr); wav.writeframes(np.concatenate(montage).tobytes())
    if "source-progress-24" in signals:
        audition = []
        for name in ["phoneme-early-24", "transition-left-24", "source-progress-24"]:
            audition.extend([np.round(signals[name][lo:hi] * common_gain * 32767).astype("<i2"), np.zeros(int(sr * .45), dtype="<i2")])
        with wave.open(str(output / "reference-left-prototype.wav"), "wb") as wav:
            wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(sr); wav.writeframes(np.concatenate(audition).tobytes())
    print(json.dumps(records, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(); parser.add_argument("--input", type=Path, required=True); parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(); analyze(args.input, args.output)
