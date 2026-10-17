# 同批自动 F0 与谐波重放及无序槽修正

接续[93](93_frm2_analytic_classification_geometry.md)。本轮把源链实际预测的所有最终帧，
包括无声帧，按顺序交给原生相位生产。目标仍为固定哈希的本机DSE.dll。

## 重放边界

每帧频率、幅度和原相位来自 `verify_frm2_pitch_state` 的预测输出。
容量不足的无声帧补为无效槽，保持相同槽索引；不重新生成另一组谐波作替代。
两个特征开关都执行重放，自有PCM保持相同；分类及能量控制量沿用第93轮的独立计算。
逐帧严格核对进入相位历史的F0、输出时间和总帧数。

第一次起音重放在第6帧失败：源F0为127.66160583496094 Hz，原生相位历史为0；
getter确认外部F0参数已正确写入。主流程 `0xd464b..0xd4aca` 可通过 analysis+c88
对象再次更新队列F0。重放时只在本次私有分析实例中暂时清空该可选对象指针，
step返回后立即恢复原指针；原生相位代码及其状态计算不变。
因此这是“已验证参数进入相位生产”的接口实验，不是声称原始整条分析配置无需适配即可直通。
源F0生产本身仍由前面的独立源链断言验证。

## 新发现：逐节点排序会影响生成算法

330 Hz重放最初出现ENV约2π的偏差，而原点、对齐和修正值仍一致。
原生 `0xd79c4..0xd79df` 与 `0xd7ae5..0xd7b1e` 每次读取当前ENV的倒数第二点，
即末尾边界点之前横坐标最大的节点，再求差分与累积；插入函数会立即排序。
所以谐波槽无序时，不能用“上一插入槽”的纵值，也不能只在最终保存前排序。

PhaseModel改为保留当前最大内部横坐标节点的纵值，仍按原槽位推进各槽差分与修正。
比较ENV时另按原生插入规则排序并合并重复坐标。220/330 Hz三类信号初验均通过。
这修正了第87/88轮仅处理最终容器排序的不足：那些历史结果证明文件与消费者一致，
不证明旧模型生成的全部无序槽相位与原生生产一致。新模型必须重新保存和消费验证。


## 同批重放验证

36组源信号在两个特征开关下共72次重放、3,168帧，逐帧F0、时间与总数均通过严格断言。
包含2,210次无声推进，95,760个相位ENV横纵坐标、原点、对齐、修正和有效差分全部零误差。
证据：[94_source_phase_replay.json](evidence/94_source_phase_replay.json)。

```powershell
python voicebank/tools/verify_frm2_source_phase_replay.py --dll 'C:/Program Files/VOCALOID6/Editor/DSE.dll' --output '../VOCALOIDPatcher-research-work/frm2/source_phase_replay_94_full.json'
```

两个改动/新增工具通过语法检查。没有使用Windows Temp、修改安装DLL、复制商业帧或启动Editor。


## 修正后重新保存与消费

使用修正后的PhaseModel重新生成全部479个有声拟合帧，993,885项字段读回、143,201次
幅度消费、52,690个相位消费槽全部零误差，479项F0 Hz到cents读回亦通过。
证据：[94_corrected_fitted_roundtrip.json](evidence/94_corrected_fitted_roundtrip.json)。

```powershell
python voicebank/tools/verify_frm2_fitted_roundtrip.py --dll 'C:/Program Files/VOCALOID6/Editor/DSE.dll' --all-frames --frequencies 50 75 110 147 196 220 294 330 440 660 880 1000 --output '../VOCALOIDPatcher-research-work/frm2/fitted_replay_fix_94_full.json'
```

当前受测源序列的相位生产、普通保存和消费已有对应验证；其它分析配置、商业预滚动与制作预设、
真人全量音质及源链的原生调度/FFT/FIR依赖仍不能据此宣布完成。
