# AI 声库音区偏移：S5API/VSM 原生渲染链调查记录

> 状态：阶段性调查记录，尚未形成可直接交付的补丁实现<br>
> 记录日期：2026-09-02<br>
> 目标：让现有的逐音符 REG（音区偏移）参数也能用于 AI Part：每个音符在送入歌声模型前按自身半音值偏移音区，同时逐帧补偿模型输出，使工程中的实际演唱音高保持不变。

## 1. 二进制基线

本轮地址和结论只对应以下本机安装文件，不能直接假定适用于其它 VOCALOID 版本：

| 文件 | 文件版本 | 大小 | SHA-256 |
| --- | --- | ---: | --- |
| `VSM.dll` | 6.13.1.1 | 3,675,392 | `78EB2D8198855C0046DD1E8AE8141DE609145A239D434F6518F08BE33E369058` |
| `S5API.dll` | 8.0.0 | 647,424 | `82F12A09F4F0FF220AE4119EB2CE1C64DB8BAE2DA377478EB465DCEE7FBFB87B` |

两者在 Ghidra 中的映像基址均为 `0x180000000`。

## 2. 已确认的总体调用链

完整渲染会先把 VSM 音符转换成 S5 的发音段，再逐帧调用声学模型：

```text
Renderer::start 的 PPL task vtable +0x70
  └─ FUN_180065570
       └─ FUN_18006c6d0
            ├─ FUN_18006db10
            │    └─ FUN_180082bf0
            │         └─ FUN_180083830
            │              ├─ Func_9cbce37f   @ VSM 180083faa
            │              └─ Func_cfc85a30   @ VSM 180084023
            └─ FUN_18006d720
                 └─ FUN_180086740
                      └─ FUN_180085070
                           ├─ Func_417eec3e   @ VSM 1800852e0（每窗口一次）
                           └─ Func_2db29822   @ VSM 180085589（逐帧间接调用；`180085572` 取表槽）
```

`FUN_18006c6d0` 的控制流确认 `FUN_18006db10` 成功完成后，才进入 `FUN_18006d720`。因此音符音区偏移必须在前一阶段完成，输出音高补偿则位于后一阶段。

导唱也复用相同的音符转换函数：

```text
FUN_180060350
  └─ FUN_180076730
       └─ FUN_180088980
            ├─ Func_9cbce37f   @ VSM 1800890c7
            └─ Func_cfc85a30   @ VSM 180089189
```

导唱是独立路径。核心实现可以先只让偏移在完整渲染的 TLS 上下文中生效，使导唱保持原状；是否也应处理导唱，需要单独做宿主行为验收，不能让无 Part 上下文的调用误用其它渲染任务的偏移。

## 3. S5API 动态函数表

`VSM!FUN_18002cf50` 在宿主对象 `+0x230` 保存 `S5API.dll` 模块句柄，并调用 `FUN_180070980(object+0x150, object+0x230)`。`FUN_180070980` 用 `GetProcAddress` 填充位于对象 `+0x150` 的可写函数表。与本课题直接相关的槽位为：

| 表内偏移 | 函数 | 用途 |
| ---: | --- | --- |
| `+0x20` | `Func_9cbce37f` | 发音段数量计算 |
| `+0x28` | `Func_cfc85a30` | 填充发音段数组 |
| `+0x48` | `Func_efe389ac` | Func417 前的输出容量估算；只读末段结束时间，不读音高 |
| `+0x50` | `Func_417eec3e` | 把 `0x28` 发音段展开成逐帧特征 |
| `+0x58` | `Func_236a1ecf` | 为当前 Func417 窗口新建并预热 `0x2e0` Func2d state |
| `+0x60` | `Func_95031259` | 释放该 Func2d state |
| `+0x68` | `Func_52f85f15` | 只读并返回 state `+0x250` 的 status；`0` 成功，非零失败 |
| `+0x70` | `Func_2db29822` | 逐帧声学音高后处理 |

该表嵌入某个 VSM 对象的 `+0x150`；若采用下文的精确双状态补偿，需要原子替换的六项相对该对象分别为 `+0x170`、`+0x178`、`+0x1a0`、`+0x1a8`、`+0x1b0`、`+0x1c0`。`Func52` 的 `+0x68`/对象 `+0x1b8` 保持原函数，只作一致性校验并由 Func236 hook 直接调用；只读的 `+0x48` 容量估算项位于对象 `+0x198`。

完整渲染前后阶段使用的是**同一个表地址**，并非两份仅内容相同的副本；而且在进入完整渲染函数前就能从 renderer 取到它：

1. `Renderer::start(shared_ptr<MidiPart> const&)` 创建的 PPL task 使用 `VSM+0x2c4d28` 的专用 vtable；其 `+0x70` 槽指向 `FUN_180065570`。构造处把 renderer 保存到 task `+0x08`，把 shared_ptr 控制块保存到 `+0x10`；
2. `FUN_180065570(this,arg2,arg3,arg4)` 取 `renderer=*(this+8)`，分配一个 8 字节 holder、写入 renderer，再唯一一次调用 `FUN_18006c6d0(holder,arg2,arg3,arg4)`；
3. `FUN_18006db10` 入口 `18006db56..18006db63` 对 holder 中的 renderer 执行 `p0=*renderer; p1=*p0; host=*(p1+0x28); table=host+0x150`。因此 task wrapper hook 可在调用原 wrapper **之前**按同一链取得函数表；
4. `FUN_18006db10` 令临时 holder 的首指针指向该表，`FUN_180083830` 在入口执行 `table = *param2`，随后经 `table+0x20/+0x28` 调用 Func9/FuncCfc；
5. `FUN_18006ec20` 把这个 holder 写入 `FUN_18006db10` 的输出结构，其中第一条指令级语义就是 `*destination = *source`，所以表地址保持不变；
6. 外层把该输出结构的 holder 传过 `FUN_18006d720 -> FUN_180086740 -> FUN_180085070`；后者再次取 holder 首指针，并经同一 `table+0x50/+0x58/+0x68/+0x70/+0x60` 完成 Func417、state 创建/检查、逐帧 Func2d 和释放；其中 `+0x68` 是无需替换的纯读取检查项。

这条身份链意味着：在 `FUN_180065570` 调用原 wrapper 前替换该表的六个槽位并校验未改写的 `+0x68`，可以同时覆盖**当前这次**完整渲染的前段、完整 baseline/shifted 特征生成和双 state 生命周期；无需修改 `FUN_180083830`、`FUN_18006c6d0` 或 `S5API.dll` 的代码页。

`Func_e8258087` 和相似的 `Func_bb2a2a42` 没有出现在这张 VSM 显式加载表中；VSM 的相关字符串中也没有前者。现阶段不能把 `Func_e8258087` 当作可由 VSM 函数表稳定替换的入口。

## 4. VSM 输入音符记录

### 4.1 记录布局与音高字段

传给 `Func_9cbce37f` / `Func_cfc85a30` 的输入记录步长是 `0x88`。目前确认的关键字段：

| 偏移 | 类型/含义 |
| ---: | --- |
| `+0x00` | `double`，音符/边界段开始时间 |
| `+0x08` | `double`，音符/边界段结束时间 |
| `+0x10` | `float`，相对 A4/MIDI 69 的音高，单位为 cent |
| `+0x14` | `float`，辅助参数；不应由 REG 改写 |
| `+0x18` / `+0x1c` | `int32`，标识/辅助字段；用于配对校验 |
| `+0x20` | `uint8`，真实音符为 `0`，合成边界记录为 `1` |
| `+0x21` | `uint8`，另一标志位；用于配对校验 |
| `+0x24` | `int32`，音素数；有效范围 `0..8` |
| `+0x28` | 8 个固定 8 字节音素字符串的存储区 |
| `+0x68` | 最多 8 个 `float` 的关联值 |

`VSM!FUN_180072580` 从音符对象取得开始/结束时刻，再从虚函数 `+0x60` 取得 MIDI key，然后计算：

```text
pitchCents = (midiKey - 69) * 100
```

随后把它限制在 `[-4500, 2400]`，即 MIDI 24–93，再经 `FUN_180072300` 写入 `0x88` 记录的 `+0x10`。

真实记录的开始/结束时间和工程音高足以作为逐音符匹配的主要键；同音、重叠或渲染窗口裁剪时还需结合原始顺序/ordinal 消歧。不能只假设 S5 输入数组与整个 Part 的音符数组下标一一对应。

`FUN_1800721d0` 生成的合成边界记录使用特殊浮点位模式 `0xff7fffff`，并把 `+0x20` 设为 `1`。任何音区偏移逻辑都必须跳过这类记录，不能对 sentinel 做普通浮点加法。

### 4.2 S5 内部重新量化

`S5API!Func_9cbce37f @ 18006beb0` 对每条输入执行：

```text
midiKey = round(pitchCents * 0.01) + 69
```

关键指令位于 `18006bf45`–`18006bf6c`；乘数常量 `S5API!18008af80` 为 `0.01f`。所得 MIDI key 写入步长 `0x98` 的 S5 内部音符记录 `+0x30`。

`Func_cfc85a30 @ 18006d0e0` 在 `18006d187`–`18006d19c` 重复同一转换，然后调用相同内部处理函数。它把结果写成步长 `0x28` 的输出记录，其中 `+0x10` 接收内部记录 `+0x50` 的分段音高。

`FUN_18001ff70` 构造该内部 `+0x50` 音高时使用的三个 double 常量分别为 `100.0`、`7200.0`、`300.0`，运算为：

```text
segmentPitch = midiKey * 100 - 7200 + 300
             = (midiKey - 69) * 100
```

所以 `0x28 +0x10`、Func417 输出首列和 Func2d 的总音高都使用“相对 A4/MIDI 69 的 cent”，逐帧 `shifted-baseline` 可以直接从 Func2d 总音高相减，不需要再乘 100 或做 Hz 换算。

`Func_9cbce37f` 是计数阶段，`Func_cfc85a30` 是填充阶段。即使当前观察到的计数很可能不随音高改变，也必须让两次调用看到完全一致的偏移输入，避免两阶段结果失配。

### 4.3 输入描述符不能只复制指针和数量

`Func_9cbce37f` 与 `Func_cfc85a30` 接收的输入描述符至少为 `0x18` 字节：

| 偏移 | 已确认用途 |
| ---: | --- |
| `+0x00` | 记录数组指针 |
| `+0x08` | `int32` 记录数 |
| `+0x0c` | `int32`，传给内部处理器 |
| `+0x10` | `uint8` 标志，内部以 `param_2[2]` 读取 |
| `+0x14` | `float`，传给内部处理器 |

Hook 必须按原始字节完整克隆 `0x18`，只替换 `+0x00` 指针。不能临时拼一个只有“指针 + 数量”的结构，否则会静默丢失模型选项。`Func_cfc85a30` 对 `0x28` 输出描述符只访问 `+0x00` 指针和 `+0x08` 低 32 位数量/容量，VSM 调用点也以 `0x10` 字节局部结构传入；shifted 私有调用应复制完整 `0x10`，只替换指针并把低 32 位容量设为 expected count，上半 32 位保持原字节。

## 5. `Func_2db29822` 的输出语义

`S5API!Func_2db29822 @ 180061330` 是逐帧声学音高后处理器。它调用内部模型/核心函数 `FUN_180018c50`，从输入结构 `*param_2` 及其 `+0x74` 起的特征向量取得数据，并将基线与残差分开平滑后重新组合。

目前确认的最终输出：

| 输出 | 指令 | 当前语义判断 |
| --- | --- | --- |
| `param_3[0]` | `MOVSS [RSI],XMM1` @ `1800618f3` | 总音高，单位 cent |
| `param_3[1]` | `MOVSS [RSI+4],XMM10` @ `1800618f7` | 微观音高/残差分量 |

VSM 在 `FUN_180085070` 中分别保存这两个输出，分别重采样，最后成对送给 `FUN_180081820`。调用点的精确形式是：

```asm
180085572  MOV RAX,qword ptr [R13 + 70h]
180085576  LEA R8,[RSP + 170h]    ; 两个 float 输出
18008557e  LEA RDX,[RSP + 88h]    ; 0x18 输入描述
180085586  MOV RCX,RBX            ; Func2d state
180085589  CALL RAX
```

`Func_2db29822` 还维护状态内的总音高和平滑状态。若在函数返回后做补偿，当前最小侵入方案是只修改本帧 `param_3[0]`，不要修改 `param_3[1]`，也不要反写 S5 内部状态，避免把工程音高补偿反馈进模型的跨帧状态。

但补偿量不能再简单认定为当前帧未经处理的 `shiftedPitch-baselinePitch`。`Func_2db29822 @ 1800618bb` 在写输出前唯一调用 `FUN_180017b40`；该函数是两级串联、带历史状态的 biquad/IIR，而不是恒等变换。第一组系数位于 S5 state `+0x298..+0x2a8`、历史位于 `+0x2ac..+0x2b8`，第二组系数位于 `+0x2bc..+0x2cc`、历史位于 `+0x2d0..+0x2dc`。`Func_236a1ecf @ 180060ef0` 分配 `0x2e0` 字节状态，明确把两组非恒等系数写入这些位置并把历史清零。它还把 `+0x270/+0x274/+0x278` 初始化为另一组非恒等一阶递推系数；Func2d 在最终 biquad 前用该递推生成瞬态分量，并按输入行位置、模式和 target pitch 做条件混合。

因此，若最终滤波器入口的 shifted/base 差值等于逐帧 `frameDelta`，其宿主可见输出差应是同一两级滤波器对 `frameDelta` 的响应，而不是原始 `frameDelta`。实现应具备一个 TLS shadow-biquad 诊断候选：为当前 `Func2d state` 维护零初始的 delta 影子滤波历史，按原函数相同的乘加顺序和从 state 读取并校验过的十个系数推进：

```text
filteredDelta[j] = shadowBiquad2(frameDelta[j])
param3[0] -= filteredDelta[j]
```

