"""Offline original-waveform experiment; no vocoder, random excitation or models.

Pitch-synchronous complex demodulation estimates slowly varying partials. Only
the source's remaining waveform is amplified. A second candidate also reduces
upper partials. These are experimental DSP choices, not an implementation of
Unravel, Expressive, or a physiological glottal-flow estimator.
"""
import argparse
import hashlib
import html
import json
import shutil
from pathlib import Path

import numpy as np
import parselmouth
import soundfile as sf
from scipy import ndimage, signal


def rms(x):
    return float(np.sqrt(np.mean(np.asarray(x, dtype=np.float64) ** 2))) if len(x) else 0.0


def pitch_track(x, sr):
    sound = parselmouth.Sound(x, sampling_frequency=sr)
    pitch = sound.to_pitch_ac(time_step=.005, pitch_floor=65, pitch_ceiling=1100,
                              voicing_threshold=.65)
    f0 = pitch.selected_array["frequency"]
    times = pitch.xs()
    samples = np.arange(len(x)) / sr
    voiced = f0 > 0
    if not voiced.any():
        return np.full(len(x), 260.0), np.zeros(len(x))
    frequency = np.interp(samples, times[voiced], f0[voiced])
    gate = np.interp(samples, times, voiced.astype(float), left=0, right=0)
    # Erode the detected interval rather than infer phoneme boundaries from notes.
    gate = ndimage.minimum_filter1d(gate, size=max(3, int(.05 * sr) | 1))
    gate = ndimage.gaussian_filter1d(gate, sigma=.006 * sr)
    envelope = np.sqrt(np.maximum(0, ndimage.uniform_filter1d(x * x, size=int(.01 * sr) | 1)))
    gate *= np.clip((envelope / max(np.max(envelope), 1e-12) - .025) / .075, 0, 1)
    # Digital silence must remain silent even when the pitch gate has a tail.
    gate[envelope < 1e-8] = 0
    return frequency, gate


def decompose(x, sr, frequency, gate):
    if len(x) < int(.15 * sr) or not np.any(gate > .5):
        return np.zeros_like(x), np.zeros_like(x), np.zeros_like(x)
    f0 = float(np.median(frequency[gate > .5]))
    # Filtering demodulated products with their endpoint values creates large
    # fictitious partials at the ends. Supply real waveform context first and
    # discard it after all narrow-band filters, including the 5 Hz tracker.
    padding = int(.6 * sr)
    x = np.pad(x, (padding, padding), mode="reflect")
    frequency = np.pad(frequency, (padding, padding), mode="reflect")
    # Base oscillator follows measured F0; the local fundamental supplies phase.
    phase = 2 * np.pi * np.cumsum(frequency) / sr
    cutoff = min(40.0, f0 / 6)
    lowpass = signal.butter(4, cutoff, fs=sr, output="sos")
    # Use a narrower phase tracker than the partial envelopes: otherwise noise
    # near H1 becomes phase jitter multiplied by every higher harmonic.
    phase_filter = signal.butter(4, min(10.0, f0 / 20), fs=sr, output="sos")
    base = signal.sosfiltfilt(phase_filter, x * np.exp(-1j * phase))
    phase += np.unwrap(np.angle(base))
    harmonic = np.zeros_like(x)
    softened = np.zeros_like(x)
    count = min(100, int(min(9000, .45 * sr) / np.min(frequency)))
    for index in range(1, count + 1):
        oscillator = np.exp(1j * index * phase)
        amplitude = signal.sosfiltfilt(lowpass, x * np.conjugate(oscillator))
        partial = 2 * np.real(amplitude * oscillator)
        harmonic += partial
        # Phase and partial frequency stay from the source. This is source tilt,
        # not measured OQ or a claim of real vocal-fold closure reconstruction.
        attenuation_db = min(6.0, np.log2(index))
        softened += partial * (10 ** (-attenuation_db / 20) - 1)
    residual = x - harmonic
    # A residual can include periodic multi-cycle motion from the original
    # renderer. It is not aspiration. Keep these narrow coherent components in
    # the dry path instead of magnifying them with the stochastic residual.
    coherent_filter = signal.butter(3, min(5.0, f0 / 60), fs=sr, output="sos")
    fractions = {k / divisor for divisor in (2, 3, 4)
                 for k in range(1, int(min(8000, .43 * sr) / np.min(frequency) * divisor) + 1)
                 if k % divisor}
    coherent = np.zeros_like(residual)
    local_rms = np.sqrt(np.maximum(0, ndimage.uniform_filter1d(residual * residual, size=int(.1 * sr) | 1)))
    noise_amplitude = local_rms * np.sqrt(2 * min(5.0, f0 / 60) / (sr / 2))
    for ratio in sorted(fractions):
        oscillator = np.exp(1j * ratio * phase)
        amplitude = signal.sosfiltfilt(coherent_filter, residual * np.conjugate(oscillator))
        confidence = np.clip((np.abs(amplitude) / np.maximum(noise_amplitude, 1e-12) - 3) / 3, 0, 1)
        confidence = ndimage.uniform_filter1d(confidence, size=int(.02 * sr) | 1)
        coherent += 2 * confidence * np.real(amplitude * oscillator)
    residual -= coherent
    bandpass = signal.butter(3, [500, min(8000, .43 * sr)], btype="bandpass",
                             fs=sr, output="sos")
    residual = signal.sosfiltfilt(bandpass, residual)
    return harmonic[padding:-padding], residual[padding:-padding], softened[padding:-padding]


