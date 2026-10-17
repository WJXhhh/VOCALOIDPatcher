# Piapro EVEC 分段构造与 V6 时值接口核验

日期：2026-09-07。本文只记录独立分析结论和自有实现；不包含专有反编译源码。

## 状态与限制

### 最新有声实测：发现链路恢复，固定收音时序仍不合格

用户将现有完整登记的根项 `POCALOID4` 更正为 `VOCALOID4` 后，原生 VDM
无需目录绕过即可发现 8 个声库；用户确认宿主可正常发声。此前手工占位登记
没有保留完整安装元数据，不能等同于恢复原有登记。下文目录适配已撤回的状态不变。

独立 harness 增加显式 `--patcher-dse-initialization`，复用当前已部署补丁的
既有 DSE 初始化实现，避免与宿主采用不同初始化环境。默认不开启；没有新增
登记或改写磁盘引擎。基准 Miku `k a` 输出峰值 0.100128、RMS 0.023120。

Miku Original EVEC、Rin Power EVEC 各 66 个用例：11 种时序场景 ×
无收音/Short/Long × 原样串/分段，共 132 个均返回 NoError、PCM 非静音，
无 PCM16 削波。覆盖 80/125/150 ms 短音符、连音、跳进、100/120 ms 间隔、
60/120/240 BPM 及音符中途变速。此项通过仅证明出声，不代表音质验收通过。

`analyze_transitions.py` 用 10 ms RMS 帧、相对最大帧 RMS 的 -50 dB 阈值
估计 WAV 内有效声音边界，并输出完整包络、PCM 哈希和未归一化 A/B 听音文件。
边界是音频估计，不是 VSM 音素位置；当前 ScoreList 仍为 0 帧。

孤立 1000 ms 音符，逻辑范围 WAV 内 250–1250 ms：

| 声库/收音 | 原样串结束 ms | 分段结束 ms |
|---|---:|---:|
| Miku 无收音 | 1260 | 1250 |
| Miku Short | 1170 | 1140 |
| Miku Long | 1140 | 1120 |
| Rin 无收音 | 1250 | 1250 |
| Rin Short | 1140 | 1100 |
| Rin Long | 1130 | 1080 |

两种构造起音估计一致（Miku 210 ms，Rin 230 ms）；不能据此判断元音入拍。
分段收音比原样串再早 20–50 ms，且 Long 的有效尾端未比 Short 更晚。
这直接否定“照搬 Piapro 的 60 ms 事件位置即可完成 V6 收音”的假设。
下一步需区分 DSE 对 VSil 的预发声/衰减与片段调度，并对声库、音素、音高和
力度做校准；不能根据单个包络阈值给所有音符硬加统一延迟。

连接和预算抑制检查：两库的 gap100、legato、pitch_up、short125、short80
分段版本 Short/Long PCM 完全一致，符合这些场景计划不发出收音事件的预期。
该一致性是抑制回归检查，不是自然过渡的听感证据。分段计划继续仅在独立
harness 中实验，未接入正式渲染器；本次没有部署新的合成时序。

测试产物位于本次系统临时目录的 `v6-evec-transition-audio-miku` 和
`v6-evec-transition-audio-rin`，各含 WAV、事件 JSON、`audio-analysis.json`、
`listening-ab.wav` 与带播放位置的 `listening-ab.json`。A/B 顺序为无收音、
Short、Long、125 ms 短音、连音、中途变速，每组先原样串后分段。

### 当前状态：按宿主反例禁用并回撤

用户在 V6 中确认新枚举的声库集体显示“使用已过期”。这推翻了“目录中能枚举到
就可以接入生产使用”的假设：跳过空 KEYS 的早退只暴露了不完整的组件对象，
没有走通安装元数据、引擎初始化及可用性的完整链路。

已从 `Patcher.ApplyPatches()` 的 EVEC 组移除 `EvecVoiceBankDiscoveryPatch` 注册。
实验源代码暂留用于复查，但主补丁不再启用该适配。核对本轮创建清单后撤回全部
8 个不完整 DATABASE41 登记；删除前逐字段及空 KEYS 校验，无用户后续修改被覆盖。
原声库 DDI/DDB/VVD 文件未改动。该方案暂停，不继续以枚举成功代替宿主发声验收。

