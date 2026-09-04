# ART/STA 单元切分与对齐候选

## 结论

新增 `plan_recording_segments.py`，把 capture validation 中通过的长 ART/STA take 转成可人工审阅、可追溯到源 WAV 的单元候选。它解决的是“长录音怎样进入现有 DRS/构建器接口”，不是完成了声学强制对齐。

每个 ART 候选同时保存：

- 源 take、相对 WAV、SHA-256、层和 repetition；
- ART edge、四类语义角色和两侧 PHDC voiced/unvoiced 分类；
- 原长 WAV 中的 boundary sample/time 与 outer extraction 范围；
- 切后单元内的 `boundary_seconds`、`source_inner_seconds`、`target_inner_seconds` 和 `f0_hz`；
- 按当前 256-sample hop 估计的 frame count、split 和 inner frame range；
- `confidence=nominal_schedule`、`review_status=needs_manual_boundary_review`。

后四个 builder 字段与 `build_minimal_sil_a_bank.py` 已验证的规格单位一致。STA 候选则从 carrier 持续区内截出一段全 voiced 稳定区，只向构建器提供切片与 `f0_hz`。

## 配置与时序模型

示例配置为 `segmentation_spec.example.json`：

```json
{
  "schema_version": 1,
  "two_phoneme_onset_fraction": 0.3,
  "art_context_seconds": 0.22,
  "art_inner_margin_seconds": 0.03,
  "art_inner_width_seconds": 0.08,
  "stationary_inner_fraction": [0.35, 0.85],
  "minimum_boundary_context_samples": 1024,
  "analysis_hop_samples": 256
}
```

ART 固定录音时序中的四种 transition 位置为：

| 角色 | 名义边界 |
| --- | --- |
| `silence_onset` | leading silence 结束处 |
| `within_syllable` | 双音素音节起点 + `0.3 * syllable_seconds` |
| `cross_syllable` | 相邻两个音节的固定分界 |
| `silence_coda` | 最后一音节结束、trailing silence 开始处 |

`0.3` 只是第一轮 onset 比例，不是从商业声库或 DSE 恢复出的普适常量。尤其塞音、擦音、零声母和复合韵母不应共享一个最终比例；真实录音必须在波形、谱图、F0/voicing 与听感上修订。

每条 ART 默认取边界两侧各 0.22 秒，共 19,404 samples。inner 与边界保留 0.03 秒间隔，再分别取 0.08 秒稳定区。两侧上下文都必须至少有 1,024 samples，以覆盖已经确认的分析窗边缘要求。

STA 使用声明 sustain 的 35%–85%，避开 carrier onset 和收尾。这个区间仍只按表定时间选择，不能替代持续音稳定度与发音正确性的人工判断。

## 使用

```powershell
python -B voicebank\tools\plan_recording_segments.py `
  E:\VoicebankResearch\recording_session.json `
  E:\VoicebankResearch\chinese_long_prompts.json `
  E:\VoicebankResearch\seven_bank_graph.json `
  E:\VoicebankResearch\capture_validation.json `
  voicebank_doc\segmentation_spec.example.json `
  E:\VoicebankResearch\segmentation_plan.json
```

工具要求：

- validation 报告中的 manifest hash 必须等于当前 manifest；
- manifest 引用的 long-prompt/graph hash 必须等于实际输入；
- ART take 的显式 `prompt_id`、拼音和展开音素必须与长提示一致；
- 只处理 `status=passed` 且带 WAV SHA-256/有效 frame count 的 take；
- 输出已存在时拒绝覆盖。

partial validation 会正常产生 partial segmentation，退出码为 3，且 `capture_validation_complete/coverage_complete=false`。只有输入是 validator 的全量 complete 报告、全部 `2,556 * layer_count` 个 ART layer-edge 与 `38 * layer_count` 个 STA layer-phoneme 都有候选、且 validation 中无 rejected take，才返回完整覆盖和退出码 0。

## 与 DRS frame alignment 的关系

当前 DRS harness 对输入 WAV 的输出帧数满足：

```text
estimated_frame_count = ceil(extracted_pcm_samples / 256)
```

但 builder 最终仍以实际 SMS2 的 frame count 为准，并沿用已经闭环的映射：

```text
frame = round(seconds / extracted_unit_duration * actual_frame_count)
```

因此 segmentation JSON 中所有 frame 下标都明确写为 `provisional_until_drs_output`。秒和采样点是切片契约；帧下标只是提前审阅用的估计，不能跳过 DRS 输出后的 split/voicing 强校验。

默认 0.44 秒 ART 单元的确定值为：

```text
samples                 19,404
estimated DRS frames        76
boundary in unit          0.22 s -> frame 38
source inner        0.11..0.19 s -> [19,33)
target inner        0.25..0.33 s -> [43,57)
```

这与旧 0.30 秒诊断单元的 52 帧不是冲突，而是 outer context 加长后的新切片长度。

## 回归结果

用 manifest 首个 L01 ART 与首个 L01 STA 生成完全自有的正弦 WAV，经 capture validator 通过后再规划：

```text
passed source takes                  2
ART candidates                      14
STA candidates                       1
distinct ART edges                  14
full coverage                    false
candidate plan SHA-256
7dc564df618604551fe4ca256e10a09bb0c8f96b6066490ba5a6cec8c1b7c01e
```

两次独立输出的完整 JSON SHA-256 都是：

```text
3121580a46f382e5c08b33ade34ccfebcf1d37e3970f71565ed8ff5fa71cbe06
```

独立遍历全部 190 条提示得到 3,376 个 transition occurrence，按 edge+role 去重后与长计划的 2,556 条 trace 完全一致。再用全 manifest 的纯结构测试模拟“每个 take 都有通过记录”，得到：

```text
ART occurrences              3,376 * 3 = 10,128
preferred ART layer-edges    2,556 * 3 =  7,668
STA candidates                  38 * 3 =    114
coverage_complete                         true
```

该全量测试没有读取或伪造 684 条 WAV 文件，只验证索引、分组和覆盖算法；不能写成真实录音通过。

## 尚未完成

1. 尚未用真人 calibration takes 比较名义边界与声学边界偏差。
2. 尚未实现谱通量、能量、F0/voicing 或识别模型辅助的边界 refinement。
3. 尚未提供人工审阅 UI、修订文件格式和“修订后锁定”签名流程。
4. preferred 候选的安全批量切 WAV、DRS 实际 frame count、四类清浊组合和 inner alignment 强校验已在局部合成 fixture 上闭环；全量真人录音尚未执行。
5. 同一 edge 的多个 occurrence/repetition 目前只按稳定顺序选占位项，未按声学质量自动排名。

所以当前链路已经能够无歧义地回答“应该从哪一条通过预检的长录音取哪一段、怎样喂给 DRS 并核对实际帧”，但还不能回答“真人音素边界是否已经准确”。后续提取与分析接口见 `27_recording_unit_extraction_and_drs.md`。
