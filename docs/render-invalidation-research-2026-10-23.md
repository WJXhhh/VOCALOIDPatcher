# 重复渲染与失效范围：第一轮定位

日期：2026-10-23。接续 [工作集 A/B](working-set-render-ab-2026-10-23.md)。

后续实现与回归见 [Part 失效范围合并与内部提交通知隔离](render-invalidation-batching-2026-10-23.md)；本文保留修改前的计数与研究结论。

本轮已在源码移除渲染完成后的自动工作集修剪，完成构建和验证；同时用真实 6.13.3.1 原生引擎和实际补丁方法定位重复失效准备及控制器编辑后的合成工作。**没有部署或更改渲染调度策略。** 结构化证据见 [实验结果](evidence/render-invalidation-research-2026-10-23.json)。

## 已落实的工作集策略

- 删除 `AudioPlayer.OnRendererCompleted` 上的 `RenderCompleteTrimPatch`，同时从 `Patcher.ApplyPatches` 移除注册。
- `WorkingSetTrimmer.Install` 的最小化事件与原有 `TrimWorkingSet` 开关继续使用。
- 本次只移除渲染完成触发；原有工程载入完成触发保持，默认值和用户设置未改。
- 没有在安装目录替换文件，没有启动或关闭用户 Editor。

## 实验方法与边界

新增 `evec_render_harness --render-invalidation-probe`，要求显式提供 `PatcherIsolatedValidation=true` 的程序集。在加载程序集前检查其身份，避免执行生产模块初始化器。全部工程、音符和控制器都是隔离进程创建的合成夹具。

探针有三部分：

1. 反射调用实际的 `SampledTimingHost.InvalidateTargets`，用仅计数的 Harmony 钩子记录原生 wrapper 的 Commit、Velocity setter、开始/停止请求与刷新入口。
2. 安装实际的 `ObserveNativeInputs`，观察真实 VSM 通知如何标记两个 Part。
3. 启用原生异步服务后直接提交编辑，记录真实 started/completed/canceled 事件、DSE 计算与两个 Part 的 WAV 摘要。

没有安装整套生产 Commit 触发补丁或 Editor UI。因此第一部分测到的 Commit/刷新次数不是完整宿主任务数，不能据此声称每次提交都进行了音频合成。原生计数器与程序集摘要均在证据中。

## 发现一：失效准备按目标音符重复执行重叠窗口

`Flush` 取出 `entry.Pending`，逐地址调用 `InvalidateTargets`。每个有效地址都会取目标及前后各最多 16 枚音符，调用 `InvalidateRenderedPart`，再请求一次波形缓存刷新。

后者对每枚音符临时改变 Velocity，再恢复原值，最后 `Commit(false)`。`Pending` 去重了地址，没有去重相邻目标的上下文窗口。

| Part 音符数 / 目标 | 实际 Commit | Velocity 写入 | 不同音符数 | 刷新请求 |
| --- | ---: | ---: | ---: | ---: |
| 12 / 中间一枚 | 1 | 24 | 12 | 1 |
| 12 / 相邻三枚 | 3 | 72 | 12 | 3 |
| 12 / 全部 | 12 | 288 | 12 | 12 |
| 33 / 全部 | 33 | 1634 | 33 | 33 |
| 64 / 全部 | 64 | 3680 | 64 | 64 |
| 64 / 两端各一枚 | 2 | 68 | 34 | 2 |

共覆盖 3/12/33/64 音符、单目标/相邻三目标/全部/两端四类，共 16 组。全部恢复原始 Velocity，无未提交状态，也没有触碰同 sequence 的另一个 Part。

所有这类准备调用期间 **DSE Start/Step 均为零**，托管渲染开始/停止请求也为零。因此已确认的是重复失效准备、提交与刷新入口，不是“33 次真实合成”。刷新入口在隔离进程没有 WPF 窗口，不代表实际执行了 33 次全画布绘制。

