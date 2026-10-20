# 神经气声离线探针

这组工具用于核对新模型的数据接口和实际声音，不加载到 VOCALOID Editor。第三方源码、权重、Python 环境和输出保存在仓库之外。

## 原波形残差实验

状态：试听不通过，不采用。2026 年 10 月 20 日用户反馈“怎么好像只是更像哑了而已”。零值精确透传与信号检查通过，仅证明实现边界成立，不能证明生成了自然气声。停止沿残差增益和谐波衰减路线继续调参；下文保留复查方法和失败对照。

`residual_air.py` 是用户否定整段声码器重建后的独立实验，不调用上轮模型，也不修改正式 PCM 后端。输入保持原采样率，输出为原波形加局部差值；0% 文件直接复制原文件，并检查计算结果也精确相等。

2026 年工程参考包括 [Unravel v1.4.0](https://github.com/themightyzq/unravel-vst) 的三路分解思路、[Expressive v0.10.0](https://github.com/NewComer00/expressive/releases/tag/v0.10.0) 的气声特征入口，以及 [Whisper Morph v1.4](https://mashav.com/sha/praat/scripts/Whisper_Morph.html) 的声道滤波耳语支路。这些实现的版本日期不代表谐噪分解或 LPC 于 2026 年才提出。Expressive 源码实际在 HPSS 谐波输出上测量频带能量，不能误称为已实现纯气流分离；Whisper Morph 自己明确采用高斯噪声，不符合本轮复用原声纹理的选择。本原型独立实现，没有移植上述项目源码，也没有使用它们的训练权重。

原型用 F0 同步复解调估计缓变谐波，从输入减去它们，再剔除可检测的二、三、四周期相干成分。剩余波形经 500–8000 Hz 支路处理，只在保守有声门限内放大。第二组同时衰减高次谐波，每倍频程最多 1 dB。它是源谱近似，不是声门开闭或真实气流的测量。输入反射扩展后再做窄带滤波，避免滤波端点伪影扩散到有效样本。

默认残差增益上限为 3 dB。12 dB 压力实验出现大量次谐波音高检测，不能声称强档已保住原发声。用户试听已指出沙哑感，保守样本的 F0 检查通过也不构成自然度或气声生成验收；该实验未部署。非零试听文件匹配原声有声区 RMS，原始浮点处理结果另存；音量匹配后的整个文件会有一个固定增益，不能据此声称辅音样本逐点不变。原始处理结果在门限外逐点相同。

```powershell
python voicebank/tools/ace_neural_probe/test_residual_air.py -v
python voicebank/tools/ace_neural_probe/residual_air.py --miku <初音原声.wav> --tianyi <天依原声.wav> --previous <上轮revision-2目录> --output <新的试听目录>
python voicebank/tools/ace_neural_probe/residual_air.py --miku <初音原声.wav> --tianyi <天依原声.wav> --residual-gain-db 12 --output <新的压力实验目录>
```

依赖当前研究环境的 NumPy、SciPy、praat-parselmouth、SoundFile 和 Matplotlib。没有新产品依赖。每次输出保存输入 SHA256、脚本 SHA256、完整脚本副本、分解数组、原始浮点与匹配电平的 WAV、音高检查和试听 HTML。音高判据要求有声检测覆盖率大于 90%、中位误差小于 10 音分、超过 100 音分的检测比例小于 1%；检测锁到次谐波是风险提示，不等同于已由人耳证实换了音高。当前素材只有初音 V4X 日语元音和天依 V5 中文元音，完整歌词、V6、高音与连读尚未验证。

## CSSC 数据准备

`prepare_cssc.py` 仅准备 24 kHz 音频和逐帧技巧 JSON，不执行推理。标准库和外部 FFmpeg 即可运行。时间、词表和负向 Air 限制见 `docs/ace-air-neural-integration.md`。

```powershell
python voicebank/tools/ace_neural_probe/prepare_cssc.py --request <request.json> --cssc-root <源码目录> --output <新的输出目录>
python voicebank/tools/ace_neural_probe/test_prepare_cssc.py --cssc-root <源码目录> -v
```

## VibE-SVC2 实际推理

`run_vibe_comparison.py` 直接调用预训练转换器、HuBERT-soft 编码器和 BigVGAN-v2 声码器。严格加载全部模型参数，权重不匹配就失败；不回退到随机初始化、控制器映射或本地噪声 DSP。

源码版本：

- VibE-SVC2：`a21a7b40dc378e59d33bde8e350f07c9392e7bcf`。
- NVIDIA BigVGAN：`7d2b454564a6c7d014227f635b7423881f14bdac`。
- BigVGAN 模型仓库：`nvidia/bigvgan_v2_24khz_100band_256x`，revision `c329ede9e9bbc100ddf5c91e2330a61921262370`。

资产目录结构：

```text
assets/
  hubert-soft-0d54a1f4.pt
  vibe_timbre/
    config.yaml
    model_200000.pt
  bigvgan/
    config.json
    bigvgan_generator.pt
    LICENSE
```

使用独立 Python 环境安装匹配显卡的 PyTorch，以及 `numpy scipy soundfile librosa praat-parselmouth pyyaml tqdm matplotlib huggingface_hub`。本探针直接导入所需模型模块，以避免上游批量 CLI 的路径约定、自动移调和不相关依赖；无需安装 Fairseq 或调用 Torchaudio。第三方源码及模型授权分别适用，未随补丁打包。

```powershell
& <独立Python> voicebank/tools/ace_neural_probe/run_vibe_comparison.py --vibe-root <VibE-SVC2源码> --bigvgan-root <BigVGAN源码> --assets <资产目录> --miku <初音原声.wav> --tianyi <天依原声.wav> --output <新的结果目录>
& <独立Python> voicebank/tools/ace_neural_probe/analyze_vibe_comparison.py <结果目录>
```

输入要求为单声道、每条不超过 15 秒。模型采样率为 24 kHz，实际 F0 从干净原声提取，不做歌手均值音高归一化。固定随机种子用于控制技巧比较。

每种声音输出以下对照：

| 文件 | 含义 |
| --- | --- |
| `native-original.wav` | 未修改的 44.1 kHz 声库渲染文件 |
| `original.wav` | 24 kHz 原声对照 |
| `vocoder-only.wav` | 原声 mel 经过声码器重建 |
| `neutral.wav` | straight 技巧的完整模型重建，不等于原声透传 |
| `breathy-035.wav`、`breathy-070.wav` | straight 与 breathy 学得嵌入之间的实验插值 |
| `breathy-100.wav` | 训练中的 breathy 技巧端点 |
| `source050-*`、`source100-*`、`source200-*`、`source400-*` | 从原声 mel 加噪后开始去噪的实验，每个起点包含 straight 和 breathy 对照 |

除 `native-original.wav` 外，每种波形均另存 `*-raw.wav` 浮点原始结果；试听版匹配原声有声区 RMS、限制峰值，并以零填充不足一帧的模型尾部，维持比较时长。原声与模型波形不混合。

现成检查点没有初音或天依的歌手嵌入。探针在已有女声 ID 中，用固定噪声的去噪损失选择参考候选，这不是声库克隆或身份识别。浅扩散和嵌入插值也只是本地适配实验，不能因为接口运行成功就判定音色或自然度通过。

分析器生成 `index.html` 同位置切换试听页、声谱图、`metrics.json`。模型与源码版本、资产 SHA256、候选歌手、推理时间和显存峰值保存在 `report.json`。声谱差、F0 与 HNR 仅作辅助，不能代替对音色和自然度的试听。