只修改调用方的 `param3[0]`，不修改 S5 state `+0x254/+0x258` 及其真实滤波历史；这样下一帧神经模型继续看到 shifted 域的内部状态。VSM `FUN_180085070` 的实际顺序是 `table+0x48` 容量估算 → `table+0x50` Func417 → `table+0x58` `Func_236a1ecf` 新建 state → `table+0x68` 初始化/检查 → 对每行调用 `table+0x70` Func2d → `table+0x60` 释放 state。因此真实 IIR 历史按 Func417 窗口从零开始；分配器还可能在下一窗口复用同一个指针地址。影子历史必须属于 `CurrentWindow` 并在每次 Func417 hook 时清零，不能仅按 state 指针跨窗口缓存。窗口内首次 Func2d 记录并校验 state 指针，后续行必须一致；模式 1/2 没有 Func2d 调用，影子滤波器也不推进。

这仍不是纯静态意义上的完全证明：`FUN_180017b40` 前还有神经模型、一阶递推和依赖 `+0x27c/+0x280` 历史的条件混合。若这些阶段对变动中的加性 pitch shift 不是严格平移等变，则最终差值不一定精确等于上述末端影子 biquad 响应。静态上已经能否定“raw delta 必然精确”，也只能把末端 shadow-biquad 视为性能较轻的近似候选。下一节的 `ExactDualState + ExactBaseDelta` 通过完整 baseline 特征、独立原生 state 和两套可观察输出的 base 差覆盖了这些前级与滤波历史，因此是当前静态证据最强的实现候选；raw-delta 与 shadow-biquad 仍应保留为动态诊断/性能对照，最终默认模式仍由宿主 F0、微观表现、音色目标与资源开销共同裁决。

`param3[1]` 的来源也给出一个有用但更侵入语义的诊断对照：`FUN_180018c50` 先让首项包含 base+residual，Func2d 立即以 `local_b8[0]-local_b0` 分离 base，最终再把经过平滑的 base 与 `local_b0` 相加。因此可以在测试构建加入 `BaselineCurvePlusResidual`，令 `param3[0]=baselineCurve[j]+param3[1]`，用来判断误差是否确实来自 base 补偿传递函数。它绕过原生 base 平滑，只能作为定位工具；下一节的 `ExactBaseDelta` 用 `(shadowTotal-shadowResidual)+actualResidual` 恢复原生平滑后的 baseline base，已经是更强的同语义候选。

### 5.1 精确候选：完整 baseline 特征与独立 Func2d state

继续沿 VSM 的 state 生命周期检查后，可以提出一个比 raw delta 或末端 shadow-biquad 更强的候选：在每个 Func417 窗口同时保留完整 baseline/shifted 特征，并让 S5 原函数创建两个彼此独立的 Func2d state。逐帧先用 baseline 行推进 shadow state，再让原函数按正常顺序推进 shifted actual state；把 actual 放在最后可与 9/cfc、Func417 的“最后一次模型调用仍处于正常 shifted 域”约束一致。该执行模式暂名 `ExactDualState`，但宿主可见总音高还需要明确区分两个组合语义：

```text
ExactBaseDelta:      output[0] = (shadowOutput[0] - shadowOutput[1]) + actualOutput[1]
BaselineTotalControl output[0] = shadowOutput[0]
                     output[1] = actualOutput[1]   // 两者都保留 shifted residual 独立通道
```

Func2d 已确认 `output[0] = filteredBase + residual`、`output[1] = residual`。因此 `ExactBaseDelta` 从 shadow 输出恢复原生 baseline filtered base，再加回 actual shifted residual；它等价于只从 actual total 中扣除两套原生 base 的差，最接近传统 REG“补偿音高基底、保留移调后模型微观输出”的语义。`BaselineTotalControl` 则让总音高严格回到未移调 state 的完整输出，适合判断最终 F0 是否能 bit/容差复现 baseline，但它同时丢掉 actual residual 对 total 的贡献，只能作为对照，不能未经宿主 A/B 默认采用。

相关四个生命周期导出的真实 ABI 已由反汇编闭合：

```text
Func236 (s5State, featureWindowDesc) -> Func2dState*  // 实际只有 RCX/RDX 两参
Func950 (func2dState)                -> void
Func52  (func2dState)                -> uint32
Func2d  (func2dState, rowDesc, out2) -> uint64
```

Ghidra 曾把 `Func_236a1ecf` 误推断成四参，但函数入口只保存并使用初始 `RCX/RDX`；后续出现的 `R8/R9` 都是在函数内为 `FUN_18003aa90` 等调用重新赋值。它分配 `0x2e0` 字节 state，用传入的完整特征窗口预热 `state+0x10`，再初始化一阶递推和两级 biquad。`Func_52f85f15 @ 180067b10` 只返回 `*(uint32 *)(state+0x250)`；该字段是 status 而不是正向 ready 布尔值：VSM 在 `180085360..180085367` 执行 `TEST EAX,EAX; JZ normal`，所以 **0 表示成功，非零进入错误码 0x17 与释放分支**。`Func_95031259 @ 18006be30` 清理 `state+0x10`、释放 `state+0x08` 的对齐缓冲并释放 state 本体。VSM 在失败分支 `1800853ec..1800853f3` 和正常分支 `18008563d..180085644` 都经表 `+0x60` 释放，所以 shadow state 可以严格附着于同一窗口生命周期。

传给 Func236 和逐帧 Func2d 的 stack descriptor 都应按 `0x18` 字节完整克隆。VSM 在 `1800852ee..18008532d` 写入 `+0x00` feature pointer、`+0x08` 标志/索引、`+0x0c` float 参数和 `+0x10` 上界；逐帧循环在 `18008555f..180085586` 只更新同一描述符的行指针与 `+0x08`，其余字段沿用。shadow 调用只能替换 `+0x00`，不能重新构造或遗漏 `+0x0c/+0x10`。

`Func_2db29822` 的直接写入全部落在传入 state；唯一的主要模型调用接收 `state+0x10`，末端 IIR 也接收该 state。未发现它回写创建 state 时使用的全局 S5 对象。因此，只要两个 state 分别由 baseline/shifted 完整窗口创建，逐帧双调用会自然复现两套原始一阶递推、条件分支和 IIR 历史，不需要在 Rust 中重写这些私有算法。

Func2d 的返回契约也已闭合：函数尾部固定 `return 0`，VSM 在 `180085589` 间接调用后立即从栈上 `output[0]/output[1]` 取数，完全不读取 `RAX`。hook 仍应保存并返回 actual 原调用的返回字，shadow 的返回字只作版本诊断并应为零；不能把它虚构成可恢复的成功/失败状态。真正的原生异常会按 C++ unwind 离开当前渲染，不能在 Rust 中捕获后继续推进半套 state。

不能用“复制 shifted 特征后只改行首 float”代替完整 baseline Func417。`Func_417eec3e` 除了用 `FUN_180055af0` 展开首列曲线，还把归一化 `0x58` 记录交给 `FUN_18004d960`；后者在 `18004dd3e` 明确读取记录 `+0x50 double` 音高，并把它写入后续 `0x84` 时序/音素特征，Func417 最后又把这些字段复制到输出行 `+0x08` 以后。baseline 与 shifted 行因此可能不止首列不同。

精确候选的窗口协议应是：

1. `Func417` hook 校验容量、stride 和乘法上限，为 baseline 建立同容量的零初始化私有输出；先以原始 baseline `0x28` 调用原 Func417 写入私有输出并验证实际行数，再以 shifted 克隆调用原 Func417 写入 VSM 原输出。这样最后一次模型调用仍处于 shifted 域；若 shifted 调用失败或行数不一致，直接把已经验证的 baseline 特征复制到 VSM 输出并建立 baseline/零偏移窗口，不必第三次调用 Func417。
2. `Func236` hook 先复制 VSM 的完整窗口描述符，只把数据指针替换为 baseline 特征，调用原 Func236 创建 shadow，并立即用保存的原 `Func52` 纯读取函数校验它。shadow 为空或 `Func52(shadow) != 0` 时，直接用保存的原 Func950 释放非空 shadow、把已经验证的 baseline 特征复制回 VSM 输出，再按 VSM 原描述符创建并返回 baseline actual state；本窗口因此完整回退 baseline。shadow 的 status 为 0 后才按 VSM 原 shifted 描述符创建 actual，并同样用原 Func52 预检：
   - actual 为空或与 shadow 不同但 status 非零：释放非空失败 actual、复制回 baseline 特征，把 status 为 0 的 shadow 所有权转交 VSM；
   - actual 与 shadow 指针相同：这是已知分配器契约下不可能的别名，不能假定对象仍是 baseline。只用原 Func950 释放该对象一次并同时清掉两份所有权，复制回 baseline 特征，再调用原 Func236 新建一个 baseline state 返回；
   - 正常 Exact 路径要求两指针非空、不同且 status 都为 0；VSM 随后仍会按原流程再次检查最终返回的 actual。
3. 表 `+0x68` 不需要 hook。`Func_52f85f15` 只有一条语义——返回 `state+0x250` status——所以 task 入口及 Func236 hook 都校验该槽仍等于 GetProcAddress 得到的原函数；Func236 hook 再直接调用保存的原地址预检 shadow/actual，并严格按 `0 == success` 解释，VSM 随后仍按原路径检查最终 actual。这样少一个全局叶子 hook，也消除了“同一次检查该返回 actual 还是 shadow”的控制流歧义；若其它补丁在 scope 中途改写 `+0x68`，当前窗口在调用任何 shadow 原函数前禁用 Exact/baseline 回退。
4. 每个 `Func2d` hook 先克隆行描述符、把行指针改为 `baselineBase + cursor * stride * 4`，用私有两 float 输出推进 shadow；随后以未经修改的原描述符和宿主输出指针调用 actual。两次正常返回时，`ExactBaseDelta` 按 `(shadow[0] - shadow[1]) + actual[1]` 覆盖宿主 `output[0]`，`BaselineTotalControl` 才直接使用 `shadow[0]`；`output[1]` 始终保留最后一次 actual 调用写入的 shifted residual，并返回 actual 的原返回字。所有指针、游标、行宽和 state 配对必须在第一次调用前完成验证；一旦进入任一原 Func2d 就不存在可靠的“中途捕获异常再降级”路径。
5. `Func950` hook 收到已配对 actual 时，先从 TLS 原子语义地取走配对并清空两份所有权，再依次用**保存的原 Func950** 释放 shadow、透传释放 actual；这样即使发生重入或外层 Drop 也看不到旧指针。收到未知指针（包括已转交后不再登记的 baseline shadow）时只调用一次原 Func950。外层 scope guard 只回收因取消/异常未走到 `+0x60` 且所有权仍在 hook 的孤立 shadow，绝不能释放 VSM 拥有的 actual。

这一方案的“精确”边界是：它能在**当前双调用执行环境中**同时取得原生 baseline/shifted 的 total 与 residual，并用可审计的输出代数补偿 base，不再依赖“前级对加性 pitch shift 线性/平移等变”的假设。`BaselineTotalControl` 可直接复现 shadow state 的 baseline `output[0]`；`ExactBaseDelta` 则精确到已观察的 `total-residual` 分解，但 `(shadow[0]-shadow[1])+actual[1]` 仍会受到一次 float 减加的舍入顺序影响，宿主门限不能盲定为全程 bit 相等。继续下钻 Func417 的直接助手也降低了隐藏写回风险：`FUN_18004f2b0` 只从配置对象读取并构造调用方拥有的临时向量；`FUN_180056bf0` 只读权重/形状并写输出；`FUN_180053c20` 和 `FUN_180054ee0` 对模型参数均只读，显式写入只落在传入工作区、环形缓冲、输出和临时向量。两者仍会经模型内部对象的 vtable `+0x28/+0x30` 调用后端算子，反编译无法证明这些更深层算子不更新会话缓存、线程局部状态或硬件后端状态，因此 `Δ=0` 动态 A/B 仍是硬门槛，不能把“未发现直接写入”写成“Func417 无副作用”。

双调用还会大致翻倍 Func417、Func236 预热和逐帧 Func2d 的计算量。Func417 每次顺序申请并释放 `0x1000000` 字节对齐 scratch；顺序双调用不会把这 16 MiB 临时区叠成 32 MiB 峰值，但额外 baseline 特征缓冲会增加窗口常驻内存。正式采用前必须同时通过 `Δ=0` 与未打补丁逐帧 bit/容差对照、非零 REG 的最终 F0 对照以及 CPU/内存/取消压力测试。若性能不可接受，`ShadowFinalBiquad` 仍是轻量候选，但不能再称为数学上精确。

### 5.2 从发音段到逐帧补偿量

VSM 在调用 `Func_2db29822` 前，通过函数表 `+0x50` 的 `S5API!Func_417eec3e @ 180061b10` 把 `Func_cfc85a30` 生成的 `0x28` 发音段展开为逐帧特征。该特征行的第一个 `float` 正是 `Func_2db29822` 读取的目标/谱面音高。

Func417 在当前函数体内对输入描述符只读取 `+0x00` 数组指针和 `+0x08` 低 32 位段数；私有 shifted 调用应完整复制这 `0x10` 字节，只替换指针。输出描述符由 VSM 以 `+0x00` 指针、`+0x08` 低 32 位容量、`+0x0c` 特征宽度构造；hook 不修改它，只在原函数返回后读取被覆盖的 `+0x08` 实际行数。

`0x28` 发音段的已确认布局为：

| 偏移 | 类型/含义 |
| ---: | --- |
| `+0x00` | `double` 开始时间 |
| `+0x08` | `double` 结束时间 |
| `+0x10` | `float` 分段音高 |
| `+0x14` / `+0x15` | 两个 `uint8` 标志 |
| `+0x16` | 8 字节音素/标签区 |
| `+0x1e` / `+0x1f` | padding；正常缓冲清零后也保证后续字符串终止 |
| `+0x20` | `int32` 标识 |
| `+0x24` | 保留/padding，当前转换未写入 |

