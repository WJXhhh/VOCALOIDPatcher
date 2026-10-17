# 自有源PCM、实际功率谱与音高PCM准备

2026-09-15。重新核对GUI `/v6/DSE.dll` 元数据及只读反汇编；哈希、架构、基址同文档79。
自生成数据仍写入仓库旁 `VOCALOIDPatcher-research-work/frm2`，不使用Windows Temp。

## 本轮去掉的原生输入依赖

文档79的最终F0链仍读取原生功率谱。本轮在真实批量流程中重建功率谱，并向前验证到源PCM。

- `owned_pcm` 集中生成实验输入，批量原生入口和独立模型使用同一自有信号定义。
- 原始特征的样本由源PCM提供，以帧时间和环位置映射回物理索引；
  保留文档78已核实的跨环尾过零规则。原生环只做逐样本比较。
- 功率谱窗口以 `round(frame_time*44100)-floor(window_size/2)` 为起点，
  超出源PCM范围补零。与实际环中窗口逐样本严格比较后，预测继续使用源PCM。
- 独立窗函数、居中和补零准备后执行明确指定的FFT底层，再独立生成谱峰与加权F0。
  原生功率谱和峰表均只做断言，不作为预测输入。

在220Hz默认样例中，窗口为1025点、11号窗、补零因子4，变换4096点。
参数来自实际 `d4bf4..d4d79 → 4e8e0` 调用关系，并逐帧检查FFT几何。
DC标量仍取绝对幅度，其余正频率取float实部平方加虚部平方；不擅自把整个数组统一成标准功率。
模型位于 `frm2_live_power.py`，接入 `verify_frm2_pitch_state.py`。

## 音高准备PCM不再直接读取

`frm2_pitch_pcm.py` 重建 `fb4a0` 开头的窗口提取和去均值。
它仍读取估计器的**降采样PCM环**，但不再把 `estimator+190` 的最终准备缓冲作为预测输入。

窗口起点由float取整后的 `(frame_time-epoch)*rate+0.5`、环游标、偏移、半窗长度共同决定；
启用补偿时另减 `512/decimation` 的整数商，然后按环长度处理跨尾。
样本均值按float顺序累加、float除法及逐样本float减法完成，不能替换成double求和。
全零窗口自然保持零。每个预测样本都与原生准备缓冲逐值严格比较。

完整36组矩阵的1,764个状态阶段全部通过，连同最终1,584帧F0，
最大最终误差仍为8.490215805e-6Hz。1,764帧功率谱在显式DSE/MKL FFT底层下零误差。
1,243,620个音高准备PCM样本严格相等。全部功率谱帧均为1025点、11号解析窗（lookup=0）、
补零因子4、2048个正频率标量；这条受测路径没有读取专有窗表。
原始特征、PCM窗口和音高准备样本均执行严格比较；观察器与未加观察器的批量结果一致。
精确比较数及各配置几何见紧凑证据，不将这一结果外推到未测采样率和配置。

## 新增的独立FFT反例

不用DSE/MKL底层、改用Python FFT时，220Hz稳态在第一个时刻0就触发谱峰断言：
第87号峰的Hz为模型21791.20703125、原生21791.533203125，差0.326171875Hz。
主谱功率相对误差虽很小，却不能保证弱谱峰插值等价。
该失败发生在最终F0比较之前，**不等于已证明最终F0失败，也不等于最终F0通过**。

没有放宽峰值容差、丢弃弱峰或回填原生峰。命令非零退出，回调内失败也保存结构化报告。
它与文档78的50Hz评分谷值反例是两个不同阶段的问题。
文档79的9组Python通过记录依赖当时的原生功率谱输入；在本轮更强输入替换后不能沿用该结论。

## 下一处明确依赖

后续文档81已独立恢复该流式状态并接回源PCM验证；默认不再读取降采样环作为预测输入。
固定系数仍是外部DLL资产，原生调度仍保留。以下为本轮结束时的定位记录。

只读代码已定位 `d1140 → 103f00`：源PCM先经过流式FIR抽取，写入 `estimator+168` 环，
并更新游标、输出计数与时间；随后才进行本轮已建模的窗口提取和均值移除。
`103f00` 按抽取因子收集一组输入，倒序写入滤波历史，以float逐项乘加生成一个输出。
这段滤波器构造、系数来源与流式状态尚未独立验证，故仍保留原生降采样环输入。

普通FRM2整块尚未完成：还包括上述降采样、原生调度替换、独立FFT敏感行为，
以及谐波/共振/保存各模块到完整批量制作接口的衔接。没有修改生产默认值或安装DLL。

## 复现

```powershell
python voicebank/tools/verify_frm2_pitch_state.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --native-fft --frequencies 50 75 110 147 196 220 294 330 440 660 880 1000 --output '../VOCALOIDPatcher-research-work/frm2/pitch_pcm_state_matrix.json'
# 已知失败，预期非零：
python voicebank/tools/verify_frm2_pitch_state.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --shapes steady --frequencies 220 --output '../VOCALOIDPatcher-research-work/frm2/power_fft_counterexample.json'
```

证据：[80_pcm_power_state.json](evidence/80_pcm_power_state.json)。仅修改Python研究工具及文档，
已做对应原生数值比较和Python语法检查；本轮未改C#，未运行编辑器或部署。
