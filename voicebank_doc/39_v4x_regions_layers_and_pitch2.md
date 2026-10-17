# V4X region、非统一音高层与 pitch2（2026-09-12）

## 进度

继文档 38 的引用验收，本轮完成当前 8 库 ART region 时间规则、key/层数分布，
并把双音素 ART 的 pitch2 选区问题收敛到一个全样本吻合的候选算法。
后续八库逐位复核、原生平均函数对照及计划式构建接线见[记录103](103_v4x_pitch2_rule_and_builder.md)；该记录区分可复现的 V4X 数值规则和仍未知的原制作软件写入调用点。

新增只读 `voicebank/tools/analyze_reference_regions.py`，消费与文档 38 相同的
外部 DDI 解析模块。先运行引用审计，再做本轮统计。结果不导出 PCM/声学帧负载。

## ART 时间按 region 归零

8 库共 23,881 个 ART 样本、54,294 个 alignment 区间、907,928 个 FRM2 帧：

```text
对每个 outer 区间 [start, end)：
FRM2[i].time_seconds * sample_rate == (i - start) * 256
```

容差设为 `1e-5 samples`，零反例；最大浮点误差 `2.1828e-11 samples`。
所以一个 ART 的 EpR 列表虽然把多个 region 拼接在一起，内部时间仍是各自局部时间。
不能由列表末帧时间求整个样本时长，也不能要求跨 region 的相邻帧时间持续递增。

例如 Original 的 `a→a` 样本有 32 帧，outer 为 `[0,15)`、`[15,32)`；
第一段时间为 0–3584 samples，第二段重新从 0 到 4096 samples。

## 四组 alignment 的意义边界

当前库的分布完全一致：

- 20,615 个双音素 ART 样本均为 2 组 alignment；
- 3,266 个三音素 ART 样本均为 4 组 alignment；没有三组的样本。

例如 `n → ^n#2 → a` 的四组 outer 为 `[0,5)`、`[5,6)`、`[6,7)`、`[7,47)`。
3,003/3,266 个四组样本的两个中间 region 都只有一帧，但另外 263 个有其它长度，
最长观察为 8/8 帧。不能将中间两组当作固定占位，也不能按三音素平均分帧。

Ghidra 目标与文档 37 相同，DSE SHA-256：
`7a4965bd377dbc2b08710e807ae1345b67d9fff6579ca0c01a769f744366f5b5`。
只读核对发现：

- 紧凑读取器 `0x180110d50` 在版本 >=3 时读取显式组数，每组四个整数，
  不硬编码两组。
- 中间态缓存建立函数 `0x18010abb0` 逐项遍历 collection 的内部 region
  （索引 1 至总数减2），累积帧数形成 outer 区间；若 region 有显式 inner 标记，
  将其局部 inner 转成累计帧坐标，否则 inner 等于 outer。
- 随后按同一顺序拼接各 region 的帧偏移表。

这说明 alignment 直接对应分析 region，并非一个音素名必对应一组。
中间两组具体分别承担什么合成角色、运行时怎样消费它们，仍需继续追踪；
不把四组擅自命名为“四音素”或两个固定时长的起音阶段。

2026-09-13补充：[文档41](41_v4x_runtime_region_consumers.md)已定位按region奇偶
处理和outer/inner时间映射的运行时路径；各组完整阶段名称与上游选择规则仍待确认。

## 8 个短余量例外都是呼吸/静音转接

文档 38 的例外已经定位到 key：

| 库 | key | PCM余量（samples） | 核心偏移（bytes） |
| --- | --- | ---: | ---: |
| Original | Sil→br3 | 1536 | 1024 |
| Original | Sil→br5 | 1280 | 512 |
| Original | br1→Sil | 1792 | 2048 |
| Original | br2→Sil | 1536 | 2048 |
| Original | br3→Sil | 1280 | 2048 |
| Original | br4→Sil | 1024 | 2048 |
| Original | br5→Sil | 1536 | 2048 |
| Dark | br5→Sil | 1024 | 2048 |

全部 pitch1/pitch2 均为 `-FLT_MAX` 无音高哨兵，帧时间也全部满足按 region 归零。
因此不是引用错位或把局部时间误判为损坏。

`0x18010abb0` 同时说明核心指针的来源：

```text
core_pointer = PCM_payload_pointer + 2 * int(sample_rate * region1_time + 0.5)
```

其中 `0.5` 已从同哈希安装二进制的 RVA `0x5bf540` 只读核实。
这条中间态路径并不硬编码“加2048字节”；紧凑 DDI 则直接读入已经保存的两个指针。
这能解释为什么不能把固定偏移当作格式约束，但不能倒推原始呼吸录音为何裁短，
也尚未验证短余量在合成时怎样处理分析窗边缘。

## 层数按 key 保存，不能用全库一个整数表示

