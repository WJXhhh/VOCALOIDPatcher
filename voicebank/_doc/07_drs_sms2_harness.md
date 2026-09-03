# DRS SMS2 实测闭环

## 结论摘要

已经把 `DSE.dll` 内部的 DRS 离线分析器做成一个最小独立 harness，并完成以下闭环：

```text
程序生成的 44.1 kHz float PCM
  -> DRS::CSMSAnalysis / CSMSRegionAnalysis
  -> CSMSCollection
  -> DRS writer 写出 SMS2
  -> DRS reader 重新载入同一文件
  -> 独立 probe 验证嵌套 FRM2 数量、时间、掩码和 F0
```

这证明 DSE 内部分析器能在编辑器外从不依赖商业帧的 PCM 产生非空、结构完整、可由同一 DSE 回读的声学结果。结果仍是 DRS 原始 SMS2，不是最终声库使用的 DSE5 FRM2；两者的字段掩码明显不同，因此不能直接把当前文件装进 DDB。

## 可复现实验工具

- `voicebank/tools/drs_harness/DrsHarness.csproj`
- `voicebank/tools/drs_harness/Program.cs`
- `voicebank/tools/probe_sms2.py`

构建：

```powershell
dotnet build voicebank/tools/drs_harness/DrsHarness.csproj -c Release
```

运行自动 F0：

```powershell
dotnet run --project voicebank/tools/drs_harness/DrsHarness.csproj -c Release -- `
  "C:\Program Files\VOCALOID6\Editor\DSE.dll" `
  "voicebank\_tmp\drs_auto.sms2" 2 220 auto
python voicebank/tools/probe_sms2.py voicebank/_tmp/drs_auto.sms2
```

运行外部 F0：

```powershell
dotnet run --project voicebank/tools/drs_harness/DrsHarness.csproj -c Release -- `
  "C:\Program Files\VOCALOID6\Editor\DSE.dll" `
  "voicebank\_tmp\drs_external.sms2" 2 220 external 220
python voicebank/tools/probe_sms2.py voicebank/_tmp/drs_external.sms2
```

harness 当前生成带淡入淡出的合成谐波测试信号，基频、时长和外部 F0 均来自命令行。这只是分析器契约测试；在人声录音闭环完成前不应把音质视为已验证。

## 调用入口与对象尺寸

以下地址均为当前 DSE 6.13.1.1 的 RVA；harness 按 `module base + RVA` 调用，不能假定跨版本稳定：

| 作用 | RVA / 大小 |
| --- | ---: |
| 分析配置构造 | `0x90de0` |
| 动态参数 envelope 写值 | `0x3af0` |
| 动态/固定参数取值 | `0x2b050` |
| DRS collection 构造 | `0x938c0` |
| DRS analysis 构造 | `0xcdde0` |
| PCM 批处理驱动 | `0xd9b10` |
| 通用 chunk writer | `0x92f40` |
| 通用 chunk reader | `0x93000` |
| 配置对象大小 | `0x35f0` |
| collection 对象大小 | `0x68` |
| analysis 对象大小 | `0xce0` |
| stream 对象最小分配 | `0x80` |

配置内有 77 个动态参数槽：首槽位于 `+0x5d0`，stride 为 `0xa0`。每个槽是随归一化时间取值的 envelope，而不是一个裸 `float`。

## hop 配置修正

分析配置构造函数的 `+0x18` 默认值是 `86.1328125`：

```text
round(44100 / 86.1328125) = 512 samples
```

参考传统库实测帧距为 256 samples，所以 harness 显式覆盖为 `172.265625`：

```text
round(44100 / 172.265625) = 256 samples
```

生成文件中帧时间步严格为 `0.005804988662...` 秒，再次独立确认该公式。不能把配置构造器的默认值误写成参考库的实际训练设置。

## 自动 F0 与外部 F0

`DRS::CSMSAnalysis` 的逐帧函数 `0x1800d2cc0` 存在明确分支：

```text
if dynamic_parameter_0x14(frame_time) == 0:
    frame.mask |= bit_9
    frame.f0 = dynamic_parameter_0x0d(frame_time)
else:
    run automatic F0 estimation using parameters 0x0c / 0x0d / 0x0e ...
```

因此参数 `0x14` 是“使用自动 F0”开关，而 `0x0d` 在外部模式下是逐帧 F0 约束。动态参数默认是至少覆盖归一化时间 0 到 1 的包络；只写 `t=0` 会向默认终点插值，不能形成常量。harness 同时写 `t=0` 和 `t=1`。