`Func_cfc85a30` 用 `strncpy(destination + 0x16, source, 8)`，而 Func417 随后从 `+0x16` 调用 `strlen`。VSM 在调用 cfc 前会清零 baseline 输出缓冲，所以即使标签正好占满 8 字节，`+0x1e/+0x1f` 仍提供 NUL。Hook 为 shifted 私有分配 `count * 0x28` 时也必须先全量清零；不能分配未初始化内存后只让 cfc 写已知字段。读取时还应验证 `+0x16..+0x1f` 内存在终止 NUL。

`Func_417eec3e` 不是直接把原始 `0x28` 段交给阶梯展开器。它先把每段复制成内部 `0x58` 记录（原段 `+0x10 float` 音高转成内部 `+0x50 double`），再调用 `S5API!FUN_180005fc0` 做音素归一化：

- 普通记录原样保留，包括 `+0x50` 音高；
- 标签长度为 2 且字节恰为小写 `br` 的记录若是首条，复制后把标签改成小写 `sil`；
- 若 `br` 前一条输出标签长度为 3 且恰为小写 `sil`，只把前一条的结束时间延长到 `br` 的结束时间，不再追加该 `br`；
- 其余 `br` 复制后同样改成小写 `sil`。

字符串比较区分大小写；替换常量 `S5API!1800894a0` 的原始字节是 `73 69 6c 00`，明确为小写 `sil`，不是 `SIL`。后续 `FUN_18004d960` 构造并行的 `0x84` 时序/音素特征，但没有重写归一化后 `0x58` 列表的音高。

随后 `S5API!FUN_180055af0 @ 180055af0` 生成首列音高曲线。反编译结果表明它不是插值曲线，而是按**归一化后**发音段结束时间生成的分段常数阶梯：

```text
frameCount = ceil(lastSegmentEnd / framePeriod)
每个发音段覆盖到 ceil(segmentEnd / framePeriod)
覆盖帧的值 = segmentPitch + callerOffset
剩余尾帧 = 最后一段的 segmentPitch
```

`Func_417eec3e` 最终直接执行等价于：

```text
outputRow[0] = pitchCurve[frameIndex]
```

写出循环还确认 `frameIndex` 从 0 开始、每个输出行递增 1，没有隐藏的窗口起始偏移；第 `j` 个输出行读取 `pitchCurve[j]`。函数返回时把实际发出的行数写入输出描述符 `+0x08` 的低 32 位，该数是内部行数、调用方容量等限制后的结果。

因此 hook 不必真的分配两条直到 `ceil(lastEnd/framePeriod)` 的完整曲线。原调用前已经能读取输出描述符容量，可用两个段游标按同一 `ceil` 边界只生成：

```text
precomputedRows = min(outputCapacity, ceil(normalizedLastEnd / framePeriod))
delta[j] = shiftedPitchAt(j) - baselinePitchAt(j), j = 0..precomputedRows-1
```

原函数返回后要求 `0 <= actualRows <= precomputedRows`，再把差值向量截为 actualRows。这样既严格复现输出索引，又避免歌曲绝对时间异常时为完整曲线重复分配巨大内存。

## 6. `Func_e8258087` 的作用与限制

`S5API!Func_e8258087 @ 18006dec0` 是分块声码器包装器。它插值 cent 音高并调用 `FUN_18001c520`；核心路径使用：

```text
ratio = pow(2, cents / 1200)
```

包装器又把 F0 换回：

```text
cents = 1200 * log2(F0 / 440)
```

输出记录中已观察到 `+0x800` 为 cent 音高，`+0x804` 为浊音标记，`+0x808` 为另一项指标。函数末尾还把调用方提供的一大块声码器状态复制回 S5 state 的 `+0x19f8` 区域，因此它不是一个可任意重复调用的无状态 F0 转换器。Ghidra 对该函数的 xref 只有导出入口和两处数据表引用，没有 S5 内部直接代码 caller；VSM 既未在显式函数表中解析该导出名，相关字符串中也没有它。它只说明后级确实使用以 A4=440 Hz 为基准的 cent 表示，不能依赖为稳定挂钩点，也不应通过额外调用它来做输出补偿。

## 7. 当前实现方案

当前 REG 接入共有八处显式 AI 排除：原生发布/渲染链四处，参数头与覆盖层 UI 四处。不能只删除其中一个：

1. `RegisterShiftService.PublishAll()` 只枚举 `!part.IsAi`；
2. `RegisterShiftService.PublishPart()` 遇到 AI Part 会主动从原生表移除；
3. `NativeRegisterShift.SetPart(...)` 遇到 `part.IsAi` 会直接返回。
4. `RegisterShiftRendererStartPatch` 的渲染前发布只接受 `e.MidiPart is { IsAi: false }`。
5. `BreathVolumePatch.SynchronizeHeader()` 对 `MidiAiControlParameterTypes` 调用 `SynchronizeList(ai, includeRegisterShift: false)`，因此 AI 参数下拉列表根本不会包含 REG。
6. 同一方法在 active track 为 `VSMTrackType.MidiAi` 且当前参数为 REG 时，强制把 `ControlParameterType` 重置为 Dynamics。
7. `BreathVolumePatch.IsCustomPanelActive()` 在 REG 模式下还要求 `vm.ActiveTrack?.Type != VSMTrackType.MidiAi`，否则原生 outside-part 遮罩会重新覆盖自定义表面。
8. `BreathVolumeOverlay.IsParameterActive()` 有同样的 AI track 排除，会让覆盖层自身停止把 REG 判作活动参数。

AI 实现应复用同一份逐音符半音值、epoch 和 Part 指针键，并同步调整以上八处，但第 4 项不能简单改成“Started 时也发布 AI”：task vtable hook 在 `OnRendererStarted` 回调之前已经建立作用域，而该 observer 还要等 DispatcherTimer 拉取 native 队列。前 3 项应开放 AI 发布，后 4 项开放 UI 选择与覆盖层生命周期；第 4 项改为渲染启动后的最后一次刷新/诊断。工程存取 `BuildProjectData` / `LoadProjectData` 已遍历所有 MIDI Part，不需要修改持久化格式；现有覆盖层也能为 AI Part 建立音符柱。编辑提交、MCP 批量修改和历史撤销/重做最终都汇入 `PublishAndRender(sequence, part)`，该入口本身没有 AI 短路；它调用的 `PublishPart` 才是当前阻断点，之后的缓存刷新、逐音符临时 velocity 脏化和 `StartAsyncRendering()` 也没有按引擎分流。

还需要新增一个此前不存在的**发布时序触点**：Harmony prefix `WIVSMSequence.StartAsyncRendering()`，在 P/Invoke 创建 `RendererManager` 异步任务之前同步 `PublishAll(__instance)`。现有 `RegisterShiftRendererStartPatch` 是原生 Started observer 的延迟回调，必然晚于 task wrapper 入口；它不能作为唯一数据发布点。该 prefix 可覆盖反编译托管代码中的项目初始化/加载两处调用，以及补丁自身的 REG/分段音素重渲染调用。一次 native manager task 内可以继续创建多个 Part renderer task，但 prefix 已先发布整个 sequence 的所有 Part；编辑器若存在完全绕过该托管入口的持续调度路径，仍要用 task-entry/Func9 epoch 诊断捕获并 baseline 回退。

还需要把支持状态从进程全局改为 active-Part/目标 Part 相关：当前 `RegisterShiftService.IsSupported` 只反映传统 DSE 安装状态。它至少直接控制 `BreathVolumeOverlay` 的 Unsupported 空状态、参数头数值框启用状态，以及 MCP 能力报告；`ExtensionParameterRegistry` 的具体参数项状态同样读取全局 `NativeStatus`，诊断日志 `WriteRenderedFlags` 还只在 `NativeRegisterShift.Status == Installed` 时读取原生 dirty 标志。AI hook 安装失败时不能借传统 hook 的就绪状态显示为可用，反过来 AI-only 就绪也不能被传统状态判为不可用。设置说明和 NoActivePart 的英/简中/繁中/日文也都明确写着“传统声库”，启用 AI 后必须同步改为两类声库均适用的文案。

现有 32 字节 `NativeNote` 只有 frame 区间；但 `NativeRegisterShift.SetPart` 在转 frame 前已经算出了与 VSM 口径相近的精确 `beginSeconds` / `endSeconds`。较稳妥的 ABI v15 方案是在保持前 32 字节字段顺序不变的前提下追加两个 `double`，使 `repr(C)` 记录增至 48 字节，供 AI 的 `0x88` 时间匹配使用；传统 DSE 路径继续只读原字段。本轮仍只调查，尚未改动源码。

字段级 ABI 应固定为：

| 偏移 | C#/Rust 类型 | 字段 |
| ---: | --- | --- |
| `0x00` | `long` / `i64` | `BeginFrame` |
| `0x08` | `long` / `i64` | `EndFrame` |
| `0x10` | `int` / `i32` | `PitchCents` |
| `0x14` | `int` / `i32` | `Semitones` |
| `0x18` | `int` / `i32` | `Ordinal` |
| `0x1c` | `int` / `i32` | `Reserved`，必须为 0 |
| `0x20` | `double` / `f64` | `BeginSeconds` |
| `0x28` | `double` / `f64` | `EndSeconds` |

两端都要增加 `sizeof(NativeNote)==48` 的断言。这里不应直接把共享的 `v6_clock_abi_version` 从 14 增至 15：同一 `v6patch_clock.dll` 还被 `NativePlaybackClock`、`NativeDseCapture` 和 `NativeBreathCapture` 分别校验为 ABI 14，盲目提升会让三个无关功能全部回退。更稳妥的是保留共享 clock ABI 14，新增 REG 专用 `v6_register_shift_abi_version() == 15`，并同时导出 `v6_register_shift_note_size() == 48`、`v6_register_shift_status_size() == 464`。`NativeRegisterShift` 必须校验这三项；旧 32 字节调用方因 REG ABI/size 不匹配被拒绝，不能猜测记录 stride。若最终仍选择提升全局 ABI，则四个托管使用者和所有原生布局测试必须同批更新，不能只改 REG。

设第 `i` 个真实音符的 REG 值为 `Δᵢ` 个半音。

### 7.1 在 Renderer task 入口绑定 Part

`VSM+0x2c4d28` 是 `Renderer::start(shared_ptr<MidiPart> const&)` 为本次完整渲染创建的专用 PPL task vtable，`+0x70`（index 14）槽为 `FUN_180065570`。Ghidra 的类型字符串、构造函数 `FUN_180064f40` 和 wrapper 本体共同闭合了对象布局：task `+0x08` 保存 renderer，renderer `+0x10` 是正在渲染的原生 `WIVSMMidiPart` 对象指针，`+0x18` 是对应控制块。也就是：

```text
renderer   = *(void **)(taskThis + 0x08)
partHandle = *(void **)(renderer + 0x10)

p0    = *(void **)renderer
p1    = *(void **)p0
host  = *(void **)(p1 + 0x28)
table = host + 0x150
```

`FUN_180065570` 随后才把 renderer 包成 holder 并同步调用唯一的 `FUN_18006c6d0`，后者完整包围前段 Func9/Cfc 和后段 Func417/Func2d。因此 hook 这个 vtable 槽即可在调用原 wrapper 前压入 TLS scope，在返回后恢复上一层；不需要给外层函数入口写 inline jump。`partHandle` 与托管侧 `WIVSMMidiPart.CppObjPtr` 可直接作为同一张 Part 数据表的键，也无需建立不稳定的 `S5 state -> voicebank ID` 映射。

现有 `PART_NOTES` 是 `RwLock<HashMap<u64, PartEntry>>`，而 `SetPart` 会用新 epoch 整体替换条目。AI scope 不能保存锁内 `PartEntry` 的借用/裸指针，也不能在整个原生渲染期间持有读锁。实现时宜把 `PartEntry.notes` 从 `Vec<RegisterNote>` 改为 `Arc<[RegisterNote]>`：`set_part` 发布时一次性分配不可变数组；task 入口短暂读表并优先克隆 `{epoch, Arc}`，立即释放锁，若恰遇锁竞争才保留一次首个 Func9 前的重试。克隆后整个 scope 固定使用同一 epoch/Arc；传统路径仍可按 slice 查找，AI 后续 cfc/417/2d hook 只读 TLS 持有的 Arc。这样没有 render-entry 大数组复制，也不会让 UI 线程等待长读锁。并发发布新 REG 时，已取得快照的渲染继续持有一致的旧 epoch/Arc，后续任务使用新快照；Remove/Clear 也只移除表引用，正在退出的 scope 不会悬空。

同一 Part 的纯 REG 修改不会改变 native `0x88` 音符身份，因此仍要明确每个 renderer wrapper 绑定哪个已发布 REG epoch。顶层 manager task 的行为已经比早期推断更明确：`FUN_1800683b0` 先在锁内把 manager `+0x1c0` active 标志置 1；若 `+0xc0` 已有 task，它以 `local_res8=0` 调用 `FUN_18006a580` 作零超时完成查询。返回“尚未完成”时直接解锁退出，不会替换或并发创建第二个顶层 `std::task`；只有确认完成后才经 `FUN_180067460` 释放旧 task，再由 `FUN_18006a0a0` 建立新 task。`FUN_180068540` 是停止/清理路径，最后把 `+0x1c0` 清零。

同一长驻 manager 内的 active job 替换协议也已继续闭合。`FUN_180068d60` 在锁内按底层 MidiPart 身份匹配到旧 job 后调用 `FUN_180100140(job+0x28, 2)`；后者以零超时查询 task，若仍运行便原子写入 job `+0x38 = 2`。`FUN_18006d720` 在等待 renderer 后端时反复读取该字节，值 2 返回取消码 `0x14`。更关键的是，`FUN_180100140` 随后无条件进入 `FUN_180067460`，而该释放路径通过 async-state vtable `+0x10` 调用 `FUN_180064430`；后者在条件变量上等待 task state `+0xbc != 0` 才返回。管理器只有在这次等待结束后才摘除旧 renderer/Part 资源、擦除旧 job，继而建立新 job。即使取消恰好落在不读取 `+0x38` 的内部计算区间，旧 task 也最多在新 job 启动前完成自己的提交，不可能晚于新 job 回写；因此当前版本不需要为了防“旧 generation 晚覆盖”再猜一个 native generation 字段。

