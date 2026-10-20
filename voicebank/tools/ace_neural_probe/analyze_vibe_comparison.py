"""Create measured comparisons and a local listening page for real model runs."""
import argparse
import html
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    import numpy as np
    import librosa
    import parselmouth
    import soundfile as sf
    from scipy.fft import dct

    root = args.directory.resolve()
    report = json.loads((root / "report.json").read_text(encoding="utf-8"))
    if report["status"] != "real_checkpoint_inference" or set(report["voices"]) != {"miku", "tianyi"}:
        raise ValueError("Expected a completed two-voice real inference report")
    names = {"original": "原声（24 kHz 对照）", "vocoder-only": "仅声码器重建", "neutral": "0% 气声 · 模型重建",
             "breathy-035": "35% 气声 · 实验插值", "breathy-070": "70% 气声 · 实验插值", "breathy-100": "100% 气声 · 训练技巧端点",
             "source050-neutral": "原声频谱起步 50 · 气声 0%", "source050-breathy": "原声频谱起步 50 · 气声 100%",
             "source100-neutral": "原声频谱起步 100 · 气声 0%", "source100-breathy": "原声频谱起步 100 · 气声 100%",
             "source200-neutral": "原声频谱起步 200 · 气声 0%", "source200-breathy": "原声频谱起步 200 · 气声 100%",
             "source400-neutral": "原声频谱起步 400 · 气声 0%", "source400-breathy": "原声频谱起步 400 · 气声 100%"}

    def features(x, sr, mask):
        pad = (1024 - 256) // 2
        mel = librosa.feature.melspectrogram(y=np.pad(x, (pad, pad), mode="reflect"), sr=sr,
                                            n_fft=1024, hop_length=256, n_mels=100, center=False,
                                            fmin=0, fmax=sr / 2, power=2)
        count = min(len(mask), mel.shape[1])
        logmel = 10 * np.log10(np.maximum(mel[:, :count], 1e-9))
        # Remove overall frame level; this describes spectral shape only.
        shape = logmel - logmel.mean(axis=0, keepdims=True)
        average = shape[:, mask[:count]].mean(axis=1)
        return average, dct(average, norm="ortho")[1:21]

    def pitch(x, sr, frame_count):
        sound = parselmouth.Sound(x.astype(np.float64), sampling_frequency=sr)
        p = sound.to_pitch_ac(time_step=256 / sr, pitch_floor=65, pitch_ceiling=1100, voicing_threshold=.6)
        raw = p.selected_array["frequency"]
        grid = np.arange(frame_count) * 256 / sr
        indices = np.clip(np.rint((grid - p.x1) / p.dx).astype(int), 0, len(raw) - 1)
        f0 = raw[indices].copy()
        f0[(grid < p.xs()[0]) | (grid > p.xs()[-1])] = 0
        return f0

    summaries, envelopes, signatures = {}, {}, {}
    for voice in ("miku", "tianyi"):
        data = np.load(root / voice / "source-features.npz")
        mask, expected_f0 = data["uv"], data["f0"]
        summaries[voice], envelopes[voice], signatures[voice] = {}, {}, {}
        for variant in names:
            x, sr = sf.read(root / voice / f"{variant}.wav", dtype="float32")
            envelope, signature = features(x, sr, mask)
            envelopes[voice][variant], signatures[voice][variant] = envelope, signature
            observed = pitch(x, sr, len(mask))
            valid = mask & (observed > 0)
            f0_error = float(np.median(np.abs(1200 * np.log2(observed[valid] / expected_f0[valid])))) if valid.any() else None
            h = parselmouth.Sound(x.astype(np.float64), sampling_frequency=sr).to_harmonicity_cc(time_step=.01, minimum_pitch=65)
            hv = h.values.reshape(-1)
            hnr = float(np.median(hv[hv > -100])) if np.any(hv > -100) else None
            summaries[voice][variant] = {
                "centered_logmel_shape_mae_db": float(np.mean(np.abs(envelope - envelopes[voice]["original"]))),
                "mfcc_1_to_20_l2": float(np.linalg.norm(signature - signatures[voice]["original"])),
                "median_abs_f0_error_cents": f0_error,
                "source_voiced_frames_with_detected_pitch_fraction": float(valid.sum() / max(1, mask.sum())),
                "median_hnr_db": hnr,
                **report["voices"][voice]["variants"][variant],
            }
        print(voice, json.dumps(summaries[voice], indent=2), flush=True)
    cross = {}
    for voice in ("miku", "tianyi"):
        for variant in ("original", "vocoder-only", "neutral", "breathy-100", "source050-breathy", "source100-breathy", "source200-breathy", "source400-breathy"):
            cross[f"{voice}/{variant}"] = {reference: float(np.mean(np.abs(envelopes[voice][variant] - envelopes[reference]["original"])))
                                          for reference in ("miku", "tianyi")}
    metrics = {"voices": summaries, "cross_source_logmel_shape_mae_db": cross,
               "interpretation": "Spectral shape and pitch proxies only; not a validated speaker identity or naturalness score."}
    (root / "metrics.json").write_text(json.dumps(metrics, ensure_ascii=False, indent=2), encoding="utf-8")
    # Static scientific figure; the listening page is the interactive comparison.
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    fig, axes = plt.subplots(2, 1, figsize=(10, 7), constrained_layout=True)
    frequencies = librosa.mel_frequencies(n_mels=102, fmin=0, fmax=12000)[1:-1]
    for axis, voice in zip(axes, ("miku", "tianyi")):
        for variant, color in (("original", "#26374d"), ("vocoder-only", "#287d57"), ("neutral", "#d08a19"), ("breathy-100", "#a34182"), ("source050-breathy", "#477ab8")):
            axis.plot(frequencies, envelopes[voice][variant], label=variant, color=color, linewidth=1.3)
        axis.set(title=voice + ": mean voiced spectral shape", xlabel="Frequency (Hz)", ylabel="Centered log-mel power (dB)", xlim=(100, 10000))
        axis.grid(alpha=.2)
        axis.legend(ncol=4, fontsize=8)
    fig.savefig(root / "spectral-comparison.png", dpi=160)
    plt.close(fig)
    rows = []
    for voice, title in (("miku", "初音 MIKU V4X"), ("tianyi", "洛天依 V5 中文")):
        singer = report["voices"][voice]["chosen_reference_speaker"]
        cards = [f'''<article><div class="name">原始声库渲染 · 44.1 kHz</div>
<audio controls preload="none" src="{voice}/native-original.wav"></audio>
<div class="numbers">未重建、未归一化的原始文件</div><button onclick="switchSample(this)">切换到同一播放位置</button></article>''']
        source_cards = []
        for variant, label in names.items():
            metric = summaries[voice][variant]
            cents = metric["median_abs_f0_error_cents"]
            destination = source_cards if variant.startswith("source") else cards
            destination.append(f'''<article><div class="name">{html.escape(label)}</div>
<audio controls preload="none" src="{voice}/{variant}.wav"></audio>
<div class="numbers">声谱形状差 {metric['centered_logmel_shape_mae_db']:.2f} dB · 音高误差 {f'{cents:.1f}' if cents is not None else '无法可靠估计'} 音分</div>
<button onclick="switchSample(this)" data-src="{voice}/{variant}.wav">切换到同一播放位置</button></article>''')
        rows.append(f'''<section><h2>{title}</h2><p>使用模型内参考歌手 {html.escape(singer['name'])}，ID {singer['id']}；由固定噪声的去噪误差筛选，并非克隆所得音色。</p><div class="grid">{''.join(cards)}</div>
<details><summary>展开：从原声频谱开始的浅扩散实验</summary><p>使用模型现有的原声频谱初始化接口，分别从 50、100、200、400/1000 的噪声时刻开始；每种设置均有气声 0% 的重建对照。这是本地适配实验，需同时判断音色保留和气声是否足够。</p><div class="grid">{''.join(source_cards)}</div></details></section>''')
    page = '''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>天依与初音 神经气声实测</title><style>
body{font:16px/1.65 system-ui,"Microsoft YaHei",sans-serif;color:#253344;background:#f4f6f9;margin:0}main{max-width:1100px;margin:36px auto;padding:0 22px}h1{font-size:29px;line-height:1.3}h2{font-size:22px}p{color:#526174}.intro,section{background:white;border:1px solid #dce2eb;border-radius:14px;padding:24px;margin:22px 0}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(290px,1fr));gap:14px}article{border:1px solid #e0e5ec;border-radius:9px;padding:15px}.name{font-weight:650;margin-bottom:10px}audio{width:100%}.numbers{font-size:12px;color:#607085;margin:8px 0}button{border:1px solid #b8c6d8;border-radius:5px;background:#f6f9ff;color:#334966;padding:7px 10px;cursor:pointer}.tag{font-size:13px;letter-spacing:1px;color:#4672a3}img{width:100%;border-radius:8px}a{color:#2d639d}li{margin:5px 0}</style>
<main><div class="tag">真实预训练权重 · 离线实验 · 2026-10-20</div><h1>天依与初音的气声转换对照</h1>
<div class="intro"><p>先听“原声 → 仅声码器重建 → 0% 气声模型重建”，判断模型在改变技巧之前是否已经改变音色。再比较三个气声强度。“0%”是模型重建对照，不能当作原声透传。</p>
<ul><li>全部来自同一批真实声库渲染，播放版本为 24 kHz，按原声有声区匹配 RMS 并限制峰值；未混入原声或新噪声。</li><li>35% 和 70% 使用学得的技巧嵌入插值；100% 使用训练中的 breathy 技巧端点。未做歌手均值音高缩放。</li><li>声谱差、F0 和 HNR 只辅助判断；不能单独证明音色身份或自然度。素材仅包含短元音，不能代表完整歌词与连读。</li></ul>
<p>切换按钮保持上一条音频的播放位置，方便比较同一时刻。</p></div>''' + ''.join(rows) + '''<section><h2>有声区声谱形状</h2><p>已去除每帧整体电平。先比较仅声码器与原声，再看模型重建增加了多少差异。</p><img src="spectral-comparison.png" alt="两种声库的声谱形状对照"><p><a href="report.json">模型、权重哈希与推理记录</a> · <a href="metrics.json">测量数据</a></p></section></main>
<script>let last=null;document.querySelectorAll('audio').forEach(a=>{a.addEventListener('play',()=>{document.querySelectorAll('audio').forEach(b=>{if(b!==a)b.pause()});last=a})});function switchSample(button){const a=button.parentElement.querySelector('audio'),t=last?last.currentTime:0,play=()=>{a.currentTime=Math.min(t,Math.max(0,a.duration-.01));a.play().catch(()=>{})};if(a.readyState>=1)play();else{a.addEventListener('loadedmetadata',play,{once:true});a.load()}}</script></html>'''
    (root / "index.html").write_text(page, encoding="utf-8")
    print(f"Listening comparison: {root / 'index.html'}", flush=True)


if __name__ == "__main__":
    main()
