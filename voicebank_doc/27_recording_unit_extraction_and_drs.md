# 录音单元提取与批量 DRS 帧契约

## 结论

长录音到最终 FRM2 之间现在已有两个独立、拒绝带病继续的接口：

```text
segmentation plan
  -> extract_recording_units.py
  -> 独立 PCM16 ART/STA WAV + unit_manifest.json
  -> analyze_recording_units.py
  -> DRS SMS2 + 实际帧数/清浊/inner alignment 清单
```

这条链已经在完全自有的合成录音上覆盖 ART 的四种清浊组合和 STA，并证明规范化后的 FRM2 负载可重复。它仍不等于真人录音已经通过：切分候选没有人工边界验收，强制 F0 envelope 也不能证明 PCM 里真的发了声明音素。

## 安全提取接口

`extract_recording_units.py` 只消费 `vocaloid-recording-segmentation-plan-v1` 的 preferred 候选。执行前会重新核对：

- segmentation 候选规范哈希和 summary 数量；
- capture 全量状态与 `coverage_complete` 的内部一致性；
- 所有相对路径均位于给定 recording root 下；
- 每个源 WAV 的 SHA-256、44.1 kHz、mono、PCM16、frame count；
- 每个采样范围、unit sample count 和同源候选的一致契约。

所有只读 preflight 通过后才创建输出目录。输出目录必须不存在，写入期间保留 `EXTRACTION_INCOMPLETE`；成功写完清单后才删除 marker。每个输出 WAV 会重新打开，要求其 PCM payload 与源 WAV 指定 sample slice 逐字节相同。

稳定布局为：

```text
art/L01/edge_0001.wav       -> ART_L01_0001
sta/L01/phoneme_001.wav     -> STA_L01_001
unit_manifest.json
```

每个清单项保留源 candidate/take/path/SHA/sample range、层、builder 秒规格、provisional frame alignment 和选择依据，并固定标为：

```text
approval_status = unapproved_extracted_candidate
```

局部合成回归从 1 条 ART 长 take 和 1 条 STA take 得到 14 ART + 1 STA。两次独立提取的全部相对路径与文件 SHA 零差异：

```text
unit manifest canonical SHA-256
61ace9a615a3043b46bb73056e6daf9c5a07bc520308bfe77c4f1d2a9ec57a18

complete unit_manifest.json SHA-256
6ffdde3ef5543401f0909778907305af77975496cfeb666133f3b5fb7a9af938
```

篡改源长 WAV 后，提取器在创建输出目录前因 SHA 不符退出。重复使用已有输出目录也会被拒绝。

## 批量 DRS 接口

`analyze_recording_units.py` 重新验证 unit manifest 的规范哈希、每个独立 WAV 的路径/SHA/容器/长度和 `ceil(samples/256)` 帧数契约，然后顺序调用一次构建好的 `DrsHarness`。默认处理全部单元，也可重复使用 `--unit-id` 做小批 calibration；只选子集或上游 coverage 不完整时，成功分析仍返回 partial 状态，不会冒充全覆盖。

分析模式由 PHDC 清浊注释确定：

| 单元 | DRS F0 envelope | 帧契约 |
| --- | --- | --- |
| STA | 固定正 F0 | 全 voiced |
| ART voiced→voiced | 固定正 F0 | split 两侧均 voiced |
| ART unvoiced→unvoiced | 固定零 F0 | split 两侧均 unvoiced |
| ART unvoiced→voiced | 0→正 F0 阶跃 | split 前 U、后 V |
| ART voiced→unvoiced | 正 F0→0 阶跃 | split 前 V、后 U |

为此 `DrsHarness` 新增 `unvoiced` pitch mode：关闭 auto F0，并把动态参数 `0x0d` 固定为零。独立测试对 19,404-sample WAV 得到 76/76 个 `UnvoicedFrame`。同类清浊两侧虽然没有 frame type 变化，DDI 仍需用标注 split 分成两个 outer group；不能用“检测 voicing 跳变”代替 alignment。

每个 DRS 结果必须同时通过：

1. SMS2 中只有一个 FRM2 run，所有帧可 byte-exact parse/serialize；
2. 普通帧通过主字段 validator，其他帧只能是 unvoiced；
3. 相邻帧时间差严格为 `256/44100`；
4. 实际帧数等于 `ceil(wav_samples/256)`；
5. 按实际帧数重算的 split 与 source/target inner ranges 均在合法 outer side 内；
6. 每一帧的 voiced/unvoiced 类型与两侧注释完全相等。

成功项仍只标为：

```text
validation_status = structurally_valid_unapproved
approval_status   = unapproved_drs_analysis
```

输出目录同样必须不存在，并在完成前保留 `ANALYSIS_INCOMPLETE`。源 WAV 被篡改时，在输出目录创建前退出；已有输出目录被拒绝且原清单不变。

## 可重复性：为什么不能直接比较整个 SMS2

两次独立分析的 15 个原始 SMS2 文件哈希全部不同。进一步比较首例发现：875,353 字节中只有 96 个外层非帧字节不同，而 76 个 FRM2 块逐帧、逐字节完全相同；15 个单元的全部 FRM2 也都如此。

因此报告同时保存两种哈希：

- `output_sms2_sha256`：追踪某一次原始中间产物，不参与规范可重复性判断；
- `frm2_payload_sha256`：依次哈希 `uint64_le(frame_size) + raw_FRM2`，作为无拼接歧义的帧流语义哈希。

两次独立运行的原始 SMS2 哈希 mismatch 为 15/15，但 FRM2 payload mismatch 为 0/15，最终规范分析哈希完全相等：

```text
analysis_manifest_sha256
cab00d108e8661518030560d793c63f77b9562d020acfb371dfe602195ce7b77
```

这也说明后续 DDB 单元构建应以已验证的 FRM2 块流为输入，不应把 SMS2 wrapper 的无关字节纳入可重复构建判据。

## 局部回归矩阵

15 个自有合成单元的结果为：

```text
ART                                            14 * 76 frames
STA                                             1 * 130 frames
voiced only ART                               11 * 76 V
unvoiced only ART                              1 * 76 U
voiced -> unvoiced ART                         1 * (38 V + 38 U)
unvoiced -> voiced ART                         1 * (38 U + 38 V)
stationary                                     1 * 130 V
```

这个 fixture 的中段实际是正弦音。全清音和跨清浊结果来自受控 F0 注释，目的只是验证 DRS/FRM2/对齐结构，不能用来评价声学正确性或音质。

## 使用

```powershell
python -B voicebank\tools\extract_recording_units.py `
  E:\VoicebankResearch\segmentation_plan.json `
  E:\VoicebankResearch\recordings `
  E:\VoicebankResearch\units

python -B voicebank\tools\analyze_recording_units.py `
  E:\VoicebankResearch\units\unit_manifest.json `
  E:\VoicebankResearch\units `
  E:\VoicebankResearch\analysis
```

录音、独立 WAV、SMS2 和后续 DDB 都应留在仓库外。仓库只保存工具、示例规格、统计和不含音频负载的研究结论。

## 下一步

1. 用真人 calibration takes 校准名义边界、F0/清浊与 inner 稳定区，再加入人工修订/锁定格式。
2. 让批量 DDB assembler 消费已验证的 FRM2 帧流与 PCM，生成每个 ART/STA 的一单元 DDB及绝对偏移清单。
3. 将全部单元合并进一份 DDB，并从 62 PHDC、38 STA、2,556 ART 与各层构造完整 DDI 树。
4. 对全量真实录音执行覆盖、失败重录、可重复构建、原生 loader 和宿主渲染矩阵。

