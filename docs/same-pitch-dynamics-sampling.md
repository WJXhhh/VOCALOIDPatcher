# 同音、同音高的力度分层：引擎能力与实际声库

2026-10-17。针对“采样声库没有同音的大中小力度采样机制”进行只读核查和独立原生试验。这里将“同音”限定为相同完整音素/过渡 key、相同录音音高坐标，避免把不同音高层、不同产品音色或 EVEC 后缀误算成力度层。

## 结论

**“当前 DSE 没有按力度选择同音同音高素材的能力”已被原生反例否定；“本机这些商业声库已经提供同音大中小三档录音”则没有证据支持。** 二者不能混为一谈。

1. 为同一 ART key 构造三条 pitch、alignment、时长均相同，仅 dynamics 为 0.2/0.6/1.0 的候选。真实直接选择器和联合选择器均随目标 dynamics 改变实际选中的 part。63 组检查零差异，独立进程重复结果逐字节相同。这是候选选择机制的证据，不是三份商业录音或最终合成声音的证据。
2. 重新只读解析本机 15 库全部 STA/ART/VQM 元数据，共 81,487 个 part，dynamics 全为 float32 0.6；没有一个完整 key 同时包含不同的 dynamics 值。没有发现由这个坐标标注的强、中、弱层。
3. 中文库和 MIKU 的 4 个真实 SDK 夹具、24 次渲染中，DYN 从 16 改至 127，音频 RMS 明显变化，但实际消费的 ART 来源序列和候选 dynamics 范围不变。恢复 DYN 64 后 PCM hash 一致。不能把 DYN 响度变化解释为力度录音切换，也不能从“没换 ART”进一步断言所有后级处理只是常量乘法。

本轮未部署、未启动/关闭 Editor、未修改用户工程、安装声库或 Ghidra 项目。

## 本轮重新确认的目标与原生证据

Ghidra 当前 GUI 目标 `/v6/DSE.dll`，x64，映像基址 `0x180000000`，SHA256 `7A4965BD377DBC2B08710E807AE1345B67D9FFF6579CA0C01A769F744366F5B5`。成功读取 dynamics 排序器 RVA `0x1970D0` 和索引候选筛选 RVA `0x1AB350` 的反编译；这些函数确实读取 part `+0x16C`，不是仅有一个无人消费的默认字段。

此前 [标量消费研究](../voicebank_doc/117_part_scalar_consumers_delivery.md)已验证字段读写、最近值、0.15f 边界和联合评分中的力度距离。本轮新增试验专门排除音高、长度的干扰：

- `probe_same_pitch_dynamics.py --dll ...`：三条目标候选全部 pitch=0、总长 12 帧、两侧 5/7 帧、相同 alignment、tempo=90，只有 dynamics 不同。直接路径另设音高范围外的保护候选以显式限定搜索边界。
- 直接选择 RVA `0x1B8F40`：6 种候选排列 × 9 个力度目标，54 次。目标 0.2/0.6/1.0 分别选中对应的同音高候选，排列不改变该结论。
- 联合路径 RVA `0x18D790 → 0x1AA0A0 → 0x1ACDA0`：真实构建同一 `a→b` 的候选索引，9 个目标力度全部选中预期候选，分配保持 5/7 帧，素材字段未变。
- 0.39/0.40 选 0.2，0.41 选 0.6；0.79/0.80 选 0.6，0.81 选 1.0。该结果针对本夹具的 float32 算法，不定义用户界面刻度。

0.2/0.6/1.0 是自有夹具标签，不是厂商“大中小”的命名或官方强度单位。引擎接收的是浮点候选坐标，不能据此说系统硬编码了三档，也没有在本轮证明力度层之间存在连续混合或交叉淡化。

## 15 库元数据复查

输入沿用 [479 哈希清单](../voicebank_doc/evidence/479_v5_v4x_cross_reference.json)，每库 DDI 和公开解析器 SHA256 均重新核对。只读 DDI，没有打开 DDB 或导出商业 PCM。普通双音素、三音素和带后缀 token 分开保留完整 key。

| 范围 | STA | ART | VQM | 合计 | dynamics 不同取值数 |
|---|---:|---:|---:|---:|---:|
| 七套中文 V5 | 836 | 56,051 | 7 | 56,894 | 1 |
| 八套日语 V4X | 688 | 23,881 | 24 | 24,593 | 1 |
| 合计 | 1,524 | 79,932 | 31 | 81,487 | 1 |

