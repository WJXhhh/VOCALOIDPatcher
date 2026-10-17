# V4X 引用全集验收与后续步骤（2026-09-12）

## 本轮完成范围

第一阶段完成：8 套 V4X 的 DDI→DDB 引用全集、VQM 归属及 ART 边界统计。
不包含原生加载、宿主演唱、VQM 消费算法或自有录音验收。

新增 `voicebank/tools/verify_reference_ddi.py`。DDI 树由外部
[ddb-tools 固定版本](https://github.com/yuukawahiroshi/ddb-tools/tree/2e78b11f0f1fd63535f1c67e8aa5096bf4ac94a7)
的 `utils/ddi_utils.py` 解释；与旧七库研究相同，为 MIT 工具。
本轮只在仓库外取得该解析模块，不复制它或商业内容进仓库，不增加发行依赖。

本仓库独立扫描 DDB 顶层与 FRM2 内部边界。每个解析出的 EpR/SND 指针还与
原 DDI 字段逐项核对；注意外部解析器把 ART 的两个 SND 指针减去 18 字节，
审计时恢复该差值。SND 指针前的 u32 同时与 DDB PCM count 核对。

结果只输出统计、结构偏移、DDI 哈希；不输出 PCM、帧负载、歌词或工程内容。
解析模块适用于本轮已安装参考库，不应当作面向任意不可信 DDI 的生产安全解析器。

## 引用集合与归属

| stem | STA 样本 | ART 样本 | VQM 样本 | ART alignment 2组 / 4组 |
| --- | ---: | ---: | ---: | ---: |
| MIKU_V4X_Original_EVEC | 144 | 5,701 | 3 | 4,707 / 994 |
| MIKU_V4X_Sweet | 48 | 1,552 | 3 | 1,552 / 0 |
| MIKU_V4X_Soft_EVEC | 144 | 4,832 | 3 | 3,838 / 994 |
| MIKU_V4X_Dark | 48 | 1,556 | 3 | 1,556 / 0 |
| MIKU_V4X_Solid_EVEC | 120 | 4,457 | 3 | 3,605 / 852 |
| RIN_V4X_Warm | 36 | 1,131 | 3 | 1,131 / 0 |
| RIN_V4X_Power_EVEC | 112 | 3,512 | 3 | 3,086 / 426 |
| RIN_V4X_Sweet | 36 | 1,140 | 3 | 1,140 / 0 |
| 合计 | 688 | 23,881 | 24 | 20,615 / 3,266 |

- 961,577 个 FRM2 的起点集合与 DDI EpR 集合完全相等，每个被引用一次。
- 24,593 个 SND 与归一后的 DDI 样本引用完全对应，每个被引用一次。
- 所有帧 kind 为 1；所有样本的 DDI 采样率与 SND 头一致，PCM count 一致。
- STA 仅使用普通主掩码；ART 使用普通主掩码和 `0x200` 无声帧。
- 每库 246 个 `0xe2287` 帧全部、且仅由 VQMp 引用，合计 1,968 帧。
  因而文档 37 的“仅知布局家族、尚未确认树归属”在本轮已补齐。
- 每库实际有 3 个 VQM 样本，不能把旧中文库的“一个 VQM”推广到所有库。

集合一致只能证明引用覆盖及唯一性；它不能单独证明一个样本选取的素材在语音上正确。
本轮也没有把 3 个 VQM 样本命名为 3 个音高层或力度层。

## 不变量与反例

688 个 STA 均满足：

```text
DDI SND pointer - chunk start = 2066 bytes
PCM count - EpR count * 256 = 2048 samples
```

24 个 VQM 均满足 `PCM count == EpR count * 256`。
全部 ART 的归一化 SND 指向块首，实际核心指针在所属 PCM 范围内。
全部 alignment 均满足 `0 <= start <= start2 <= end2 <= end <= frame_count`，
outer 区间从 0 连续覆盖到 frame_count，没有边界异常。3,266 个样本有 4 组，
其余为 2 组；尚未进一步把每组映射到 EVEC 子音素语义。

**普通 ART 的固定余量和核心偏移不是跨库通用常量。**
23,881 个 ART 中有 8 个不满足旧中文基线的 `PCM count = frames * 256 + 2048`：

| 库 | SND 块偏移 | 帧数 | PCM count | PCM余量（samples） | 核心偏移（bytes，相对payload） |
| --- | --- | ---: | ---: | ---: | ---: |
| Original EVEC | `0x4bd18ef4` | 7 | 3,328 | 1,536 | 1,024 |
| Original EVEC | `0x4bd1c458` | 5 | 2,560 | 1,280 | 512 |
| Original EVEC | `0x865074a0` | 35 | 10,752 | 1,792 | 2,048 |
| Original EVEC | `0x8650d032` | 60 | 16,896 | 1,536 | 2,048 |
| Original EVEC | `0x86515ae4` | 53 | 14,848 | 1,280 | 2,048 |
| Original EVEC | `0x8651d536` | 50 | 13,824 | 1,024 | 2,048 |
| Original EVEC | `0x86524888` | 58 | 16,384 | 1,536 | 2,048 |
| Dark | `0x24e39bd6` | 115 | 30,464 | 1,024 | 2,048 |

这 8 个样本的原 DDI 指针/PCM count、DDB 物理块及 alignment 相互一致。
其余 23,873 个 ART 具有 2048-sample 余量；除表中前两项外，所有 ART 核心
偏移为 2048 bytes。注意 bytes 与 samples 的单位不同。
目前不能由这些反例断言裁剪原因或生成算法，后续应核对帧时间与原生消费者。

审计器把这些关系作为统计观察，不以旧常量拒绝真实样本。
自有数据构建器仍可使用明确约定的 2048-sample 余量；本轮未放宽其训练输入契约。

## 复现与验证

将 `$ddi`、`$dependency` 指向实际 DDI 与上述固定版本的外部目录：

```powershell
python -B voicebank/tools/verify_reference_ddi.py $ddi --ddb-tools $dependency
python -B -m unittest discover -s voicebank/tools -p 'test_*.py' -v
dotnet build VOCALOIDPatcher.sln -c Debug --nologo -v quiet
```

第一条默认读取同 stem 的 DDB，可用 `--ddb` 显式指定。
聚合报告在当前设备 `%TEMP%/v4x_reference_audit_20260912.json`，不作为源码提交。
工具对缺失/越界引用、PCM count 不一致、漏覆盖失败退出；重复引用输出 multiplicity，
只有所有 multiplicity 均为 1 才能宣称一一对应。

新增 7 项合成测试覆盖集合完整性、块内假起点、漏引用、PCM count 不一致、
重复引用统计、ART 指针修正与原字段比对，以及截断/越界 DDB。
加上上轮帧布局测试共 12 项通过，完整 Debug 构建 0 警告、0 错误。

## 分阶段待办

| 顺序 | 工作 | 当前状态与验收点 |
| --- | --- | --- |
| 1 | DDI/DDB 引用全集、VQM归属 | **本轮完成**，范围为当前8库 |
| 2 | ART 边界与音高层 | [文档39](39_v4x_regions_layers_and_pitch2.md)已确认局部region时钟、呼吸例外及非统一层数；[文档40](40_v4x_material_coverage.md)已完成剔除空key的素材图和逐key坐标盘点，运行时消费和完整可用图仍待做 |
| 3 | ART pitch2 取帧规则 | [文档39](39_v4x_regions_layers_and_pitch2.md)的PHDC有声区间候选对19176个双音素ART全吻合；制作端函数仍待确认 |
| 4 | 普通FRM2幅度、ENV、第二共振 | 待做：用DSE和自有测试信号核实数值标度 |
| 5 | VQM缺省频谱消费及生成 | 待做：追踪运行时补全/忽略行为、控制字段和位13特征 |
| 6 | 构建器大规模与失败恢复 | 待做：自生成数据压力回归，单独记录时间/内存/确定性 |

真实自有人声、中文专属覆盖、新组件宿主演唱与正式发行条件仍单独保留，
不以参考库结构验证替代。EVEC/REG 宿主操作属于另一条验收线。