| 库 | STA key 的层数分布 | 双音素ART key数 | 三音素ART key数 |
| --- | --- | ---: | ---: |
| Original | 36个×4层 | 1327 | 284 |
| Sweet | 12个×4层 | 610 | 0 |
| Soft | 36个×4层 | 1326 | 284 |
| Dark | 12个×4层 | 610 | 0 |
| Solid | 24个×3层，12个×4层 | 1323 | 284 |
| Rin Warm | 12个×3层 | 525 | 0 |
| Rin Power | 32个×3层，4个×4层 | 1202 | 142 |
| Rin Sweet | 12个×3层 | 521 | 0 |

这里“层数”是每个 key 的 part 数，尚未证明每个 part 都代表独立且不同的录音音高。
三音素 ART：Original/Soft 各有 142 个3-part key和142个4-part key；
Solid 的284个key均3-part；Rin Power的142个key均3-part。
Sweet/Dark 虽然 STA 全为4-part，但双音素ART最多3-part。

Solid 的 STA 差异还与后缀有关：plain 与 `#6` 各12个key、每key3-part；
`#2` 的12个key各4-part。按 pitch1 排序后的组中位数为：

- plain/#6：约 `-799.61 / -397.57 / -99.58 cent`；
- #2：约 `-914.29 / -528.97 / -211.73 / 272.98 cent`。

上述只是样本选择坐标的统计，不解释为原始录音脚本或推荐歌手音域。
旧 `analyze_reference_layers.py` 的统一 STA 层数要求适用于原中文研究，
本轮使用新工具按每个 key/part 数分组，没有为兼容 V4X 破坏旧规划器的契约。

Rin Power 还有7个目录key没有任何 ARTp：
`a→a#3`、`M→M#3`、`e→e#3`、`o→o#3`、`m→b'`、`N→k'`、`N→g'`。
上表1202包括这些空key，实际有素材的双音素key为1195。
统计可用转接图时必须区分“目录节点存在”和“至少一个素材part”，更不能仅凭PHDC存在判可用。

## pitch2：双音素 ART 的候选选区全部吻合

对每帧正F0先换算音分：`1200 * log2(F0/440)`，再取算术平均。
不是先对Hz求平均再换算。

| 样本类别 | 有效pitch2样本数 | 候选取帧规则 | 最大误差（cent） |
| --- | ---: | --- | ---: |
| STA | 688 | 全部正F0帧 | 0.00002939 |
| 双音素ART | 19176 | 只取PHDC voiced音素对应outer区间中的正F0帧 | 0.00005832 |
| 三音素ART | 3266 | 全部正F0帧 | 0.00005832 |

其余1439个双音素ART的pitch2为无音高哨兵，不参与均值拟合。
三音素key有四组region，本轮没有强行将PHDC音素与region一一配对。

如果双音素ART只过滤 `F0 > 0` 而不按PHDC区间选择，只有17784/19176能在
0.01 cent内吻合，1392个不吻合。最明显反例是Sweet的 `e→p\\`：全帧均值与
pitch2相差约490.87 cent；排除无声音素区间后吻合。
因此“F0数值非零”不能自动推出“该帧应参与样本参考音高计算”。
仅取inner稳定区的候选也没有达到全样本吻合，不能以inner代替PHDC有声选区。

这是对当前8库的全量数值证据，已把旧“ART有声选区未知”大幅收敛。
尚未找到并验证制作端计算pitch2的具体函数，亦未重跑历史V5实体样本；
因此仍将其标为强候选，不据此改写未知声库的元数据。

2026-09-24 补证：[记录103](103_v4x_pitch2_rule_and_builder.md)将该候选扩大到八库所有 STA/ART part，全部有效保存值与独立模型写出的 float32 逐位相同；还以自生成帧核对了 DSE 的集合均值入口。计划式构建器现从最终 F0 单独计算 `pitch2`，不再把标注的层坐标复制过去。原制作软件最终写入点仍不在上述证据内。

## 复现与下一步

```powershell
python -B voicebank/tools/verify_reference_ddi.py $ddi --ddb-tools $dependency
python -B voicebank/tools/analyze_reference_regions.py $ddi --ddb-tools $dependency
python -B -m unittest discover -s voicebank/tools -p 'test_*.py' -v
dotnet build VOCALOIDPatcher.sln -c Debug --nologo -v quiet
```

本机聚合输出 `%TEMP%/v4x_regions_20260912.json` 不提交。
新增7项合成测试，与前两轮共19项通过；完整Debug构建0警告、0错误。
本轮未写入声库、未启动/部署编辑器、未修改Ghidra项目。

下一步优先：

1. 从DSE消费者继续核对四region与短余量的使用方式；
2. 查pitch2计算/更新入口，用自有受控帧验证上述PHDC选区规则；
3. 图统计明确剔除空key，并按音素/风格区分层坐标；
4. 再推进普通帧数值标度、ENV/第二共振与VQM算法。