def apply(x, gate, residual, softening, amount, soften=False, residual_gain_db=3):
    if not 0 <= amount <= 1:
        raise ValueError("amount must be 0..1")
    if amount == 0:
        return x.copy()
    if not 0 <= residual_gain_db <= 12:
        raise ValueError("residual_gain_db must be 0..12")
    delta = (10 ** (residual_gain_db * amount / 20) - 1) * residual
    if soften:
        delta = delta + amount * softening
    return x + gate * delta


def wav_digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def properties(x, source, sr, gate, reference_f0):
    mask = gate > .8
    if not mask.any():
        mask = np.ones(len(x), dtype=bool)
    observed, detected = pitch_track(x, sr)
    valid = mask & (detected > .5)
    cents = float(np.median(np.abs(1200 * np.log2(observed[valid] / reference_f0[valid])))) if valid.any() else None
    outside = gate == 0
    return {
        "samples": len(x), "sample_rate": sr, "peak": float(np.max(np.abs(x))),
        "voiced_rms_db": 20 * np.log10(max(rms(x[mask]), 1e-12)),
        "delta_to_source_rms_ratio": rms((x - source)[mask]) / max(rms(source[mask]), 1e-12),
        "median_f0_error_cents": cents,
        "pitch_detected_source_voiced_fraction": float(valid.sum() / max(1, mask.sum())),
        "pitch_outside_100_cents_fraction": float(np.mean(np.abs(1200 * np.log2(observed[valid] / reference_f0[valid])) > 100)) if valid.any() else None,
        "outside_gate_max_delta": float(np.max(np.abs((x - source)[outside]))) if outside.any() else None,
    }