禁用版完整 Release 构建 0 警告、0 错误，并已部署；安装/构建 SHA-256：
`BAC42752A3A15BEE4CB06D0F9761CF84A26DD22E3466EB9A98ABAD40A41BCD5E`。
下文“已接入生产”是此前过程记录，不代表当前启用状态。

### 最新补充：声库发现已接入生产并部署

用户指出此前空 KEYS 适配只在独立 harness 中，安装完整 Patcher 并不会自动包含该实验。
现新增 `EvecVoiceBankDiscoveryPatch`，归入默认启用的 `EvecEnabled` 组，在
`DatabaseManagerIF.CreateDatabaseManager` prefix 中进入 `EvecCatalogDiscoveryScope`，
finalizer 恢复原生指令；既有 `DatabaseManagerCreatePatch` 的后置钩子继续使用枚举结果。
该入口只允许已注册 V3/V4 目录缺少 key descriptors，不扫描任意磁盘目录，也不负责自动注册。

范围限于 VDM 6.13.0.1、SHA-256
`73F1E128932521A1BA7DDB4736B59BC2D437E134753A3F3AFA2DF7751D430DB2`，
RVA `0xDC3D0`；同时检查原始指令。只在同步数据库创建期间修改进程内指令，
串行化并支持嵌套调用，退出/异常时恢复。未知文件或指令不符时保留原生回退。
磁盘 VDM.dll 未修改。运行中切换 EVEC 开关不会重建已经创建的数据库；列表需下次启动确认。

`--catalog-scope-probe` 链接生产共用实现，实测原生目录数 `0 → 8 → 0`，
嵌套退出、异常清理、错误二进制拒绝通过。完整 Release 构建 0 警告、0 错误。
发行合并版含 `EvecVoiceBankDiscoveryPatch`、`EvecCatalogDiscoveryScope`、Harmony 类型，
无独立 0Harmony 引用，7,225,856 bytes；已部署到 V6，安装/源 SHA-256 一致：
`989523967A0E331D5B4B61E67F49F2FBA82E47AEE887BD8D78866AAFFD7CC6EE`。
Rust/Cargo 1.98.1 已安装，24 项原生测试通过，原生 DLL 已随完整 Release 部署。

本机经用户授权手工新增了 8 份 Miku/Rin 的 DATABASE41 登记，原库文件未修改。
名称、ID、路径来自已安装 VVD/同名 DDI-DDB；DRP/TIME 是目录实验占位元数据，
不是恢复得到的原厂授权数据，KEYS 为空。目录适配后可枚举 8 份，EVEC 能力识别正确。
此前独立 DSE `k a` 测试仍为 `NoError` 但 PCM 全零，不能当作发声成功。
最新生产宿主的实际发声和 EVEC 分段声音尚待验证；分段 planner 仍仅用于实验工具。

### 前序排查记录

已定位旧时值方案不生效的根因，已实现独立分段实验与真实 VSM 时序验证。
没有完成宿主渲染接入或听感调校，不能宣称机械感已经解决。
本机 VDM 尚未枚举到目标 EVEC 声库，独立渲染预检返回 VoiceBankNotFound。
经用户纠正后已确认 MikuV4x 与 Rin_V4 的安装记录及 VoiceDB 数据目录存在；
Miku Original EVEC 的 CompID 与测试使用的 BCNFCY43LB2LZCD4 一致。
因此不能把枚举失败当作“用户没有安装声库”。当前只读检查发现 VDM 引用的
HKLM 下 WOW6432Node/VOCALOID3、WOW6432Node/VOCALOID4、VOCALOID5 分支不存在，
VOCALOID6/Voice/Components 下也没有该 Miku ID；尚待核对宿主列表与安装登记方式。
该阶段没有修改注册表，也没有启动、关闭或部署 VOCALOID Editor；后续注册及部署见上文。

## 分析对象

