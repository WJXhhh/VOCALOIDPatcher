# 原生中文 G2PA 音节清单与 ART 提示覆盖

## 结论

VOCALOID 6.13.0.1 随附的中文 G2PA 已通过真实 VSM 音符对象完整探测。项目现有 `Pinyin2Xsampa` 的 441 个拼音写法全部满足：

```text
CanConvert                 = 441 / 441
有原生候选                 = 441 / 441
候选总数                   = 441
首候选与项目映射精确相等   = 441 / 441
```

规范化的 `pinyin<TAB>phonemes` 清单 SHA-256 为：

```text
1d79dd0db27d65eded7da82bc4fc3844a13d3bf381df18bcc1b90b2ec7a4f7ba
```

441 个写法合并同音别名后得到 407 个不同音素音节，每个音节恰好含一个或两个传统中文音素。用这些音节解释七库共有的 2,556 条 ART 边，得到一个零重叠、零遗漏的精确划分：

| ART 语义 | 边数 |
| --- | ---: |
| 单个拼音音节内部 | 373 |
| 两个拼音音节之间 | 2,090 |
| `Sil` 到音节开头 | 55 |
| 音节结尾到 `Sil` | 38 |
| 合计 | 2,556 |

这证明公共 ART 图不是任意稠密二音素集合：它正好等于中文合法音节内部、任意合法音节相邻和首尾静音所需的边集合。没有一条边需要用非法拼音音节解释，也没有一条边同时落入多个类别。

## 为什么以前的空 note 探测不够

`G2PAManager` 的候选接口需要 `WIVSMNote*` 上下文。传空指针时，441 个拼音虽然全部 `CanConvert=true`，候选数仍全部为零，无法证明真实音素输出。

新增的 `g2pa_harness` 只在内存中建立以下最小对象链：

```text
VSM sequence manager
  -> sequence
  -> MIDI track
  -> MIDI part
  -> Chinese note (language ID 4)
  -> G2PA candidate query
```

实验结束后 `WIVSMSequence_close=true`、`WIVSMSequenceManager_destroy=true`。它不保存工程、不启动 Editor、不写注册表。

实现前还用运行时字段偏移核对了 ABI：`VSMNoteExpression` 是五个 32-bit 整数后接两个 1-byte 布尔值；G2PA 的 `useExtensionDictionary/isAi` 输入参数则沿用默认 4-byte Win32 `BOOL`。这避免了“空 note 没崩溃”掩盖错误 P/Invoke 声明。

代表性原生结果：

```text
a     -> a
zhi   -> ts` i`
guang -> k uAN
yuan  -> y{_n
```

## 可复现探针

`probe_chinese_g2pa.py` 只读取 `Pinyin2Xsampa` 字典的第一个映射块，调用 C# harness，再检查：

- 每个 token 都有一条 harness 记录；
- `CanConvert` 为真；
- 有且只有一个候选；
- 原生首候选与仓库映射完全一致；
- VSM sequence 和 manager 正常释放。

```powershell
python -B voicebank\tools\probe_chinese_g2pa.py `
  --output E:\VoicebankResearch\chinese_g2pa_inventory.json
```

输出是可审计的拼音/音素元数据，不包含商业声库音频、FRM2 或 DDI/DDB 载荷。文件已存在时工具拒绝覆盖。

## 双音节录音提示模型

`plan_chinese_g2pa_prompts.py` 接收图分析器的 `--include-keys` JSON 和上述原生 G2PA 清单。一个候选录音片段固定为：

```text
Sil + 一个或两个合法拼音音节 + Sil
```

双音节片段最多同时覆盖五条边：静音起始、左音节内部、音节间、右音节内部和静音收尾。工具拒绝任何包含目标 ART 图之外 transition 的候选。

七库交集实测：

| 指标 | 数量 |
| --- | ---: |
| ART 边 | 2,556 |
| 原生验证拼音写法 | 441 |
| 不同音素音节 | 407 |
| 不同合法 coverage set | 166,056 |
| 最终双音节提示 | 2,090 |
| 覆盖边 | 2,556 |
| 未覆盖/非法边 | 0 / 0 |

交集边清单哈希仍为 `5763b8765ab85724d45a099388ccfafd6f44de851fb32c25e09867d8b089e7e8`，与 ART trail 规划器相同。

这 2,090 在“每片最多两个音节且显式保留首尾静音”的模型内是达到下界的，不只是一次贪心结果：公共图有 2,090 条互斥的音节间边，而一个双音节片段最多只能覆盖其中一条。规划器输出恰好 2,090 个片段并覆盖其它 466 条音节内/静音边，因此达到该模型下界。

```powershell
python -B voicebank\tools\plan_chinese_g2pa_prompts.py `
  E:\VoicebankResearch\seven_bank_graph.json `
  E:\VoicebankResearch\chinese_g2pa_inventory.json `
  --art-set intersection `
  --output E:\VoicebankResearch\chinese_art_prompts.json
```

输出同时保存每条 ART 边的最短合法拼音 witness、目标 transition 在片段中的索引，以及贪心覆盖过程中每条提示首次贡献的边。

## 与 278 个任意音素 clips 的关系

两个结果回答不同问题：

- 278 个 clips 是允许任意合法 ART 边连续、每片最多 12 个音素的结构压缩上界；
- 2,090 个 prompts 强制每个片段都是首尾静音的一到两个合法拼音音节，并给出该严格模型的最优值。

因此不能把 2,090 当作最终录音量。下一步应把双音节 witness 作为约束，寻找更长的合法拼音链，使一个录音片段包含多条音节间边，同时加入最大秒数、呼吸、声调、重音和歌手负担。只要允许三个以上音节，2,090 的双音节下界就不再适用。

这一步现已由 [中文长提示压缩与 190 段最优覆盖](21_chinese_long_prompt_optimizer.md) 完成其离散音节部分：每段最多 12 个合法拼音音节时，190 段恰好达到新的理论下界，2,556 条 ART 边仍为零遗漏。自然汉字、声调、秒数和呼吸成本仍是后续层，不能由该最优性结论代替。

## 尚未解决

1. 拼音序列目前是可发音训练提示，不保证构成自然词句或汉字文本；
2. 尚未指定声调、每音节时长、力度、重音、呼吸点和两侧静音长度；
3. 尚未把 2/3/4 个录音音高层与 `Sil→unvoiced` 单层例外合入提示；
4. 尚未建立长提示自动切分、forced alignment、人工边界 QA 和录音文件 provenance；
5. 原生 G2PA 一致只能证明输入音素拼写正确，不能替代真实录音训练和宿主渲染验收。
