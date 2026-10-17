# 独立选样时长：timing 漂移与恢复不一致的根因

2026-10-17，延续[评分原型](independent-duration-selection-prototype.md)。本轮只修改独立研究进程的后端和诊断工具，没有接前端、启动 Editor 或部署。

**所测组合的三条故障路径已分别定位并修正。** 增强选材实验的正序、倒序共 240 次渲染，以及原生候选范围的 120 次渲染全部通过。每个音符的 VSM 边界、实际原生音素接点、重复档位和恢复检查均通过。两次独立进程以相反顺序执行夹具，对应档位的 PCM 逐字节相同。

完整数字及原始报告摘要见[可核验的证据](evidence/duration-selection-root-causes.json)。结论限于这批独立进程夹具，不等于已完成宿主并发和通用功能接入。

## 二进制与入口

通过 Ghidra 当前目标元数据和实际只读反编译/反汇编核对：DSE.dll 6.13.1.1，x64，映像基址 `0x180000000`，SHA256 `7A4965BD377DBC2B08710E807AE1345B67D9FFF6579CA0C01A769F744366F5B5`。以下均为该映像 RVA，不能直接套到其它版本。

| 路径 | RVA | 本轮核实的作用 |
|---|---|---|
| 最终分配 | `0x19D650` | 返回前置余量及半段长度；RAX 指向输出结构 |
| 接点整形 | `0x1B3D30` | 按素材 alignment、音素类别重新分配接点两侧时长 |
| 静音插入 | `0x1A7310` | 提交音符时插入素材相关的前置静音 |
| 音色校正器查询 | `0x182220` | 返回同时含声库表引用和可变历史的对象 |
| 有声校正/历史更新 | `0x1C44F0` | 更新两个元音索引、来源音高、来源动态和过渡比例 |
| 无声频谱校正 | `0x1C5220` | 读取上述历史，入口不更新它 |
| 校正器构造 | `0x1C4240` | 给出五项可变字段的新会话初值 |
| 层间混合 | `0x1722C0` | 读写 DLL 全局平滑历史，RVA `0xA20050` |

## 根因一：评分时长隔离了，真实时间计划仍依赖新素材

候选工作副本计算假设时长的代价，没有直接写真实 NOTE；但选中另一条录音后，最终分配仍使用新素材的内外段长度，接点整形又会移动两侧分配。分配器还返回一项前置余量，提交链另算前置静音。因此“没有修改音符 Duration”不能保证音素 timing 不变。

乐正绫 `ts\_h i iE_r @N` 原边界为 `[-108,-2,756,884,956]`，256 帧档曾变为 `[-142,-2,717,845,956]`。MIKU 上行接续还有另一条路径：即便固定分配结果和接点两侧长度，新素材的前置静音仍会少 2 帧，使整组音素提前。

[DurationTimingContract.cs](../voicebank/tools/evec_render_harness/DurationTimingContract.cs) 将分配结果、前置余量、接点整形和提交静音长度归入同一份真实时间计划。选材保留新素材描述符；真实分配在进入原生取样/帧映射前使用计划。流式运行时补入的一帧静音不当作音素段长。

计划从现有输入准备器和独立原生 planner 生成，无需渲染基准音频。每组夹具先把计划与真实渲染观察到的分配逐项核对，再用 planner 的计划执行所有档位。拓扑或提交次序不匹配会使探针失败，不猜测匹配、不改用另一份音频。

这是合成前的时间契约，没有改工程位置、长度、VEL、音素或控制器，也没有在 WAV 上移动、拉伸或补齐边界。

## 根因二：无声辅音读到了上一轮渲染的音色历史

乐正绫恢复失败时，输入事件、素材及 stage、逐帧取样位置、目标音高和音素边界均已恢复，起音却不同。直接记录 DSE 输出证明差异已经存在于原生 PCM，与编辑器波形缓存或 WAV 复制无关。

逐层观察定位到无声频谱校正：读出的素材 PCM/频谱相同，校正器保存的来源音高和过渡比例不同。有声处理更新这些字段，无声处理读取它们。新一轮起音尚未进入有声段时，会读到上一轮尾部值。现场中来源音高曾从 `-919.06055` cent 变为 `-203.64296` cent，比例从约 `0.8` 变为 `0.8636364`。

[DurationEffectScope.cs](../voicebank/tools/evec_render_harness/DurationEffectScope.cs) 为每次渲染建立独立的可变头部，保留声库表引用；初值取原生构造流程，不取上一次 A 的快照。三个已审核的查询调用者使用该头部，所测处理函数只访问头部字段及共享表。每轮检查共享对象的五项历史未被修改。

修正后，冷启动、重复和恢复 PCM 相同。不能以已经被上一轮历史污染的 warm 输出为基准。撤掉此修正、保留其它两项，会重现 `CFB49F… → C1387D…` 的恢复失败；冷启动则与修正后的 `42D96F…` 完全相同。

## 根因三：层间混合的平滑历史存放在 DLL 全局变量

MIKU 上行接续的 64 帧档，在素材、所有边界、逐帧位置、输入 PCM、FFT 窗和 FFT 结果都相同时，重复渲染仍有 13 个 PCM16 采样点各相差 1，位于文件内约 `990.204–997.438 ms`。

定位到 `0x1722C0`：混合保护比例的平滑历史存入 DLL BSS `+0xA20050`，读写引用均在本函数，渲染重开没有将它变为独立会话状态。旧历史影响第一帧的归一化倍数，产生这组小差异。

