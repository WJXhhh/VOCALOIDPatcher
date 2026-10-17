# FRM2 分析链：谱峰候选与谐波归属

2026-09-14。目标为 GUI 项目 `/v6/DSE.dll`，x86-64，基址 `0x180000000`，
SHA256 `7a4965bd377dbc2b08710e807ae1345b67d9fff6579ca0c01a769f744366f5b5`。
只读反编译与本机同哈希 DLL 的自有输入对照；未保存商业谱峰或反编译源码。

## 已验证范围

| 模块 | 独立模型对照 | 结果 |
| --- | ---: | --- |
| 候选选择 `RVA f0ec0` | 576 组，5,760 个浮点字段，另逐项核对索引/排序/状态 | 浮点最大差 0，离散字段全一致 |
| 模式 1 交叉拒绝 `RVA f18a0` | 6,228 组，实际拒绝 644 组 | 全一致 |
| 模式 1 归属冲突 `RVA f16a0` | 800 组，126 组递归，最多 3 次冲突 | 全一致 |

模型位于 `voicebank/tools/frm2_peak_selection.py`，预测不调用 DSE。
候选矩阵覆盖模式 1 有序峰/模式 2 无序峰、三种评分、配置 0x33 开关、
谐波索引 -1/0/3、缺省与已有 F0、空峰、单峰、等距等幅、极低幅、正幅和频率越界。
冲突矩阵覆盖递补开关、相等代价、候选耗尽、无前帧归属、交叉与递归争用。
所有配置在本次试验中为静态 ENV，不据此声称动态配置已验证。

## 候选数值契约

每条轨迹保留三个候选。候选、当前/前帧峰索引及排序槽是 **int32**；
距离、幅差和代价为 float32。反编译器的部分 float 类型显示不能直接当作布局。

- 模式 2 半窗宽为 `cfg28 + cfg29 * expectedFrequency`。
- 普通非负谐波索引半窗宽为 `(cfg21 + cfg22 * index) * historyF0`；
  历史 F0 非正时写回 cfg0d，cfg33 非零时再减半。
- 索引为负时半窗宽为有效历史 F0 或 cfg0d 的一半，但不写回缺省历史值。
- 频率区间受 cfg27 和 cfg02 裁剪，区间端点可参与候选；距离仍须严格小于半窗宽。
- cfg33 为零时保留三个最近峰；替换最远槽时并列取靠后的槽。
  cfg33 为整数 1 时保留三个最强峰，初始幅值 -1000，替换须严格大于已有最小值。
- cfg08=0 按距离评分；=2 使用峰幅值的负值；=1 使用
  `0.7 * distance + 0.3 * abs(amplitude - previousAmplitude)`。
  前幅为负 FLT_MAX 时幅差为零。混合计算的 double/float 舍入顺序已纳入模型。
- cfg08=1 且 cfg33 非零时改用幅值评分；非负幅值代价为 10000。
  模式 1 负幅值还叠加 `65 * frequencyError / (expectedFrequency / (index+1))`。
- 无候选槽的代价/幅差为 1e8、距离为半窗宽加 1。
- 排序依次比较交换 `(0,1)`、`(0,2)`、`(1,2)`，只在严格更小时交换。
  这不是稳定排序：例如代价 `[1e8,1e8,0]` 的槽顺序是 `[2,1,0]`。

此处“幅值”仅指上游峰记录的数值，尚未凭这段消费者确定上游检测器的幅度单位。

## 交叉与冲突契约

模式 1 对当前峰附近检查 `[max(0,current-2), min(peakCount-1,current+2))`，
**上界排除**。遇到已占用峰后，任一轨迹无前帧峰索引会立即结束检查。
前后帧索引相对顺序反转则清除当前归属。

两个轨迹争用同峰时，用各自当前候选的代价比较；相等时已有归属者胜出。
失败者已在第三候选或禁用递补时，当前峰置 -1，预测频率清零。
否则移至下一排序槽，再检查交叉；新峰若已占用，递归解决归属。
无效候选与交叉拒绝路径不自动套用“预测频率清零”，这与直接耗尽路径不同。

## 尚未闭合的边界与下一入口

上述输入是**已经检测出来的峰数组**，不是 FFT bins；不能据此宣布
FFT 到谐波提取全链完成。普通调度 `RVA f0690` 已查到这些函数的调用关系：
推进前帧索引、清空本帧占用、可选邻近谐波频差修正、选择、交叉检查、冲突，
最后调用 `RVA f0cf0`。其整体状态推进和模式 2 轨迹增删尚未独立验证。
后续[文档58](58_frm2_harmonic_scheduler.md)已补上模式1整帧推进与已归属输出的独立对照；
模式2增删及外层初始化仍保留为未完成。
FFT 的实际峰检测、瞬态/噪声分支、商业预滚动与制作配置仍保留为未完成。

## 复现

先确保项目同级 `VOCALOIDPatcher-research-work/frm2` 已存在；所有报告放此目录。

```powershell
python voicebank/tools/verify_frm2_peak_selection.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --output '../VOCALOIDPatcher-research-work/frm2/peak_selection.json'
python voicebank/tools/verify_frm2_peak_conflicts.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --output '../VOCALOIDPatcher-research-work/frm2/peak_conflicts.json'
```

聚合证据：[57_frm2_peak_selection.json](evidence/57_frm2_peak_selection.json)。
本次仅新增 Python 研究工具和文档，未修改 C# 构建器或宿主补丁。
