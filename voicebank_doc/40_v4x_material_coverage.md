# V4X 素材覆盖与逐 key 音高坐标

2026-09-13，接续文档38的引用完整性验收和文档39的region研究。
范围仍为当前8套Miku/Rin V4X。本轮只读DDI，不复制音频或声学帧。

## 统计契约

新增 `voicebank/tools/analyze_reference_coverage.py`：

- 目录key与含至少一个part的素材key分别计数；空key不构成边。
- 双音素ART构成有向边；三音素ART独立统计，不拆成两条边。
- 保留完整音素token，包括 `#2`、`#6`、撇号和反斜线，不自动归并风格。
- 每个key保留part数、无音高哨兵数、排序后的pitch1坐标及精确不同坐标数。
- 只把有限且大于 `-3e38` 的pitch1计入音高统计；哨兵素材仍属于有效目录素材。
- 默认仅输出汇总；`--include-inventory`可输出逐key坐标用于本机检查。

这里的“素材”依赖前置引用验收，不等于宿主可合成。脚本自身只读取DDI，
不会重新校验DDB指针。外部解析器仍仅用于已知合法安装库，不作为不可信文件加载器。

## 全量结果

| 库 | STA素材key | 双音素目录key | 双音素素材key | 三音素素材key | 双音素图节点 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Miku Original EVEC | 36 | 1327 | 1327 | 284 | 131 |
| Miku Sweet | 12 | 610 | 610 | 0 | 49 |
| Miku Soft EVEC | 36 | 1326 | 1326 | 284 | 131 |
| Miku Dark | 12 | 610 | 610 | 0 | 49 |
| Miku Solid EVEC | 36 | 1323 | 1323 | 284 | 131 |
| Rin Warm | 12 | 525 | 525 | 0 | 49 |
| Rin Power EVEC | 36 | 1202 | 1195 | 142 | 73 |
| Rin Sweet | 12 | 521 | 521 | 0 | 49 |

合计7437个双音素素材key、994个三音素素材key（跨库相加，不是去重总数）。
Rin Power的7个空key与文档39一致；其他7库无空ART key。
所有STA都有至少一条双音素入边和出边，也都有直接到Sil的双音素材。
所有双音素图节点都可从Sil沿有向边到达。

但“可到达”不是“存在直接录音”，更不是引擎一定可以沿这条路径生成目标音。
四个EVEC库各有24个带 `#2/#6` 后缀的STA token没有直接 `Sil→token` 素材。
例如Original中有 `Sil→a`、`a→a#2`、`a#2→Sil`，没有 `Sil→a#2`。
这为后续入口选择提供结构证据，不能直接作为运行时转写规则。

反方向也不完全连通：三个Miku EVEC库各有58个带风格后缀节点无法沿
双音素边返回Sil；三个Rin库各有 `*#1`、`*#2` 两个这样的节点。
这些节点均没有出现在本轮三音素key中，但尚未核对特殊token的运行时用途，
因此不把它们定性为损坏或应补录的转接。
Miku Sweet/Dark的全部图节点都可以返回Sil。

## 层数相同也不能共享坐标

8库所有STA/ART key内均未发现重复的精确有限pitch1值。
这比“part数”多提供一项证据，但浮点值不同不等于音乐意义上不同的录音层；
尚未采用容差聚类，也不把无音高素材计成录音音高层。

Original的三个4-part key，其pitch1（相对A4的cent，四舍五入到0.01）为：

| key | 排序后的pitch1坐标 |
| --- | --- |
| Sil→a | -915.56, -569.10, -341.61, 87.69 |
| a→a#2 | -819.85, -398.84, -91.38, 300.38 |
| a#2→Sil | -805.87, -367.29, -117.02, 304.03 |

同为4个part，坐标仍明显不同。自建声库规划应保存各key实际坐标，
不能用库级第1/2/3/4层统一替换，更不能直接拿STA坐标覆盖ART。
现有中文库规划器未据此改动；新的分析器独立输出证据。

## 复现与边界

```powershell
python -B voicebank/tools/verify_reference_ddi.py $ddi --ddb-tools $dependency
python -B voicebank/tools/analyze_reference_coverage.py $ddi --ddb-tools $dependency
python -B voicebank/tools/analyze_reference_coverage.py $ddi --ddb-tools $dependency --include-inventory
python -B -m unittest discover -s voicebank/tools -p 'test_*.py' -v
dotnet build VOCALOIDPatcher.sln -c Debug --nologo -v quiet
```

沿用文档38锁定版本的外部解析器。本机全量报告位于
`%TEMP%/v4x_coverage_20260913.json`，含DDI SHA-256与逐key音高坐标，不提交。
新增6项合成测试，累计25项通过；Debug解决方案构建0警告、0错误。
未修改声库、部署或启动编辑器；本轮不需要Ghidra查询。

下一步仍需从DSE消费者核对特殊token、四region和素材选择规则。
本轮完成的是物理素材覆盖和独立坐标盘点，完整运行时可用图仍未闭合。