实现语义仍采用“每个 wrapper task 入口克隆当时最新已发布 `{epoch, Arc}`”，原因是这能固定一次渲染内部的 REG 一致性，而不是为了区分并行的同 Part job。必须保留快速连续修改同一 Part 的宿主压力测试，确认观察到的任务顺序与上述等待协议一致；若未来版本不再经 `FUN_180064430` 等待完成，才重新把 renderer/job generation 作为兼容性必需键。

task 入口即使暂时没有 Part 条目，也建立一个不可激活的 `AwaitSnapshot { partHandle }` scope 并完成安全的表槽校验；首个 Func9 若仍找不到条目、全部 REG 为零（当前 `set_part` 会移除此类条目）、epoch 为零或快照校验失败，就只调用原函数并把本 scope 置为 Disabled。ABI v15 的原生入口还应新增 `beginSeconds/endSeconds` 的 finite、非负及 `end >= begin` 校验。

### 7.2 复制并逐音符修改 `0x88` 输入

在 `Func_9cbce37f` 和 `Func_cfc85a30` 前复制输入描述符与记录，仅对匹配成功且 `+0x20 == 0` 的真实音符执行：

```text
shiftedPitchCentsᵢ = clamp(pitchCentsᵢ + 100 * Δᵢ, -4500, 2400)
```

托管 `NativeNote.PitchCents` 当前保存未裁剪的 `(NoteNumber-69)*100`，而 VSM 在构造 `0x88` 前已裁剪。因此 AI 身份匹配必须使用：

```text
expectedBasePitch = clamp(nativeNote.PitchCents, -4500, 2400)
```

否则 MIDI 24–93 外的音符永远匹配失败。裁剪结果都是 `[-4500,2400]` 内 100 的整数倍，可要求输入 `float` 与 expected base pitch 的 IEEE-754 位模式完全相等，不需要给音高另设模糊容差。时间匹配采用 ABI v15 的 seconds，结合严格递增 ordinal；允许窗口只包含 Part 的子集，但同一记录候选不唯一时不得猜测。当前描述符记录数还必须验证为非负且处于合理上限，`count * 0x88` 做溢出检查，真实记录的 double 时间/float 音高必须 finite 且 `end >= begin`，音素数必须在 `0..8`。

计数和填充两次调用必须看到同一份匹配结果。计数 hook 应分别对原始副本和偏移副本调用原函数；只有两者返回相同段数时才允许本 scope 继续偏移，否则直接返回原始计数并禁用该 scope。synthetic/sentinel 记录始终保持原值；任何真实记录无法在当前 Part 快照中唯一匹配时，应禁用整次 scope 并只走原始输入，不能静默形成“部分音符生效”。也不能原地修改 VSM 共享数组。

9 hook 不应只保存 hash 后让 cfc 再做一遍逐音符匹配，而应把当次完整 `0x18` 描述符附加字段、baseline `0x88` 克隆、shifted `0x88` 克隆、实际匹配结果和 expected count 一并保存在 pending-fill 状态。紧随其后的 cfc hook 先核对当前描述符除指针外的字段及原始记录字节仍与 pending baseline 一致，然后直接复用这两份克隆。这样计数/填充阶段在构造上使用同一批输入；校验失败则 cfc 完全按当前原始输入 pass-through，并清除 pending 状态。

### 7.3 保存未偏移基线并生成逐帧差值

更安全的宿主可见策略是：**VSM 前段始终保留原始 baseline `0x28`，只在 Func417 入模调用的私有克隆中替换 shifted pitch**。这样 Func417 前任何匹配、分配或版本校验失败时，直接调用原函数处理 VSM 原始输入即可完整回退，不需要从已经写入 VSM 的 shifted 数据反推 baseline。

为取得精确的 shifted 结果，Func9/FuncCfc 仍分别对 baseline/shifted 两组 `0x88` 输入调用 S5，但把额外的 shifted 调用放在前、正常 baseline 调用放在最后：

```text
Func9 hook:   shifted count（私有） -> baseline count（宿主可见并返回）
FuncCfc hook: shifted fill（私有）  -> baseline fill（写入 VSM 原输出）
```

这既保留了未打补丁时宿主最终看到的 baseline 返回值/缓冲，也让这两次 hook 返回前最后一次触及潜在隐藏状态的调用仍是正常 baseline。两组段数必须都等于 baseline count；由于两块输出都预先清零，随后可以对每条 `0x28` 做除 `+0x10..+0x13` 音高四字节外的完整逐字节比较，而不只比较几个推测字段。任何其它字节不同都禁用 scope；通过后再保存不可变的 `{identity, baselinePitch, shiftedPitch}` 对。

返回 ABI 不应混用：Func9 的低 32 位返回值就是内部 `0x58` 记录数，hook 必须把最后一次 baseline Func9 的原返回值交给 VSM；FuncCfc 固定返回 0，实际写入行数由输出描述符 `+0x08` 低 32 位报告，hook 同样返回最后一次 baseline FuncCfc 的原返回字。VSM 对 Func9 返回值作分配计数，但不把 FuncCfc 的 `RAX` 当行数使用。

这里不能利用 `0x28 +0x20` 直接把 baseline 段映射回原始音符，从而省掉额外的 shifted 9/cfc 调用。字段来源已经闭合：

1. VSM 的 `FUN_1800721d0` / `FUN_180072300` 把每个 `0x28` 音素临时记录的 `+0x20` 原样复制到 `0x88 +0x68` 起的 `uint32[8]`，数量等于 `0x88 +0x24`；
2. 普通音素临时记录的该值来自 `FUN_18006f9c0`，它只把类别 `0/1/4` 映射为 `0/1/2`，其它值映射为 `0xffffffff`。相邻合成记录还会继承前后值，所以它从源头就是高度重复的类别标签，不是 note ordinal 或唯一 ID；
3. `Func_cfc85a30` 把 `0x88 +0x68` 数组复制到内部 `0x98 +0x60` 的 `vector<uint32>`；`FUN_18001ff70` 再把它放入内部 `0x58 +0x30/+0x38/+0x40` 的 begin/end/capacity；最终 cfc 的 `0x28 +0x20` 只是取 `**(uint32 **)(internal + 0x30)`；
4. `FUN_1800560c0` 在拆分/合并相邻内部记录时会复制、截取该数组，因此多个输出段可以共享同一个 `+0x20`，一个源记录也可能产生多个带相同值的输出段。

更关键的是，`FUN_1800560c0` 对相邻内部 `0x58` 记录的音高差做分段插值，并按音素字符串查表得到权重；输出 `+0x50 double` 不保证等于任何一条原始 `0x88` 的音高。逐音符移调量不相同时，不能只凭 baseline 段的时间、标签和音高推导 shifted 段音高。除非完整复刻并版本锁定这条 S5 私有预处理链，否则“由 baseline 单次结果本地推算 shifted 结果”没有可证明的等价性。因此当前方案仍保留额外 shifted 9/cfc 调用，并把 `+0x20` 仅作为 baseline/shifted 同构校验和后续子集匹配字段。

在 `Func_417eec3e` hook 中，输入是 VSM 从 baseline 结果裁剪/复制出的当前子集。先按 baseline 身份和 baseline pitch 作唯一、单调匹配；成功后完整克隆当前输入，仅把每条 `+0x10` 换成对应 shifted pitch。轻量诊断模式可只把 shifted 描述符传给原 Func417，并保留本地展开的 baseline/shifted 曲线；`ExactDualState` 则必须先把 baseline 描述符送入私有完整输出，再把 shifted 描述符送入 VSM 原输出。baseline/shifted 两条归一化曲线的差值仍可用于校验和轻量回退：

```text
frameDelta[j] = shiftedSegmentPitch[j] - baselineSegmentPitch[j]
```

调用次数也已核对：每次 `FUN_18006c6d0` 只在 `18006ca34` 进入一次前段 `FUN_18006db10`，该函数只在 `18006e1a1` 调用一次 `FUN_180082bf0`，最终对应一组 9/cfc。后段可以处理多个窗口，但它们都来自这同一组发音段。因此 segment pairs 应在 cfc 后保存到外层 scope，供后续所有 Func417 子集匹配，直到外层退出再统一释放。

进一步检查表明，`Func_cfc85a30` 本体只直接读取 S5 状态的 `+0x10` 模型和 `+0x1a8` 映射；`FUN_18001ff70` 的主要写入目标是调用方提供的局部输出容器。其模型助手 `FUN_180053490` 只读权重/形状，申请三块 64 字节对齐 scratch，最后写调用方 `param_5`；`FUN_180054a60` 同样只读模型层和权重，写入传入的 scratch/output。两者的间接矩阵内核调用把 weights/input/output/shape 作为参数，没有传模型对象 `this`。描述符标志开启时调用的 `FUN_180041c60` 也没有 S5 state 参数，只把传入 `0x58` 数组规范化到调用方输出 vector。当前层级未发现对持久模型状态的直接或对象虚调用写回，因此双调用从静态上看较可能安全；额外 shifted 调用先执行、baseline 正常调用最后执行，是在仍需宿主 A/B 的前提下更保守的顺序。

更强的旁证是：未打补丁的正常管线本来就先由 `Func_9cbce37f` 完整调用一次 `FUN_18001ff70` 来计数，再由 `Func_cfc85a30` 用相同 state、记录数、描述符 `+0x0c/+0x10/+0x14` 和固定参数 `0xe` 完整调用第二次来填充；描述符标志开启时，两者还都会按同一条件调用 `FUN_180041c60`。S5 自身因此已经依赖这组计算可重复执行。候选方案只是把原有的“baseline 计数 + baseline 填充”扩成“baseline/shifted 各自计数 + 各自填充”。

`Func_cfc85a30` 会把输出描述符 `+0x08` 的低 32 位改为 `min(内部段数, 调用方容量)`。baseline 与 shifted 两次的返回行数都必须等于前一计数阶段确认的 expected count，不能只比较二者相等，否则“两边同时被同一容量截断”会掩盖错误。baseline 使用 VSM 原描述符；shifted 私有描述符克隆其字段，只替换指针，并使用同一 expected count 容量。

不过模型计算中仍存在间接调用，静态反编译不能完全排除隐藏缓存或副作用。只有在 `Δ=0` 的双调用结果与后续渲染通过宿主测试后才能正式采用。若 shifted 私有结果的数量或身份与 baseline 不一致，只丢弃 shifted 私有缓冲并禁用本 scope；VSM 输出从未离开 baseline，因此不会留下“模型输入已偏移、输出补偿却被跳过”的半状态。

`Func_417eec3e` 收到的可能只是 `Func_cfc85a30` baseline 结果的某个复制子集，不能假设指针相同。Hook 应在调用原函数**之前**，按开始/结束、`+0x14/+0x15` 标志、`+0x16` 的 8 字节音素、`+0x20` 标识及 baseline pitch，在已保存段对中作唯一且单调的匹配。匹配后以当前子集为模板构造 shifted 克隆，分别复现 `FUN_180005fc0` 的 `br` 归一化，并按当前 S5 状态中的：

```text
framePeriod = *(int32 *)(state + 0x50) / *(float *)(state + 0x54)
```

生成 baseline/shifted 两条阶梯曲线的有界前缀及其差值。`framePeriod` 必须有限、正数且处于合理范围；输出描述符原始容量必须非负。每个 scope 应设统一、可配置的 checked scratch budget；若采用 64 MiB 初始上限，它必须覆盖两份 `0x88`、shifted `0x28`、逐帧向量以及**完整 baseline feature 输出**，不能再按早期的“Func417 子集克隆”估算。所需容量超过上限、任何乘法溢出或对齐分配失败都整次回退 baseline。S5 每次 Func417 内部自行申请的 16 MiB scratch 不计入补丁私有缓冲额度，但必须计入宿主峰值内存压力测试。

若当前调用在进入原函数前不能完整匹配或生成差值，直接用原始 baseline 描述符调用原 Func417，并为它设置全零差值；不需要也不允许猜测对应段。调用任何 Func417 前必须保存输出描述符原始 `+0x08` 容量，并校验 `capacity * stride * 4` 不溢出且不超过 scope scratch budget。返回后若 `0 <= actualRows <= precomputedRows`，把差值截断到 actualRows 并建立 `CurrentWindow`。

轻量模式中若 shifted Func417 返回的行数无效，不能只是清空 delta 后继续，因为输出缓冲已经包含 shifted 特征；应恢复容量，以原始 baseline 输入再次调用 Func417 覆盖输出并建立全零窗口。`ExactDualState` 已经先在私有缓冲得到并验证 baseline 完整特征，此时可直接把 `baselineRows * stride * 4` 字节复制到 VSM 输出、写回 baseline 实际行数并丢弃 shadow 候选，不需要第三次模型调用。两种模式都必须在调用 shifted 前保证 rollback 所需资源已经完整就绪，不能在宿主缓冲已被覆盖后才尝试分配。

Func417 前的 `table+0x48` 是 `S5API!Func_efe389ac @ 18006e240`。它只在段数大于零时计算：

```text
capacityEstimate = int(lastSegment.end / *(float *)(state + 0x100))
```

不读取 `+0x10` 音高。由于 cfc 配对已要求 baseline/shifted 的时间与段数完全一致，让该计数函数看到 baseline、让随后的 Func417 看到只改音高的 shifted 私有克隆，不会造成输出容量契约失配；无需再 hook `+0x48`。

### 7.4 在模型返回后只补偿总音高

