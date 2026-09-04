# 分层录音 session manifest

## 目的

`plan_recording_sessions.py` 把已经验证的 190 条中文长提示、38 个 STA、七库层模板和一份显式录音配置合并成逐 take manifest。它不创建或假装已经录好 WAV；所有 provenance/QA 字段初始都是 `pending`。

这补上了之前链路中的一个接口空洞：图覆盖计划不再只是一组拼音，而能确定“哪一条提示、在哪一层、应写到哪个 WAV、预期多长、需要怎样的 QA”。

## 输入配置

示例为 `recording_session_spec.example.json`：

```json
{
  "schema_version": 1,
  "session_id": "research_voice_cn_3layer_v1",
  "layer_count": 3,
  "center_midi": 64.0,
  "comfortable_midi_range": [60.0, 68.0],
  "repetitions": 1,
  "timing": {
    "art_syllable_seconds": 0.5,
    "art_leading_silence_seconds": 0.5,
    "art_trailing_silence_seconds": 0.5,
    "stationary_sustain_seconds": 1.5,
    "stationary_leading_silence_seconds": 0.5,
    "stationary_trailing_silence_seconds": 0.5,
    "maximum_prompt_seconds": 8.0
  },
  "capture": {"sample_rate": 44100, "channels": 1, "bit_depth": 16},
  "qa": {
    "pitch_tolerance_cents": 25.0,
    "minimum_pitch_correlation": 0.7,
    "minimum_snr_db": 45.0,
    "duration_tolerance_seconds": 0.25,
    "maximum_peak_dbfs": -1.0,
    "minimum_signal_rms_dbfs": -30.0,
    "maximum_dc_offset": 0.01
  }
}
```

时间和 QA 阈值是明确、可修改的 session 决策，不伪装成从 DSE 唯一恢复出的常量。当前分析/构建链只接受 44.1 kHz、单声道、PCM16，因此规划器拒绝其它 capture 格式。若未来引入 24-bit 原始录音，应先建立可审计的重采样/量化步骤，再放宽该门槛。

四个首尾静音时长必须为正数，不能用零秒绕过边界上下文或 SNR 检查。

`comfortable_midi_range` 是硬门槛。规划器把参考层模板平移到 `center_midi` 后，只要一个层点超出歌手声明的舒适区就失败，不会静默裁掉一层或改音高。

## 使用

```powershell
python -B voicebank\tools\plan_recording_sessions.py `
  E:\VoicebankResearch\chinese_long_prompts.json `
  E:\VoicebankResearch\seven_bank_graph.json `
  E:\VoicebankResearch\chinese_g2pa_inventory.json `
  E:\VoicebankResearch\reference_layers.json `
  voicebank_doc\recording_session_spec.example.json `
  E:\VoicebankResearch\recording_session.json
```

五个输入文件都写入 SHA-256；输出已存在时拒绝覆盖。当前示例的三层为：

| 层 | MIDI | 近似音名 | Hz | 相对中心 |
| --- | ---: | --- | ---: | ---: |
| L01 | 60.3301 | C4 | 266.661 | -3.6699 semitone |
| L02 | 64.3348 | E4 | 336.064 | +0.3348 semitone |
| L03 | 67.3351 | G4 | 399.658 | +3.3351 semitone |

它保留参考库层坐标中的小数偏差，而不是提前四舍五入。真正分析后，每个 take 仍应从 F0 得到自己的 `pitch2`；这些目标点主要用于演唱引导和 `pitch1` 层选择。

## 示例规模

190 条 ART 提示与 38 个 STA 全部录三层、每层一遍：

```text
ART takes             190 * 3 = 570
STA takes              38 * 3 = 114
总 takes                         684
每条 ART 预估                  7.0 s
每条 STA 预估                  2.5 s
净计划音频                   4275 s = 71.25 min
```

这里的 71.25 分钟只把配置中的边界静音和发音时长相加，不含 count-in、报幕、失败 take、休息、设备调整和工程操作。不能把它当作整个录音棚工时。

规范 take 清单哈希为：

```text
a5b09d2ef1d88ba4bef22c6e99a6091bff9c3e78bea3b766af4284e919558402
```

两次独立生成的完整 JSON SHA-256 也一致：

```text
138658e645aa0983c11a43cebafb5ef2da17248af66d653d54b94d94b9dc4a04
```

完整 JSON 哈希随 QA 合同字段变化；不含 QA 阈值的规范 take 路径/时长清单哈希仍保持 `a5b09d...`。

## STA carrier

每个 STA 是持续音素，不一定有同名拼音。规划器从 441 项原生 G2PA 清单中选择一个以目标音素结尾的最短 carrier，并记录目标在音节中的下标。例如：

```text
7    <- e    [7]       target index 0
@N   <- beng [p @N]    target index 1
@_n  <- en   [@_n]     target index 0
uo   <- wo   [uo]      target index 0
y{_n <- yuan [y{_n]    target index 0
```

歌手指令是短促读过 carrier onset、持续目标音素；后续切分只把稳定目标区送入 STA。carrier 的自然度和发音便利性仍需真人预读，工具只证明 G2PA 与目标音素一致。

## 文件和 provenance

相对路径固定为：

```text
art/L01/prompt_0001_R01.wav
sta/L01/sta_001_R01.wav
```

每个 take 都包含：

- ID、类型、层 ID 和精确目标频率；ART take 还显式保存 `prompt_id`，无需从文件名反推长提示；
- 拼音、展开音素和负责的 11 条音节间 ART 边；
- 预期时长与 WAV 相对路径；
- `status/wav_sha256/recorded_utc/performer_id/microphone_chain_id/qa_status` 占位；
- 发音或持续音指令。

任何实际 WAV 都应放在 Git 忽略的外部工作目录。只有完成哈希、录音链 ID、人工 QA 和边界标注后，take 才能从 `pending` 进入可分析状态。

## 保守层策略

manifest 对全部 ART 和 STA 都安排全部主层。七库证据允许某些 `Sil→unvoiced` 最终只保留一个 `-FLT_MAX` 无音高样本，但具体辅音集合随产品变化。因此本阶段宁可多录，不在原始数据采集前删层；后续切分器可从多个 take 中选择一个最佳无声候选并丢弃其它副本。

VQM/growl 没有加入 session。其 FRM2 物理布局虽已恢复，但生成算法和录音要求仍不足以支撑可执行脚本。

## 尚未完成

1. 190 条拼音链尚未替换为保持同一音素序列的自然汉字台本。
2. 尚未用真人试唱验证 0.5 s/音节、1.5 s STA 和三层中心是否舒适；配置只是第一轮试录规格。
3. ~~实现实际 WAV 的格式、峰值、SNR、F0 稳定度、静音污染和 provenance 自动预检。~~ 已完成机器预检；真人阈值校准与 approved 回填仍未完成。
4. ~~尚未把长 WAV 展开为逐 ART 单元，并生成人工 outer/inner 边界审阅表。~~ 已能按录音时序生成逐 ART/STA 切分候选和审阅状态；真正的声学强制对齐与真人修订仍未完成。
5. 尚未把通过 QA 的数百个单元批量送入 DRS、建树和 DDI/DDB 打包。

因此这个 manifest 把“录什么”闭合成机器可检查的清单，但没有把“已经录好且分析合格”提前判为完成。

第 3 项的机器预检见 [录音 capture 自动预检](24_recording_capture_validation.md)，第 4 项的名义切分接口见 [ART/STA 单元切分与对齐候选](25_recording_segmentation_candidates.md)；真人 WAV 尚未提供，因此仍缺真实阈值校准、声学边界修订与人工确认。
