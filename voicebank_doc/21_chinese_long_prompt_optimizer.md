# 中文长提示压缩与 190 段最优覆盖

## 结论

七个中文传统库共有的 2,556 条 ART 边，现在可以压缩成 **190 段**首尾带静音、每段恰好 12 个合法拼音音节的录音提示。该结果不是近似值：在“每段最多 12 音节”的模型内，190 达到严格下界。

```text
必需 ART 边                         2,556
其中音节间边                       2,090
每段最多音节                       12
每段最多音节间边                   11
理论下界 ceil(2090 / 11)            190
实际提示                            190
漏边 / 图外边                       0 / 0
音节间边恰好覆盖一次               2,090 / 2,090
原生验证的不同音素音节已出现       407 / 407
```

规范化 `pinyin` 提示数组的 SHA-256 为：

```text
014dea6ae502f5495cc427ee3299129cdbe381d5ebfff8f8a1068efc5a343796
```

这把上一轮 2,090 条双音节 witness 压缩了约 11 倍，同时保留逐边追溯能力。它仍是拼音音素覆盖脚本，不是已经润色成自然汉语句子的最终歌手台本。

## 为什么下界是 190

公共 ART 图的角色划分已经证明互斥且完备：

- 373 条边只会出现在单个合法音节内部；
- 2,090 条边只会出现在相邻两个音节之间；
- 55 条是 `Sil → 音节开头`；
- 38 条是 `音节结尾 → Sil`。

一段含 12 个音节的提示只有 11 个音节边界，所以最多容纳 11 条“音节间”必需边。无论怎样选择拼音：

```text
prompts >= ceil(2090 / (12 - 1)) = 190
```

规划器实际构造 190 条各含 11 条不同音节间边的路径，因而达到下界。音节内部和静音边不是拿总边数除以每段 transition 数量来估算，而是由同一批 12 音节提示额外覆盖。

## 构造方法

`plan_chinese_long_prompts.py` 把一条音节间 ART 边看成：

```text
前一音节尾音 -> 后一音节首音
```

若要把两条这样的边连续放进同一录音片段，中间必须存在一个原生 G2PA 已验证的音节，其首音等于前一条边的终点、尾音等于后一条边的起点。工具按以下步骤构造：

1. 从图报告读取 2,556 条交集 ART 边，并再次验证四类角色互斥且无遗漏。
2. 从 G2PA 清单恢复 407 个不同音素音节；每个音节的首尾音对在本清单中唯一。
3. 用带下界的整数 circulation 选择 1,900 个片段内部音节连接。每个规范音素音节至少出现一次，并为所有 55 个静音起始和 38 个静音收尾预留片段边界位置。
4. 把 2,090 条不同音节间边分解为 190 条固定长度路径，每条恰好 11 条边；搜索过程使用固定 seed，输出可重复。
5. 给每条路径补首尾音节与 `Sil`，重新展开全部音素 transition，拒绝任何图外边、漏边、重复音节间边或未出现的规范音节。
6. 为 2,556 条必需边输出 `prompt ID + transition index` 追踪表。

这里的 circulation 只是在合法音节之间分配离散计数，不修改、推断或复制任何商业声库的音频与声学帧。

## 使用

输入沿用前两轮工具的 JSON：

```powershell
python -B voicebank\tools\plan_chinese_long_prompts.py `
  E:\VoicebankResearch\seven_bank_graph.json `
  E:\VoicebankResearch\chinese_g2pa_inventory.json `
  --art-set intersection `
  --max-syllables 12 `
  --output E:\VoicebankResearch\chinese_long_prompts.json
```

输出文件已存在时拒绝覆盖。主要字段包括：

- `recording_prompts[].pinyin`：带 `<sil>` 的拼音序列；
- `phoneme_syllables`：每个拼音对应的原生音素组；
- `phonemes`：展开后的完整音素路径；
- `cross_edges`：本片段负责且只出现一次的音节间 ART 边；
- `required_edge_trace`：每条必需边的片段和 transition 下标；
- `prompt_plan_sha256`：不含本机路径的规范化提示清单哈希。

生成后可用不导入规划器实现的独立验证器复核：

```powershell
python -B voicebank\tools\verify_chinese_long_prompts.py `
  E:\VoicebankResearch\chinese_long_prompts.json `
  E:\VoicebankResearch\seven_bank_graph.json `
  E:\VoicebankResearch\chinese_g2pa_inventory.json
```

验证器重新检查每个拼音 token 的原生音素、音节展开、片段上限、图外 transition、全部边的 occurrence/trace、音节间边唯一性、规范音节全集、理论下界和提示哈希，不信任生成器写入的 summary。

默认 12 音节实测由 seed 0 直接成功；190 条均为 12 音节，共 2,280 个录音音节出现次数，单段展开后为 12–21 个非静音音素。两次独立进程生成的 JSON SHA-256 完全相同，独立验证器也得到相同的 2,556/2,090/407 计数与提示哈希。作为参数化交叉检查，`--max-syllables 8` 也生成了达到其下界的 299 段计划，仍为零漏边、零图外边。

## 示例

前 3 条确定性输出为：

```text
<sil> zi an en en yin en wo en ye yin yin you <sil>
<sil> si yin wo yin ye wo ye ye ying yin ying feng <sil>
<sil> ci ye a yin a ye ang yin ang ye min guai <sil>
```

这些序列在音素图上完全合法，但可能包含重复、语义无关或不符合自然词法的拼音组合。不能直接把“可发音”写成“自然句子”。

## 尚未解决

1. 尚未为拼音链选择自然汉字、词句和词法边界；替换文本时必须重新跑原生 G2PA，并证明音素序列不变。
2. 尚未分配声调、固定演唱音高、每音节时长、力度、重音、左右静音长度和呼吸点。
3. 12 音节只是离散上限；真实可录长度应由目标 BPM、音节秒数、歌手换气和辅音密度共同决定。
4. 2/3/4 层采样音高、`Sil→unvoiced` 单层例外以及不同层的录音轮次尚未展开到台本。
5. 尚未把录音文件、强制对齐、outer/inner 边界、人工 QA 结论和最终 DDI 单元建立 provenance 清单。

因此 M4 的“合法长提示压缩”已经完成，但“可直接交给歌手并自动进入批量构建器的录音工程”仍未完成。