VSM 的 `FUN_180085070` 每次执行只在 `1800852e0` 调用一次 `Func_417eec3e`，随后从输出描述符 `+0x08` 把实际行数读入 `RDI`；Func417 自身固定返回 0，VSM 不读取它的 `RAX`。普通渲染路径再从 `ESI=0` 开始，在 `180085520`–`1800855bc` 严格递增循环，每行恰调用一次 `Func_2db29822`。因此每次 `Func_417eec3e` hook 应**覆盖** TLS 中的当前窗口状态并把游标归零，而不是把多个窗口累积成 FIFO。轻量补偿只需按游标消费 delta；Exact 模式必须保存并校验输出描述符 `+0x0c` 的动态 feature stride，后续每次 Func2d 用同一游标定位 baseline 行。hook 返回 actual 调用的原返回字，行数完全以经过校验的描述符为准。调用原函数后执行：

```text
ExactBaseDelta:      output[0] = (shadow.output[0] - shadow.output[1]) + actual.output[1]
BaselineTotalControl output[0] = shadow.output[0]
ShadowFinalBiquad:   output[0] -= shadowBiquad2(frameDelta[currentFrame])
RawDelta:            output[0] -= frameDelta[currentFrame]
```

诊断阶段应以 `ExactDualState + ExactBaseDelta` 作为最强语义候选，以 `BaselineTotalControl` 作为严格 baseline 总音高对照，同时保留 `ShadowFinalBiquad`、`RawDelta`，并可用 `BaselineCurvePlusResidual` 作定位对照；宿主逐帧 F0、微观音高表现和性能 A/B 通过后才固定生产默认值。所有候选都保留 shifted 调用的 `param3[1]` 和 actual shifted state；双状态模式只用 shadow 输出计算宿主可见的 total 补偿。模型及 actual 跨帧状态因此仍看到移调后的音区条件。`FUN_180085070` 的模式 1/2 分支会在第一次 `Func_2db29822` 前直接退出；对应 shadow state 仍应在紧随其后的 `table+0x60` 中释放，下一次 Func417 覆盖旧窗口向量。外层退出时要核对 Func417 baseline/shifted 调用数、两个 state 的创建/检查/释放、总发出行数、两套 Func2d 调用数、已消费行数和当前窗口剩余量。游标越界时才发现错误已经太晚，因此“进入 shifted Func417 前 baseline rollback 已完整就绪、进入首个 Func2d 前 shadow state 已完整就绪”都是必须满足的安全边界。

### 7.5 Hook 定位、原型与安全安装顺序

七个 S5 函数都是具名导出，应使用 `GetProcAddress` 定位。当前二进制可直接声明为 Windows x64 ABI：

```text
Func9   (state, input0x88Desc)                 -> uint64
FuncCfc (state, input0x88Desc, output0x28Desc) -> uint64
Func417 (state, input0x28Desc, outputFeatDesc) -> uint64
Func236 (state, featureWindowDesc)             -> Func2dState*
Func950 (func2dState)                          -> void
Func52  (func2dState)                          -> uint32
Func2d  (func2dState, inputFeatDesc, output2Floats) -> uint64
```

其中 VSM 对 Func9 的返回值只读取 `EAX` 并符号扩展为段数；FuncCfc/Func417/Func2d 的返回值在当前调用点不参与控制流，FuncCfc/Func417 的实际行数由描述符字段回写，Func2d 则只通过传入的两 float 输出；Func236 返回的 state、Func52 的 `EAX` 检查结果和 Func950 的生命周期都是宿主控制流的一部分。唯一需要接管的 VSM 外层调用边界仍是：

```text
FUN_180065570(taskThis, arg2, arg3, arg4) -> void
    renderer = *(taskThis + 0x08)
    holder = alloc(8)
    *holder = renderer
    FUN_18006c6d0(holder, arg2, arg3, arg4)
```

task hook 必须原样转发全部四个寄存器参数，并以保存的原 vtable 槽函数指针调用 wrapper。它不复制任何被覆盖指令，也不需要 trampoline。

首选实现不是给这些 S5 导出写 inline jump，而是在 VSM 的宿主函数表中原子替换六个指针：Func9、FuncCfc、Func417、Func236、Func950、Func2d。Rust 侧保存全部七个 `GetProcAddress` 原地址；Func52 仅供 Func236 hook 直接预检 shadow 并校验表 `+0x68` 未被改写，不安装 hook。此前核对的四个主要导出入口长度仍可作为版本诊断证据，但不应成为正常安装路径；三个短生命周期函数同样只按导出地址和表槽验证，不作 inline patch：

| S5 目标 | 导出地址 | 若被迫 inline 时的最短整指令长度 | 入口是否含 RIP-relative/相对控制流 |
| --- | --- | ---: | --- |
| `Func_9cbce37f` | `18006beb0` | 15 | 否 |
| `Func_cfc85a30` | `18006d0e0` | 19 | 否 |
| `Func_417eec3e` | `180061b10` | 19 | 否 |
| `Func_2db29822` | `180061330` | 18 | 否 |

其余导出地址为 `Func_236a1ecf @ 180060ef0`、`Func_95031259 @ 18006be30`、`Func_52f85f15 @ 180067b10`。

VSM 代码页不再需要任何 inline hook。只需把专用 PPL task vtable 的一个对齐函数指针从 `FUN_180065570` 原子替换为 Rust wrapper：

| VSM 数据目标 | 当前地址/槽 | 用途 | 写入方式 |
| --- | --- | --- | --- |
| Renderer start task vtable | base `1802c4d28`，`+0x70`/index 14，当前值 `180065570` | 建立 Part/TLS scope，并在当前渲染第一次 Func9 前换 S5 表槽 | 临时 `VirtualProtect` vtable 页，`InterlockedCompareExchangePointer`，随后恢复页保护 |

`FUN_180065570` 当前 48 字节 wrapper 结构为；两处 `E8 rel32` 的位移按版本匹配或作掩码：

```text
48 89 5C 24 10 57 48 83 EC 20 48 8B 59 08 B9 08
00 00 00 E8 ?? ?? ?? ?? 48 8B F8 48 89 44 24 30
48 89 18 48 89 44 24 30 48 8B C8 E8 ?? ?? ?? ??
```

其中第二个 call 位于 wrapper `+0x2b`，目标必须为 `FUN_18006c6d0`；第一个 call 必须是 8 字节分配器，尾部还要验证以同一指针调用释放器。19 字节短前缀在当前文件还命中 `FUN_1800654d0`，不能只靠它扫描；后者的第二个 call 指向 `FUN_180050260`，可由 call target 明确排除。

`FUN_180083830` 当前入口的 32 字节版本签名仍可作为前段结构校验证据，但不再是 patch 目标：

```text
48 8B C4 48 89 58 18 55 56 57 41 54 41 55 41 56
41 57 48 8D A8 18 FD FF FF 48 81 EC B0 03 00 00
```

签名必须在按文件版本确定的 RVA 上校验完整 32 字节，而不是拿可重复的短前缀做唯一扫描。

VSM 外层当前入口前 31 个完整指令字节为：

```text
48 8B C4 48 89 58 10 48 89 70 18 48 89 78 20 55
41 54 41 55 41 56 41 57 48 8D A8 38 FA FF FF
```

在当前 `VSM.dll` 中唯一命中一次。正式定位可先按文件版本/PE 标识在该 RVA 校验完整签名，再扫描解析到它的 `E8 rel32`：Ghidra 只发现 `FUN_180065570+0x2b @ 18006559b` 这一处代码 caller。由 callsite 减 `0x2b` 得 wrapper 起点并验证上述 48 字节；然后在只读、非执行映像区查找对齐的 wrapper 绝对指针，唯一有效项应为 `1802c4d98`。另一个 Ghidra DATA xref `1803753b4` 属于异常展开元数据，不是对齐的 8 字节 vtable 函数指针。最后验证 vtable base 的构造引用来自 `FUN_180064f40` / `FUN_180065ca0`，且 slot 当前值只能是原 wrapper 或本补丁 hook。S5 的 Func9 15 字节短前缀在文件内出现三次，同样只能用于导出地址校验，不能作为全模块唯一扫描签名。

推荐安装和运行协议如下：

1. AI 安装只要求 `VSM.dll` 已加载。应定位并校验 wrapper/vtable 槽，先保存原槽值，再用一次对齐 CAS 安装 task hook；若 VSM 暂未加载则返回可重试状态。安装不依赖 `S5API.dll` 已加载，也不在 `OnRendererStarted` 或渲染中的任意函数入口写代码页。
2. task hook 先读 renderer/Part，建立当前 Part 的 TLS scope。`StartAsyncRendering` prefix 必须已经发布快照；若 task 入口取得快照失败，可保留到首个 Func9 前再做一次短 `try_read` 作为并发/兼容性兜底，但不能等待 Started observer——该事件由 UI DispatcherTimer 延迟拉取，不会在 native wrapper 内同步发布。
3. 对当前 task 按 `FUN_18006db10 @ 18006db56..18006db63` 的已确认链取得 `host+0x150`，这一步不依赖 Part 快照已经发布。每次解引用都要做空值、地址加法溢出、对齐和可读页校验；`table+0x00` 必须是有效 PE 模块的 S5 `HMODULE`。正常情况下 `FUN_18002cf50` 已在宿主对象初始化时加载 S5 并由 `FUN_180070980` 填表；若此刻表仍未就绪，本 scope 直接 baseline，后续 task 入口再重试，不能为抢当前渲染而安装 loader/front inline hook。
4. 以 `table+0x00` 调用 `GetProcAddress` 解析并校验七个原导出；`+0x68` 必须仍等于原 Func52，另外只对 `+0x20/+0x28/+0x50/+0x58/+0x60/+0x70` 六个对齐指针做 compare-exchange。需要替换的槽只允许是“预期原导出”或“本补丁 hook”；遇到其它值即令当前 scope 失效，不能覆盖别人的 hook。虽然当前表位于宿主堆对象且正常可写，CAS 前仍必须用 `VirtualQuery` 验证每个槽所在 committed page 可读、可写且非 guard；不满足时拒绝安装，不能擅自给未知宿主页改保护。
5. 首次成功解析时把 `HMODULE` 和七个原导出作为一个不可变集合发布；**发布完整原函数集合后**才能开始换任一槽。以后遇到其它表实例时，只有其 HMODULE/七导出与首次集合完全相同才允许换槽，否则保持原表。六个 hook 槽全部确认等于本补丁 hook、且 `+0x68` 仍是原 Func52 后，把当前 TLS scope 的 `tableReady` 置真，再调用原 `FUN_180065570`。当前渲染随后才进入 Func9，所以可以从第一阶段完整生效。
6. 当前 scope 最迟在第一次 Func9 调用任何 shifted 私有 S5 路径前，以 `partHandle` 短暂 `try_read` 并克隆 `{epoch, Arc<[RegisterNote]>}`。成功才进入 `AwaitCount`；没有条目、锁竞争或布局无效则本 scope 置为 Disabled 并 baseline。从计数阶段起快照不再变化；Started observer 只能刷新后续任务或诊断状态，不能挽救当前首帧。
7. 任一中间状态下，已经换入的叶子 hook 看到 `tableReady=false`、没有 TLS scope、尚未取得快照、Busy/reentry 或状态机不匹配时，都只调用已发布的原导出。槽位替换失败不允许把半套逻辑激活；已换入的部分槽可以永久保留为无 scope 透传，下一次入口继续 CAS 剩余槽。
8. 原 wrapper 返回后用 guard 恢复上一层 TLS，即使嵌套调用也不串 scope；异常/取消的宿主行为仍需动态验证。状态汇总只在 scope 退出时做，Func2d 热路径不递增全局原子计数。

`tableReady` 属于本次 TLS scope，而不是进程全局布尔值。换槽后导唱会经同一全局表进入 Rust 叶子，但它没有 Renderer task scope，始终调用原导出。S5 六个 hook 槽和 VSM vtable 槽都是对齐指针写：S5 表只在已验证可写时 CAS，不调用 `VirtualProtect`；只在安装 vtable 槽时短暂改变已确认 `.rdata` 数据页保护。并发线程读取函数表/vtable 时只会看到完整的旧指针或新指针，不会像 14 字节 `write_absolute_jump` 那样取到半条指令。

### 7.6 TLS 状态机与热路径约束

建议把每层嵌套外层 scope 明确建模为：

```text
AwaitSnapshot { partHandle }
  -> AwaitCount { epoch, Arc<[RegisterNote]> }
  -> PendingFill { baseline88, shifted88, descriptorFields, expectedCount }
  -> SegmentsReady { immutable baseline/shifted 0x28 pairs }
  -> Disabled

每次 Func417：CurrentWindow { deltas, baselineFeatures, stride, cursor, emittedCount,
                               actualState, shadowState, compensationMode/state }
```

- 首个 Func9 先执行 `AwaitSnapshot -> AwaitCount`，并在同一次调用继续 `AwaitCount -> PendingFill`；后续再次出现 Func9 或其它异常顺序只调用原函数并记录诊断，不复用旧数据。
- FuncCfc 只允许匹配的 `PendingFill -> SegmentsReady`，之后立即释放两份 `0x88` 大缓冲。
- Func417 只读 `SegmentsReady`，每次覆盖 `CurrentWindow`；Exact 模式必须在 shifted 调用前完成 baselineFeatures 的分配与验证。
- Func236/Func950 管理当前窗口的 actual/shadow state 配对；Func236 内部只用保存的 Func52 原地址预检两者，且 status 必须为 0。Func950 遇到未知 state 指针时原样透传，不能猜测归属。
- Func2d 只做“读取当前行/基线行、依次调用 shadow/actual 原函数、选定 `output[0]`、保留 actual 写入的 shifted `output[1]`、推进游标并返回 actual 返回字”，不得获取全局 `RwLock`、分配内存或写日志。
- 外层返回时弹出当前 scope 并恢复上一层；Disabled 也必须正常恢复，不能简单清空整个 TLS。