它确实可能由一次用户操作扩大：`SampledTimingHost.SourceChanged` 会把脏 Part 内已保存的时值、时长与过渡地址并入 Pending。如果一个 Part 有多枚受控音符，一次原生输入编辑可能让多个重叠上下文同时待刷新；并非所有普通工程每次编辑都会达到表中的“全部目标”情形。

优先优化方向是按 Part 合并同一轮已验证目标的覆盖集合、去重触碰、合并刷新。不能未经验证就把 ±16 邻域缩成单音符；33 音符的原生入口约束、长 Part 的分批、旧地址过滤、异常恢复和撤销仍须保留。这轮没有把该方案接入生产。

依据：`SampledTimingHost.Flush/InvalidateTargets/SourceChanged`、`Backend/SampledTimingVsmBinding.InvalidateRenderedPart`。

## 发现二：内部提交的屏蔽没有覆盖整个通知扇出

`entry.Rebuilding` 使时值 Host 自己忽略内部提交，但 Commit 之后仍有其它入口：

- `ConsonantTimingCommitPatch` 继续进入 `ConsonantTimingRuntime.SourceChanged`；即使 Host 内部提前返回，Runtime 仍会推进 revision、清已有后端缓存并通知覆盖层。
- 同一 postfix 继续通知 `PhonemeVolumeHost.SourceChanged` 和 `TransitionSmoothingHost.SourceChanged`。这两者遍历本 sequence 已跟踪 Part，重新排队并清对应结果缓存，接口没有携带实际变化的 Part 集合。
- `AceParameterCommitMappingPatch` 在 Commit prefix 请求 `AceAcousticHost.SourceChanged`；后者有按 source 的合并，但会扫描符合条件的 Part。它还会检查输入指纹，不能把每个请求都当成一次最终 DSP。
- 扩展拼音的 Commit 入口会请求扫描；目前已有去抖、内部 sequence 排除和 render-start 的 pending/job/override 保护，不能因出现多处入口就判定扫描一定生成了多份内部音源。

这些是代码核对到的放大入口，尚未用完整宿主链路量出各自实际开始任务数。已有去抖/generation 能丢弃很多过期请求，**不能把上表的 Commit 数直接乘成 DSP 或 Comp 渲染次数**。

合理的后续结构是：在事务边界生成一次不可变的变化快照，含 Part、变化类别及是否为补丁内部缓存失效；各 Host 只处理相关范围。实际 tempo/未知归属等全局输入保留保守路径，内部提交不再被所有消费者当作独立用户编辑。

## 发现三：正常原生输入的归属可以保持局部

真实 VSM 通知配合实际 Host observer，两个 Part 的结果如下：

| 操作 | 标脏的 Part |
| --- | --- |
| 修改 Part 0 音高、Velocity | 仅 0 |
| 在 Part 0 插入控制器 | 仅 0 |
| 删除前记录控制器归属 | 仅 0 |
| 删除时没有归属记录 | 0、1 |
| 修改 Part 名称 | 无 |
| 插入 tempo | 0、1 |
| 无 staged 数据的 Commit | 无；本次原生返回 false |

未知归属删除的全量回退是已证实的范围扩大路径；tempo 的全局影响则是必要语义。没有证据表明所有音符编辑都会经这套 observer 把全工程标脏。删除已移除对象的 Parent 仍不能读取，不能为缩范围取消生命周期保护。

另一个容易误判的点：Rust `timing_cache::fingerprint` 明确排除指针、epoch 与游标；`sampled_timing::cache_key` 由原生 key 和内容指纹组成。仅看到重新发布 epoch，不能断言未改 Part 的缓存 key 必然变化或 PCM 必然重算。

## 发现四：ACE 中性载体仍触发实际原生合成

在 `Luo_Tianyi_Wan`、12 枚音符、两个 Part 的隔离夹具中，先完成两个 Part 的基线渲染，保留原生异步服务开启。之后每次只调用 Commit，不再显式调用托管 `StartAsyncRendering`。

