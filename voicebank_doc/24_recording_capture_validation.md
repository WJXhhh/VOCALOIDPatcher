# 录音 capture 自动预检

## 结论

新增 `validate_recording_capture.py`，可把外部录音目录与 session manifest 逐 take 对照，生成只读验证报告。它不会修改 manifest，也不会把 `pending` 自动升级为 approved。

当前自动门槛包括：

- WAV 必须是 44.1 kHz、单声道、PCM16、无压缩；
- 实际时长与 manifest 预期值的偏差；
- peak dBFS、整数削波样本数、DC offset 和有效发声 RMS；
- 声明的首尾静音区相对发声区的 SNR；
- ART 每个音节稳定区、STA 持续区的目标 F0 偏差与自相关置信度；
- provenance 中已有 WAV SHA-256 时必须与文件一致；
- 全量验证时报告漏文件和未在 manifest 中出现的额外 WAV。

通过只能说明机器预检满足配置阈值，不能替代发音、音色、情绪、辅音形态和 outer/inner 边界的人工 QA。

## 使用

验证整个 session：

```powershell
python -B voicebank\tools\validate_recording_capture.py `
  E:\VoicebankResearch\recording_session.json `
  E:\VoicebankResearch\recordings `
  E:\VoicebankResearch\capture_validation.json
```

录音过程中也可只检查一个或多个 take：

```powershell
python -B voicebank\tools\validate_recording_capture.py `
  E:\VoicebankResearch\recording_session.json `
  E:\VoicebankResearch\recordings `
  E:\VoicebankResearch\capture_validation_partial.json `
  --take-id ART_L01_prompt_0001_R01 `
  --take-id STA_L01_001_R01
```

退出码：

| 退出码 | 含义 |
| ---: | --- |
| 0 | 所选 take 全部通过，且全量模式下没有额外 WAV |
| 2 | manifest、参数、路径或 WAV 容器无法解释 |
| 3 | 有 missing/failed take，或全量目录含额外 WAV |

输出已存在时拒绝覆盖。相对路径会拒绝绝对路径和 `..`，避免 manifest 把验证器引出指定录音根目录。

## 信号指标

边界 SNR 直接使用 session 配置声明的 leading/trailing silence：

```text
SNR = 20 * log10(signal RMS / boundary-silence RMS)
```

如果边界区包含报幕、节拍器或说话，这个数值会按设计失败；不能把非静音前导误当作房间底噪。

默认示例阈值为：

```text
duration tolerance       ±0.25 s
maximum peak             -1 dBFS
minimum signal RMS       -30 dBFS
minimum boundary SNR      45 dB
maximum normalized DC      0.01
pitch tolerance          ±25 cent
minimum pitch correlation  0.70
clipping samples           0
```

这些是第一轮试录 QA 参数，不是 DSE 文件格式常量。真人录音应先做少量 calibration takes，再根据麦克风、自噪、歌手颤音和房间条件调整；任何放宽都应改配置并产生新的配置哈希。

## F0 预检位置与算法

ART 片段按 manifest 的固定音节时长，在每个音节约 72% 处取一个 2048-sample 窗；这会避开多数起辅音，落在音节末端的有声韵尾/韵母。STA 在持续区约 65% 处取窗。

每个窗只在目标音高 `±150 cent` 的 lag 范围内做去 DC 的归一化自相关，并对峰值 lag 做三点抛物线插值。报告同时保存：

- `estimated_hz`；
- 相对目标的 `cents_error`；
- 自相关 `correlation`；
- 该窗 `rms_dbfs`。

限定搜索范围的目的是检查“是否贴近录音目标”，不是做任意音频的通用基频识别。实际人声可能因颤音、清辅音泄漏、声门噪声或 octave ambiguity 需要看完整 F0 contour；单窗结果不能写回最终 `pitch2`。

## 合成回归

使用当前三层 manifest，在外部临时目录程序生成两条完全自有的正弦 WAV：

- `ART_L01_prompt_0001_R01`：7.0 秒，12 个待检音节窗；
- `STA_L01_001_R01`：2.5 秒，一个持续音窗；
- 中间信号为该层目标约 266.661 Hz，峰值约 `-8.725 dBFS`，首尾为数字静音。

验证结果：

```text
selected=2  passed=2  failed=0  missing=0
F0 error 绝对值 < 0.004 cent
correlation ≈ 0.9999
```

负向回归把同一 ART 信号幅度提升到 int16 极限，检测到 461 个削波样本并同时报告：

```text
failures = clipping, peak_level
exit      = 3
```

这证明通过/失败路径和 F0 数值链能够工作，但正弦测试不能证明真人音频会通过，更不能校准 45 dB SNR 或 0.70 correlation 是否适合最终歌手。

## 尚未完成

1. 尚未用真人 calibration take 校准各阈值。
2. 尚未验证拼音/音素次序、漏读、吞音和共构影响；这些需要人工听审或后续识别辅助。
3. 尚未输出完整逐帧 F0 contour、颤音范围和层内音色一致性指标。
4. ~~尚未完成 outer/inner 边界建议。~~ 已按固定录音时序生成名义候选；尚未完成基于真人声学变化的强制对齐与人工修订。
5. 尚未把通过报告中的 SHA-256 以受控流程回填到 provenance manifest。

因此下一步可以从少量真人 ART+STA calibration takes 开始，而不必先录完 684 条才发现格式、响度或目标音高配置有系统性问题。通过预检的 take 可继续交给 [ART/STA 单元切分与对齐候选](25_recording_segmentation_candidates.md)，但不能因自动报告通过就跳过听审和边界修订。
