# 计划驱动的多音素、多层 DDI 构建

## 结论

录音单元到传统声库容器的局部工程链已经闭合：

```text
recording unit manifest + DRS analysis manifest
  -> 流式多单元 DDB
  -> 完整 62-PHDC + 当前已有 STA/ART 的显式 tree plan
  -> DSE 原生 skeleton
  -> 按 DDB 绝对偏移注入全部 STAp/ARTp 元数据
  -> DBSe stem digest + compact normalization
  -> 公开 ddb-tools 独立解析
  -> DSE 原生 loader 回读同 stem DDI/DDB
```

15-unit 自有合成局部样例包含 62 个 PHDC、1 个 STA part 和 14 个不同 ART edge/part。公开解析器恢复 14/14 条边；DSE 6.13.0.1 返回 `load.result=0`、`root.authenticated=1`、`load.valid=True`。两次独立上游分析、DDB、tree-plan、原生 skeleton 和 finalization 最终得到逐字节相同的 DDI。

这证明的是多音素、多边和多层容器生成方式，不是完整中文覆盖、真人声学质量、stock Editor 产品许可证或最终演唱质量。

## 显式 tree plan

`plan_ddi_tree.py` 同时消费七库公共图和流式 DDB manifest。计划固定：

- 全部 62 个 PHDC 名称与 voiced/unvoiced 标志；
- 当前 DDB 中已有的 STA 音素及其 unit 顺序；
- 当前 DDB 中已有的 ART source/target、目标 PHDC index 及其 unit 顺序；
- 每个 ARTp 的虚拟源流 `SND`/`EpR` 位置；
- 扁平化的 STA/ART 注入顺序。

partial DDB 仍保留完整 PHDC，但只建立当前存在的 STA/ART。这使局部回归能检查真实索引和空 source 节点，又不会把 15 个单元冒充 7,782-unit 三层完整库。

15-unit 计划的规范哈希为：

```text
a302efc43e7c88501ccb6a2ae8e8c3dde684506b61ee7b2e64ab4065dbd90fa1
```

两份计划文件的完整 SHA 不同，因为各自记录不同上游 manifest 文件 SHA；规范内容哈希相同。

## STAu/ARTu index 语义

DSE 构造器能正确生成对象层级和名称，但当前已知对象字段写法不能可靠控制序列化后的 unit index。若所有 `ARTu` 都保留默认 index 0，公开解析器会在同 source 下用字典键互相覆盖：14 条物理边只能看到 9 条。

商业 DDI 的只读交叉检查给出了关键约束：

- ART source index 覆盖 PHDC index 空间；
- 同一个 source 内的 ARTu index 是 target 映射；
- STA index 使用公共 STA inventory 的序号；
- ARTp 字典键不是层号，而是该 part 在虚拟单元流中的 SND source offset。

因此 `finalize_planned_ddi.py` 在注入前严格验证 skeleton header/count/order，再把序列化 `STAu+16` 写为 `stationary_index`、`ARTu+16` 写为 target PHDC index。修正后公开解析器看到完整 14/14 edge；DSE 原生 loader 同样按名称恢复 62 个 source、14 个 target 和 14 个 part。

这里采用的是已由参考文件、公开解析器和原生 loader 三方交叉验证的序列化字段。未再猜测未闭合的 DSE 对象内存字段。

## 多层 part 的唯一键

同一 STA 音素的多个 STAp 由 DSE `AddStationaryPart` 依次命名为字符串 `"0"`、`"1"`……。同一 ART edge 的多个 ARTp 不能都使用 `0x6c`；其虚拟源位置按上一单元的物理布局递推：

```text
first_snd_source = 0x6c
epr_source       = snd_source + snd_chunk_size + 7
next_snd_source  = epr_source + total_frm2_bytes
```

两层结构回归把同一份完全自有合成 STA/ART 数据复制成 L01/L02，只用于验证层级与索引。结果为：

```text
STA 7 part keys              ["0", "1"]
ART @_n -> @_n part keys     [108, 916853]
ART EpR source offsets       [43141, 959886]
```

公开解析器验证 2 个 STA parts 和同边 2 个 ART parts 的全部 frame offsets、SND payload/core pointers、采样率、duration 与两组 alignment；DSE 原生 loader 返回 `load.valid=True`。这排除了“多层只是重复挂相同 part key”的错误实现。

## 通用 finalizer 与独立 verifier

`finalize_planned_ddi.py` 会在写文件前：

