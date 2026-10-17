# DRS 拟合响应精度与源 E7 反例修正

本轮属于「普通 FRM2 的精确数值语义」，接续 [84](84_frm2_source_e7_to_fit.md)。
目标是修正上一轮保留的 10 个拟合反例，不改变 `1e-4` 验收阈值、不跳过失败帧。

## 原因与修正

DRS 共振 setter（RVA `0x62760`）的幅度缓存计算为：

```text
db32 = float32(amplitude)
linear = float32(32768 * exp(double(db32) * (ln(10) / 20)))
```

`db32` 到达最小 float 哨兵时返回零。指数运算和乘常数在 double 精度下完成，
最后将缩放后的结果舍入成 float，再存进 double 缓存字段。
此前拟合器复用了 `audit_frm2_numeric.response` 的 float 指数换算：
它先舍入指数的乘积，再舍入 exp 结果，不能用于这里的 DRS setter。
本次在拟合模块中建立专用响应函数，未修改那条独立的消费端研究公式。

响应计算还按当前 DRS 实现保留平方和开方及高斯指数的运算顺序。
精修的 E7 查值坐标则按 `0x71259` 的 `DIVSS`，先将中心频率转成 float，
除以 22050 后再次舍入成 float，再作为 double 坐标查值。
仅调整上述坐标与运算顺序不能消除旧反例；改正幅度缓存换算后，针对性反例才全部通过。

三点 E7 的两个失败案例来自 330 Hz steady/gap，在时间 `0.005804988662131519`：
第一处超过阈值的差异出现在第一轮第 7 号主共振，幅度约差 0.2 dB，
随后带宽差扩大到约 296.235 Hz。另 8 个 880 Hz 案例最终只有第 5 号主共振幅度超限。
这说明微小缓存误差可经残差相减和离散网格选择放大，不能以末端误差大小推断字段单位。

## 验证

- 覆盖旧 10 个反例及相邻源案例的 48 项比较全部通过，最大误差 `1.52587890625e-5`。
- DRS setter 的 407 组幅度缓存误差全部为零；旧换算在相同输入下存在差异。
- 2,849 个响应值与原生 setter 缓存系数计算的参考值一致。缓存系数仅用于参考侧，预测侧独立生成。
- 原有 54 组完整拟合回归、82,944 个字段全部通过，最大误差 `3.814697265625e-6`。
- 全量 36 组源信号生成 479 个有声 E7 帧，原生显式调度与独立拟合共 958 项比较全部通过，最大误差 `1.52587890625e-5`。上一轮 10 个反例全部消除。

逐项数字与回归证据见 [85_fitting_precision.json](evidence/85_fitting_precision.json)。
四个涉及的 Python 工具均通过语法检查；本轮未改动 C#，未执行解决方案构建。

目标为本机 DSE.dll，SHA-256：
`7a4965bd377dbc2b08710e807ae1345b67d9fff6579ca0c01a769f744366f5b5`。
只读 Ghidra 查询与自有内存测试，未修改目标 DLL、Ghidra 项目或启动编辑器。

## 复现与边界

```powershell
python voicebank/tools/verify_frm2_drs_response.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --output '../VOCALOIDPatcher-research-work/frm2/fit_85_setter.json'
python voicebank/tools/verify_frm2_fit_schedule.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --independent --output '../VOCALOIDPatcher-research-work/frm2/fit_85_regression.json'
python voicebank/tools/verify_frm2_source_fit.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --all-frames --frequencies 50 75 110 147 196 220 294 330 440 660 880 1000 --output '../VOCALOIDPatcher-research-work/frm2/source_fit_85_full.json'
```

测试继续保留原生初始 A/B/C 估计和最终残差生成；源链的既有 FFT、系数资产和调度依赖不变。
尚未完成普通 FRM2 磁盘保存、读回、消费者联验及 bit37 相位 ENV 接入。
因此本轮解决的是受测配置的拟合数值反例，不能据此宣称普通 FRM2 整块完成。
所有运行产物写入项目旁的研究目录，没有使用 Windows Temp。