当前 MCP 为独立后台 PyGhidra 项目，不共享用户 GUI 当前打开的 Program。
两份安装文件已经导入并成功反编译，无需用户反复在 GUI 打开它们。
地址仅适用于下列文件；换版本必须重新核对，不能据此直接打原生补丁。

| 文件 | 版本 / 架构 / 基址 | SHA-256 |
| --- | --- | --- |
| VOCALOID6 Editor/VSM.dll | 6.13.1.1 / x64 / 0x180000000 | 78EB2D8198855C0046DD1E8AE8141DE609145A239D434F6518F08BE33E369058 |
| Piapro Studio Free Edition/VST/PPS.dll | 0.9.0 / x86 / 0x10000000 | A2195147E050DF823E83F1E29D759C832F691716F6B76C92AC4A60997DA092C6 |

Piapro 安装包含 VSTi 入口、PPS.dll、VSTI.dll、VsqDatabase.dll，以及 OpenVS4、
va_DSE4、va_VSQ4、va_DBM4 和各语言 G2PA 模块。就 EVEC 而言，已确认的关键职责在 PPS：
保存逻辑选项、判断分段时长和邻音上下文、构造供合成的续接音符。DSE 接收最终事件；
把所有音素塞进一颗长音符不能自动复现 PPS 的事件结构。
以上是 EVEC 调用链范围的构造认识，不代表整个 Piapro 已完成逆向。

## V6：旧边界适配无效的证据

- 导出 `VIS_VSM_WIVSMNote_setEditedPhonemePosition`（0x18001bb50）先调用
  0x1800ef180 检查所属轨道；该函数只接受类型 2，即托管枚举 `MidiAi`。
  失败设置 0x19（InvalidTrackType）。Reset 导出 0x18001bbe0 有相同限制。
- 原始音素位置与编辑位置合并路径 0x1800e9510 也受该轨道条件控制。
  可以读到显示位置不等于可以改写 DSE 的发音时间。
- 传统渲染链：0x180050260 → 0x180050eb0 → 0x18005a620 → 0x1800575d0。
  最后一个函数把逻辑音符位置、毫秒长度、音高、Velocity、音素串和表达参数编码为
  MIDI/NRPN 事件，没有使用 AI edited-phoneme-position map。
- 0x180060150 在渲染开始时克隆 Part。渲染副本/事件编码是后续接入候选点，
  但其 C++ 生命周期、线程和增量渲染仍须验证；本轮没有安装未经验证的原生 detour。

真实探针 `--phoneme-timing-probe` 在不依赖声库的私有 sequence 中得到：

| 轨道 | 写边界 | 重置边界 |
| --- | --- | --- |
| Midi | False / InvalidTrackType | False / InvalidTrackType |
| MidiAi（未渲染，无分配位置） | False / InvalidArgument | True / NoError |

这证明轨道能力差异，不证明 AI 或传统声库已经合成成功。
生产 `EvecTimingAllocator` 和 `RestorePhonemePositions` 现跳过传统轨道写入；
同时修正辅助函数在原生写入失败前就把 desired 标为 applied 的问题。

## PPS 的时序契约

主调度 0x102216e0；时长计算 0x10219630；续接事件构造 0x102227c0；
CTop 重组 0x10222cc0。安装内 articulation 配置与先前记录的参数一致。

1. Color 先分出逻辑起点后的 45 ms 起音段，剩余为彩色延音；不是从 V6 自动分配的
   元音起点再推迟 45 ms。Color 单独至少需要 90 ms（起音 45 + 最小延音 45）。
2. Release 从音符逻辑末尾向前保留 60 ms，且以 Color 分配后的剩余时长为预算。
   单独 Release 至少 105 ms；Color+Release 同时生效至少 150 ms。
   Short/Long 使用不同录音标识 *#1 / *#2，不是自动按音符长度互换。
3. 0x101ffc70 比较前后音符的共同语言 ID，并检查后继起点减当前终点是否 <=100 ms；
   满足时不生成 Release。比较对象不是音高或声库名。
   语言判定链为 0x10202c70 → 0x10211050 的五项 translator 表；初始化
   0x1020f3f0 的 RTTI 明确为 Ja/En/Ko/Es/Cn。阈值常量在 0x107b5600，为 double 100。
