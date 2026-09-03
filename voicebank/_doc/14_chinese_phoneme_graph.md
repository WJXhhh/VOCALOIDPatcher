# 七库中文音素表、ART 图与音高层覆盖

## 结论

新增 `analyze_reference_graph.py`，使用公开 `ddb-tools` 的 DDI reader 取得对象树，再由本仓库工具独立做集合、图与音高层聚合。七个 V5 中文传统库显示出几乎完全一致的产品级语言规格：

```text
PHDC phonemes        = 62，七库完全相同
voiced / unvoiced    = 42 / 20，七库分类完全相同
STA keys             = 38，七库完全相同
common ART keys      = 2,556，七库全部存在
union ART keys       = 2,559
ART arity            = 全部为 2，没有三音素 key
ART graph nodes      = 60
strong components    = 1
```

这说明 2,556 不是某个产品偶然拥有的样本数，而是一套非常稳定的中文二音素有向边基线。多音高产品增加的是同一 key 下的 ARTp 层数，不是另造不同的音素图。

## 音素表

七库共有 62 项 PHDC：

```text
7 ? @N @U @_n @` AN AU Asp Sil UN a aI a_n ei f i i@U iAN iAU
iE_n iE_r iN iUN i\ i_n i` ia k k_h l m n o p p_h s s\ s` t
t_h ts ts\ ts\_h ts_h ts` ts`_h u u@N u@_n uAN ua uaI ua_n
uei uo x y yE_r y_n y{_n z`
```

20 个 unvoiced：

```text
? Asp Sil f k k_h p p_h s s\ s` t t_h ts ts\ ts\_h ts_h ts` ts`_h x
```

其余 42 项为 voiced。该分类是七个库的交集也是并集，零产品差异。

## STA 持续音集合

38 个 STA key 也在七库完全相同：

```text
7 @N @U @_n @` AN AU UN a aI a_n ei i i@U iAN iAU iE_n iE_r iN
iUN i\ i_n i` ia o u u@N u@_n uAN ua uaI ua_n uei uo y yE_r y_n y{_n
```

STA 层数对每个产品内部完全统一：

| 声库 | 每个 STA 的层数 | STA 总样本 |
| --- | ---: | ---: |
| Luo_Tianyi_Ning | 2 | 76 |
| Luo_Tianyi_Wan | 4 | 152 |
| Yan_He_Mu | 3 | 114 |
| Yuezheng_Ling_You | 3 | 114 |
| Yan_He_Qing | 3 | 114 |
| Luo_Tianyi_Meng | 3 | 114 |
| Yuezheng_Ling_Chi | 4 | 152 |

所以面向完整中文库时，“先决定 2/3/4 层录音音高，再为全部 38 个 STA 保持相同层数”是有直接产品证据的设计，不应逐元音随意选择层数。

## ART 交集与三条可选边

所有七库共同含 2,556 条有向二音素边。并集只多三条：

```text
s   -> ei
t_h -> ei
z`  -> ua
```

三条都只存在于：

```text
Luo_Tianyi_Wan
Yan_He_Mu
Yuezheng_Ling_You
```

其它四库三条全都没有。因此：

- 2,556 条交集可作为保守的“七产品一致”中文图；
- 2,559 条并集可作为更宽松的兼容目标；
- 不能把单个库的 2,556/2,559 差异解释成分析遗漏或版本随机性。

## 图结构

60 个音素至少参与一条 ART；`?` 与 `Asp` 不参与任何 ART。2,556 边交集图满足：

```text
strongly connected components = 1
self edges                    = 34
directed edges whose reverse exists = 1,970
maximum indegree              = 57
maximum outdegree             = 56
```

这里的 1,970 是按有向边计数，包含 self edge。图远不是完整的 `60*60` 笛卡尔积，而是受中文音系/声库选择规则约束的稠密子图；构建器不能只生成所有可能组合，也不能只覆盖普通拼音音节中肉眼可见的声韵母组合。

如果禁止重复任何边，把全部 2,556 边分解成 edge-disjoint trail，图论下界为：

```text
minimum trails = 449
total phoneme tokens across those trails = 2,556 + 449 = 3,005
```

这只是任意音素路径的数学下界，不是可直接交给歌手的中文录音句表。允许少量重复连接边可显著减少 trail 数；自然发音、音节合法性、呼吸长度、同化和音高层又会增加约束。它的用途是给未来句表优化提供可检验下界，防止声称用远少于 2,556 个相邻边就“完整覆盖”。

## `Sil` 边界的非对称性

并集图中：

```text
incoming to Sil = 38
outgoing from Sil = 55
```

所有 `x→Sil` 的源恰好就是 38 个 STA 持续音；辅音不直接收尾到 `Sil`。这解释了为什么当前最小库选择 `a→Sil` 是规范路径，而不是任取一个辅音。

`Sil→x` 覆盖 55 个目标，包含持续音和大多数辅音，但不含 `Sil` 自环；再排除从不参与 ART 的 `?`/`Asp`，仍有少量音素不从静音直接起音。完整录音计划应按实际边集合生成，不能假设每个 PHDC 音素都必须有双向 `Sil` 边。

## ART 音高层的稀疏例外

每个产品绝大多数 ART key 都使用与 STA 相同的 2/3/4 个音高层，但少数 key 只有一个 ARTp：

| 声库 | 主层数 | 满层 ART keys | 单层 ART keys |
| --- | ---: | ---: | ---: |
| Luo_Tianyi_Ning | 2 | 2,543 | 13 |
| Luo_Tianyi_Wan | 4 | 2,544 | 15 |
| Yan_He_Mu | 3 | 2,552 | 7 |
| Yuezheng_Ling_You | 3 | 2,548 | 11 |
| Yan_He_Qing | 3 | 2,540 | 16 |
| Luo_Tianyi_Meng | 3 | 2,539 | 17 |
| Yuezheng_Ling_Chi | 4 | 2,539 | 17 |

所有单层例外无一例外都是：

```text
Sil -> unvoiced consonant
```

具体辅音集合随产品略有变化。没有发现某个普通 voiced→voiced、voiced→unvoiced 或 unvoiced→voiced key 只做中间层数，也没有 2/3/4 层中的部分缺层：ART key 要么只有 1 层，要么具有该产品的全部主层数。

这给录音设计一个明确的优化方向：绝大多数 ART 按主音高层全覆盖；纯静音起始的无声辅音可作为经 QA 决定的单层候选，而不是对任意“看似不重要”的边随意减层。

## 工具与复现边界

`analyze_reference_graph.py` 不捆绑第三方代码，运行时显式传入合法取得的 `ddb-tools` 目录和 `NAME=DDI_PATH`。默认只输出计数、音素小集合、图摘要和最多 100 个非全库 key；`--include-keys` 才输出完整边集合。

该工具只读取 DDI，不读取或导出商业 DDB PCM/FRM2。文档记录的是语言图、计数和聚合规律，不保存商业音频或声学负载。

## 下一步

1. 在 2,556 交集图上加入中文音节合法性和最大录音长度，求允许重复连接边的句表候选。
2. 把 38 STA、主音高层数和 `Sil→unvoiced` 单层例外写入通用训练规格，而不是继续把构建器固定为 `a`。
3. 研究每个 ARTp 层的选择坐标，确认 2/3/4 层的实际音高分布与边界样本层是否同 STA 一致。