### 7.7 现有源码的具体落点与不可直接复用部分

Rust 侧现有代码大多位于 `native/playback-clock/src/lib.rs` 的 `register_shift_hook` 模块，已经有 PE `ImageLayout`、唯一/掩码签名扫描、rel32 解析、安装锁、`PART_NOTES` 和状态结构，可复用这些基础设施，但 AI 路径不应继续复用传统 DSE 的控制状态：

- 当前 `install()` 在 `BIT_PREPARE_1B | BIT_SELECTOR_1B` 已齐全时立即返回，并固定查找 `DSE.dll`；AI 必须有独立的安装锁、结果、bitmap 和 `v6_register_shift_install_ai()`，不能把 task-slot 成功混进传统 bitmap。
- 当前 `install_hook()` 会建立 trampoline 后逐字节写 14 字节 absolute jump；DSE vtable 安装也在 `VirtualProtect` 后用普通 `*slot = hook` 连写六个槽。AI 单槽安装不能调用这两条路径，应新增“发布 original → 对齐 CAS → 无条件恢复页保护”的专用 helper；S5 六个 hook 槽使用同一 CAS helper，但只在 `VirtualQuery` 已确认宿主页可写时操作且不改页保护。
- 当前 `ImageLayout` 只保存 image/code 的起止，`find_unique[_masked]` 只扫 `.text`。定位 task vtable 时要扩展 PE section 解析，确认候选 `1802c4d98` 位于只读、非执行 `.rdata`；`1803753b4` 位于 `.pdata`，必须排除。不能把整个 image 中偶然出现的 8 字节值直接当 vtable。
- 当前 kernel32 FFI 没有 `GetProcAddress`、`VirtualQuery`/`MEMORY_BASIC_INFORMATION` 或原子指针交换封装；AI 需要补齐，用于按表内 HMODULE 解析导出、保护 renderer→host 的堆指针读取及拒绝不可读/不可写页。
- 当前 `ACTIVE_SHIFTS: Cell<ActiveShifts>` 只保存传统路径的两个 `Option<i32>`，是 Copy 小状态，既不能持有 `Arc` 也不表达 AI 的多阶段缓冲。AI 应使用独立 `thread_local RefCell<Vec<AiScope>>`（或等价可嵌套 guard），不要把 0x88/0x28/逐帧向量塞进 `ACTIVE_SHIFTS`。
- `PartEntry.notes` 从 `Vec<RegisterNote>` 改为 `Arc<[RegisterNote]>` 后，传统 `offset_candidates`/record lookup 仍可透明按 slice 读取；AI 在 task 入口短 `try_read` clone Arc，锁竞争时才允许首个 Func9 前重试一次。`set_part` 仍一次整体替换条目，并保留传统 frame calibration；无需建立第二张可能失同步的 Part 表。

C# 侧 `NativeRegisterShift` 当前把“DLL 已加载”“clock ABI 14 正确”“传统 DSE install 成功”压成一个 `_loadState`，且 `SetPart` 明确拒绝 `part.IsAi`。实现时应拆为 library/REG ABI、traditional install、AI install 三组状态和 retry；`SetPart` 按 Part 引擎确保相应安装，但两类 Part 都写同一原生表。`NativeNote` 追加 seconds，`NativeStatus` 尾部追加 AI 96 字节，`GetSnapshot()`/诊断 record 同步扩展。

发布时序还需要两个托管改动：

1. 新增 `WIVSMSequence.StartAsyncRendering()` Harmony prefix，在其唯一一行 P/Invoke 前调用 `PublishAll`。REG 自己的 `PublishAndRender` 已经先发布再调用该方法；实现时要避免双增 epoch，推荐把有序的 `NativeNote` 身份字段（ordinal、seconds、pitch）与 REG 值组成 managed fingerprint，未变化时复用原 epoch 且不重复 P/Invoke。音符位置/音高改变但 REG 不变必须产生新 fingerprint；RemovePart、Clear、Part 删除和工程关闭同步删除 fingerprint/epoch。
2. `RegisterShiftRendererStartPatch` 不能再被描述为“渲染前”发布。`WIVSMRendererObserver` 的 native Started vtable 方法虽然同步 tail-call 已注册回调，但渲染线程只是把 observer 事件放入 sequence 队列；托管 `Sequence` 构造函数把 `timerInvokeRendererObserver` 设为 DispatcherTimer，并在每次 Tick 中循环调用 `VSMSequence.InvokeRendererObserver()` 才实际触发 `WIVSMRendererObserver.InvokeStartEvent -> MusicalEditorViewModel.OnRendererStarted`。因此该 Harmony patch 在调度上明确晚于 task 启动，不能依赖 `FUN_180060640` 到首个 Func9 之间的准备时间。`StartAsyncRendering` prefix 是唯一主发布路径；Started 只作后续刷新和诊断。

项目加载还有单独竞态：反编译 `Sequence.InitializeForLoad()` 在原方法内部调用 `vsmSequence.StartAsyncRendering()`，而现有 `RegisterShiftProjectLoadPatch` 直到 `LoadProjectSequenceFile` Postfix 才执行 `LoadProjectData`。因此普通 StartAsync prefix 在第一次加载渲染时仍看不到存档 REG。首选闭合方式是在 Load Prefix 读取 archive 后建立线程局部 pending-load context；StartAsync prefix 在同一同步调用栈中拿到已经构造好的 `WIVSMSequence`，先无 UI 通知地应用 pending 数据并发布，再允许 P/Invoke；Load Postfix 只做通知和未消费 fallback，Finalizer 必须清掉 pending context。另一方案是抑制初次 StartAsync、Postfix 应用后再启动，侵入原流程更大。两种方案都必须用“打开含非零 AI REG 的工程后第一次渲染即生效”验收，不能靠任务调度碰巧慢于 Postfix。

所有缓冲都使用带上限和乘法溢出检查的失败回退，避免宿主进程因异常记录数或分配失败 panic/abort。传给 S5 的私有 `0x88`、`0x28` 和完整 feature 缓冲不能依赖 `Vec<u8>` 只有 1 字节的类型对齐保证；应使用 panic-free 的 RAII `AlignedBuffer`（例如 checked `Layout` + `alloc_zeroed/dealloc`）提供至少 32 字节对齐，并在调用 shifted 原函数前完成全部分配。零长度直接走 baseline，不把伪造的 dangling 指针交给 S5。TLS 若使用 `RefCell`，不能在调用原函数期间持有可变借用；应先把本次所需数据移到局部/标记 Busy，释放借用后调用原函数，再无 panic 地写回结果。任何重入看到 Busy 状态都只走原函数。

还必须处理 C++ 异常穿越 hook 的 ABI。当前 `Cargo.toml` 的 Release 配置是 `panic = "abort"`，现有原函数指针和 hook 都声明为非展开的 `extern "system"`；但 Func417 已观察到 `_CxxThrowException` 分配失败路径。Rust Reference 明确规定：外部异常穿过非 unwind ABI 是未定义行为，而 `panic=abort` 即使配合 unwind ABI，native unwind 到达 Rust 边界也会 abort。若 AI hook 继续由 Rust 直接调用 S5/VSM 原函数，首选改为 `panic = "unwind"`，并把 task wrapper、六个 hook 定义及其原函数类型全部声明为 `extern "system-unwind"`；本机 Rust 1.97.1 已接受该 ABI。现有 C ABI 导出和传统 `extern "system"` hook 仍保持非 unwind，Rust panic 到这些边界继续安全 abort；AI hook 自身则必须用 checked access、`try_borrow_mut`、无毒锁处理和 panic-free Drop，不能让 Rust panic 逃入 C++。也不能用 `catch_unwind` 包住原 S5 调用来“捕获”C++ 异常，官方对此只保证 abort 或不透明 Err，行为不可作为恢复协议。若不愿把 crate 改为 panic-unwind，唯一稳妥替代是让一个有 C++ unwind 语义的原生 shim 实现六个 hook，并只在原函数调用前后进入 Rust；不能维持现状后假设这些导出永不 throw。

异常清理所需的 state 析构链也已静态核对：`Func_95031259 -> FUN_1800109a0 -> FUN_18000f8e0/FUN_18000f9a0` 的正常有效对象路径只清空/释放内部向量、对齐缓冲和 state，没有 `_CxxThrowException`；检测到损坏的 MSVC 容器元数据时会进入 `_invalid_parameter_noinfo_noreturn`，那是终止进程而不是可恢复异常。因此 shadow RAII owner 可以在 foreign unwind 的 Rust cleanup 中直接调用**保存的原 Func950**，前提是所有权位已保证它未转交 VSM、也未由 Func950 hook 清除。Drop 绝不能走已换槽表形成递归，也不能触碰 VSM 拥有的 actual；若 VSM 的异常清理先调用 Func950 hook，hook 必须先从 TLS 取走/清零配对，再分别释放 shadow 和透传 actual，使外层 Drop 看到空所有权。