| 模式 | 参数 `0x14` | 参数 `0x0d` |
| --- | ---: | ---: |
| 自动 | 默认 `1` | 默认/估计器种子 `75` |
| 外部 220 Hz | `0`（两端） | `220`（两端） |

两秒、220 Hz 合成谐波信号的结果：

| 模式 | 总帧 | 有声 | 无声 | 掩码分布 | probe 解码 F0 |
| --- | ---: | ---: | ---: | --- | --- |
| 自动 | 345 | 128 | 217 | `0x801c6fa6`: 128；`0x800c6b20`: 217 | `96.415`–`290.739` Hz |
| 外部 | 345 | 345 | 0 | `0x801c6fa6`: 345 | `219.708`–`220.969` Hz |

外部约束消除了淡入/淡出区自动估计失败产生的无声帧，说明该分支确实控制输出 F0，而不只是影响一个后处理标签。边缘帧有约 1 Hz 摆动，当前应解释为序列化量化或后续帧处理，不能宣称逐字节保留输入的 220.0。

## writer 字段掩码陷阱

DRS frame writer 输出的字段集合不是只由 `frame.mask` 决定，而是：

```text
serialized_mask = frame.mask & stream.field_mask
```

其中 stream 的 `field_mask` 位于 `+0x20`。全零初始化会过滤掉绝大多数声学字段，只留下 writer 强制加入的位 31 标志，早期得到的 28 字节小帧因此是 harness 初始化错误，不是分析器只产生无声/空帧。

诊断输出必须把 `stream + 0x20` 设为全 1。修正后外部 F0 的 345 帧文件约 2.54 MB，每帧约 7.2–7.5 KB，而不是 28 字节。

## DSE 回读闭环

harness 写完文件后重新打开它，构造一个新的 DRS collection，并调用同一 DSE 的通用 chunk reader。0.5 秒、外部 220 Hz 的一次固定实验输出为：

```text
analyze.result=0
track.total_frames=87
write.result=0 stream_bytes=641288
output_bytes=641288
readback.result=0 stream_bytes=641288 generics=1 total_frames=87
```

writer 计数、文件长度、reader 消费长度和重新构造的 frame 数全部闭合。该验证排除了“只是在内存中看见若干貌似 frame 的对象”这种较弱解释。

## 独立 probe 的边界

`probe_sms2.py` 会：

- 校验顶层 `SMS2` magic 与声明长度；
- 搜索并选择最大的连续合法 FRM2 run，而不是只数裸 `FRM2` 字节串；
- 验证位 0/1/2 的谐波数组、位 4/5 的第二频谱数组、位 6 的 `int16` 数组和位 9 F0 至少不会越过帧尾；
- 对位 31 的音分编码换算为 Hz，把 `-FLT_MAX` 类哨兵统计为无声；
- 汇总时间步、帧大小、掩码、谐波数和第二频谱 bin 数。

它还没有完整解释 DRS 原始帧的所有尾部字段，因此当前是独立结构探针，不是完整 SMS2 重写器。

## DRS 原始帧与最终声库帧

外部 F0 实验的常见 DRS 原始有声掩码为：

```text
0x801c6fa6 = bits {1,2,5,7,8,9,10,11,13,14,18,19,20,31}
```

自动模式无声帧为：

```text
0x800c6b20 = bits {5,8,9,11,13,14,18,19,31}
```

参考传统 DDB 的普通最终帧是：

```text
0x0000002000e00207 = bits {0,1,2,9,21,22,23,37}
```

所以 DRS 输出虽已包含基频、谐波相位、频谱、共振/包络类中间量，但不能直接复制进 DDB。后续工作已经定位并调用了这条有实质计算和字段筛选的 DRS→DSE5 普通帧转换链；本节保留原始掩码对照，最终单元闭环见 `08_minimal_stationary_bank.md`。

## 当前结论边界

已经达到：程序生成的自有 PCM 可重复地产生完整 DRS 分析结果；外部 F0 约束已实测；DSE writer/reader 和独立 probe 三方对帧数、长度、时间、字段掩码相互印证。

后续已经达到：DRS→DSE5 最终普通帧转换、一个 STA 的树/DDB 构建和 DSE 原生回读。

仍未达到：真实人声验证、正式音素/region 外部标注注入、ART 转接树、完整覆盖以及宿主渲染。因此这仍不是“已经可以自训”的完成声明。
