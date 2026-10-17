# 自有 PCM 到分类功率缓冲的独立计算

接续[91](91_frm2_live_classification_phase.md)。目标元数据再次核对为同一 DSE.dll，
SHA-256 `7a4965bd377dbc2b08710e807ae1345b67d9fff6579ca0c01a769f744366f5b5`。

## 当前二进制的旧入口没有 FFT

`0xe3640` 调用 `0x519a0`。该入口先加窗并补零，随后直接把缓冲中的相邻float当作
实部/虚部求平方和，中间没有傅里叶变换调用。
[54](54_frm2_analysis_and_resonance.md)关联的窗口工具已有单位脉冲反例；本轮又通过
Ghidra函数查询核对，并在真实分类路径上验证了这一行为。
不能把这里的“功率缓冲”当作正确的傅里叶功率谱，也不能擅自插入标准FFT后声称复现了此DLL。

## 独立计算与输入边界

观察器以原始帧时间定位自有float32 PCM，逐样本核对与原生环形缓冲一致。
以窗口长度的一半向前定位起点，实际使用 window_size-1 个采样，乘对应窗系数后
按float舍入；余下位置补零。窗系数及42个频带边界仍从原生实例读取，属于显式资产依赖。

设加窗缓冲为d，half为输出频带分辨率：

```text
p[0]    = f32(d[0]²)
p[k]    = f32(f32(d[2k]²) + f32(d[2k+1]²)), 0<k<half
p[half] = f32(d[1]²)
```

全部half+1项与原生逐项比较，包括旧入口特殊的最后一项。
之后频带计算使用独立预测的p，而非读取的原生功率；原生值只作断言参考。
预测分类继续传给第91轮的相位生产比较，能量控制量同样来自自有PCM。

这消除了当前分类路径的原生功率计算依赖，不消除窗/边界资产、原生调度及F0输入依赖。
谐波仍是自有注入序列，同批自动F0/谐波重放与其它分析分支尚未完成。


## 全量验证

48组（启用/关闭特征各24组）全部通过。启用特征的3,466,908个功率缓冲值、
67,680个频带值均零误差；全部3,144次相位推进的90,344个ENV横纵坐标也均零误差。
控制量、对齐相位、跨帧修正及有效差分继续通过原有逐项断言。
证据：[92_owned_classification_power.json](evidence/92_owned_classification_power.json)。

```powershell
python voicebank/tools/verify_frm2_phase_pipeline.py --dll 'C:/Program Files/VOCALOID6/Editor/DSE.dll' --source-controls --output '../VOCALOIDPatcher-research-work/frm2/owned_classification_92_full.json'
```

两个涉及工具通过Python语法检查。本轮没有使用Windows Temp、修改安装DLL、复制商业负载或启动Editor。
