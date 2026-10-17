# 跨帧短轨迹删除、缺口补齐与相位累计

2026-09-14，目标仍为文档57所固定SHA256的DSE.dll。
本轮独立实现`RVA f1a80`的普通模式队列修补，并把它接回`ef9f0`外层验证。
所有输入均为自有数值序列，无商业声库帧或反编译源码落盘。

## 验证结果

| 验证层 | 覆盖 | 结果 |
| --- | --- | --- |
| 队列修补入口 | 432段，每段16帧；100/200帧每秒；三档短轨迹与缺口阈值；六类占用图案；首帧时间0/0.1；含空帧 | 55,026个数值误差为0 |
| 实际分支命中 | 删除1,665个短轨迹槽，补齐1,026个缺口槽，为282个空帧分配数组 | 全部与独立结果一致 |
| 外层整流程 | 72条12帧序列、864次调用；固定索引0对照和增长队列各36条 | 71,280个轨迹数值误差为0 |
| 历史输出核对 | 每次增长队列推进后重新比较全部历史帧 | 34,020个频率/幅度/相位坐标完全一致 |
| 整流程实际修补 | 825个补槽操作；此批无短轨迹删除 | 短轨迹删除由上一独立队列矩阵覆盖 |

独立算法：`voicebank/tools/frm2_track_repair.py`。
入口验证：`verify_frm2_track_repair.py`。
整流程验证：扩展后的`verify_frm2_outer_tracking.py`。
证据：[60_frm2_track_repair.json](evidence/60_frm2_track_repair.json)。

## 短轨迹规则

- 队列索引小于2时，普通模式不做修补。
- 有效幅值须严格大于cfg03且不等于10000；10000为无效槽哨兵。
- 对已结束的有效连续段，以`float32(length / frameRate) < cfg06`判断短段。
  阈值相等时保留；队尾仍未结束的有效段不删。
- 删除将频率置0、幅值置10000，**不清相位**。
- 首帧时间大于0且首槽有效时，该初始连续段豁免，遇到第一个无效槽后解除。
- 遇到整帧无数组会终止当前短段计数，但不按普通无效槽路径删除前段。

## 缺口与相位规则

只有左右均有有效槽的内部缺口参与插值；前导和尾随缺口不补。
条件为`float32(gapLength / frameRate) < cfg07`，同样是严格比较。

频率与幅值的每步增量取左右端点差除以`gapLength+1`，以float32保存。
填充时在**前一个已填结果**上逐次加增量，不能改成一次计算的双端点加权公式。

补槽相位根据前一槽和当前补槽的平均频率积分：

```text
phase = float32(float32(previousFrequency + frequency) * 2π * 0.5 / frameRate + previousPhase)
phase = float32(phase - trunc(phase / 2π) * 2π)
```

使用向零截断，所以负相位可保持负值，并非强制归入`[0,2π)`。
右端已有相位不作为缺口相位的插值终点；不把这段算法误写成两端相位线性插值。

当补槽所在帧完全没有谐波数组，原生按队列最大槽数分配：
所有频率/相位先置0，幅值置10000，再写当前槽。
验证使用null输入指针让原生自行分配，未让原生释放Python管理的内存。

## 整流程完成边界

文档57–60合计已覆盖普通模式的候选、归属、整帧推进、初始化/反馈/重置，
以及本轮验证范围内的历史缺口修补。增长队列试验每次推进都比较全部旧帧，
不再只有队列索引0的验证。

仍保留：

- 非空帧之间槽数不一致的配置变化；本轮非空帧宽度一致，允许整帧为空。
- 模式2的轨迹生命周期、排序与特有交叉规则。
- 从FFT检测谱峰、瞬态/噪声路径、动态制作配置及商业预滚动选择。
- 完整原始PCM生产链和既有C#构建器的非零相位集成。

不能把这组自有峰数组的闭合扩大为完整商业分析器的闭合。

## 复现

```powershell
python voicebank/tools/verify_frm2_track_repair.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --output '../VOCALOIDPatcher-research-work/frm2/track_repair.json'
python voicebank/tools/verify_frm2_outer_tracking.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --output '../VOCALOIDPatcher-research-work/frm2/outer_tracking_temporal.json'
```

只修改Python研究工具和文档。报告均在专用研究目录，不使用Windows Temp。
