# 跟踪后的eff80频率修正

2026-09-14。DSE目标哈希与文档66一致，重新确认Ghidra目标。
本轮补上主分析函数在`ef9f0`之后调用的`eff80`，使用原生构造器建立实际参考ENV，
独立计算选择标志和最终频率，不调用原生函数生成预测结果。

## 验证

768组原生入口对照，2,432个频率数值误差为0；选择标志全部一致，幅值数组保持一致。
实际选择并修正264个槽。覆盖空/单峰/密集/无效频率/乱序/相等频率数组，
F0=0/220，输入强度0/0.5/1/1.5，长度256/1024、尺度44100/48000，mask低两位四种组合。

模型：`frm2_frequency_correction.py`。
验证：`verify_frm2_frequency_correction.py`。
证据：[67_frm2_frequency_correction.json](evidence/67_frm2_frequency_correction.json)。

## 条件与数值契约

默认cfg24 ENV把强度限制到[0,1]。输入1.5实际按1处理，不会外推到目标之外。
强度须为正，frame mask必须同时有bit0/bit1，且槽数为正才执行。
否则不清除已有选择标志。本轮特意以非零标志初始化，核对了早退行为。

频差尺度为`float32(4.5 / windowLength * frequencyScale)`。
原生构造器的内部参考ENV使用线性非规则插值，五个点为：

| x | y |
| --- | --- |
| 0 | 0 |
| 2/9 | float32(-1.6) |
| 4/9 | float32(-6.3) |
| 2/3 | float32(-15.2) |
| 1 | float32(-57.6) |

对每个正频率源槽i及另一个正频率目标槽j，计算归一化绝对频差，
经ENV取得E，再比较`amplitude[j] < float32(float32(E+amplitude[i])+10)`。
严格小于才标记。已经标记的目标不再重复比较，但已标记的源仍可参与比较。
选择阶段不改频率，所以不得边比较边立即修改。

全部标记结束后，目标槽j的频率改为：

```text
frequency[j] += ((j+1)*F0-frequency[j])*strength
```

每一步float32舍入按原生实现保留。此处没有要求F0>0；F0=0的实际输入也纳入矩阵。
只改frame频率数组；不改峰幅，不把修正重新写回跟踪对象的期望频率。
该函数自身也不按跟踪模式1/2分流。

## 完成边界

文档66新增的eff80单入口缺口，在上述默认配置ENV与有限输入矩阵范围内已完成。
主分析器何时选择非零cfg24、完整固定延迟队列流程、额外队列槽可达性，
以及此前保留的FFT区间计数消费者、瞬态/噪声、商业制作配置和生产集成仍未完成。
本轮没有把单入口通过扩大为完整商业PCM分析路径已闭合。

## 复现

```powershell
python voicebank/tools/verify_frm2_frequency_correction.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --output '../VOCALOIDPatcher-research-work/frm2/frequency_correction.json'
```

仅新增Python模型、验证工具和文档。报告继续使用专用研究目录。