def make_page(output, report):
    names = [("original", "原声"), ("neutral", "新方案 0%"),
             ("residual-035", "原声残差 35%"), ("residual-070", "原声残差 70%"),
             ("residual-100", "原声残差 100%"), ("soft-035", "残差加柔化谐波 35%"),
             ("soft-070", "残差加柔化谐波 70%"), ("soft-100", "残差加柔化谐波 100%"),
             ("previous-noise", "上轮噪声版（已否定的对照）")]
    sections = []
    for voice, label in (("miku", "初音 MIKU V4X"), ("tianyi", "洛天依 V5 中文")):
        cards = []
        for key, title in names:
            if key not in report["voices"][voice]["variants"]:
                continue
            metric = report["voices"][voice]["variants"][key]
            outliers = metric["pitch_outside_100_cents_fraction"]
            warning = '<p>音高检测出现明显异常，保留作边界对照。</p>' if outliers is not None and outliers >= .01 else ''
            cards.append(f'<article><b>{html.escape(title)}</b><audio controls preload="none" src="{voice}/{key}.wav"></audio>'
                         f'<button onclick="swap(this)">切到相同位置</button>{warning}</article>')
        sections.append(f'<section><h2>{label}</h2><div class="cards">{"".join(cards)}</div>'
                        f'<details><summary>分离诊断（原始电平）</summary><p>残差不等于纯气流，可能混有颤动和分离误差。</p>'
                        f'<audio controls preload="none" src="{voice}/residual-diagnostic.wav"></audio></details></section>')
    page = '''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>保留原波形的气声实验</title><style>body{background:#f2f4f7;color:#243347;font:16px/1.7 system-ui,"Microsoft YaHei";margin:0}main{max-width:1100px;margin:32px auto;padding:0 20px}section,.intro{background:white;border:1px solid #dbe1e9;border-radius:12px;padding:22px;margin:20px 0}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(290px,1fr));gap:12px}article{border:1px solid #dce2ea;border-radius:8px;padding:14px}audio{display:block;width:100%;margin:12px 0}button{padding:7px 12px;border:1px solid #bccadc;background:#f7f9fc;border-radius:5px;color:#2b5278;cursor:pointer}h1{font-size:28px}p{color:#526176}summary{cursor:pointer}img{max-width:100%}</style>
<main><h1>保留原波形的气声实验</h1><div class="intro"><p>2026-10-20 · 两种离线候选。原声直接保留，0% 文件与输入逐字节相同；非零时只加回改动的差值。采样率仍为 44.1 kHz，未使用声码器、歌手模型或新生成的随机噪声。</p>
<p>先比较“原声”和“新方案 0%”，再听“残差加柔化谐波 70%”。第一组仅放大原声残差；第二组同时削弱部分高次谐波。残差增益上限为 3 dB：较强增益出现次谐波检测不稳定；当前天依“残差加柔化谐波 100%”也未通过音高检测，已标出。保守档的气声变化可能偏弱，整条路线尚未验收通过。</p>
<p>非零候选用单个文件增益匹配原声有声区 RMS；只用于比较音色，不代表感知响度完全相同。残差诊断不做响度提升。自然度和音色保留均尚未通过试听。</p></div>''' + ''.join(sections) + '''<section><h2>记录</h2><p><a href="report.json">输入哈希、测量、算法参数</a> · <a href="spectra.png">平均声谱对比</a></p><p>两份素材均为短元音，不足以证明完整歌词、高音或连读的效果。检测到的有声区仅作保守处理门限，不作为真实音素边界。</p></section></main>
<script>let last;document.querySelectorAll('audio').forEach(a=>a.addEventListener('play',()=>{document.querySelectorAll('audio').forEach(b=>{if(b!==a)b.pause()});last=a}));function swap(b){const a=b.parentElement.querySelector('audio'),t=last?last.currentTime:0;const play=()=>{a.currentTime=Math.min(t,Math.max(0,a.duration-.01));a.play().catch(()=>{})};if(a.readyState>=1)play();else{a.addEventListener('loadedmetadata',play,{once:true});a.load()}}</script></html>'''
    gain = report["parameters"]["maximum_residual_gain_db"]
    if gain != 3:
        page = page.replace("残差增益上限为 3 dB", f"强档压力对照：残差增益上限为 {gain:g} dB")
    (output / "index.html").write_text(page, encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--miku", type=Path, required=True)
    parser.add_argument("--tianyi", type=Path, required=True)
    parser.add_argument("--previous", type=Path)
    parser.add_argument("--residual-gain-db", type=float, default=3)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        parser.error("output must be a new directory")
    if not 0 <= args.residual_gain_db <= 12:
        parser.error("residual-gain-db must be 0..12")
    # Validate both inputs before creating artifacts.
    sources = {}
    for voice in ("miku", "tianyi"):
        path = getattr(args, voice)
        info = sf.info(path)
        if info.channels != 1 or not 16000 <= info.samplerate <= 96000 or not .15 <= info.duration <= 60:
            parser.error("Expected mono input at 16..96 kHz lasting .15..60 seconds")
        x, sr = sf.read(path, dtype="float64")
        if not np.isfinite(x).all():
            parser.error("Non-finite audio")
        sources[voice] = (x, sr, path)
    args.output.mkdir(parents=True)
    report = {"status": "offline_experiment_pending_listening", "date": "2026-10-20",
              "algorithm": "original PCM plus pitch-synchronous residual and optional source-tilt delta",
              "new_random_noise": False, "vocoder": False, "model": False,
              "script_sha256": wav_digest(__file__),
              "parameters": {"partial_bandwidth_hz": 40, "phase_tracker_bandwidth_hz": 10,
                             "excluded_coherent_subharmonic_divisors": [2, 3, 4],
                             "subharmonic_tracker_bandwidth_hz": 5, "residual_band_hz": [500, 8000],
                             "maximum_residual_gain_db": args.residual_gain_db, "source_tilt_db_per_octave": 1.0,
                             "voiced_gate_erosion_ms": 25, "voiced_gate_smoothing_ms": 6},
              "human_listening_pass": None, "voices": {}}
    figure_data = {}
    for voice, (x, sr, path) in sources.items():
        print(f"Analyzing {voice}", flush=True)
        folder = args.output / voice
        folder.mkdir()
        frequency, gate = pitch_track(x, sr)
        harmonic, residual, softened = decompose(x, sr, frequency, gate)
        np.savez(folder / "decomposition.npz", harmonic=harmonic.astype("float32"),
                 residual=residual.astype("float32"), gate=gate.astype("float32"), f0=frequency.astype("float32"))
        sf.write(folder / "residual-diagnostic.wav", residual * gate, sr, subtype="FLOAT")
        raw = {"original": x, "neutral": apply(x, gate, residual, softened, 0)}
        for amount in (.35, .7, 1.0):
            suffix = f"{round(amount * 100):03d}"
            raw[f"residual-{suffix}"] = apply(x, gate, residual, softened, amount, residual_gain_db=args.residual_gain_db)
            raw[f"soft-{suffix}"] = apply(x, gate, residual, softened, amount, soften=True, residual_gain_db=args.residual_gain_db)
        if args.previous:
            previous, old_sr = sf.read(args.previous / voice / "air-medium.wav", dtype="float64")
            if old_sr != sr or previous.shape != x.shape:
                raise ValueError("Previous DSP comparison must have identical sample rate and length")
            raw["previous-noise"] = previous
        mask = gate > .8
        if not mask.any():
            mask = np.ones(len(x), dtype=bool)
        reference = rms(x[mask])
        item = {"input": str(path.resolve()), "input_sha256": wav_digest(path),
                "source_voiced_rms": reference, "gated_source_residual_rms_ratio": rms(residual[mask]) / max(reference, 1e-12),
                "variants": {}}
        figure_data[voice] = {}
        for key, y in raw.items():
            if not np.isfinite(y).all() or len(y) != len(x):
                raise ValueError(f"Invalid audio: {voice}/{key}")
            result = properties(y, x, sr, gate, frequency)
            level = reference / max(rms(y[mask]), 1e-12) if reference else 1.0
            level = min(level, .95 / max(float(np.max(np.abs(y))), 1e-12))
            if key in ("original", "neutral"):
                shutil.copyfile(path, folder / f"{key}.wav")
                level = 1.0
            else:
                sf.write(folder / f"{key}-raw.wav", y, sr, subtype="FLOAT")
                sf.write(folder / f"{key}.wav", y * level, sr, subtype="PCM_16")
            result.update({"preview_gain_db": 20 * np.log10(max(level, 1e-12)),
                           "preview_sha256": wav_digest(folder / f"{key}.wav")})
            item["variants"][key] = result
            figure_data[voice][key] = y * level
        item["neutral_file_exact"] = wav_digest(folder / "neutral.wav") == wav_digest(path)
        if not item["neutral_file_exact"] or not np.array_equal(raw["neutral"], x):
            raise AssertionError("Neutral must be exact")
        candidates = [values for key, values in item["variants"].items() if key.startswith(("residual-", "soft-"))]
        item["candidate_pitch_check_pass"] = all(v["median_f0_error_cents"] is not None
                                                 and v["median_f0_error_cents"] < 10
                                                 and v["pitch_outside_100_cents_fraction"] < .01
                                                 and v["pitch_detected_source_voiced_fraction"] > .9
                                                 for v in candidates)
        report["voices"][voice] = item
        print(f"{voice}: residual/source {item['gated_source_residual_rms_ratio']:.4f}; neutral exact", flush=True)
    import matplotlib
    matplotlib.use("Agg")
    from matplotlib import pyplot as plt
    fig, axes = plt.subplots(2, 1, figsize=(10, 7), constrained_layout=True)
    for ax, voice in zip(axes, ("miku", "tianyi")):
        x, sr, _ = sources[voice]
        for key in ("original", "residual-070", "soft-070", "previous-noise"):
            if key in figure_data[voice]:
                f, p = signal.welch(figure_data[voice][key], fs=sr, nperseg=4096)
                p = ndimage.gaussian_filter1d(p, 4)
                ax.semilogx(f[1:], 10 * np.log10(np.maximum(p[1:], 1e-14)), label=key)
        ax.set(xlim=(100, 10000), ylim=(-125, -35), title=voice, xlabel="Hz", ylabel="Power density dB/Hz")
        ax.grid(alpha=.2)
        ax.legend(fontsize=8)
    fig.savefig(args.output / "spectra.png", dpi=150)
    plt.close(fig)
    (args.output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    shutil.copyfile(__file__, args.output / "experiment-source.py")
    make_page(args.output, report)
    print(args.output / "index.html", flush=True)


if __name__ == "__main__":
    main()
