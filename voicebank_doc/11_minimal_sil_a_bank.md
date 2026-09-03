# `Sil`/`a` 双向边界库闭环

## 结论

已经从 DSE 原生中间树构建出一个包含两个音素、一个静态元音和两条边界转接的最小传统库：

```text
PHDC voiced:    a
PHDC unvoiced:  Sil
STA:            a
ART:            Sil -> a
ART:            a -> Sil
```

最终 `.ddi/.ddb` 同时通过公开 `DDIModel` 与 VOCALOID6 随附 DSE 的原生加载器。两条 ART 分别引用 DDB 中两个不同的单元，说明它们不仅存在于序列化字节中，也被原生加载器重建为正确的源音素、目标音素和 part 对象。

本轮仍是结构诊断：STA 与两条 ART 都重复使用同一份完全自有的 220 Hz 合成单元。它不能证明真实 `Sil↔a` 的音质，也不能代替静音/元音边界录音和人工标注。

## 新增构建入口

- `tree_harness` 的 `TREE_HARNESS_ADD_SIL_A=1` 会创建 `Sil`、`a` 两项 PHDC，并把 `Sil` 标为 unvoiced、`a` 标为 voiced。
- 与 `TREE_HARNESS_ADD_EMPTY_STAP=1`、`TREE_HARNESS_ADD_EMPTY_REFS=1`、`TREE_HARNESS_ADD_EMPTY_ARTP=1` 组合后，会生成一个 `a` STAp 和 `Sil→a`、`a→Sil` 两个 ARTp 骨架。
- `finalize_sil_a_ddi.py` 检查 PHDC 类型和两条转接的序列化名称，然后把三个 manifest 单元分别绑定到 STA、`Sil→a`、`a→Sil`。
- `TREE_HARNESS_EXPECT_SIL_A=1` 让原生回读同时按名称检查两条转接，并要求两个 ART 使用不同的 SND payload 指针。

旧的 `finalize_minimal_articulation_ddi.py` 已把“在指定 ARTp 位置插入缓存”的逻辑拆成可复用函数；单 `a→a` 输出与此前文件 SHA-256 完全一致，证明重构没有改变已有格式。

## PHDC 音素项

当前版本序列化的每个 PHDC 音素项为 31 字节：18 字节定长名称区、其余标量/存在位，以及末字节的 voiced 类型。DSE writer 与公开解析器的联合结果为：

```text
末字节 0 -> voiced
末字节 1 -> unvoiced
```

双音素骨架的 PHDC 块中，`Sil` 项末字节为 1，`a` 项为 0。公开解析器读回：

```python
{'phoneme': {'voiced': ['a'], 'unvoiced': ['Sil']}, ...}
```

这一步很重要：仅在 ART 树中写一个名为 `Sil` 的字符串不够，PHDC 的类型决定它在音素字典中的类别。

## 两条 ART 的序列化顺序

原始 DSE 骨架共 1,244 字节，两个 ARTp magic 分别位于：

```text
0x02bb = Sil -> a
0x038a = a -> Sil
```

每个 ARTp 子树结束后依次出现 length-prefixed 的 part、目标和源名称：

```text
"default", target, source
```

收尾器在插入任何变长缓存前验证该名称尾部，并从文件后方的 ARTp 向前插入。这样前一个插入不会使尚未处理的原始 ARTp 位置失效，也不会依赖字典碰巧保持某种顺序。

## 最终文件与指针

诊断 DDB 由三个 636,058 字节的自有单元顺序合并：

```text
unit 0: STA a
unit 1: ART Sil -> a
unit 2: ART a -> Sil
```

最终结果：

```text
DDB bytes = 1,908,174
DDI bytes = 2,926
frames per unit = 52
PCM values per unit = 15,360
```

公开解析器得到两个 ART 键，各自 52 个 EpR：

```text
Sil a
a Sil
```

两条转接的 DDB 指针为：

```text
Sil -> a payload/core = 1,241,396 / 1,243,444
a -> Sil payload/core = 1,877,454 / 1,879,502
```

两组 core 都严格等于 payload 加 2,048 字节；两条转接的 payload 不相同。公开解析器还分别恢复 SND source `0x6c`、EpR source `0x7885` 和两组 `[0,26]`、`[26,52]` alignment。

## DSE 原生回读

关键结果：

```text
load.result=0
root.authenticated=1
stationary.count=1
phoneme.count=1
articulation.count=2

sil_to_a.target_count=1
sil_to_a.part_count=1
sil_to_a.frame_count=52
sil_to_a.snd_payload_pointer=1241396
sil_to_a.snd_core_pointer=1243444
sil_to_a.alignment_count=2

a_to_sil.target_count=1
a_to_sil.part_count=1
a_to_sil.frame_count=52
a_to_sil.snd_payload_pointer=1877454
a_to_sil.snd_core_pointer=1879502
a_to_sil.alignment_count=2

load.valid=True
```

因此 M3 的“最小 PHDC/TDB/DBV/STA/ART 拓扑”在结构层已经完成。剩下的 M3 硬缺口是用真实边界录音替换两个诊断 ART，并进行隔离宿主渲染，而不是继续增加占位块。

## 真实训练输入还缺什么

真实 `Sil↔a` 至少要为每条转接提供：

1. 自有 44.1 kHz PCM16 录音；
2. 源/目标音素的 outer 分界 frame；
3. 两侧各自的稳定 inner 区间；
4. 录音参考 F0 或可靠的逐帧 F0；
5. 静音区应生成 unvoiced frame、元音区应生成 voiced 主帧的 region 约束。

`finalize_sil_a_ddi.py` 现已通过四个 `--*-source-inner START:END` / `--*-target-inner START:END` 选项接受完整 inner 标注；两条转接使用不同裁剪区间的测试已经由 DSE 原生加载器读回。DRS 的单边界 voiced/unvoiced 约束也已通过 external F0 envelope 的 0/目标值阶跃跑通，见 `12_articulation_annotation_injection.md`。剩余缺口是把这些标注用于真实自有人声与静音 PCM。
