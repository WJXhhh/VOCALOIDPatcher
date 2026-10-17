# 普通谐波整帧跟踪调度的独立验证

2026-09-14。沿用文档57同一 GUI Program 与 SHA256 固定的 DSE.dll。
本次独立实现模式 1 的 `RVA f0690`，包括末尾 `RVA f0cf0` 的已归属峰输出。
预测函数 `frm2_peak_selection.advance_harmonics` 不调用原生函数。

## 证据

- 144 条序列，每条连续 8 次推进，共 1,152 次原生整帧调用。
- 55,296 个轨迹数值最大差 **0**；轨迹前后索引、排序、峰占用和归属表全一致。
- 实际发生 73 次归属冲突；每个已归属输出的频率、幅度、相位与原生数组完全一致。
- 矩阵包括 0/1/8 条轨迹，评分 0/1/2，cfg33 开关，邻近修正 0/0.7，
  邻居数量 1/5，递补开关；序列含空峰、F0=0、稀疏峰及正/负峰幅数值。
- 峰和相位由固定随机种子的自有数据生成，未导出商业帧。

证据：[58_frm2_harmonic_scheduler.json](evidence/58_frm2_harmonic_scheduler.json)。
这次调用完整调度入口，不是用原生候选函数拼出“独立”预测。

## 状态与数值规则

每帧先把 current 移至 previous，再把 current 清为 -1，并清空当前峰占用表。
模式 1 只有本帧 F0 为正才执行匹配。期望频率非正或峰集合为空的轨迹跳过选择。

cfg12 为正时，对之前最多 cfg13 条已经匹配的轨迹累积频差：

```text
numerator += (peakFrequency - (earlierTrackIndex+1)*currentF0) / peakAmplitude
denominator += 1 / peakAmplitude
correction = denominator > 0 ? numerator/denominator : 0
expectedFrequency += correction * cfg12
```

累积的 float 舍入、分母倒数的 double 中间运算均已按实际路径验证。
负幅样本的分母可能非正，因此不能把该分支简写成无条件加权平均；
此处仍不把峰数组的数值类型推广为上游检测器的幅度单位。
测试使用非零幅值，零除、NaN/Inf 不属于这次输入范围。

随后执行文档57的候选、交叉拒绝和递归冲突处理。候选失败会将期望频率清零，
而空峰的直接跳过、F0=0 和交叉拒绝不会统一执行这个清零动作。
因此后一帧能否再参与匹配取决于实际失败路径，而不是仅看上一帧是否有输出。

输出步骤把 frame mask 的低三位置位，数组长度设为轨迹数量。
已归属轨迹从峰记录复制 frequency/amplitude/phase，并以该 amplitude 更新轨迹历史幅度。
跟踪期望频率没有在此输出步骤直接改写为峰频率；模型保留了这一区别。

## 完成边界

已验证的是**已有轨迹、已有排序峰数组**进入模式 1 调度后的整帧推进。
仍未包括：

- 外层 `ef9f0` 初始化/重置轨迹、F0 历史与工作峰准备；
- FFT bins 到峰记录的检测及幅度归一化；
- 模式 2 轨迹增删及其基于频率的交叉检查；
- 未匹配输出槽的分配器默认值、动态配置、瞬态/噪声与商业制作条件。

后续[文档59](59_frm2_outer_tracking.md)已验证队列索引0下的模式0/1外层初始化、
重置、历史F0和双向反馈；多帧队列修补及模式2仍未纳入。

连续序列保留轨迹状态和 F0 历史；每次使用新的自有输出帧，
所以不能把结果表述为已经验证原始生产帧缓冲的全部复用行为。

## 复现

```powershell
python voicebank/tools/verify_frm2_harmonic_scheduler.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --output '../VOCALOIDPatcher-research-work/frm2/harmonic_scheduler.json'
```

报告写项目同级专用研究目录；未创建系统 Temp 研究文件。
仅修改 Python 研究工具和文档，未修改 C# 构建器或启动编辑器。