| Part 0 编辑 | 真实开始/完成，Part 0、1 | DSE Step | Part 0 音频变化 |
| --- | --- | ---: | --- |
| 一枚音符 Velocity | 1、0 / 1、0 | 3495 | 有 |
| 插入 WeightHmm 控制点 | 1、0 / 1、0 | 3938 | 无 |
| 插入 S5Air 控制点 | 1、0 / 1、0 | 3938 | 无 |
| 插入 XSynth 控制点 | 1、0 / 1、0 | 3938 | 无 |
| 插入 Expression 控制点 | 1、0 / 1、0 | 3938 | 无 |

四类载体控制点依次加入，每次与加入前的 WAV SHA-256 比较；不代表全部种类分别从空控制器状态独立测量。没有取消事件，Part 1 没有开始渲染，其 WAV 摘要始终不变。证据保存了每次前后摘要。

这说明本夹具中存在“音频不变，但原生仍做了合成”的明确成本。载体的声学中性不意味着原生调度会忽略它；与已有 [ACE 载体约定](ace-parameters-integration.md) 一致。与此同时，Commit 可以自行唤醒已开启的原生异步服务，**只统计托管 StartAsyncRendering 调用会漏掉实际工作**。

可继续研究声学无关载体编辑的原生缓存命中或有效 PCM 复用。不能简单吞掉 Commit/原生启动、伪造缓存有效位或改回其它载体：需要同时保持原生 Undo/Redo、VPR 归档、音符/tempo/声库变化、原生同名参数共享、时值与导出就绪语义。这里仅覆盖一个 V5 夹具，没有新增 V4/V6/AI 或完整 Patcher DSP 验收。

## 后续顺序

1. 先合并一个 Part 内同一轮重叠失效准备，并证明最终覆盖集合与现有行为一致；以 Commit/写入/刷新次数和最终音频、时值几何作对照。
2. 给内部提交与外部编辑统一标记，让音素音量、过渡、ACE、覆盖层和 MCP 各自按实际变化范围处理；保留安全的全局回退。
3. 单独验证 ACE 载体编辑的缓存路径。这比继续压缩十几毫秒的工作集成本更值得研究，但本轮没有可宣称的端到端提速比例。
4. 在用户授权的宿主测试中记录 Part/revision/epoch/来源、实际 native started/completed/canceled、内部源与后处理实际开始；区分请求合并、取消重启、缓存命中和真正重复 PCM。

## 验证与复现

- 整个解决方案 Debug / Release 使用独立 artifacts 目录构建，均 0 警告、0 错误，Release ILRepack 成功。
- 现有 `VOCALOIDPatcher.McpTests`：472 项通过，0 失败、0 跳过。
- 隔离探针：16 组失效准备、9 组通知归属、5 组实际渲染检查通过，输出及证据断言已核对。
- 没有安装全局诊断热路径，没有修改用户工程、用户设置或授权状态。运行中的 Editor 曾可只读查询，后续实例已不可用；本轮未采到可用于逐编辑关联的宿主事件，未操作其生命周期。

```powershell
$scopeArtifacts = Join-Path $env:TEMP ('v6patch-render-scope-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
dotnet build VOCALOIDPatcher/VOCALOIDPatcher.csproj -c Debug -p:PatcherIsolatedValidation=true --artifacts-path (Join-Path $scopeArtifacts 'isolated')
dotnet build voicebank/tools/evec_render_harness/EvecRenderHarness.csproj -c Release --artifacts-path (Join-Path $scopeArtifacts 'harness')
$env:VOCALOID_SCOPE_PROBE_PATCHER = Join-Path $scopeArtifacts 'isolated/bin/VOCALOIDPatcher/debug/VOCALOIDPatcher.IsolatedValidation.dll'
$env:VOCALOID_TIMING_PROBE_NATIVE = (Resolve-Path 'native/playback-clock/target/release/v6patch_clock.dll').Path
& (Join-Path $scopeArtifacts 'harness/bin/EvecRenderHarness/release/EvecRenderHarness.exe') (Join-Path $scopeArtifacts 'results') --render-invalidation-probe
```

原生计数器 DLL 必须已构建、ABI 匹配且支持当前引擎版本。探针不用于加载生产程序集或直接附着 Editor。
