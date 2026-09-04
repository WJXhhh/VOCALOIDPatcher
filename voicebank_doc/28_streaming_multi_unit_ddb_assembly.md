# 流式多单元 DDB 装配

## 结论

新增 `assemble_recording_units_ddb.py`，把已验证的 unit WAV 与 DRS FRM2 流直接写成一份多单元 `.ddb`，不再为每个单元先落一份临时 DDB。对三层完整计划的 7,782 个单元，这避免了几千个近 1 MiB 中间文件，同时仍为将来的 DDI 生成保存每一项绝对偏移。

当前闭合范围是：

```text
unit_manifest + analysis_manifest + WAV + SMS2
  -> 严格 provenance/帧契约复核
  -> FRM2... + SND 逐单元顺序流式写入
  -> 一份确定性 DDB
  -> 每个 DDI 所需的绝对 frame/SND 指针与 ART alignment 清单
```

后续已经在 [计划驱动的多音素、多层 DDI 构建](30_planned_multi_unit_ddi.md) 中构造了完整 PHDC 与当前单元对应的 DDI 树，并通过公开解析器和 DSE 原生 loader。它仍没有通过 stock Editor 产品授权，所有输出继续明确标记为 unapproved。

## 输入复核

装配器同时要求 extracted-unit manifest 和 DRS analysis manifest，防止任一中间清单脱离上游。创建输出目录前会核对：

- unit manifest 的完整文件 SHA 与规范 unit SHA；
- analysis manifest 记录的上游两个 SHA 与当前 unit manifest 相等；
- analysis 的规范 FRM2 清单 SHA、unit 数量和 complete/partial 状态；
- 每个 unit ID/kind/WAV path/WAV SHA 在两个清单中一致；
- WAV 仍为 44.1 kHz mono PCM16，长度仍满足 `ceil(samples/256)`；
- SMS2 相对路径受根目录约束，原始文件 SHA 与分析清单一致。

开始流式写入后，每个单元还会重新：

1. 提取唯一 FRM2 run；
2. 核对带长度分隔的 FRM2 payload SHA；
3. 核对实际 frame count 与 voicing runs；
4. 对 ART 再执行 split 两侧逐帧清浊和 inner range 校验；
5. 对 STA 再要求全 voiced；
6. 在读 WAV/SMS2 前后复核 SHA，拒绝 preflight 后被替换的输入。

## DDB 物理布局与 DDI 接口

每个单元按以下顺序连续写入：

```text
FRM2[0] ... FRM2[n-1] SND
```

SND 的 core PCM 会按 DRS 实际帧数裁剪或补零到 `frame_count * 256`，两端各附 1,024 samples 的分析 margin。补差绝对值必须小于一个 hop。清单为每个单元保存：

- `base_offset/end_offset/unit_bytes`；
- 所有绝对 `frame_offsets`；
- `snd_chunk_offset/snd_chunk_size`；
- `snd_payload_pointer = snd_offset + 18`；
- `snd_core_pointer = snd_payload_pointer + 2048`；
- sample rate、channel、PCM count 与 core padding；
- ART 的两组 `[outer_start, outer_end, inner_start, inner_end]`；
- edge/phoneme、层、F0、FRM2 payload SHA 和单元 DDB SHA。

STA 的 DDI 指针使用 core pointer；ART 同时使用 payload/core 两个指针，二者差 2,048 bytes，与已原生回读的参考/最小库关系一致。

写入先发生在 `.tmp`，完成并 `fsync` 后才原子替换最终 DDB；期间保留 `ASSEMBLY_INCOMPLETE`。随后工具从最终文件重新：

- 计算完整 DDB SHA；
- 对每个单元绝对范围重新计算 SHA；
- 按绝对 offset 逐帧 parse/serialize FRM2；
- 重算 FRM2 payload SHA；
- 检查每个 SND header、大小、采样率、声道、PCM 数量与单元末端。

只有这些检查全部通过才写 `ddb_manifest.json` 并删除 marker。

## 局部回归结果

两份独立分析输入的 SMS2 wrapper 全部不同，但规范 FRM2 payload 相同。分别流式装配后得到逐字节完全相同的最终 DDB：

```text
units                         15
ART                           14
STA                            1
DDB bytes             12,656,830
DDB SHA-256
72dfb6190191252499e866613e9449960df56afa32ee7d5410b06a8b7526a4cb

canonical DDB manifest SHA-256
5aa0f398abdaa0acc1e693e94c9434b993e254b4aa3d7d2bc6b17f29feb2f603
```

两份 JSON 清单的完整文件 SHA 不同，因为它们忠实记录了不同的原始 analysis-manifest 文件 SHA；这不影响 DDB 与规范偏移清单相同。换言之，provenance 保留单次外层产物差异，构建语义仍是确定的。

负向回归：

- 已有输出目录被拒绝，原 DDB SHA 保持不变；
- 篡改任一 SMS2 字节后，在创建输出目录前因原始 SHA 不符退出；
- 上一阶段已验证篡改 WAV 同样在创建分析/装配输出前失败。

由于上游只包含两个 synthetic takes，`coverage_complete=false`，工具成功构造局部 DDB后仍返回 partial 状态。不能把这份 15-unit DDB 称为完整中文声库。

## 使用

```powershell
python -B voicebank\tools\assemble_recording_units_ddb.py `
  E:\VoicebankResearch\units\unit_manifest.json `
  E:\VoicebankResearch\analysis\analysis_manifest.json `
  E:\VoicebankResearch\units `
  E:\VoicebankResearch\analysis `
  E:\VoicebankResearch\ddb `
  --stem ResearchVoice
```

输出 stem 只允许 ASCII 字母、数字、下划线和连字符，为后续同 stem `.ddi/.ddb` 与 DBSe digest 保持一致。

## 下一步

1. ~~让原生 tree harness 从显式 PHDC/STA/ART 计划构造多音素、多层 skeleton。~~ 已完成。
2. ~~依据本清单的 unit ID/order，从后向前向每个 STAp/ARTp 注入可变 EpR/SND/alignment，再统一加入 DBSe digest 和 compact normalization。~~ 已完成。
3. ~~对 15 个局部单元完成公开解析器与 DSE loader 双重回读，并验证同边多层 part key。~~ 已完成；结果见文档 30。
4. 扩大到真人全量 62 PHDC、38 STA、2,556 ART 和全部层；真人边界 QA、许可证与宿主渲染仍是独立完成条件。
