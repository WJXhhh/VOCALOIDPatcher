# 初始 A/B/C 与 DRS 残差的独立生成

接续 [85](85_frm2_drs_fitting_precision.md)，本轮继续「普通 FRM2 的精确数值语义」。
独立拟合路径已移除初始 A/B/C 的 `0x759a0` 和残差生成的 `0x763b0` 调用。
参考侧仍运行原生单体入口，预测侧只使用 Python 算术与原生对象分配、字段安装。

## 初始曲线

`frm2_initial_curve.py` 覆盖新建 set-0 模型的 E7 输入及已有受测谐波数组输入。
不是任意现存共振模型的重新估计接口；原生尾部的旧共振幅度补偿不在此接口范围内，
因为本调度随后显式重设所有主共振并创建第二列表。

- E7 少于 5 个点时不估计；排除首尾边界点后，10000 Hz 以内须至少有 3 个样本。
- 参考幅度取 250 Hz 内的最大值；第一个内部点已超过该频率时取它的幅度。
- gain 先以 float 加到样本幅度上。估计使用 `log(max(level, -99.999) + 100)`，逐项 float 累加。
- E7 的横坐标以第一个内部点为零，但反推 B 使用 F0；两者不能随意混为同一个锚点。
- A 上限为 -3，B 限制到 20..100，C 限制到 -0.0009..-0.00003。
  NaN 回退及未估计状态按原生初始对象处理；成功估计的标记也一并核对。

108 组独立边界案例覆盖 2/3/4/5/12/80 点、三种 F0、三种 gain、正常和低于 -100 的输入。
324 个字段误差为零，估计标记全部一致。

## 残差生成

`frm2_drs_residual.py` 使用已经修正的 DRS 解析共振响应，不能换成 DSE 的查表响应。

1. 建立 30 Hz 网格及归一化尾点；22050 Hz 设置下共 736 点，初值全零。
2. 无频率数组时按 F0 枚举谐波；有数组时按其频率枚举。
3. 每个共振的线性响应依次 float 累加，再转 dB。
4. A、指数曲线、该 dB 值相加时，按原生双精度加法后一次转 float；
   不在 A 与曲线相加后提前舍入。
5. 谐波频率除以最大频率的坐标按 float 舍入，从 E7 查值并加 gain，再减模型。
6. 找最近网格索引，在上次和本次索引之间线性填充，结果限制到 -100..10。
   索引 0 与最后一个谐波未覆盖的尾部保持零，不额外延拓或补 DC。

这些是当前 DRS 生成路径的规则，不能用来否定商业库在其他生成路径下的 DC/尾部值。

## 验证与复现

针对性源输入 20 项比较通过，最大误差 `1.52587890625e-5`。
原有 54 组完整拟合回归的 82,944 个字段全部通过，最大误差 `3.814697265625e-6`。
全量 36 组源信号、479 个有声 E7 帧的 958 项比较全部通过，最大误差 `1.52587890625e-5`。
证据见 [86_initial_and_residual.json](evidence/86_initial_and_residual.json)。
五个涉及的 Python 工具通过语法检查；没有 C# 改动，未执行解决方案构建。

```powershell
python voicebank/tools/verify_frm2_initial_curve.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --output '../VOCALOIDPatcher-research-work/frm2/initial_86_edges.json'
python voicebank/tools/verify_frm2_fit_schedule.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --independent --output '../VOCALOIDPatcher-research-work/frm2/fit_86_regression.json'
python voicebank/tools/verify_frm2_source_fit.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --all-frames --frequencies 50 75 110 147 196 220 294 330 440 660 880 1000 --output '../VOCALOIDPatcher-research-work/frm2/source_fit_86_full.json'
```

本次只读查询再次确认 Ghidra 目标 DSE.dll 的 SHA-256 为
`7a4965bd377dbc2b08710e807ae1345b67d9fff6579ca0c01a769f744366f5b5`，映像基址 `0x180000000`。
没有修改 Ghidra 项目或已安装文件，也没有启动编辑器。

## 剩余边界

独立拟合算术已覆盖初始曲线、主/第二列表优化、精修和残差。
原生分配及 setter 仍用于安装结果；源分析链仍依赖此前明确的 FFT 后端、系数资产及调度。
**本轮不是磁盘普通 FRM2 的完整验收**：还要连接普通布局的保存、原生读回、
幅度消费者和 bit37 相位链，不能将本轮 DRS 内存输出掩码直接认作磁盘布局。
运行产物在项目旁研究目录，不使用 Windows Temp。