1. 验证 tree plan 和 DDB manifest 的规范哈希及完整 provenance 绑定；
2. 验证每个 unit 的 kind、phoneme/edge、PHDC target index、层内 source offset 递推和 part order；
3. 验证 skeleton 的 PHDC、STAu/ARTu/STAp/ARTp 数量、名称和顺序；
4. 从文件末端向前注入可变长度 metadata，避免早期插入使后续绝对位置漂移；
5. 对 STA 写所有 EpR、采样率、PCM count 和 SND core pointer；
6. 对 ART 写所有 EpR、payload/core pointers 和两组 outer/inner alignment；
7. 统一加入 DBSe digest 并执行 compact normalization；
8. 以临时文件、`fsync` 和原子替换写出，不覆盖已有输出。

新增 `verify_planned_ddi.py` 把公开 `ddb-tools` 当作独立消费者，不复用 finalizer 的二进制解析。它逐项比较 PHDC、STA/ART index、part key、frame offsets、SND pointers、duration 和 alignment 与 plan/DDB manifest。对 partial 输入，验证成功仍以退出码 3 表示“结构成功但覆盖不完整”。

构建 manifest 的 `native_loader_valid` 故意保持 `false`：finalizer 不把自己尚未执行的外部 DSE 验证写成成功。公开 verifier 另写 verification report；原生 loader 结果保留在回归记录中。

## 15-unit 回归结果

```text
PHDC                           62 (42 voiced + 20 unvoiced)
STA groups / parts              1 / 1
ART edges / parts              14 / 14
DDB bytes              12,656,830
DDB SHA-256
72dfb6190191252499e866613e9449960df56afa32ee7d5410b06a8b7526a4cb

DDI bytes                  24,555
DDI SHA-256
5f601025d6aae5c5b5269bb12118500a6692e90792ca56fad32960c1008377c7

public parser valid             true
DSE load result                    0
DSE root authenticated             1
DSE planned load valid          true
```

两次原生 `.tree` skeleton 的 SHA 不同，但 finalizer 规范化和注入后的 DDI 逐字节相同；两份 DDB 也逐字节相同。原生 skeleton 中存在的非语义差异没有污染最终产物。

旧的一 STA/一 ART finalizer 在拆出通用插入函数后也重新执行了回归：130-frame STA、76-frame ART、两个 alignment、全部 SND/EpR 指针由 DSE 回读一致，`load.valid=True`。

## 使用顺序

```powershell
python -B voicebank\tools\plan_ddi_tree.py `
  E:\VoicebankResearch\seven_bank_graph.json `
  E:\VoicebankResearch\ddb\ddb_manifest.json `
  E:\VoicebankResearch\tree_plan.json

dotnet voicebank\tools\tree_harness\bin\Release\net8.0-windows\TreeHarness.dll `
  'C:\Program Files\VOCALOID6\Editor\DSE.dll' `
  E:\VoicebankResearch\tree ResearchVoice `
  --plan E:\VoicebankResearch\tree_plan.json

python -B voicebank\tools\finalize_planned_ddi.py `
  E:\VoicebankResearch\tree_plan.json `
  E:\VoicebankResearch\ddb\ddb_manifest.json `
  E:\VoicebankResearch\tree\ResearchVoice.tree `
  E:\VoicebankResearch\tree\ResearchVoice.ddb `
  E:\VoicebankResearch\tree\ResearchVoice.ddi

python -B voicebank\tools\verify_planned_ddi.py `
  E:\VoicebankResearch\tree_plan.json `
  E:\VoicebankResearch\ddb\ddb_manifest.json `
  E:\VoicebankResearch\tree\ResearchVoice.ddi `
  E:\VoicebankResearch\tree\ResearchVoice.ddb `
  E:\VoicebankResearch\ddb-tools
```

原生回读需要在同目录放置同 stem `.ddi/.ddb`，再设置 `TREE_HARNESS_LOAD_EXISTING=1` 并传入相同 `--plan`。它只验证 DSE 反序列化对象与通用数据契约，不代表 Editor 产品授权。

## 尚未完成

- 684 条真人录音、人工 boundary/stability QA 与 7,782 个三层单元的实际全量分析；
- 完整 DDI 的内存/时间峰值和失败恢复压力测试；
- stock Editor 的隔离注册、许可证结果、音符建立与最终渲染；
- 音域、长短音、连音、辅音、跨层选择和参数极值的试听矩阵；
- Yamaha 对新第三方传统 DSE 声库的正式产品身份、签发、QA 和发行条件。

因此“容器构建器”已从最小诊断提升到计划驱动的多音素、多层闭环，但目标仍不能标记完成。