[DurationLayerScope.cs](../voicebank/tools/evec_render_harness/DurationLayerScope.cs) 在渲染内持续保存自己的历史。调用期间持有原生递归互斥锁，交换入本次值，返回后保存更新值并还原外部值；每轮还检查外部值已恢复。没有逐帧清零或关闭混合。

仅撤掉此项，13 点差异原样重现；启用后同档位逐字节相同。没有扩大 PCM 容差、舍入或降精度。

## 原生调用边界

[DurationPostCallHook.cs](../voicebank/tools/evec_render_harness/DurationPostCallHook.cs) 使原函数沿原栈执行，托管观察前后保留 GP/SSE 寄存器、MXCSR 和原生返回值。`0x19D650` 在 RAX 返回输出结构地址；静音入口中的条件跳转也做了明确重定位，没有直接复制原 rel32。

旧低层转发实验未单独重放到足以证明其唯一 ABI 错误，本轮不据此声称引擎随机不稳定。当前桥的无修改档位通过了严格 PCM 对照。桥只用于固定哈希、串行独立进程，内部上下文不是可部署的多线程 Hook，没有注册到补丁启动或生产原生 ABI。

## 回归与性能

10 个夹具覆盖 MIKU、RIN、乐正绫及另一中文采样库，包含 `k a`、`k a i`、`l a`、`ts\_h i iE_r @N`、`s\ i_n n`、上下行双音符接续和固定 PIT 扫动。

| 对照 | 档位渲染数 | 结果 |
|---|---:|---|
| 1200 cent 窗口、时长权重 128，正序 | 120 | 全部边界、重复及恢复通过，54 次素材和音频改变 |
| 相同参数，新进程倒序 | 120 | 再次通过，对应档位 PCM 与正序完全相同 |
| 原生候选窗口、权重 1 | 120 | 无换样、无音频改变，全部边界及恢复通过 |
| 去掉真实时间契约，保留两项历史隔离 | 12 | 乐正绫 8 个档位漂移，重复 PCM 稳定 |
| 去掉音色历史隔离，保留另两项 | 12 | timing 不漂移，恢复时 PCM 再次失败 |
| 去掉层间历史隔离，保留另两项 | 10 后严格检查停止 | timing 不漂移，64 帧重复出现 13 点差异 |

30 次只规划查询的 `Start/Step/Samples` 增量全部为 0；最小 `0.253 ms`、中位 `0.364 ms`、最大 `4.301 ms`。这是已准备输入、初始化独立引擎后的耗时，不含工程输入准备和一次性初始化。严格矩阵的输入准备约 `2.3–12.5 ms`，私有引擎初始化约 `50.5–70.8 ms`，分别记录，不摊入每档查询假装零成本。

增强矩阵自动档 A 固定使用扩大后的候选范围；`chinese-multi`、`miku-ka-down` 的 A 本身不同于原生候选基准。恢复比较同一配置下的 A，真实 timing 仍约束为原生计划。原生窗口严格矩阵另行验证原生基准，不混淆两种配置。

六项目标音高字段在共同有效帧上的最大数值差为 0 cent；部分换样档位尾部仍有最多 4 项有效性标记不同，未宣称完整音高状态相同，也未以散列代替试听验收。

## 重放与边界

```powershell
dotnet build voicebank/tools/evec_render_harness/EvecRenderHarness.csproj -c Debug
$env:VOCALOID_TIMING_PROBE_NATIVE = Join-Path ${env:ProgramFiles} 'VOCALOID6/Editor/VOCALOIDPatcher/native/v6patch_clock.dll'
$env:VOCALOID_DURATION_EFFECT_SCOPE = '1'
$env:VOCALOID_DURATION_LAYER_SCOPE = '1'
$env:VOCALOID_DURATION_TIMING_CONTRACT = '1'
$env:VOCALOID_DURATION_PLAN_ONLY = '1'
$env:VOCALOID_DURATION_PITCH_WIDTH = '1200'
$env:VOCALOID_DURATION_WEIGHT = '128'
$result = Join-Path $env:TEMP ('duration-root-' + [guid]::NewGuid().ToString('N'))
dotnet voicebank/tools/evec_render_harness/bin/Debug/net8.0-windows/EvecRenderHarness.dll --independent-duration-probe --patcher-dse-initialization $result
```

`VOCALOID_DURATION_FIXTURE` 可只选一个夹具；`VOCALOID_DURATION_FIXTURE_ORDER=reverse` 改为倒序。严格矩阵去掉窗口变量并将权重改为 1。三项修正可独立置 0 做消融，失败记录先保存再停止。`VOCALOID_DURATION_TRACE=1` 才启用逐阶段声学/FFT/混合诊断，性能统计不启用这些重诊断。

[汇总脚本](../voicebank/tools/summarize_duration_root_regression.py) 接收 `--forward`、`--reverse`、`--strict`、`--ablations` 四个目录和 `--output`，重验全部条件，生成去除地址及填充字节的证据。原始 WAV 和诊断报告留在仓库外，未提交声库数据或工程。

整个 Debug 解决方案及独立工具构建通过，零警告、零错误，安装 DLL 哈希不变。未验证宿主并发、多 Part 生命周期、所有音高曲线/声库类别，未接前端；扩大窗口及高权重仍是诊断条件，不能直接作为产品默认值。
