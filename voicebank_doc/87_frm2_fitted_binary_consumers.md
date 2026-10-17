# 独立拟合、普通 FRM2 保存与两个消费者联验

接续 [86](86_frm2_independent_initial_and_residual.md)。本轮将源 PCM 预测谐波与 E7、
独立 A/B/C / 两组共振 / 残差，以及已有相位状态模型，写入同一普通 FRM2，
再通过原生 DSE reader 与幅度、相位消费者验证。

## 实际连接与数据边界

`verify_frm2_pitch_state.verify` 可返回全部最终帧的预测 F0 和预测谐波，
包括无声帧，供相位状态机保持完整的时间序列。原生输出只作断言参考。
相位配置明确设为 derivative=0、classification=None、hop=256；
这是实验选择的控制输入，不宣称恢复了商业声库的分析特征或制作预设。
无声帧仍推进相位状态；只对有声 E7 帧执行拟合和普通帧保存。

普通磁盘 mask 显式采用 `0x2000e00207`，不是把 DRS 中间对象 mask 原样搬到文件。
E7 与 gain 作为拟合输入，不作为该磁盘布局中的额外字段。
两个 ENV 的 flags 分别是 bit37 `0x01820002`、bit23 `0x00960002`。
保存时横纵坐标按磁盘 float32 表示量化；共振三元组保持 double。
谐波槽数沿用实际受测跟踪输出，不能据此宣称采用了构建器的 350 槽制作预设。

读取链为 `0x106e20` → 普通帧 reader `0x1363a0`。
验证块尾游标、mask、全部谐波数组、A/B/C、主/第二列表和两个 ENV。
F0 在磁盘上为 Hz；该 reader 转为相对 440 Hz 的 cents 并设置 bit31。

幅度消费直接对读回对象调用 `0x12da40`，以当前 DLL 的查表资产作显式依赖，
独立计算二维插值响应、A/B/C、bit23 规则网格查值与加法舍入顺序。
使用 cutoff=0、全部共振路径，覆盖 201 个均匀频率和每个正谐波频率。
它不是以 DRS 解析响应代替 DSE 查表响应，也不是以“接近原始 PCM”代替消费算法对照。

相位消费调用 `0x16fa70`，复用已经验证的映射、四点插值、周期归约和相位加回参考公式。
bit37 来自有历史的相位模型，为非零包络；没有使用零占位作为最终联验结果。

## 本轮发现的节点顺序问题

实际跟踪槽位不保证按频率排序。330 Hz 样例中，某帧相邻槽频率为
1136.6024、1320.0422、990.0046 Hz。
相位累积仍须按槽位顺序计算，但不能按该顺序直接序列化 ENV 节点。
直接写入会使原生消费者在 3 个槽上偏离，最大约 4.0584 rad。

安装 ENV 时恢复原生插入语义：坐标限制到 0..1，同坐标后写覆盖前写，最后按坐标排序。
预测侧自己完成转换；参考侧对原始节点逐个调用原生 ENV 插入，比较全部最终节点。
这一步只调整容器表示，不对谐波槽或相位状态计算做重排。
原有 readback 工具增加了错误帧/槽位诊断，容差没有放宽。

## 验证与复现

首批 9 组源信号、27 帧的字段、幅度消费均零误差，非零 bit37 相位消费也通过。

完整 36 组源信号中，25 组产生有声 E7，共 479 帧完成联验：

| 检查 | 数量 | 最大误差 |
| --- | ---: | ---: |
| 读回字段（含时间，不含单独列出的F0） | 993,885 | 0 |
| F0 Hz→cents | 479 | 0 |
| 幅度消费频率 | 143,201 | 0 |
| 非零相位ENV点 | 47,880 | 全部逐点读回一致 |
| 相位消费槽 | 52,690 | 0 |
| 原生ENV安装节点 | 47,880 | 0 |

199 个相位帧需要对槽位顺序产生的节点排序。所有 479 个已保存帧又独立读回一次，
补验类型、时间和 F0，不依赖重新拟合。数字证据见
[87_fitted_binary_consumers.json](evidence/87_fitted_binary_consumers.json)。

```powershell
python voicebank/tools/verify_frm2_fitted_roundtrip.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --all-frames --frequencies 50 75 110 147 196 220 294 330 440 660 880 1000 --output '../VOCALOIDPatcher-research-work/frm2/fitted_roundtrip_87_full.json'
python voicebank/tools/verify_frm2_fitted_roundtrip.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --read-existing '../VOCALOIDPatcher-research-work/frm2/fitted_roundtrip_87_full.json' --output '../VOCALOIDPatcher-research-work/frm2/fitted_roundtrip_87_readback.json'
```

目标 DSE.dll 元数据再次核实，SHA-256：
`7a4965bd377dbc2b08710e807ae1345b67d9fff6579ca0c01a769f744366f5b5`。
仅生成自有输入派生的帧流，放在项目旁研究目录；没有复制商业帧、使用 Windows Temp、
修改安装文件或启动编辑器。此次修改仅为 Python 工具和研究文档。
三个改动工具通过 Python 语法检查，未执行无关的 C# 解决方案构建。

## 尚不能外推的范围

本轮验证的是显式实验配置的文件与数值接口，不是完整声库打包或真人音质验收。
相位控制特征的自动衔接、商业预设、其它分析模式与纯 Python FFT 的敏感反例仍保留。
源链的原生调度、FFT 后端、FIR 资产，以及消费者查表资产仍是明确依赖。
不能把这些限制重新称为 A/B/C、ENV 或幅度单位未知；也不能因为本轮文件能读就声称全部制作流程已恢复。