参考：[Rust Reference：FFI unwinding](https://doc.rust-lang.org/stable/reference/panic.html#unwinding-across-ffi-boundaries)、[Rust Reference：ABI 与 unwind 行为表](https://doc.rust-lang.org/stable/reference/items/functions.html#unwinding)。

## 8. 已知边界与风险

1. **音域截断**：VSM 原始音高被限制为 MIDI 24–93。移调后继续限制会让实际差值小于 `100 * Δᵢ`，输出补偿必须采用“实际偏移后值减基线值”，不能盲减配置值。
2. **两阶段一致性**：计数和填充阶段必须使用同一组逐音符匹配结果与同一记录内容。
3. **额外基线调用的状态风险**：9/cfc 一侧的 `FUN_180041c60`、`FUN_180053490`、`FUN_180054a60` 已确认只读模型/输入并写调用方 vector 或对齐 scratch，矩阵内核不带模型对象 `this`；静态副作用风险已明显降低。Func417 本体及其直接助手 `FUN_18004f2b0`、`FUN_180053c20`、`FUN_180054ee0`、`FUN_180056bf0` 同样未直接写 S5 顶层/模型参数，但 `053c20/054ee0` 的后端算子仍通过模型内部对象虚调用，可能存在更深层缓存。未通过 `Δ=0` 动态验证前，仍不能把 9/cfc 四调用或 Func417/Func2d 双调用视为完全无副作用。
4. **线程安全与嵌套**：不能使用单个进程全局的当前 Part/偏移。TLS scope 必须支持恢复上一层，并且私有缓冲的生命期至少覆盖本次完整渲染。
5. **导唱行为**：导唱调用相同的前端转换函数，但目前没有确认与完整渲染相同的 Part 绑定入口。第一版应在无明确 TLS scope 时保持原状，再单独研究是否需要支持。
6. **版本适配**：硬编码地址只用于研究。正式实现应校验模块版本、关键函数签名和记录布局；导出项优先按名称定位，VSM 内部函数用版本化签名定位。
7. **失败回退**：找不到函数、逐音符匹配不唯一、基线/偏移段数量不一致、帧数不符或上下文失配时，应完全跳过本次偏移，不能影响正常渲染。
8. **窗口提前退出**：`FUN_180085070` 的模式 1/2 不消费 Func417 输出。差值状态必须按 Func417 调用覆盖，不能跨窗口排队；提前退出本身不是补偿失配，因为该路径未进入逐帧模型循环。
9. **F0 补偿的动态传递函数**：`Func2d` 末端存在两级非恒等 IIR，直接减 raw delta 在边界处没有理论等价性；影子 biquad 只能复现已确认的末端滤波响应。`ExactDualState + ExactBaseDelta` 静态上覆盖了前级、条件分支和全部滤波历史，并保留 shifted residual，但仍依赖额外 Func417/Func2d 调用不改变共享隐藏状态，动态 A/B 通过前只能视为实验性 AI 支持。
10. **精确模式资源成本**：Exact 模式需要完整 baseline 特征缓冲，并大致翻倍 Func417、Func236 预热和逐帧 Func2d 计算。必须设置统一 scratch 上限、在 shifted 覆盖宿主输出前完成 rollback 资源分配，并测量长 Part、并行 Part、取消和低内存条件；正确但明显拖慢渲染时应保留轻量模式供选择。
11. **双 state 所有权**：actual state 永远由 VSM 拥有并按原路径释放；shadow state 只由 hook 拥有。Func950 hook、外层取消清理和重入回退必须保证 shadow 恰好释放一次，任何情况下都不得抢先或重复释放 actual。
12. **同 Part 多 generation**：当前版本对同一底层 MidiPart 的旧 job 会设置取消状态并同步等待完成，之后才移除并创建新 job，静态上排除了旧 job 晚于新 job 覆盖结果。task 入口仍必须克隆固定 `{epoch, Arc}`，并以连续重渲染测试验证版本签名和运行时顺序；若兼容版本缺少 `FUN_180100140 -> FUN_180067460 -> FUN_180064430` 的等待链，应拒绝启用 AI hook 或重新定位 native generation，不能降级成读取“最后一次表值”。
13. **外部异常与 panic 策略**：S5 有真实 C++ throw 路径；非 unwind ABI 是未定义行为，当前 Release `panic=abort` 即使改 ABI 也会在 native unwind 时 abort。Rust 直调方案必须同批切 `panic=unwind`、使用 `system-unwind` 并保持 AI hook/Drop 无 panic，或改用 C++ shim，不能只改函数类型的一半。
14. **私有缓冲对齐**：Rust `Vec<u8>` 的语言级对齐不足以充当任意 S5 C++ 数组。所有交给原函数的克隆/feature 缓冲都要显式对齐、清零并由 panic-free RAII 回收；溢出、超预算或对齐分配失败发生在宿主输出被修改前。

## 9. 尚未闭合的问题

### 9.1 Part 绑定与单槽入口：静态链路已闭合

完整 AI 渲染 task 的专用 vtable 槽可定位到 `FUN_180065570`；task `+0x08` 的 renderer `+0x10` 可直接取得与托管 `CppObjPtr` 相同的 Part 指针，同时能在调用原 wrapper 前沿确定链取得 `host+0x150` S5 表。实现不再需要修改 `FUN_18006c6d0` 或 `FUN_180083830` 代码页。剩余动态工作是验证取消/宿主异常时 Rust wrapper 的退出 guard 一定执行，以及不同补丁/版本下 vtable 槽冲突会安全拒绝而不是覆盖。

### 9.2 AI 时间字段与逐音符发布 ABI

托管侧并不是自行实现 tempo 换算：`WIVSMSequence.GetTimeFromTick(begin,end)` 直接 P/Invoke `VSM!VIS_VSM_WIVSMSequence_timeFromTick`，后者调用 sequence 虚表 `+0x60`；`PresendTimeSec` 同样直接调用 VSM 导出及虚表 `+0xd8`。`FUN_180072580` 构造原生 `0x88` 时间时使用 VSM 的同一 tempo 积分原语 `FUN_18004a560`，再加渲染上下文中的 `double` 时间偏移。因此托管现有的：

```text
PresendTimeSec + GetTimeFromTick(part.AbsBeginTick, note.AbsPos/EndTick)
```

已经可以进一步闭合为同一偏移口径：`FUN_18006db10` 写入传给 `FUN_180072580` 的 holder `+0x38` 是 `VSM!1802cabf8`，原始 double 字节为 `00 00 00 00 00 00 E0 3F`，即 `0.5`；Sequence 主 vtable `+0xd8` 指向 `FUN_18008e730`，也直接返回同一常量。因此这里不存在因裁剪/前一 Part 改用另一 presend 值的问题。

tempo 积分仍经两条等价但不完全同一的内部包装路径：托管导出经 vtable `+0x60 -> FUN_18008e5d0 -> FUN_1800431c0`，原生记录构造经 `FUN_18004a560`。不能静态承诺最后一位 bit 必然相同。匹配应先尝试 double 位相等；否则允许：

```text
abs(nativeTime - managedTime) <= max(1e-7, 16 * f64::EPSILON * max(abs(a), abs(b), 1))
```

`1e-7` 秒在 44.1 kHz 下不足 `0.005` 个采样点。容差必须同时满足 begin/end、裁剪后的 baseline pitch 和严格单调 ordinal；出现多个候选即整次回退，绝不能只凭时间容差匹配。

现有原生 REG 表只发布音符起止 frame、工程音高和 ordinal。ABI v15 仍应追加上述 begin/end seconds；用 frame 反推秒会额外引入量化误差，没有采用理由。

### 9.3 未偏移 `0x28` 基线的安全取得方式

静态链路和 S5 原生 9/cfc 的重复计算契约都强烈倾向于该转换可重复；继续审计到 `FUN_180041c60/180053490/180054a60` 后也未发现持久模型写回或带对象上下文的后端调用。剩余动态问题主要是分配/异常路径、浮点后端可重复性以及更深层未命名 helper 的版本差异。仍需测试“shifted 额外调用在前、baseline 正常调用在后”的四调用方案；若 `Δ=0` 产生任何输出或后续状态差异，就不能用双组调用，必须研究状态影子或解析基线段的替代方案。

### 9.4 后级字段链与精确双状态路径已静态闭合，仍需宿主 A/B

Func2d 的 8 字节输出在 `FUN_180085070` 中按原顺序拆开：`output[0]` 进入第一条逐帧数组并重采样为 `local_148`，`output[1]` 进入第二条数组并重采样为 `local_a8`。`FUN_180081820` 把 `local_148` 写入临时 0x48 记录 `+0x04`，把 `local_a8` 独立写入 `+0x14`；`FUN_180081690` 只补齐 note/音素元数据，不覆盖这两个字段，`FUN_18000df90` 又把它们原样复制进最终对象。至少到该打包边界，两条通道没有被重新相加，也没有按 shifted MIDI key 重算。

继续检查 `FUN_180085070` 的成功返回路径后，音频主 F0 的来源已经可以静态命名。函数先以 `local_148` 为基础生成/修正 `local_130`，再经 `FUN_1800666b0` 重采样成 `local_f0`，并把该 vector 作为自己的 `param_1` 返回；`local_a8` residual 只作为上述 0x48 记录的 `+0x14` 传出，不参与 `local_130 -> local_f0`。`FUN_180086740` 把这一返回 vector 捕获给 vtable `1802c5df8` 的调用项 `FUN_18008b570 -> FUN_180088280`；后者逐帧取该 vector、加固定偏置并限幅，写入声码器输入结构 `+0x08`，随后经捕获的函数指针生成音频块。闭包没有把 0x48 residual 记录 vector 作为另一条 F0 输入，也没有在调用声码器前再次把 `+0x14` 叠加到 total。

这不等于声称 residual 记录在整个产品中毫无用途；它仍可供事件、缓存或诊断对象消费。但在当前版本的即时音频合成路径上，宿主可听 F0 由已经包含 residual 的 `output[0]` 派生，`output[1]` 不会作为独立音高通道二次注入。因此轻量候选只修改 actual `output[0]`；`ExactDualState` 的首选组合确定为 `ExactBaseDelta=(shadow[0]-shadow[1])+actual[1]`，只替换两套原生 filtered base 的差并保留 shifted residual 对 total 的贡献。直接采用 `shadow[0]` 的 `BaselineTotalControl` 降为严格 baseline total 对照。两种双状态组合仍都保留 actual 写入的 `output[1]`，以维持记录/非即时消费者兼容。

此前可疑的 `0xa0 +0x54` 也已按字节闭合。`FUN_180072580` 构造普通 `0xa0` 记录时：

- `+0x50 float` 明确写入 `(noteNumber - 69) * 100`，这是 baseline 音符 cent；
- `+0x54..+0x57` 分别由四个只取 `0/1` 的状态标志逐字节组成，`FUN_180083480` / `FUN_180083660` 也分别按 byte 读取它们；合成边界记录由 `FUN_1800724d0` 把整个 `+0x54 dword` 清零；
- `FUN_180085070 @ 180085910` 的机器指令确实执行 `MOVSS [record+0x54]`，并在 `18008593d` 用 `SUBSS` 从第一条逐帧数组相减。这是把四个标志的原始位模式解释成 float；普通路径最大位模式 `0x01010101` 约为 `2.37e-38`，不是 cent 量级，也不依赖音符 key 或 REG。

`FUN_180081690` 只把 baseline `record+0x50` 放入最终记录的基准音高字段；它不读取 `+0x54`。由于方案刻意让 VSM 前段始终保留 baseline `0x28/0xa0`，这个基准字段本来就应保持工程音高；真正受 shifted S5 模型影响的 Func2d `output[0]` 只在叶子 hook 的调用方输出上应用候选补偿。`FUN_180086740` 没有读取该 `0xa0 +0x54` 字段，也未发现重新加回 shifted note key 的路径。静态上因此没有重复补偿点；最终仍应以宿主输出 F0 A/B 验证音高保持而音色发生预期变化。

这里“没有后级重复补偿点”不等于“raw delta 就是精确补偿量”。`FUN_180017b40` 两级 IIR 位于 Func2d 输出之前；更前面还有神经模型、一阶递推和条件分支。静态上最完整的生产候选现已改为 `ExactDualState + ExactBaseDelta`：完整 baseline Func417 特征、独立原生 Func2d state、逐帧用两套原函数输出恢复 baseline filtered base 并加回 shifted residual。`BaselineTotalControl` 是严格 baseline total 对照，`ShadowFinalBiquad` 是性能较轻的候选，raw-delta 仅保留为诊断对照。最终默认值仍必须由逐帧 F0、微观表现、音色目标和渲染耗时共同裁决，不能只因静态链路闭合就宣称宿主结果已经验证。

### 9.5 VSM task hook 安装与 S5 表延迟就绪

当前 Rust `register_shift_hook::install()` 在传统 DSE 的 `BIT_PREPARE_1B | BIT_SELECTOR_1B` 已齐全时立即返回，根本不会再尝试其它模块；托管 `EnsureLoaded()` 在 `_loadState == 1` 时也直接返回。因此若首次安装传统 hook 时 `S5API.dll` 尚未加载，之后即使打开 AI Part 也不会自动补装 AI hook。

AI 安装状态必须独立且可重试。推荐增加幂等的 `EnsureAiInstalled()`，但它只负责在 `VSM.dll` 上定位 `FUN_180065570` 并原子替换专用 PPL task vtable `+0x70` 槽；不要求此刻已经存在 `S5API.dll`，也不修改任何 VSM/S5 代码页。首个带合法 AI Part 快照的 task 入口再从 renderer 取得当前 S5 表并换六个叶子槽，同时校验未替换的 Func52。正常对象生命周期中表已由 `FUN_18002cf50 -> FUN_180070980` 填好，可覆盖同一次渲染；若校验发现尚未就绪，则当前渲染完整 baseline，下一次 task 入口重试。

Native status 至少要区分 `traditional-ready`、`AI task-slot-ready` 和最近一次 `AI table-ready/validation error`；托管单一 `_loadState == 1` 不能再被解释成两种引擎均已就绪。`EnsureAiInstalled()` 应在 Patcher 初始化阶段调用；若那时 VSM 尚未加载，`PublishPart` 可在 UI 调度边界作幂等补偿重试。`OnRendererStarted` 已经晚于 PPL task 启动，不能作为首次安装点。现有 `write_absolute_jump` 是 14 字节非原子代码写，而新方案只 CAS 一个 8 字节对齐 vtable 指针；六个 S5 叶子槽同样只做原子指针替换。

建议在 REG 专用 ABI v15 新增独立导出 `v6_register_shift_install_ai()`，保留现有 `v6_register_shift_install()` 只表示 traditional DSE 安装；同批增加 `v6_register_shift_abi_version` 和两个布局 size 导出，保持共享 `v6_clock_abi_version()==14`。托管层把“原生库和 REG ABI 已成功加载”与“两种引擎各自安装结果”拆开：`EnsureLoaded()` 只加载 DLL/导出；`EnsureTraditionalInstalled()` 与 `EnsureAiInstalled()` 分别缓存和重试自己的结果。AI 安装只在 `VSM.dll` 暂未加载时返回可重试的 `ModuleNotLoaded`；传统 DSE 不支持不能阻止 AI 发布，反之亦然。

一旦 VSM vtable 槽或任一 S5 函数表槽已指向 `v6patch_clock.dll`，该原生库必须保持加载到宿主进程退出；后续某一引擎安装失败也不能 `NativeLibrary.Free`。关闭设置只清除 Part 表/TLS 激活条件，task wrapper 与叶子继续以无 scope 透传原函数。

现有 `RegisterShiftStatus` 的前 368 字节保持不变，ABI v15 可在尾部按下列顺序追加 96 字节，使总大小固定为 464：

| 新字段 | 类型 | 更新位置/含义 |
| --- | --- | --- |
| `AiInstallResult` | `i32` | AI task vtable hook 安装结果 |
| `AiInstallBitmap` | `u32` | wrapper 已定位 / vtable 槽已 CAS 两个状态位；不把动态 S5 表换槽当永久位 |
| `AiTableReadyScopes` | `u64` | 外层退出时汇总，本 scope 六个 hook 槽完整且 Func52 保持原值 |
| `AiFallbackScopes` | `u64` | 外层退出时汇总，本 scope 全程或中途回退 baseline |
| `AiValidationFailures` | `u64` | 表、布局、配对或行数校验失败总数 |
| `AiLastPart` / `AiLastEpoch` / `AiLastTable` | 各 `u64` | 最近一次外层快照与实际表地址 |
| `AiLastFunc417Calls` / `AiLastFunc2dCalls` | 各 `u64` | 最近一次 scope 对原导出的总调用数；Exact 正常路径通常分别是窗口/消费行数的两倍 |
| `AiLastEmittedRows` / `AiLastConsumedRows` | 各 `u64` | 最近一次 scope 的行数守恒检查 |
| `AiLastError` / `AiLastCompensationMode` | `i32` / `u32` | 最近失败码与实际采用的补偿/回退模式 |

这些计数在外层退出时一次性发布；Func2d 热 hook 不做全局原子递增。C# `ExpectedNativeStatusSize`、Rust `size_of` 单测和两端字段顺序必须同时更新。

`AiLastCompensationMode` 的 ABI 数值也应固定而不是直接序列化 Rust enum：`0=BaselineFallback`、`1=RawDelta`、`2=ShadowFinalBiquad`、`3=ExactBaseDelta`、`4=BaselineTotalControl`、`5=BaselineCurvePlusResidual`；未知值在托管侧显示为 Unknown，不能默认解释成 Exact。

托管支持判断也要改为引擎相关：增加类似 `IsSupported(WIVSMMidiPart? part)` 的入口，traditional Part 看 DSE 状态，AI Part 看 AI task-slot 状态。`BreathVolumeOverlay`、参数头数值框和渲染开始发布都必须传 active/rendering Part；设置页只需在两种引擎都不支持时隐藏。MCP `ExtensionParameterRegistry` 当前也只报告全局 DSE 文案，应改成“任一引擎可用”的能力状态，并在具体 Part 操作时返回该 Part 的引擎状态。诊断日志中以 `NativeRegisterShift.Status == Installed` 为条件的分支同样不能漏掉 AI-only 就绪情形。

### 9.6 宿主验证

实现前后至少需要以下 A/B：

- 用强制诊断 scope 让 `Δ=0` 确实进入 hook，比较未打补丁单调用、9/cfc 四调用、Func417 双调用和 shadow/actual Func2d 双状态的逐帧输出；shadow 的 baseline total/residual 必须达到 bit 一致或预先定义的浮点门限，`ExactBaseDelta` 在 Δ=0 时还应与 actual total 落在事先规定的 float 舍入门限内，并确认 actual 返回字仍为零、VSM 结果不依赖 shadow 返回字，不能只比较最终 WAV；
- 单一持续音分别测试正负偏移，确认最终 F0 不变而音色/发声区发生变化；
- 相邻音符设置不同 REG，确认补偿在真实段边界切换且不串音符；
- 对同一组 constant REG、长音、极短音及 `+12/-12` 交替音符比较 `ExactBaseDelta`、`BaselineTotalControl`、raw-delta 与 shadow-biquad；至少记录边界前后逐帧 F0 误差、稳态误差、最大瞬态误差、residual/微观表现差异、总渲染耗时、峰值常驻内存和取消响应，不能只听最终音频；
- 音符跨越 MIDI 24 或 93 边界时确认按实际裁剪差值补偿；
- 两个不同 AI Part 并行渲染，确认 Part、epoch 和帧游标不串线；
- 对同一 AI Part 在前一渲染未完成时连续修改 REG 至少几十次，确认旧 job 的取消/完成总发生在新 job 启动前，且旧 generation 不会在新 generation 后提交音频；日志要把 task-entry epoch、job `+0x38` 状态、Started/Completed 顺序和最终缓存 generation 对齐；
- 完整渲染、重新渲染、模式 1/2 提前退出、取消和异常退出均不泄漏 TLS scope 或 shadow state，且 actual state 仍只由 VSM 释放一次；
- 导唱在第一版无 scope 时与未启用补丁一致；
- 用诊断故障注入强制 shifted Func417 行数失败，确认 Exact 模式把已验证的私有 baseline 特征复制回宿主输出、后续 Func2d 走 baseline/零偏移窗口；再分别强制 shadow 分配失败/status 非零、actual 分配失败/status 非零及两指针意外相同，确认 status=0 才被接受、非空失败对象恰好释放一次、成功 shadow 只在转交后由 VSM 释放，而别名对象释放一次后必须新建 baseline state，整个窗口安全降级；
- 两个线程同时首次进入同一/不同函数表，确认原函数指针先发布、六个 hook 槽最终一致且 Func52 保持原值，未 ready 的 scope 全部透传；
- AI 安装时 S5 尚未加载与已经加载两种顺序；表在 task 入口有效时确认当前渲染的首个 Func9 前已完成换槽，故障注入为未就绪时确认当前渲染 baseline、后续入口可重试；
- 安装 task vtable hook 时并发执行旧任务，确认调用方只会观察到完整旧/新指针；槽已被其它补丁改写时必须拒绝覆盖；
- Release 构建确认 AI hook 与原函数类型均为 `system-unwind`、PE `.pdata` 包含相应 unwind metadata，Rust 单测覆盖 AlignedBuffer 的 32 字节对齐、清零、零长度、溢出和 Drop；切换 `panic=unwind` 后重新跑全部 Rust 测试并核对 DLL 大小/依赖没有意外变化；
- 记录 `StartAsyncRendering` prefix 发布 epoch、Started observer 延迟送达 epoch 和首个 Func9 捕获 epoch，确认 prefix 先于首帧、Started 不被误当成本任务前置发布且同一 fingerprint 不重复增 epoch；在不改 REG 的情况下修改音符位置/音高后重渲染，首个 Func9 必须取得新音符身份快照；
- 打开一个存有非零 AI REG 的工程，确认第一次自动渲染就使用存档值；同时验证 load pending context 在正常、异常和未触发 StartAsync 三条路径都被清理。

生产代码当前会在 Part 所有 REG 都为 0 时直接 `RemovePart`，因此普通 UI 无法真正触发第一项 `Δ=0` 双调用测试。实现阶段必须提供一个仅用于本地诊断、默认关闭且不写入用户配置的强制 AI scope/test export，或编译两个测试变体；否则看到“0 偏移无差异”只说明 hook 根本没有进入。A/B 应同时核对最终音频/逐帧输出、六个叶子 hook 的调用与 state 所有权计数、Func52 预检、表 ready、baseline/shifted emitted/consumed 行数和 fallback 计数。

## 10. 2026-09-02 实现落地状态

本轮已按本文的 `ExactDualState + ExactBaseDelta` 主方案落地源码，尚未做 VOCALOID 宿主内 A/B：

- `native/playback-clock/src/ai_register_shift.rs` 新增独立 AI 安装器，只在 `VSM.dll` 的 Renderer task vtable `+0x70` 做对齐指针 CAS；每次 task 入口再验证当前 S5 表并 CAS 六个叶子槽，`Func52` 保持原值。
- 原生叶子状态机实现 `Func9 -> FuncCfc -> Func417 -> Func236/Func2d/Func950` 的 baseline/shifted 双路径；逐帧采用 `(shadowTotal-shadowResidual)+actualResidual`，并在布局、数量、配对、容量、状态或所有权校验失败时尽量保持/恢复 baseline。
- 私有 S5 缓冲统一使用 32 字节对齐、清零的 RAII 分配，单 scope scratch 上限为 64 MiB；Release panic 策略改为 unwind，外部调用 ABI 使用 `system-unwind`，scope guard 只回收仍归补丁所有的 shadow state。
- REG 专用 ABI 固定为 15；`NativeNote` 扩展至 48 字节并发布精确秒时间，`RegisterShiftStatus` 扩展至 464 字节。共享 `v6_clock_abi_version` 继续保持 14，避免影响播放时钟、DSE capture 和 breath capture。
- C# 原生加载状态已拆为 library、traditional install、AI install 三层；AI/传统安装互不阻断且 `ModuleNotLoaded` 可重试，支持状态改为按 Part 引擎判断。
- AI Part 已开放参数列表、覆盖层、发布、MCP 查询/修改和工程持久化；新增 `WIVSMSequence.StartAsyncRendering` prefix，使用内容 fingerprint 复用 epoch，音符位置/时长/音高或 REG 改变都会发布新快照。
- 工程加载用线程内 pending context 在第一次 `StartAsyncRendering` 前无通知应用存档 REG；Postfix 负责通知及未消费 fallback，Finalizer 清理上下文。
- VSM 定位器不再假设映像只有一个可执行节；它会逐个扫描全部可执行节，并用全部可执行节共同校验外层调用目标和 vtable 邻槽。这样可兼容仅发生代码节拆分、重排或 RVA 改变的构建，同时仍保留 wrapper 唯一性和只读非执行 vtable 区域约束。
- 已通过 Rust 24 项单元测试（含 48 字节布局、秒时间匹配、AI pitch 重写、对齐/清零/预算及不连续多代码节范围校验）、严格 Clippy 和解决方案 Release 构建；还需要按 9.6 节完成真实 AI 声库的 `Δ=0`、正负偏移、相邻不同值、取消/并行/快速连续编辑、首次加载渲染与性能 A/B。未经过这些宿主验证前，不能把静态实现结论等同于声音结果已确认。
- 对本机安装文件做了只读离线校验：VSM wrapper 签名唯一定位到 RVA `0x65570`，对应只读槽唯一定位到 `0x2c4d98`；S5API 七个具名导出的 RVA 与下表一致。Release DLL 保留 `.pdata`/Exception Directory，并确认六个新增/相关 REG v15 导出存在。

## 11. 关键地址索引

| 模块 | 地址 | 证据 |
| --- | ---: | --- |
| S5API | `18006beb0` | `Func_9cbce37f` |
| S5API | `18006bf45`–`18006bf6c` | `0x88` 输入记录、`+0x10` cent 到 MIDI key |
| S5API | `18006d0e0` | `Func_cfc85a30` |
| S5API | `18001ff70` | `0x98` 内部音符到 `0x58` 发音段的主要转换 |
| S5API | `1800560c0` | 拆分/插值 `0x58` 段；复制 `+0x30` 类别数组并插值 `+0x50` 音高 |
| S5API | `180041c60` / `180053490` / `180054a60` | 9/cfc 的规范化/模型助手；只读输入与权重，写调用方 vector/64-byte aligned scratch，未见持久 state 写回 |
| S5API | `180061b10` | `Func_417eec3e`，`0x28` 发音段转逐帧特征 |
| S5API | `18006e240` | `Func_efe389ac`，Func417 前按末段结束时间估算容量，不读音高 |
| S5API | `180005fc0` | 小写 `br` → `sil` 发音段归一化/相邻 `sil` 合并 |
| S5API | `1800894a0` | 小写 `sil\0` 常量（`73 69 6c 00`） |
| S5API | `18004d960` / `18004dd3e` | 并行 `0x84` 时序/音素特征构造；明确读取归一化记录 `+0x50` 音高，所以 baseline/shifted 不保证只差输出首列 |
| S5API | `180055af0` | 分段常数音高曲线展开 |
| S5API | `18004f2b0` / `180053c20` / `180054ee0` / `180056bf0` | Func417 下级特征/模型助手；模型参数显式只读、写调用方缓冲，但 `053c20/054ee0` 仍含内部对象虚调用，需动态排除后端隐藏状态 |
| S5API | `180062a80`–`180062a8c` | Func417 按从 0 递增的曲线索引把首个 float 写入输出行 |
| S5API | `180061330` | `Func_2db29822` |
| S5API | `1800618f3` / `1800618f7` | 总音高与残差输出 |
| S5API | `180017b40` | Func2d 唯一调用的两级 biquad/IIR 总音高滤波器 |
| S5API | `180060ef0` / `180061107`–`1800611bf` | `Func_236a1ecf`，分配 `0x2e0` Func2d state 并初始化前级递推、两级 biquad 系数和零历史 |
| S5API | `180067b10` | `Func_52f85f15`，只读并返回 Func2d state `+0x250` status；0 成功、非零失败 |
| S5API | `18006be30` | `Func_95031259`，清理并释放完整 Func2d state |
| S5API | `18006dec0` | `Func_e8258087` |
| VSM | `180070980` | S5API 加载与函数表填充 |
| VSM | `18002cf50` / `+0x150` / `+0x230` | 加载 S5API；同一宿主对象内的函数表与模块句柄 |
| VSM | `180064f40` / `180065ca0` | Renderer start PPL task 的构造/析构；确认 vtable 与 task `+0x08` renderer 布局 |
| VSM | `1802c4d28` / `1802c4d98` | 专用 task vtable base / `+0x70` wrapper 槽 |
| VSM | `180065570` / `18006559b` | 唯一 task wrapper / wrapper `+0x2b` 对外层的唯一代码 call |
| VSM | `18006c6d0` | 完整 AI 渲染外层；作为 wrapper 定位和结构校验证据，不再 patch |
| VSM | `18006db56`–`18006db63` | 从 renderer 解引用到 host，并取 `host+0x150` S5 表 |
| VSM | `18006ec20` | holder 复制；首 qword 原样保留函数表地址 |
| VSM | `180060640` / `18006ca34` | 外层先执行使用 renderer `+0x10` Part 的准备阶段，之后才进入 `FUN_18006db10 -> Func9` |
| VSM | `1800683b0` / `18006a580` / `18006a0a0` | StartAsync 顶层 task 管理：零超时检查旧 task，运行中则不另起并发 manager，完成后才释放并重建 |
| VSM | `180068d60` / `180100140` | 长驻 manager 的 active job 调度；同 Part 替换时写取消状态并同步收口旧 task |
| VSM | `180067460` / `180064430` | async-state 释放/等待链；在条件变量上等 task 完成后才允许旧 job 被移除 |
| VSM | `18006d720` | renderer 后端等待循环读取 job `+0x38`；值 2 返回取消码 `0x14` |
| VSM | `180083830` | 完整渲染前段先调用 Func9/Cfc；作为调用链/签名校验证据，不再 patch |
| VSM | `180083faa` / `180084023` | 完整渲染的计数/填充调用 |
| VSM | `180086c42` | 后段把复制后的同一表 holder 传入 `FUN_180085070` |
| VSM | `1800852e0` | 每个窗口唯一一次 `Func_417eec3e` 调用 |
| VSM | `180085334`–`180085367` | 经 `table+0x58` 创建 Func2d state，并经 `table+0x68` 检查 status；`TEST EAX,EAX; JZ` 进入正常路径 |
| VSM | `1800852ee`–`18008532d` / `18008555f`–`180085586` | 构造并复用 `0x18` feature descriptor；shadow 只能替换首指针 |
| VSM | `180085572` / `180085589` | 读取 `table+0x70` / 逐帧间接调用 `Func_2db29822` |
| VSM | `180085520`–`1800855bc` | 按 Func417 返回行数顺序消费，每行一次 Func2d |
| VSM | `1800853ec`–`1800853f3` / `18008563d`–`180085644` | 失败/正常两条路径都经 `table+0x60` 释放 Func2d state |
| VSM | `180081820` / `180081690` / `18000df90` | total 写 0x48 记录 `+0x04`、residual 独立写 `+0x14`，随后补元数据并原样复制；该边界不重组两通道 |
| VSM | `180085070` / `1800666b0` | total 经 `local_148 -> local_130 -> local_f0` 形成主 F0 返回 vector；residual 另存 0x48 记录，不参与该 vector |
| VSM | `1802c5df8` / `18008b570` / `180088280` | 异步区域 lambda 把主 F0 vector 写入声码器输入 `+0x08`；未把独立 residual 记录再次叠加进即时 F0 |
| VSM | `1800890c7` / `180089189` | 导唱的计数/填充调用 |
| VSM | `180072580` | `0x88` 真实音符记录构造 |
| VSM | `180072300` | 中间音高复制到记录 `+0x10` |
| VSM | `1800721d0` / `18006f9c0` | 音素临时记录 `+0x20` 到 `0x88 +0x68`；类别只映射为 `0/1/2/0xffffffff` |
| VSM | `1800724d0` | 合成边界 `0xa0` 记录构造，`+0x50` sentinel、`+0x54` 清零 |
| VSM | `180085910` / `18008593d` | 把 `0xa0 +0x54` 标志位模式作为 float 检查并从第一逐帧数组相减 |
| VSM | `18008e5d0` / `1800431c0` | 托管 `GetTimeFromTick` 的 Sequence tempo 积分路径 |
| VSM | `18008e730` / `1802cabf8` | `PresendTimeSec` 与原生 holder 共用的固定 `0.5` 秒 |
