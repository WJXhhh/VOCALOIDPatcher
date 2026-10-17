# 原始特征与完整模式4音高状态推进

2026-09-15。当前 DSE.dll SHA256 为
`7a4965bd377dbc2b08710e807ae1345b67d9fff6579ca0c01a769f744366f5b5`，
Ghidra `/v6/DSE.dll`，基址 `180000000`。本轮全部实验使用自生成数据，未修改安装文件。

## 原始特征恢复

原生入口 `d92e0` 从上下文 PCM 环形缓冲生成特征；模型为 `frm2_raw_features.py`。
设 hop 为 H，样本数 N=2H，起点为 `(ring_cursor-H) mod ring_length`：

- `+4` 是去均值样本的过零计数除以 N−2，再按采样率/44100缩放。
  原生还将当前去均值值恰好为零计为一次过零。是否计数取决于环形缓冲的物理索引小于终点，
  不是展开后的位置小于终点；跨环尾实验必须保留这个行为。
- `+8` 是相邻斜率变化的带滞回状态翻转率，分母和采样率缩放同上。
  阈值为 `max(1, f32(0.1 * 最大去均值绝对幅度))`。它不能直接等同于噪声能量。
- `+c` 对去均值样本平方按 float 累加，额外重复一次末样本平方，
  然后乘 `(1/32768)^2` 并除以 N−1。因此不是标准 RMS，也不是标准无偏方差。

`verify_frm2_raw_features.py` 在三个环长度/hop组合、三种起点（含跨环尾）、
三个采样率及11种信号上直接调用原生入口。包括零、常量、脉冲、正负交替、斜坡、
三种幅度噪声和三种谐波信号。297组、891个字段完全零误差。

`RawFeatureObserver` 只读取原生PCM环，按模型算出特征并关联到帧。
36组完整分析中的1,764个新帧、5,292个原始特征也全部零误差，观察器不改变整批结果。
原来的21个快照链已使用这三个预测值，不再将原生特征字段作为概率计算输入。

## 完整历史推进

`verify_frm2_pitch_state.py` 在每次真正的音高阶段收集准备PCM和帧标识，整批结束后执行模型。
候选F0、概率、连续接受计数、十项F0历史、十项概率历史和前四帧的修正后音高均由模型推进。
原生对应字段仅用于断言比较，不回填模型。初始条件明确检查，未从第一份有效快照抄入历史。
全零准备PCM走原生相同的早退/清零行为；非正时间不取前帧概率最大值。

这里仍依赖原生调度、原生准备PCM和原生PCM环；尚不是从WAV字节到FRM2的独立生成器。

### 修正先前的低能量分支结论

完整尾帧触发了文档76原先未覆盖的情况：噪声特征为零，但能量也为零。
汇编 `ff66c → ff674` 证明低能量惩罚不要求噪声分支成立。
已将 `frm2_pitch_voicing.py` 的能量惩罚移出噪声条件，并更正文档76。
先前21个内段样本本来就未触发此分支，不能用它们证明原先错误的分支关系。

## 两种FFT后端必须分开报告

| 范围 | 独立Python FFT | 显式DSE/MKL FFT底层 |
| --- | ---: | ---: |
| 信号组数 | 9（110/220/440，稳态/渐入/静音段） | 36（文档77的12频率×3形状） |
| 音高阶段数 | 441 | 1,764 |
| 五帧音高及F0历史最大差 | 7.62939453125e-5 Hz | 0 |
| 概率及概率历史最大差 | 2.384185791015625e-7 | 0 |
| 评分最大差 | 1.7881393432617188e-7 | 0 |

`--native-fft` 只在独立生成的缓冲上调用同一FFT底层的正逆变换，
加权、频谱、平方及Nyquist处理、归一化、候选、特征、概率、历史和五帧修正仍由模型执行。
它使用已结束实验的FFT描述符，不读取原生评分结果作为预测输入。
这个后端用来分离FFT数值行为和上层规则，不能称为完全独立FFT实现。

### 保留的FFT敏感反例

纯Python后端的50Hz稳态输入在约0.191564626秒失败。评分最大差只有1.1920929e-7，
但候选索引41右侧的近乎并列谷值发生次序变化：模型选择索引52，原生选择80。
后续右峰搜索的起点随之变化，置信度由原生约0.00665618变成模型约0.04253615，
概率差达到0.14063394。该时刻F0和接受计数碰巧相同，不能因此忽略概率错误。

没有用增大容差或读取原生谷值位置掩盖它。该失败命令返回非零，并保存结构化反例；
同一输入使用DSE/MKL FFT底层后，评分、概率和历史全部精确匹配。

## 最终输出尚有后续F0重估

后续更正：文档79确认下述直接平均受配置87控制，默认并不执行；
默认差异由更早的谱峰幅度加权重估解释，479帧已在文档79完成动态衔接。
以下保留本轮发现时的状态，不再作为最新缺口。

逐帧比较最终批量输出后，9组矩阵仍有104帧、36组矩阵有479帧与五帧修正后音高不同。
例如220Hz稳态的一帧，队列模型为143.711868Hz，最终为127.664918Hz。
测试报告明确将 `state_stage_passed` 与 `final_output_matches` 分开，后者为false，
不将状态链通过说成最终输出通过。

静态定位到 `d2cc0` 的 `d8a50..d8b66`：自动音高启用且有有效首谐波时，
对前最多15项谐波按 `frequency/(index+1)` 汇总、按纳入项数求均值并写F0。
之后 `86cb0` 还会把频率变成相对F0的量，并移除/折叠幅度字段。
因此不能把最终数组直接当成此处的原始Hz输入求均值，也不能使用最终F0反算输入再宣称独立验证。
当前尚未得到这个写入点所需的原始谐波输入的完整独立衔接，故479帧差异仍保留未验收。

## 复现与证据

```powershell
python voicebank/tools/verify_frm2_raw_features.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --output '../VOCALOIDPatcher-research-work/frm2/raw_features.json'
python voicebank/tools/verify_frm2_pitch_state.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --output '../VOCALOIDPatcher-research-work/frm2/pitch_state_independent.json'
python voicebank/tools/verify_frm2_pitch_state.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --native-fft --frequencies 50 75 110 147 196 220 294 330 440 660 880 1000 --output '../VOCALOIDPatcher-research-work/frm2/pitch_state_expanded_mkl.json'
# 已知失败回归，预期非零退出并保存失败报告：
python voicebank/tools/verify_frm2_pitch_state.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --shapes steady --frequencies 50 --output '../VOCALOIDPatcher-research-work/frm2/pitch_state_fft_counterexample.json'
```

证据在 `evidence/78_raw_features.json`、`78_raw_live.json`、`78_pitch_state.json`、
`78_fft_counterexample.json`。完整诊断在仓库旁研究目录，不使用Windows Temp。

下一步优先衔接最终谐波F0重估，再处理PCM准备与完整制作流程。
独立FFT的近并列数值敏感问题、未覆盖配置和普通FRM2完整生成验收仍未完成。