唯一位型为小端 `9a99193f`，即 float32 `0.6000000238418579`。

对每个相同完整 key，另统计跨度不超过 75 cent 的候选窗口，保留有音高与无音高哨兵的区别。有限的普通录音音高坐标（本次统计范围 -20000..20000 cent）中，没有任何窗口含三个候选；少数含两个候选，它们 dynamics 仍完全相同。这只是可复现的近音高统计，不是厂商录音层归类算法。

确实存在同一 key 有三、四条 `pitch1=-FLT_MAX` 素材，例如部分 `Sil→p` 和日语 `g→g`。它们的 pitch1 是“无音高”哨兵而非相同的实际演唱音高，dynamics 又相同，所以仅凭数量不能命名为强、中、弱层。也不能反向声称这些录音的实际发声力度完全一致；本轮没有厂商制作标注或针对这种分类的声音验证。

Soft/Power 等不同产品、EVEC 的编号音素是另一些显式来源维度。即使它们带来更柔或更强的声音，也不自动等于同一个 key 随 DYN 切换的三档力度采样。

## 实际 DYN 渲染

独立 SDK harness 入口 `--dynamics-selection-probe`，固定音高 60、VEL 64、音符位置和长度均 960 tick，分别使用：

- 中文 `Luo_Tianyi_Wan`：`a`、`p_h a`。
- MIKU V4X Original EVEC：`a`、`k a`。

各夹具 DYN 顺序为 16、32、64、96、127、64。每次强制失效原生渲染缓存，记录实际消费的来源，并观察原候选筛选函数；观测钩子的 pitch 扩大选项保持 null，不改变候选或评分。

| 夹具 | DYN 16 RMS | DYN 64 RMS | DYN 127 RMS | ART 来源切换 |
|---|---:|---:|---:|---|
| 中文 a | 0.003158 | 0.009302 | 0.030345 | 无 |
| 中文 p_h a | 0.003180 | 0.009237 | 0.029606 | 无 |
| MIKU a | 0.005732 | 0.016242 | 0.051777 | 无 |
| MIKU k a | 0.005525 | 0.015428 | 0.048733 | 无 |

候选筛选入口观测到的 low/high dynamics=0/0.5、width=0.075 在这些变体中保持不变。它是特定调用现场，不能擅自把该标度直接当成 Editor DYN 的归一化定义。24 次渲染不能覆盖其它参数、声库、音区、连读和产品版本；没有将所有可能输入都排除。

## 对过渡力度功能的影响

数据模型应保留“过渡音量包络”和“素材力度偏好”两个独立语义。前者可以明确作用于音频增益；后者只有在实际候选包含经验证的力度差异时才有可用来源，不应给这些只有一个 dynamics 值的 key 虚构三档录音。

现阶段仍可继续做过渡 timing 和音量包络。要承诺真实咬字/发声力度控制，还需在具体声库找到相应可消费来源或验证合成音色参数；不能用 dB 改名实现，也不能因本机样本没有标注层而删除底层扩展能力。

结论的边界是：**支持选择机制已证实；这 15 库通过 dynamics 存在三档录音未证实；所有采样声库、所有其它编码方式均不存在该机制未证实。**

## 复现与验证

工具：`voicebank/tools/probe_same_pitch_dynamics.py`、`voicebank/tools/evec_render_harness/DynamicsSelectionProbe.cs`。元数据复查需要传入 479 清单、当前机器声库根目录及固定解析器目录，路径不写入工具默认值。

```powershell
python -B voicebank/tools/probe_same_pitch_dynamics.py --dll <DSE.dll> --output <native.json>
python -B voicebank/tools/probe_same_pitch_dynamics.py --reference voicebank_doc/evidence/479_v5_v4x_cross_reference.json --voice-root <中文根目录> --voice-root <V4X根目录> --parser-root <固定ddb-tools目录> --output <inventory.json>
dotnet voicebank/tools/evec_render_harness/bin/Debug/net8.0-windows/EvecRenderHarness.dll --dynamics-selection-probe --patcher-dse-initialization <新临时目录>
```

原生 63 组在独立进程重复一致；15 库清单与旧哈希统计一致；24 次真实渲染通过恢复检查。Python 语法、harness 和整个 Debug 解决方案构建通过，零警告、零错误。可提交的本轮摘要见[证据](evidence/same-pitch-dynamics-20261017.json)。