4. CTop 在起音段内重组辅音，不建立额外固定 45 ms 的 CTop 段。
5. 续接 Color/Release 事件 Velocity=64、Accent=50、Decay=50、上下行 Portamento 关闭，
   保留来源音高、Opening、BendDepth、BendLength，音素保护打开。
   基础音符保留来源表达。此为 PPS 合同，不是“在 V6 中已证明最佳”的参数。

尚未完全闭合的 PPS 条件包括特殊续音标志与用户自定义分割参数。
实验 planner 只接受当前已支持的末尾元音/鼻音结构，拒绝带自定义 coda 的输入，
不擅自推广到任意英语/多音节串。邻音上下文由调用方传入。

## 实验实现与运行

`EvecRenderTimingPlan` 为纯规划器，输出 Base/Color/Release 的毫秒区间与音素，
不改原 `EvecNoteState`。短音符省略实际分段而保留逻辑选择。尚未注册到生产 renderer。
独立 harness 同一 Part 内插入这些事件，让 DSE 处理相邻过渡；没有使用 PCM 硬拼接。
每条边界只经 tempo map 转换一次，段间共享 Tick，末尾保留原音符精确终点。

```powershell
dotnet run --project voicebank/tools/evec_logic_harness/EvecLogicHarness.csproj -c Debug
dotnet run --project voicebank/tools/evec_render_harness/EvecRenderHarness.csproj -c Debug -- --phoneme-timing-probe
dotnet run --project voicebank/tools/evec_render_harness/EvecRenderHarness.csproj -c Debug -- --transition-plan-probe "$env:TEMP\v6patch-evec-transition-plan" miku
dotnet run --project voicebank/tools/evec_render_harness/EvecRenderHarness.csproj -c Debug -- --transition-render "$env:TEMP\v6patch-evec-transition-audio" miku
```

最后的声库参数可用 miku/rin/len；有声实验要求对应已安装且可正常合成的声库。
输出是临时目录内自建案例的 JSON 和（合成成功时）WAV，不改用户工程。
11 种场景 × 两种收音 × raw/split = 44 案例：孤立音、80/125/150 ms、连唱、
音高上行、100/120 ms 间隙、60/240 BPM、音符内部 120→60 BPM。
原始音素单音符和分段版保持同样的原始音高/时长，便于 A/B。

初次原生测试暴露 double 误差：100 ms 间隙读回 100.00000000000009 ms，
导致临界收音误触发。planner 仅对比较加入 1e-7 ms 数值容差，远小于一 Tick；
并补充显式预期段数回归，不能只检查“插入成功”。

## 后续声学与宿主验收

本轮最终验证：

- Debug 完整解决方案构建成功，0 错误；1 个现有环境警告为 Cargo 缺失，播放时钟使用托管回退。
- 纯逻辑回归通过，包括新增 112 组共享预算/邻音组合、收音 ID、非法输入、double 临界误差，
  以及既有 Miku 11,664、Rin/Len 5,184、Luka 900 组有向切换。
- `--phoneme-timing-probe` 通过，结果如上表。
- `--transition-plan-probe` 44/44 通过，含显式分段数量、共享 Tick 和变速图换算检查。
- `--transition-render` 在声库预检明确报告 VoiceBankNotFound，未执行有声 A/B。
- `git diff --check` 无空白错误；没有生成生产 Release、部署或写入用户工程。

声库就绪后先获得非零 WAV 和可用 score，比较 raw/split 的辅音起点、元音颜色切换、
收音长度与邻音侵占、连续同音/异音高是否重复起音。非零峰值不等于自然或优于基线，
需要听感对照。若 V6 仍有提前/延后，须以实际 score/PCM 调整渲染段，不能继续调用 AI API。

只有上述对照成立后，才把规划器接到隔离的渲染副本/事件层，并覆盖后台全量、增量、
导出、取消、重复请求和失败回退。不得为暂时得到听感修改源音符几何或让合成片段污染
选择、歌词、撤销、sidecar 和用户保存工程。
