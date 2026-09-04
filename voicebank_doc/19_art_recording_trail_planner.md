# 中文 ART 录音 trail/clip 规划器

## 目的

`plan_art_recording_trails.py` 把 `analyze_reference_graph.py --include-keys` 输出的 ART 交集或并集变成可验证的录音音素链候选。它解决的是“怎样保证每条必需二音素边至少出现在某个片段中”，不是直接生成自然中文歌词。

输出同时保留三层结果：

1. 不重复任何边的最少 trail 分解，作为数学下界；
2. 用图内最短合法路径连接这些 trails 后的单条连续 route；
3. 按最大音素数切分、相邻片段重叠一个边界音素的录音 clips。

每条必需 ART 边都带有 `clip + transition_index + role` 反查记录，可以从目标 DDI 单元追溯到具体录音片段位置。

## 算法与可验证不变量

### 最少 edge-disjoint trails

对每个节点计算 `outdegree - indegree`。交集图的正差总和为 449，因此任何不重复边的 trail cover 至少需要 449 条。规划器把负差节点按确定顺序连到正差节点，加入 449 条只用于求解的虚拟边，使图平衡；随后运行 Hierholzer Euler traversal，并在每条虚拟边处切开。

输出前强制验证：

- trail 数等于 `max(1, Σ max(0, out-in))`；
- 每条 trail 内相邻 token 连续；
- 2,556 条真实边各出现且只出现一次；
- 不允许输出输入图以外的连接。

这给出已在上一轮统计中预测的精确下界：

```text
minimum trails = 449
minimum tokens = 2,556 + 449 = 3,005
```

### 允许重复合法连接边

449 条 trail 仍不适合逐条录音。规划器从最长 trail 开始，每轮从当前终点做有向 BFS，贪心选择距离最近的下一条 trail 起点；连接路径的每一条边也必须来自同一 ART 图。原本覆盖必需边的 transition 标为 `required`，额外连接标为 `connector_repeat`。

这是确定性的可复现基线，不宣称是全局最短 directed postman 解。它的价值是给后续加入自然音节、呼吸和句长代价之前提供一个零漏边、零非法边的上界。

### 录音片段切分

默认每个 clip 最多 12 个音素 token。切分时下一个 clip 从上一个 clip 的最后一个 token 开始，因此跨切分点的 transition 不会丢失。规划器再次从 clips 重建全部相邻边并验证：

- 每个 clip 有 2–12 个 token；
- 所有 transition 都属于输入 ART 图；
- 2,556 条必需边全部可追溯；
- 边界重叠只重复 token，不制造新的跨片段边。

## 七库交集实测

2026-09-04 直接读取七个已安装 V5 中文 DDI、由公开解析器取得 key 后，完整交集结果为：

| 指标 | 数量 |
| --- | ---: |
| 必需 ART 边 | 2,556 |
| 最少不重复 trails | 449 |
| 不重复 trails 总 token | 3,005 |
| 连接后 route transitions | 3,053 |
| 其中重复连接 transitions | 497 |
| 连接后 route tokens | 3,054 |
| 12-token clips | 278 |
| 含片段边界重叠的实际 token 总数 | 3,331 |
| 有 trace 的唯一必需边 | 2,556 |

排序交集边清单的规范化 SHA-256 为 `5763b8765ab85724d45a099388ccfafd6f44de851fb32c25e09867d8b089e7e8`，用于确认后续计划是否仍来自同一张图。

并集 2,559 边也通过同一整图验证：最少 trails 为 446，重复连接 494，仍得到 3,053 个 route transitions 和 278 个 clips。这种总数巧合不表示两张图的具体路径相同。

实际 JSON round-trip 输出为约 875 KB；2,556 条 trace、278 个 clips 和 clip 长度约束全部通过独立断言。另用一个不平衡的六边小图验证了虚拟边切分、连接、三 token 分片和 trace 重建。

## 使用

先让图分析器输出完整 key，再通过 stdin 直接交给规划器，避免在仓库保存商业库派生的完整边清单：

```powershell
$analysisArgs = @(
  '--ddb-tools', 'C:\path\to\ddb-tools',
  '--include-keys',
  '--bank', 'BankA=C:\path\to\BankA.ddi',
  '--bank', 'BankB=C:\path\to\BankB.ddi'
)

python -B voicebank\tools\analyze_reference_graph.py @analysisArgs |
  python -B voicebank\tools\plan_art_recording_trails.py `
    - E:\VoicebankResearch\art_recording_plan.json `
    --art-set intersection --max-tokens 12
```

只验证计数和覆盖、不写计划文件：

```powershell
python -B voicebank\tools\analyze_reference_graph.py @analysisArgs |
  python -B voicebank\tools\plan_art_recording_trails.py `
    - ignored.json --max-tokens 12 --dry-run
```

如果中间 JSON 没有 `aggregate.art.intersection/union`，工具会要求重新使用 `--include-keys`；输出文件已存在时拒绝覆盖。

## 输出结构

- `source.edge_sha256`：排序边清单的规范化 SHA-256，用于确认计划输入；
- `inventory`：图节点、voiced/unvoiced 分类及独立 STA prompts；
- `minimum_trails`：严格不重复边的 449 条基线；
- `joined_trail_order`：贪心连接顺序；
- `recording_clips`：token、transition role 和 route 全局位置；
- `required_edge_trace`：每条必需边在一个或多个 clip 中的全部出现位置；
- `limitations`：工具尚未满足的录音语义。

## 不能据此声称已经有中文句表

每一对相邻音素都来自实际中文 ART key，但三音素以上的任意拼接不一定是合法普通话音节、可读汉字或自然歌词。当前 `12 tokens` 也只是结构参数，不代表秒数、呼吸长度或歌手负担。

后续原生 G2PA 研究已证明全部边都能落入合法拼音音节或音节边界，并给出 2,090 条双音节首尾静音提示，见 [原生中文 G2PA 音节清单与 ART 提示覆盖](20_chinese_g2pa_prompt_cover.md)。仍需要继续加入：

1. 把已验证的双音节 witness 合并为更长且自然的拼音/汉字提示；
2. 为长提示建立音节边界、呼吸和可逆切分约束；
3. 每个音素的目标时长、重音、力度、呼吸和最小稳定区；
4. 2/3/4 音高层复制策略与 `Sil→unvoiced` 单层例外；
5. 录音文件、切分标注、STA/ART/ARTp 和最终 DDI key 的一对一 provenance manifest。

因此 M4 的“图覆盖压缩”已经有严格基线，但“可直接交给歌手的中文录音脚本”仍未完成。
