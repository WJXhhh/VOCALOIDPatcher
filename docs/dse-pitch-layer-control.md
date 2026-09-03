# DSE 音高层独立控制逆向笔记

## 2026-08-12 实现状态

补丁中已有逐音符 `REG`（`0x524547`）的实验实现。2026-08-12 已按本文方案接入两套传统 DSE 后端，但仍属于 **待宿主 PCM/F0 A/B 验证的实验实现**：

- 原生 ABI 已升至 10；`v6_register_shift_set_part` 增加 render epoch，状态结构增加安装位图、1A/1B 独立计数及 slot/outer/parser/synthesis/thread/mode 观测字段。其它三个原生消费者同步要求 ABI 10，避免同一 DLL 被部分功能拒绝。
- VSM `FUN_180050eb0` 的渲染上下文 hook 由自动呼吸和 REG 共用，并记录 `vsm_mode_candidate`。TLS 失配时从 prepare 的 parser 解出 `synthesis=*parser`、`outer=parser[1]`，再查询带 epoch 的 `{slot,outer,parser,synthesis,part}` 条目；发布新 epoch 会清除该 Part 的旧 state 映射。
- 仅绑定 DSE 6.13.0.1：PE 时间戳 `0x69167C99`、`SizeOfImage=0xAC6000`，音符准备函数 RVA `0x1B3FB0`，selector RVA `0x1B7AA0`。这两个 Hook 只覆盖 `FUN_1801b3fb0 -> FUN_1801b7aa0` 分支；同一事件解析器还可能转入完全不同的 `FUN_1801a1800` 旧式候选评分分支。
- 1A 已安装四个版本签名保护的入口 Hook，并只重定向 `0x18019D39C` 的 getter CALL-site：候选裁剪的 pitch min/max 和评分帧 scratch 使用同一偏移。五组件安装位图不完整时 1A 强制零偏移，部分 Hook 不会产生半生效行为。
- 音符准备 hook 先按当前/目标记录的时值和 cents 查找不可变 Part 音符表；存在可唯一匹配的目标音符时使用目标音符值，否则使用当前音符值。selector 只把 `semitones * 100.0f` 加到第一个声学目标，第二声学特征和最终 F0 数据不改。
- 工程数据保存在 `VOCALOIDPatcher/register-shift.json`（schema v1），UI 值域为 `-12..+12` 半音。AI Part 不注册该参数。

Rust 的签名拒绝、ABI 状态大小、TLS 嵌套恢复、render epoch 过期拒绝、1A current/target 容器角色、五组件未齐时零偏移、同音高相邻音符时间消歧、重复候选回退和值域回退已有 15 项单元测试。`cargo clippy --all-targets -D warnings`、完整 Release 解决方案构建和 18 项 MCP 测试均已通过且无警告。宿主内的分支计数、`-12/0/+12` PCM/F0 A/B、并行 Part 隔离及连音目标归属仍必须按本文清单验证。

当前状态名称也应分层理解：

```text
Installed  模块身份和入口签名通过，Hook 已安装
Armed      当前 Part 已发布非零 REG 表
Entered    当前渲染 epoch 确实进入受支持的 DSE 准备分支
Matched    原生音符记录唯一映射到补丁音符
Applied    选样目标或选样代价实际应用了非零偏移
Verified   PCM 改变，并且最终 F0 在误差范围内保持不变
```

旧版日志中的 `Ready` 最多等价于当前的 `Installed`，不能代替后五级证据。

ABI 10 的 `install_bitmap` 定义如下：

```text
bit 0  1B prepare
bit 1  1B selector
bit 2  1A prepare
bit 3  1A candidate scope
bit 4  1A candidate prune
bit 5  1A candidate score
bit 6  1A frame-getter CALL relay
```

`install1a=1` 且 `bitmap & 0x7C == 0x7C` 才表示 1A 完整安装；负值保留具体的签名、内存保护或近地址 relay 分配错误。1A 部分入口即使已经安装，只要五位不齐，所有候选偏移都会返回 0。1B 的两位独立决定 1B 是否可用。

## 目标

在 VOCALOID6 的传统 `VOCALOID` 轨道中，寻找一种不改变最终目标音高（F0 / 音符音高），只改变 DSE 选取的原始录音音高层或等价音色来源的控制方法。预期效果类似 Synthesizer V 的“音区偏移”：同一旋律保持原音高，但以偏高或偏低音区的发声素材合成。

本文前半记录只读逆向证据，后半同步记录 2026-08-12 的实现和验证状态。

## 范围与术语

- **DSE**：VOCALOID3/4/5 等传统拼接式声库在 V6 中使用的原生引擎；本轮主线。
- **VSM**：V6 的分数、Part、渲染任务与 DSE/DNN 调度层。
- **DNN**：VOCALOID:AI 路径；仅在发现相邻或共享控制点时顺带调查。
- **目标音高**：最终应合成到的 F0/音高曲线。
- **源音高/音高层**：声库录音片段自身的原始音高或其分区/候选索引。

## 已确认环境

调查日期：2026-08-11 至 2026-08-12。

- Ghidra 主分析程序：`DSE.dll`
- 同一项目另开：`VSM.dll`、`S5API.dll`
- 三者均来自 `C:\Program Files\VOCALOID6\Editor\`
- `DSE.dll`：PE x64，image base `0x180000000`
- `VSM.dll`：PE x64，image base `0x180000000`
- `S5API.dll`：PE x64，image base `0x180000000`，产品版本 `8.0.0.0`

## 工作假设

要实现“音高不变、只换采样”，控制必须位于以下分离点之一：

1. DSE 在已知目标音高后，从多个同音素候选中选择源样本/音高层；
2. 声库查询接收独立于目标 F0 的 range/key/recorded-pitch 参数；
3. 合成内部先确定源样本，随后独立计算 `target F0 / source F0` 的移调比率；
4. 若不存在显式层索引，可在候选评分或查询音高上加偏移，同时保留原始目标 F0 给后续移调阶段。

其中第 4 种最接近预期参数，但要求找到“选择音高”和“合成目标音高”已经分叉的位置，否则直接偏移会连最终音高一起改变。

## 证据记录

### E-001：V6 将传统和 AI 声库分为 DSE 与 DNN 两条路径

状态：已确认（托管层和打开的原生模块名一致）。

V6 托管代码中的 `VDMVoiceBankType` 包含 `Dse` 与 `Dnn`；普通 MIDI Part 使用 `Dse`，AI MIDI Part 使用 `Dnn`。本轮因此以 `DSE.dll` 为选择机制主目标，以 `VSM.dll` 查找调用和数据传递边界。

### E-002：DSE 数据库确实把同类音素素材按一个独立浮点键排序

状态：已确认；该键是以 A4=440 Hz 为参考的 cents 音高。

相关 RTTI 类：

- `DSE5::CDBVStationaryPhU` / `DSE5::CDBVStationaryPhUPart`
- `DSE5::CDBVVQMPhU` / `DSE5::CDBVVQMPhUPart`
- `DSE5::CDBVVQMSample`

`CDBVStationaryPhU` 的 `FUN_180113f30` 和 `CDBVVQMPhU` 的 `FUN_180115d80` 都对 `+0x148` 指向的子对象指针数组执行 `qsort`。两者的比较器分别位于 `0x180114270` 与 `0x1801161d0`，逻辑相同：读取每个子对象 `+0x160` 的 `float` 并升序排列。

相关数据库字段：

- `PhU + 0x148`：子素材指针数组。
- `PhU + 0x150`：数组元素数。
- 子素材 `+0x160`：第一个排序/匹配浮点键。
- 子素材 `+0x16c`：第二个匹配浮点键；用途尚未命名。

这说明 DSE 数据层中确有独立于最终音频 F0 曲线的“同音素、多素材、按连续数值组织”结构。

### E-003：已找到按目标值选择最近 `+0x160` 素材的函数

状态：已确认。

`DSE.dll` 的 `FUN_1801b7930`（`0x1801b7930`）接收：

- 已按 `+0x160` 排序的对象指针数组 `[begin, end)`；
- 一个目标 `float`；
- 一个允许的邻域宽度 `float`。

它先找 `+0x160` 与目标值距离最近的元素（距离相等时偏向较低一侧），随后返回该中心元素附近、差值不超过邻域宽度的索引范围。该函数本身是选择辅助逻辑，不执行最终音高变换。

### E-004：实际音素素材选择路径把第一个声学目标和最终合成处理分开

状态：已确认到函数边界及调用约定。

`FUN_1801b7aa0`（`0x1801b7aa0`）针对每个音素：

1. 从音素对象 `+0x148/+0x150` 取得所有候选素材；
2. 按 `+0x160` 排序；
3. 以传入的第一个 `float` 目标选择最近素材及邻域；
4. 再在邻域内按 `+0x16c` 排序，并以第二个 `float` 目标继续筛选；
5. 将最终选中的素材指针写入后续合成工作结构。

主调用者 `FUN_1801b3fb0`（`0x1801b3fb0`）在准备传统音素素材时调用它。三个已确认调用点位于 `0x1801b451d`、`0x1801b54d9` 和 `0x1801b58ca`。第一个目标来自相邻分数/声学帧结构的 `+0x8`，第二个目标紧邻其后位于 `+0xc`。同一主函数在素材选定后继续生成时长、帧和输出数据，因此选择函数是目前最清晰的“只改变素材候选、不直接改最终 F0”切入点。

Win64 调用现场表明其可按以下原型理解：

```cpp
uint64_t SelectPhoneticUnits(
    void* engineState,
    SelectionWork* work,
    int duration,
    float pitchCents,
    float secondFeature,
    bool flag);
```

其中第 4 个位置参数 `pitchCents` 通过 `XMM3` 传入；第 5、6 个参数位于栈上传入。入口立即把 `XMM3` 保存到 `XMM8`，后续所有 `+0x160` 最近邻搜索都使用该副本。

### E-005：`+0x160` 和选择目标的单位已经确定为 cents

状态：已确认。

DSE 的相邻评分代码会把候选 `+0x160` 与目标帧的音高值相减，再按以下关系转成 Hz：

```text
frequency = 440.0 * 2 ^ (pitchCents / 1200.0)
```

相关只读常量包括 `440.0` 和 `1200.0`；在 1B selector 中，第一轮候选邻域常量为 `150.0`，即以最近音高层为中心保留相差不超过 150 cents 的候选。由此可以确定 UI 若采用半音整数，传给选择器的换算是：

```text
selectionOffsetCents = registerShiftSemitones * 100.0
```

这不是“猜测层号”：数据库使用连续的录音音高元数据，实际可用层数、层间距和边界由声库自身决定。偏移越过可用范围时，最近邻逻辑自然夹到最高或最低可用素材。

### E-006：初步可行的控制方式

状态：高可信方案，尚未做运行时验证。

在 `FUN_1801b7aa0` 入口只对“用于匹配 `+0x160` 的目标值”加偏移，保持后续分数/F0 缓冲不变：

```text
selectionTarget = originalSelectionTarget + registerOffsetCents
finalPitchTarget = originalFinalPitchTarget
```

这会迫使候选选择偏向更高或更低的录音素材，而后续重采样仍需把选中素材变换到原始目标 F0，正符合“音高不变，只换采样”的目标。

潜在挂接层级：

1. **首选：**拦截 `FUN_1801b7aa0` 的选择目标，修改面最小；
2. 拦截 `FUN_1801b7930` 或其内联等价逻辑，但该函数本体在当前版本没有直接代码调用，实际路径存在内联；
3. 临时改写声库对象的 `+0x160` 不推荐：对象可能跨 Part/渲染共享，容易产生线程竞争和缓存污染。

### E-007：最终 F0 使用独立缓冲，不需要也不应随选层目标改写

状态：静态调用链已确认，仍需宿主 A/B 验证听感和基频。

`wbhsm_getsynf0` 返回内部独立的合成 F0 数组及其长度；`wbhsm_compute_gain_and_f0` 也在另一条路径上计算、修正这些 F0 数据。`FUN_1801b7aa0` 的职责是收窄候选并把选中的 sample/unit 写入后续工作结构，不返回或改写那组最终 F0 缓冲。

因此首个动态实验应只改入口 `XMM3`，并同时观测：

- 选中 sample/unit 的标识或输出 PCM 哈希是否变化；
- `wbhsm_getsynf0` 返回的数组是否逐元素保持不变；
- 输出音频测得的稳态基频是否保持不变。

### E-008：项目已有可复用的原生挂钩承载层

状态：已确认；承载层已扩展到 ABI 10 和双 DSE 后端。

现有 `native/playback-clock/src/lib.rs` 已包含：

- 对 VSM 内部函数安装 14 字节 absolute-jump inline hook、分配 trampoline、恢复页保护和刷新指令缓存；
- 对 `DSE5::EngineImpl` vtable 做精确 RVA/目标地址验证后替换多个槽位；
- 托管侧从 `v6patch_clock.dll` 校验 ABI、取导出函数并安全降级的桥接方式。

`FUN_1801b7aa0` 的入口前 15 字节是四条完整的寄存器保存指令，不含 RIP-relative 寻址或相对分支；按当前版本可用 15 字节 patch length 建立 trampoline。实现时仍必须像现有 hook 一样同时校验 PE 时间戳、image size 和足够长的函数签名，版本不匹配时完全不写入。

### E-009：按 Part 控制的真正难点是上下文绑定，而不是选层本身

状态：已有首选绑定方案，需用线程 ID 做一次宿主验证。

全局单值偏移可以直接由选择 hook 读取，但会影响同一进程里所有传统声部。要做到类似编辑器参数的 Part/音符级控制，必须把 VSM 的 `WIVSMMidiPart`/渲染上下文与 DSE 引擎实例或内部 synthesis state 建立无锁、线程安全的映射。

仓库现有自动呼吸探针已经在 VSM 传统渲染入口 `FUN_180050eb0` 记录 `renderContext -> Part`，证明 Part 身份可以在原生渲染边界取得。它的 inline hook 在调用原函数前可读到：

```text
renderer = *(rendererHolder)
part     = *(renderer + 0x10)
context  = *(arguments)
```

首选方案不是进程全局“当前 Part”，而是让这个 VSM hook 在进入原函数前设置**线程局部渲染作用域**，保存 `{part, registerOffsetCents, nesting}`；DSE 的 `FUN_1801b7aa0` hook 在同一线程的嵌套调用中读取它，VSM 原函数返回后再用 guard 清除/恢复上一层作用域。这样不同渲染线程不会串值，异常早退也不会留下陈旧状态。

静态调用链支持“DSE 选择发生在 VSM 传统 render core 的同步调用期间”，但仍不能仅凭静态结果断言没有内部工作线程。落正式功能前应先做一个零行为探针，同时记录 VSM render core 与 DSE selector 的 `GetCurrentThreadId()`：

- 若线程 ID 一致，采用 TLS 作用域，绑定最短且最稳；
- 若线程 ID 不同，则改为 `DSE5::EngineImpl` 实例/slot 映射。`EngineImpl + 0x10` 是 slot id；DSE 内部通过 `DAT_180aab8c0[slot]` 找到每槽**外层引擎对象**，再由固定的父子关系解析实际 parser/synthesis state。具体层级见 E-018，不能把表项本身直接当成 prepare 首参。

无论哪种方案，没有有效上下文、Part 不在表中、版本签名不符时都必须按 `0 cents` 调用原函数。

### E-010：`DSE_DFT.dll` 不是 AI 引擎

状态：已确认。

安装目录中的 `DSE_DFT.dll` 导出的是 Intel MKL 风格的 FFT/LAPACK 接口，包括 `DftiComputeForward`、`DftiComputeBackward`、`DftiCommitDescriptor`、`DftiFreeDescriptor`、`LAPACKE_sgels` 和 `MKL_Free_Buffers`。`DSE.dll` 导入这些符号，因此它只是传统引擎的频域/线性代数依赖，不能作为 AI 音区控制的入口。

### E-011：AI 合成走独立的 `S5API.dll`，DSE selector hook 不会自动覆盖 AI

状态：模块边界、函数表和首批关键接口已确认；逐帧字段的精确语义仍需动态标定。

`VSM.dll` 中存在 `S5Renderer`、`S5RendererContext`、`S5VoiceParam`、`S5Section` 等类型/字符串，并动态加载 `S5API.dll`。初始化函数 `FUN_180070990` 建立一张 27 项函数表，目标是名称被散列化的 `Func_xxxxxxxx` 导出；`S5API.dll` 总计有 39 个同类导出。它没有导入或复用 DSE 的 `FUN_1801b7aa0` 选择路径。

因此传统声库方案和 AI 方案必须视为两个功能后端：

- DSE：偏移录音素材的 `pitchCents` 最近邻目标，机制明确；
- AI/S5：若能实现，预计要偏移模型的音高/音域 conditioning，再在独立 F0 或声码器阶段补偿；目前没有证据表明它存在离散的“采样层”。

`S5API.dll` 已单独导入 Ghidra 项目并完成分析，目标文件未被修改。27 项表已经逐项映射到导出地址；VSM 侧 `FUN_18006db10 -> FUN_180082be0 -> FUN_180083af0 -> FUN_180072590` 是已确认的 AI 分数/section 准备链，模型资源则由 `FUN_180072030` 调度四类缓存加载器。这个边界适合取得 Part 和原始音符语义，但真正的连续特征和合成状态位于 S5API 内。

### E-012：现有 AI 工程参数里没有显式 register/range 字段

状态：已确认公开/托管参数面；不能据此排除 S5 内部隐藏输入。

V6.13 托管层和 VPR 结构暴露的 AI 音符表达包括：

- `PitchFine`
- `PitchDriftStart` / `PitchDriftEnd`
- `PitchScalingCenter` / `PitchScalingOrigin`
- `PitchTransitionStart` / `PitchTransitionEnd`
- `AmplitudeWhole` / `AmplitudeStart` / `AmplitudeEnd`
- 前后振音深度

连续控制器包括 `S5Character`、`S5Expression`、`S5Air`。这些字段中没有与 SV “音区偏移”语义直接对应、且能保证不改变最终 F0 的公开参数。`PitchFine` 等是可听音高表达，不能直接拿来冒充 register shift；`Character` 等可能改变音色，但也不等价于独立选择/偏移发声音区。

### E-013：AI 路径没有离散采样层，但存在可调查的“条件音高 / 输出音高”双点切口

状态：结构已确认，功能语义是中等可信假设；尚不足以直接实现。

对 S5API 散列导出的首轮反编译表明，它把音符和控制器转换为连续特征及有状态的逐帧数组，不存在 DSE 那种 `候选数组 -> recorded pitch 最近邻 -> sample` 结构。几个高价值接口如下：

| 导出 | 已观察到的行为 | 当前判断 |
| --- | --- | --- |
| `Func_9cbce37f` (`0x18006beb0`) | 读取步长为 `0x88` 的音符输入；将 `note + 0x10` 乘 `0.01`、四舍五入后加 `69`，生成离散音名/键位，并复制时值、音素和其它表达字段 | `note + 0x10` 是以 A4 为零点的 cents 音高；这是改变模型音区 conditioning 的前端候选 |
| `Func_2db29822` (`0x180061330`) | 有状态地计算一帧并输出两个浮点；输出一项是平滑/扰动结果与另一项之和，另一项被单独保留；内部有随机扰动、过渡状态和二级 IIR 式滤波 | 很像音高轨迹的“总量 + 基准量”分解，但目前不能把两个输出直接命名为 F0/cents |
| `Func_e8258087` (`0x18006dec0`) | 在一个 block 内从旧状态到新状态做插值，反复调用同一帧计算器，写两组逐帧数组，并额外写一个对数域标量和有效标志 | 连续声学特征或声码器条件的高价值观测点；单位待标定 |
| `Func_6b47c126` (`0x18006a980`) | 建立大型推理/渲染状态并复制多组特征，带约 8 MiB scratch 区 | 模型输入张量交界候选，但直接挂钩风险较高 |
| `Func_92864e29` (`0x18006add0`) | 执行深层的按 block 合成处理并写输出缓冲 | 更靠后的合成/声码器候选，不适合作为第一观测点 |

`Func_9cbce37f` 的换算关系可写为：

```text
modelMidiKey = round(notePitchCents * 0.01) + 69
```

这给出了 AI 侧一个真实的“送给模型的音区/键位条件”，但**只改它并不等于音区偏移**：模型或后级很可能据此一并生成升降后的 F0，最终听感会直接移调。AI 若要达到和 DSE 相同的用户语义，预计必须采用双点控制：

```text
模型条件键位 = 原始键位 + registerOffset
最终音高轨迹 = 仍使用原始音高（或在后级抵消同量偏移）
```

因此 AI 可以顺带推进，但应独立标为实验后端。当前优先级是先用观测探针确定 `Func_2db29822`/`Func_e8258087` 哪个输出与音符 cents、`PitchFine` 和实际 F0 呈 1:1 关系，再决定补偿点；在这之前不能把其中任一浮点字段硬编码成“最终 F0”。

### E-014：DSE 6.13 实际存在两套互斥的传统素材准备后端

状态：已确认分支、调用关系和第二后端的候选评分骨架；尚未用声库类型动态标定分支含义。

解析事件 `0x7D` 时，`FUN_1801adb30` 会检查引擎对象 `+0x78` 的字节标志：

```text
engine + 0x78 == 0  -> FUN_1801a1800
engine + 0x78 != 0  -> FUN_1801b3fb0
```

两个调用点分别位于 `0x1801AE059` 和 `0x1801AE052`。现有 REG 原生实现只 Hook 后者及其 `FUN_1801b7aa0` selector。交叉引用确认 `FUN_1801a1800` 不调用 `FUN_1801b7aa0`，因此只要声库走第一分支，`prepare` 和 `selector` 计数就会永久为零；增加缓存失效、放宽音符匹配或修复 TLS 都不会让这两个 Hook 突然生效。

`FUN_1801a1800` 使用的是另一套候选序列算法：

- `FUN_1801a9e00` 整理并裁剪候选素材，直接读取候选对象的 `+0x160/+0x16C` 浮点特征；
- `FUN_18019d160` 从 `FUN_180189ca0` 取得逐帧目标特征，把第一项与候选 `+0x160` 比较并累计音高相关代价，同时计算第二特征及相邻素材的连续性代价；
- `FUN_1801ab850` 多次调用上述函数，组合当前/相邻音符代价并通过动态规划选出素材序列。

所以传统 REG 需要共享同一套参数、Part 上下文和失效框架，但至少准备两种原生后端：

1. `1B` 后端：在 `FUN_1801b7aa0` 入口偏移第一个选择目标；
2. `1A` 后端：只在候选评分期间偏移目标音高特征，不能改写后续合成/F0 使用的共享帧数据。

在动态标定 `engine + 0x78` 与实际声库家族的关系之前，不应把两个分支先验命名为 V4、V5、V6 或 AI。探针必须同时记录编辑器已知的 voice-bank type、引擎 flag 和两个准备入口计数；AI 仍应额外检查是否完全绕过 DSE 而进入 S5。

### E-015：当前运行日志还没有验证源码中的最新失效事务

状态：VSM 静态行为已确认；当前源码与已采集宿主日志不是同一代。

VSM 的 `WIVSMMidiPart.EndScoreEdit` 对应 `FUN_1800e3090`。一次成功结束的真实 score edit 会把 Part 的 `+0x4C2` 清零；该字节正是 `HasValidRenderedScore`，而 `+0x4C1` 是 `HasValidRenderedWave`。因此“临时改变首音符 Velocity、调用 `UpdateScoreEdit`、恢复 Velocity、再次更新并结束事务”在静态上具备使原生 score 失效的依据。

当前源码的渲染请求日志包含 `invalidate=...`，但已采集的 `register-shift.log` 只有：

```text
begin=True update=True end=True commit=False staged=False async=True
```

这说明运行中的补丁早于当前源码，尚不能用这份日志否定或肯定最新 Velocity 触碰方案。另外，日志里的 `async=True` 只是 `CanAsyncRendering()` 的返回值；`StartAsyncRendering()` 是 `void`，它不提供“任务已入队”或“缓存未复用”的成功值。

后续每次 REG 渲染请求都应带唯一 render epoch，并依次记录：

1. `HasValidRenderedScore/HasValidRenderedWave` 的事务前值；
2. 两次 `UpdateScoreEdit` 和 `EndScoreEdit` 的结果；
3. 事务后两个 valid flag；
4. Renderer Started/Completed 对应的同一 epoch；
5. `1A/1B` 两个 prepare 入口、匹配、应用计数的 epoch 增量；
6. 最终 PCM/F0 散列。

只有 `validScore: true -> false -> Renderer Started -> DSE Entered/Applied` 连续成立时，才算原生失效链真正闭合。

### E-016：`1A` 后端存在不改共享帧和最终 F0 的局部代价偏移切口

状态：调用约定、调用点角色和目标特征读取点已确认并实现；尚待宿主验证。

`FUN_1801adb30` 在分支前一次性准备好相同的 Win64 参数：

```text
RCX = engine/synthesis object
EDX = frame/event argument
R8  = current note record
R9  = target note record
```

随后才在 `0x1801AE04C` 检查 `engine + 0x78`，并分别调用 `FUN_1801b3fb0` 或 `FUN_1801a1800`。因此 `1A` 准备 Hook 可以复用现有 `begin + duration + float cents` 的记录解析、Part 解析以及 current/target TLS 作用域，不需要再猜一套音符布局。

旧后端的候选代价函数 `FUN_18019d160` 只被 `FUN_1801ab850` 的三个调用点使用：

| CALL 地址 | 返回地址 | 代价归属 |
| --- | --- | --- |
| `0x1801AC886` | `0x1801AC88B` | 当前音符 |
| `0x1801AC90D` | `0x1801AC912` | 当前音符的另一分段模式 |
| `0x1801AC996` | `0x1801AC99B` | 目标音符 |

前两次把第 5 个参数作为正在评分的当前音符候选，并把第 6 个参数作为相邻目标；第三次反过来评分目标音符并把当前音符作为前邻。`FUN_1801ab850` 自身的第 5/6 参数就是这两个候选容器，所以不必在热路径做完整堆栈回溯：进入 `FUN_1801ab850` 时把 `{current_candidates=param5, target_candidates=param6}` 压入 TLS，`FUN_18019d160` 再按自己的第 5 参数与二者的指针相等关系选择 shift。三个返回地址仍可作为诊断校验，但不应是唯一的业务判据。

仅改后续代价仍不够。`FUN_1801ab850` 会先统计目标帧第一/第二浮点特征的 min/max，再调用 `FUN_1801a9e00` 建候选窗口。该函数的第 7/8 参数是第一特征的 min/max，第 9/10 参数是第二特征的 min/max；第 11/12 参数是窗口扩展量。两个调用点固定传入：

```text
pitch window extension  = 75.0 cents
second-feature extension = 0.075
```

随后 `FUN_1801a9e00` 按候选 `+0x160` 与 `[pitchMin - 75, pitchMax + 75]` 取子集，再按 `+0x16C` 做第二次窗口筛选。这意味着原音高附近没有远端录音层时，单独把评分 scratch 加 `1200` 会因为目标层已被裁掉而无效。

好消息是这两个 min/max 都是按值传参，不是共享帧指针。`FUN_1801a9e00` 的第 1 参数会在当前/目标候选容器之间切换，因此可以用上一层保存的容器指针选择对应 shift，并只在调用原函数时执行：

```text
pitchMin += shift * 100
pitchMax += shift * 100
```

窗口宽度 `75.0`、第二特征范围、候选数组和数据库对象均保持原值。这样远端层可以进入评分，但不会改变共享目标帧或后续 F0。

`FUN_18019d160` 在 `0x18019D39C` 调用 `FUN_180189ca0`。后者只是从循环帧缓冲返回一个大小为 `0x2C` 的记录指针；返回后 `0x18019D3A3` 和 `0x18019D3B8` 立即读取前两个 `float`，随后把第一项的帧均值与候选素材 `+0x160` 比较。该调用在函数内只有一个静态位置，返回地址固定为 `0x18019D3A1`。

由此得到一个比“临时改写数据库对象”更安全、且覆盖前置裁剪的实验结构：

```text
FUN_1801a1800 hook
    建立 {part, current_shift, target_shift} TLS 作用域
        │
        ▼
FUN_1801ab850 hook
    保存 {current_candidates=param5, target_candidates=param6}
        │
        ├─ FUN_1801a9e00 hook
        │  按 param1 对应 current/target，偏移按值传入的 pitchMin/pitchMax
        │
        └─ FUN_18019d160 hook
           按 param5 对应 current/target，建立本次 score_shift
        │
        ▼
0x18019D39C 的 CALL-site relay
    只把评分器内部这一次 FUN_180189ca0 调用导向包装器：
    先调用未修改入口的原函数取得记录指针
    score_shift != 0 时复制完整 0x2C 到线程局部 scratch
    scratch.float0 += score_shift * 100
    float1 和其余字段保持不变，返回 scratch
```

每层 hook 返回后恢复上一层 TLS；`FUN_180189ca0` 的全局入口保持原样，只有 `0x18019D39C` 这一处调用经过 relay，其它数十个调用者完全不受影响。这样偏移同时参与“哪些录音层可以进入候选”和“这些候选如何按目标音高计分”，但原始环形帧缓冲、数据库候选、第二特征、后续重采样和 F0 路径均不被原地修改。单个 TLS scratch 可以成立，是因为 `0x18019D3A1` 返回后立即读取前两个浮点，没有保存指针；实现时仍应复制完整 `0x2C`，避免依赖只读取八字节的偶然布局。

首轮探针仍应记录 shift 前后的 `pitchMin/pitchMax`、裁剪后候选 `+0x160` 范围和最终选中素材。若偏移窗口后仍没有远端层，那是声库/音素本身没有对应素材或还有更早的语音上下文过滤，不能通过扩大数组边界或伪造候选数量解决。

### E-017：`1A/1B` 由 DSE 初始化模式 `1/2` 决定，声库代际含义必须动态标定

状态：DSE 内部数据流已确认；VSM 有可在现有 render-core hook 读取的关联候选字节，但两者的静态传递尚未闭合。

`engine + 0x78` 不是随音符变化的临时标志，而是大块 DSE synthesis state 在初始化时写入的后端模式：

| 初始化路径 | 写入 `state + 0x78` | 环形容量参数 | 事件 `0x7D` 后端 |
| --- | ---: | ---: | --- |
| `FUN_180165830` | `0` | `0x280` | `FUN_1801a1800`（1A） |
| `FUN_1801658d0` | `1` | `0x20` | `FUN_1801b3fb0`（1B） |

总初始化函数 `FUN_180165c00` 从配置的 `param_2[0x8B]` 读取一个 32 位模式值，并作如下映射：

```text
config mode 1 -> state + 0x78 = 0 -> 1A
config mode 2 -> state + 0x78 = 1 -> 1B
```

它还把相同的 `0/1` 交给 `FUN_1801aec10`，同时分别采用 `0x280/0x20` 的内部容量，因此这不是只为分支而设的偶然布尔值，而是两套处理布局的初始化选择。上游 `FUN_1801cf280` 把 `EngineImpl` 初始化配置的 `+0x18`（反编译为 `param_2[6]`）复制到本地配置的 `+0x22C`，恰好就是 `param_2[0x8B]`；`FUN_1801d0250` 则复制完整的 `0x28` 字节配置后调用它。

VSM 的传统 render core `FUN_180050eb0` 在 `0x180051407` 和 `0x180051E75` 都读取 `*(byte *)(*renderContext + 0x38)`，并明确按 `1/2` 分流。这个字节可以在已有 VSM 入口 hook 零成本记录，是与 DSE config mode 对照的高价值候选；但目前没有静态证据证明它就是传入 DSE `EngineImpl + 0x18` 的同一字段，所以日志中必须分别命名为 `vsm_mode_candidate` 和 `dse_mode`，不能先合并为一个枚举。

托管层可通过 `VoiceBank.MajorVersion` 取得声库主版本，因而第一次宿主标定不需要记录声库名、CompID、路径或工程内容。推荐每个 render epoch 记录以下低隐私矩阵：

| 字段 | 传统 Part | AI Part |
| --- | --- | --- |
| Part 分类 | `IsAi=false`、`VDMVoiceBankType.Dse` | `IsAi=true`、`VDMVoiceBankType.Dnn` |
| 声库代际 | `VoiceBank.MajorVersion` | `VoiceBank.MajorVersion`，仅作样本说明 |
| VSM | `vsm_mode_candidate`（期望为 `1/2`） | 记录传统 render core 是否完全未进入 |
| DSE | `dse_mode`、`prepare_1a_delta`、`prepare_1b_delta` | 正常预期两个 prepare 增量都为零 |
| S5 | 正常预期入口增量为零 | `s5_prepare_delta/start_delta` |

测试样本应至少包含一个 V4、一个 V5、可用时再加一个 V6 传统库，以及一个 AI 库。只有运行时矩阵重复证明某个 `MajorVersion -> mode -> backend` 映射后，代码和 UI 才能给模式加代际标签；在此之前内部只使用 `Mode1/Mode2`、`Backend1A/Backend1B`。

### E-018：当前跨线程 `STATE_PARTS` 回退少解了一层 parser 指针

状态：静态对象关系和调用首参已确认；已按 parser 解引用和 render epoch 修正，尚待宿主验证。

当前 `register_engine_part` 读取：

```text
outer = DAT_180aab8c0[EngineImpl.slot]
STATE_PARTS[outer] = part
```

但 `FUN_1801b3fb0` 和 `FUN_1801a1800` 的第一个参数不是 `outer`，而是 `outer + 0x1D570` 的事件 parser。证据链是：

- `FUN_180165c00` 在 `outer + 0x1D568` 保存新分配的 synthesis state，并调用 `FUN_18019bcb0(outer + 0x1D570, outer, synthesis)`；
- `FUN_18019bcb0` 明确写入 `parser[0] = synthesis`、`parser[1] = outer`；
- `FUN_180166c20` 和 `FUN_1801671f0` 都把 `outer + 0x1D570` 传给 `FUN_1801b1540`，后者再调用事件解析器 `FUN_1801adb30`；
- `FUN_1801adb30` 最终以这个 parser 指针调用两个 prepare 后端，并通过 `*parser + 0x78` 读取模式位。

所以旧版 `resolve_part(state)` 在 TLS 丢失时直接查 `STATE_PARTS[state]` 会 miss：表里是 outer，传入的是 parser。当前实现采用以下关系：

```text
parser   = prepare 第 1 参数
synthesis = *(parser + 0x00)
outer     = *(parser + 0x08)
mode      = *(byte *)(synthesis + 0x78)
part      = STATE_PARTS[outer]
```

当前表以 `outer + 0x1D570` 的 parser 为键，使热路径只查一次；表项仍保存 outer/parser/synthesis 并在 prepare 时逐项核对，失败即回退零偏移。模式直接读取 `synthesis + 0x78`，同时报告 slot 和线程。

生命周期也已改为 `{slot, outer, parser, synthesis, part, render_epoch}`：发布新 epoch 时先清除该 Part 的旧 state 条目，随后由新一轮 add-event 建立映射；显式移除 Part 和关闭工程会同时清表。即使旧渲染与新发布短暂重叠，旧线程也只会因为 epoch 不符回退零偏移，不会读到新参数。

### E-019：原型审计出的观测与行为缺口及其实现状态

状态：观测 ABI、parser 映射和双后端计数已修正；ordinal 与 1B 热路径优化仍保守保留。

ABI 10 已增加以下观测，并把托管状态名从 `Ready` 改为语义更准确的 `Installed`：

- 当前/最近 render epoch，以及每个 epoch 的 Part、线程、slot、outer/parser/synthesis 和模式；
- `prepare_1a/prepare_1b`、resolved、matched、applied 的独立计数；
- 1B selector 与 1A score/scratch 的独立计数及最后一次偏移；
- Renderer Started/Completed 与失效事务前后 valid flag 的同 epoch 关联。

行为侧的处理状态是：

- `RegisterNote.ordinal` 已由托管发布，但 `find_shift` 完全没有读取它；当前重复的 `{begin,end,pitch}` 只能整体回退。ordinal 只能在已经证明 DSE 记录顺序稳定后参与消歧，不能为了提高命中率盲选第一个；
- `STATE_PARTS` 已改以 parser 为热路径键，并保存/校验 slot、outer、parser、synthesis、Part 和 epoch；发布新的 Part epoch 会移除旧 state 条目，旧工作线程只会回退零偏移；
- 1A/1B 已各有独立 prepare 计数；1A 另有 prune/score/scratch/applied，1B 有 selector/applied；
- 1B 的 `selector_callsite()` 每次用 `RtlCaptureStackBackTrace` 区分 current/target，适合探针但属于合成热路径成本；1A 可以优先按候选容器指针区分角色，只把直接返回地址当校验。1B 正式版也应评估一个只读取直接返回地址的小型 ABI shim，或对已确认调用点建立更轻的作用域；
- `PART_NOTES` 仍以原生 Part 指针为第一键，但表项携带单调 render epoch；查找必须同时匹配 Part 和 epoch，已有单元测试确认旧 epoch 在替换后不再命中。

实现没有放宽音符匹配；重复 `{begin,end,pitch}` 仍安全回退。宿主验证因此可以先看 epoch/后端计数，再独立判断匹配和选样是否生效。

### E-020：托管 `HasValidRendered*` 会被仓库内其它补丁改写，不能代替原生 flag

状态：补丁交互已由当前源码确认；raw/effective 双日志已实现，尚待宿主验证。

`SegmentedPhonemeValidRenderedWavePatch` 和 `SegmentedPhonemeValidRenderedScorePatch` 会在 `ExtendedChinesePinyin` 开启且 override 文件存在时，把原始 `false` 的 getter 结果改成 `true`。这只改变托管调用者看到的返回值；VSM.dll 内部仍读取 Part 的原生状态。因此失效诊断若只记录：

```csharp
part.HasValidRenderedScore
part.HasValidRenderedWave
```

可能出现“日志仍为 true，但原生 renderer 已认为失效”的反向假象。`RuntimeObservationLog` 目前也走这些属性，不能自动解决这个差异。`StartAsyncRendering` 还带有分段音素协调器的 postfix，Renderer Started 又可能触发其扫描，因而 REG epoch 之外可能同时出现扩展拼音任务。

当前探针在 render-before、render-after、Renderer Started/Completed 边界同时记录：

- `rawValidScore/rawValidWave`：直接调用未被 Harmony 包装的 VSM 原生导出，或在精确版本保护下读取已确认的 `part + 0x4C2/+0x4C1`；
- `effectiveValidScore/effectiveValidWave`：正常托管属性结果；
- `extendedChinesePinyin`：记录功能开关；当前没有稳定、低耦合的公开 jobId，因此未伪造任务关联。

失效链的判定必须使用 raw 值；effective 值只用于解释 UI/缓存层为什么仍显示已有结果。做 REG 首次 A/B 时最好先关闭 Extended Chinese Pinyin 以减少变量，但正式实现仍要在两功能同时开启时验证，不应靠互斥设置掩盖问题。

### E-021：1A 的四个函数入口可沿用现有跳板，但帧 getter 必须改 CALL-site

状态：已按 DSE 6.13.0.1 的实际入口字节实现；尚待宿主验证。

当前原生 Hook 的 trampoline 只会原样复制被覆盖字节，不会重定位 RIP-relative 操作数、相对分支或相对调用。因此 patch length 必须落在完整指令边界，而且被复制区间内不能含任何需要重定位的指令。四个拟用函数入口均满足这个限制：

| 作用 | RVA | 最小安全 patch length | 被复制的入口指令 |
| --- | ---: | ---: | --- |
| 1A prepare | `0x1A1800` | `21` | 8 个寄存器压栈后接完整的 `lea rbp,[rsp-0x2568]` |
| current/target 候选作用域 | `0x1AB850` | `15` | `mov rax,rsp` 加 8 个完整压栈指令 |
| 候选窗口裁剪 | `0x1A9E00` | `21` | 8 个寄存器压栈后接完整的 `lea rbp,[rsp-0x98]` |
| 候选评分 | `0x19D160` | `15` | 4 条只访问 `rax/rsp` 参数 home area 的 `mov` |

这些区间都不含 RIP-relative 访存、相对控制流或落在中间的指令，因而可以使用现有的 `14-byte absolute jump + copied prologue + absolute jump back` 结构。包装器仍须严格保持实际 Win64 签名：`FUN_1801AB850` 为 8 个有效参数，`FUN_1801A9E00` 为 14 参数，`FUN_18019D160` 为 12 个被函数实际读取的参数。尤其 `FUN_1801A9E00` 的第 7–12 参数虽有四个被反编译器显示为 `undefined4`，调用点和浮点比较都表明六项应按 `f32` 转发，不能用整数寄存器类别去猜；评分函数其余未知语义参数则应按 caller 的 qword/dword/byte 宽度原样透传。

`FUN_180189CA0` 不能这样处理。它的前 16 字节中在 `+0x06` 有一条短 `JNS`，目标是原函数内部 `+0x1F`；原样复制到任意 trampoline 后，该相对跳转会落到错误位置。它同时至少有数十个静态调用点，给全局入口安装包装器也会无谓扩大热路径和兼容性风险。

评分器所需的调用却只有一个，位于 `0x18019D39C`，原始 5 字节为：

```text
E8 FF C8 FE FF    call FUN_180189CA0
```

实现只把这个相对 `CALL` 的目标改成一个位于调用点 `±2 GiB` 内的 relay；relay 再用绝对跳转进入 Rust 包装器。包装器以原始 `RCX/EDX` 调用未修改的 `FUN_180189CA0`，仅在当前 TLS `score_shift` 非零时复制并偏移 scratch，然后返回。这样返回地址仍是 `0x18019D3A1`，getter 的其它调用点没有额外开销，也不需要实现通用指令重定位器。

安装过程先完成 PE 版本、四个函数签名和该 CALL-site 字节的全量校验，再预分配四个 trampoline 与近地址 relay，最后写入补丁。5 字节 CALL-site 只在补丁初始化时写一次；运行期不反复改回机器码。若无法取得 `rel32` 可达的 relay，1A 安装位图不会完整，所有已装包装器仍强制零偏移；禁止退回共享帧原地改写。状态 ABI 分别报告四个入口和 relay 的安装位，只有五个组件都就绪才允许 1A 行为生效。

## “V4/V5 一般有几层”的更准确解释

逆向结果不支持“所有声库固定 N 层”的模型。DSE 对每个音素单元维护一个候选数组，并按录音音高元数据 `+0x160` 连续排序；数组长度是该音素自己的 `PhU + 0x150`。因此：

- 层数可随声库、音素、发音上下文而变化；
- 同一声库内不同音素也可能有不同的候选数和录音音高分布；
- 1B 的 `150 cents` 最近邻范围与 1A 在 min/max 两侧各扩展的 `75 cents` 都只是后端内部的候选筛选宽度，不表示声库只有固定几层，也不表示层间距等于这些常量；
- 对外参数应叫“选择音区偏移/源音高偏移”，不应暴露一个假定固定的层号。

若以后要显示实际层数，必须在运行时按当前音素读取候选数组统计，而不能给 V4/V5 写一个通用常数。

## 已采用的实现轮廓

### 第一阶段：先闭合失效链并标定分支

1. 每次 REG 修改分配唯一 render epoch，记录 `validScore` 的 `true -> false`、Renderer Started/Completed 和最终 PCM 身份；没有 Renderer Started 就停止分析 DSE，先修失效链。
2. 在现有 VSM render-core hook 只读记录线程 ID、engine slot、`vsm_mode_candidate`；在 DSE 分流或两个 prepare 入口记录 `dse_mode`、1A/1B 增量。
3. 用 `VoiceBank.MajorVersion` 分别跑 V4、V5、可用时的 V6 传统库；AI 样本单列 S5 计数，不要求 DSE 进入。
4. 验收标准是同一 epoch 只进入一个传统后端，且不同 Part 并行时 `Part -> slot/state -> backend` 不串联。仅有 Hook 安装成功或 `StartAsyncRendering()` 返回不报错不算通过。

这一阶段全部为零行为探针；先把“有没有重新渲染、走哪套后端、属于哪个 Part”变成可证伪事实。

### 第二阶段：分别验证两套传统选样切口

```text
WIVSMMidiPart / REG note table
        │ immutable snapshot + render epoch
        ▼
VSM render core / engine slot
        │ TLS，失配时 state 映射回退
        ▼
DSE event 0x7D
        ├─ Backend 1B: selector target += shift * 100
        └─ Backend 1A: candidate min/max 与 scoring scratch.float0 同量偏移
        ▼
原始后续重采样与最终 F0
```

1B 继续使用 `FUN_1801b3fb0 -> FUN_1801b7aa0`，记录原始/偏移目标、候选 `+0x160` 和最终素材稳定标识。1A 使用 E-016 的“候选 min/max 按值偏移 + 局部评分 scratch”双点方案，并记录裁剪前后候选音高范围。两者都做 `-1200 / 0 / +1200 cents` 离线 A/B，同时散列 `wbhsm_getsynf0` 或已标定的最终 F0 数据；必须看到选样/PCM 改变且稳态 F0 保持不变，才能从 `Applied` 升为 `Verified`。

### 第三阶段：把逐音符参数收敛为可发布语义

现有实现已经是逐音符表，因此后续重点不是再退回 Part 级滑块，而是收紧目标归属和生命周期：

1. current/target 调用点分别匹配开始帧、持续帧、浮点 cents，并加入稳定顺序/ordinal，避免相邻同音高音符串值；
2. Part 快照按 render epoch 发布和回收，不能只累积到全局 clear；线程丢失时的 slot/state 回退也必须带 epoch；
3. 连音或跨音符素材按已确认的 current/target 角色取值，无法唯一归属时回退 0，不猜最近音高；
4. REG、BVL 和原生 score edit 继续由同一历史协调器编排，撤销后以当前活动 Part 的全局刷新入口更新 UI；
5. 任一版本签名、上下文、音符匹配或后端识别失败时原样调用 trampoline，并把状态停在对应证据级别。

### AI 支线：先标定，再做双点 A/B

AI 不应直接跟随 DSE 第一版一起发布。最小调查顺序是：

1. 在 VSM 调用 S5API 的包装边界做零行为日志，只针对一个很短的 AI Part，记录 `Func_9cbce37f` 音符输入和 `Func_2db29822`、`Func_e8258087` 输出的长度、范围与散列，不记录歌词或工程路径。
2. 固定所有表达，分别输入 A3、A4、A5，判断候选逐帧字段是否每八度呈固定差值；再只改变 `PitchFine`，分清“键位条件”和“最终音高表达”。
3. 只在 `Func_9cbce37f` 的临时输入副本中偏移 `note + 0x10`，做 `-1200 / 0 / +1200 cents`，确认模型输出音色和实际 F0 各自怎样变化。禁止原地改写 VSM 共享音符数组。
4. 只有某个后级字段已被证明与最终 F0 单调且单位明确，才做第二个补偿 hook，把最终音高恢复到原曲线；同时比较 PCM、基频、音素时值和辅音稳定性。
5. 如果偏移键位只造成移调、补偿后没有稳定音色差异，或明显破坏咬字/音素时值，则判定当前 S5 模型不提供可用的独立音区维度，AI 后端不暴露该参数。

AI 首轮实验仍可复用 DSE 的 Part/TLS 上下文框架，但 hook 地址、参数语义、版本签名和功能开关必须完全分开；任何 S5 识别或补偿失败都应回到原始调用。

## 首次宿主 A/B 验证清单

使用同一歌手、同一短句、同一音高与表达，分别渲染 `-12 / 0 / +12` 半音：

1. 同一 epoch 必须先出现 `validScore: true -> false` 和 Renderer Started，再出现且只出现一个 `prepare_1a/prepare_1b` 增量；
2. V4/V5/可用时的 V6 样本分别记录 `MajorVersion / vsm_mode_candidate / dse_mode / backend`，不根据声库名人工猜分支；
3. 1B 三次的 selector 目标只相差 `±1200 cents`；1A 三次的评分 scratch 第一浮点只相差 `±1200`，原始帧记录散列不变；
4. 至少一部分有声素材的选中候选 `+0x160` 或素材标识应不同；
5. 三次最终 F0 数据长度一致，逐元素值相同或只在已定义浮点容差内变化；
6. 输出 PCM 散列应不同，但稳态基频误差应只在正常分析抖动范围内；
7. 无可替代素材的音素允许三次选择相同，这不是失败；
8. 同时渲染两个传统 Part（偏移相反）验证不串值，并核对 VSM/DSE 线程 ID、slot、state 和 epoch；
9. AI 样本应表现为 DSE 两后端计数均不增长、S5 计数增长；之后再单独执行 AI 双点 A/B。
10. 进入 1A A/B 前，四个入口和 CALL-site relay 的安装位图必须全部成立；relay 不可达或 CALL 原字节不符时应看到明确的未 Armed 状态，而不是部分 Hook 的 `Ready`。

建议先选持续元音和跨至少一个八度的旋律；爆破音/擦音的层差不一定容易从听感判断。

## 已知风险与失败回退

- 所有 RVA、入口签名和对象偏移都只对应当前 6.13 样本，版本不符必须禁用功能。
- 极端偏移会夹到边界素材，多个相邻参数值可能得到同一候选。
- 第二声学目标 `+0x16c` 仍未命名；首版必须保持原值，避免同时改变力度/动态维度。
- `vsm_mode_candidate` 与 DSE config mode 虽然都取 `1/2`，静态传递尚未闭合；动态对照前不能把两者当成同一字段。
- 若 DSE 在内部工作线程执行，TLS 不会从 VSM 调用线程自动传播，必须使用带 render epoch 的 engine slot / synthesis state 映射。
- 1A 必须让 `FUN_1801a9e00` 的 pitch min/max 与后续评分 scratch 使用同一偏移；只做其中一点会得到“候选进不来”或“候选范围变了但代价仍偏向原层”的半生效状态。
- 1A 的评分 scratch 必须通过 `0x18019D39C` 的单一 CALL-site relay 接入；现有不带指令重定位的 trampoline 不能直接复制 `FUN_180189CA0` 入口的短条件跳转。
- hook 或参数上下文不可用时应无条件原样调用 trampoline，不影响编辑器渲染。
- AI 后端没有证据表明存在采样层；在找到独立 conditioning/F0 分叉前，不应给 AI 暴露一个名义相同但实质只是移调或 Character 的参数。
- S5API 导出名经过散列，当前地址和结构只能绑定 6.13 样本；AI 探针也必须做模块版本、image size 和入口签名校验。

## 后续待定位对象

- 动态建立 `VoiceBank.MajorVersion -> vsm_mode_candidate -> dse_mode -> 1A/1B` 的实际映射。
- 运行时确认 VSM render core 和两个 DSE 后端是否同线程，并验证带 epoch 的 slot/state 回退。
- 为最终选中的 unit/sample 找一个跨调用稳定、可低成本记录的标识。
- 用当前源码重新采集带 `invalidate=` 的日志，确认 Velocity touch 事务确实使 `HasValidRenderedScore` 清零并启动新 epoch。
- 动态确认 1A 的 pitch min/max 偏移会让目标层进入评分器，并且第二特征窗口仍保持原值。
- 动态标定 S5 的 `Func_2db29822` 两个输出和 `Func_e8258087` 两组数组的单位及与实际 F0 的关系。
- 核对 AI 键位 conditioning 偏移后，模型音色是否在补偿最终 F0 后仍有可重复差异。

## 结论状态

传统 DSE 已确认不是单一 selector，而是初始化模式 `1/2` 控制的两套互斥后端。1B 的 `FUN_1801b7aa0` 直接目标偏移和 1A 的“候选 min/max 按值偏移 + 评分帧 scratch”双点切口均已实现；1A getter 通过只重定向 `0x18019D39C` 的 CALL-site relay 接入。旧日志的 `Ready + prepare=0` 现在可由 ABI 10 的安装位图、独立后端计数、parser 映射和 render epoch 进一步拆解。下一步是在宿主中用 `MajorVersion / VSM mode / DSE mode / backend / render epoch` 闭合分流和失效链，再分别进行 `-12/0/+12` PCM/F0 A/B。

AI 路径已确认由独立的 `S5API.dll` 承担，公开参数面没有 register shift。已经找到前端键位 conditioning 的明确换算点，以及两个可能承载连续音高/声学轨迹的后级导出；这使“偏移模型音域 conditioning、再保持最终 F0”成为可实验的双点方案。但它仍没有 DSE 那种离散采样层，后级字段单位也尚未动态标定，不能复用 DSE selector，更不能把现有 `PitchFine`/`Character` 简单包装成同一功能。

### 2026-08-12 部署闭环

REG 的原生 Hook 与诊断 ABI 位于 `v6patch_clock.dll`，只更新合并后的托管主程序集会留下旧 ABI，并使托管层安全降级。`scripts/deploy.ps1` 现同时部署 Release 输出中的 `VOCALOIDPatcher/native/v6patch_clock.dll`：首次覆盖普通文件前保留 `v6patch_clock.dll.bak`，随后精确复制新文件；主程序集、翻译和硬编码映射仍沿用原有链接方式。部署后必须同时校验主程序集链接目标以及原生 DLL 的 SHA-256，不能只以脚本返回成功作为生效依据。

### 2026-08-12 首次宿主失败与帧原点标定

首次 V5 宿主运行已经排除部署、ABI、Hook 安装和缓存失效问题：`install bitmap=0x7F`，每次编辑均出现 raw valid flag `true -> false`，1A 的 prepare、候选裁剪和评分计数持续增长。但 15 次 prepare 全部 `resolved` 后在音符匹配处 miss，最终 `applied=0`。现场单音符为 MIDI 68，DSE 记录的浮点 cents 为 `-100`，证明音高单位 `(noteNumber - 69) * 100` 正确；失败来自 DSE 渲染局部帧原点与托管发布帧原点之间存在固定偏移。

修正不采用放宽绝对时间容差。每个 Part 的新 render epoch 初始为“未标定”，首次只允许用在 `{pitch cents, duration frames}` 上唯一的音符作为锚点，计算 `DSE begin - published begin`；随后仍以校准后的 begin/end 各 `±2` 帧严格匹配。若锚点不唯一（例如重复的同音高、同时长音符），该次保持零偏移，直到遇到可唯一标定的记录。新 epoch 会重置标定值，并发标定用原子 compare-exchange 固定首个有效原点，避免不同 Part 或渲染代次串值。

部署时还确认，安装目录的主 DLL 是指向 Release `out/` 的符号链接；编辑器运行期间，该源文件也会被宿主占用，ILRepack 无法覆盖它。部署脚本现会在任何链接或复制动作前检查 `VOCALOID6` 进程并明确失败，避免出现主 DLL 已换链、原生 DLL 却因占用未更新的半部署状态。

第二次 V5 宿主运行确认帧原点标定生效：`matches` 随每轮 render 从 1 增长到 7，`misses` 始终为 0；1A 的 prune/score/scratch 和 `applied1A` 同步增长，单轮约有 68 个评分帧被替换。但用户仍确认最终效果失败，因此 `Applied` 不能升级为 `Verified`。ABI 11 新增 current/target 最终候选序列指纹、序列长度和实际 shift：在 `FUN_1801ab850` 返回后，对候选容器中每个已选记录的 `{sample pointer, variant}` 做 FNV-1a 聚合。下一轮 `-12/0/+12` 若指纹不变，说明当前两点偏移没有改变最终选样；若指纹变化而 PCM 不变，则继续检查后续回退/重采样是否丢弃了该序列。该指纹只写入进程内诊断状态，不记录声库名、路径、歌词或样本内容。

随后 V4 的三音符对照暴露了单锚点策略的边界：三个同音高、同时长音符让每次记录都产生三个合法 frame-offset 候选，因而安全策略全部回退，日志为 `prepare=6, matches=0, misses=6, applied=0`。修正后，未标定 Part 会跨 prepare 调用对候选 offset 集合求交，只有集合收敛为单值时才建立原点；等距三音符在第三条记录收敛。已标定 offset 和尚未收敛的候选集合会在同一 Part 发布新 render epoch 时保留，严格匹配不再成立时才清除并重新标定。这样重复音符不依赖未经证明的 ordinal 顺序，同时后续渲染能从第一枚音符开始匹配。

第三次 V4 宿主运行确认交集标定收敛：除最初 2 次冷启动 miss 外，current/target matches 持续增长到 `42/32`，`applied1A=1442`。然而 current 提交容器指纹在 shift `-12/0/+12` 下始终为 `0x08E0543D7ECD5148`，target 也始终为 `0xE032214EEF63C87B`，说明评分偏移没有改变提交记录。反汇编进一步确认 `0x1801A2D9E` 调用 `FUN_1801ab850` 时第 5 参数是栈上输出容器，成功后 `0x1801A2DE?` 立即把同一容器交给 `FUN_1801b0cf0`，因此该指纹不是误记输入。

ABI 12 在 `FUN_1801a9e00` 内原始候选表构造完成、音高窗口裁剪前的 `0x1801AA5A8` CALL-site 增加只读 relay。包装器在原排序函数执行前遍历最多 128 条 `0x68` 记录，读取每条 sample 对象的 `+0x160` 录音音高，报告候选数、min/max、FNV-1a 指纹和本次 shift，然后完全按原参数调用排序函数。这样下一轮可以区分“源池根本不覆盖偏移目标”和“源池有远端素材但当前评分/提交路径仍固定”。

ABI 12 首次 V5 宿主运行确认安装位图为 `0xFF`，三音符 render 的 prepare/match/prune/score/scratch 均持续增长。探针在当前音素的原始池中看到 2 个有效录音候选，`+0x160` 音高范围约为 `-681.92..-186.65 cents`，跨度约 495 cents；最终 current/target 提交指纹在 `-12/+10/+12` 下仍未改变。日志末尾的 `pool shift=0` 不是裁剪 hook 未进入，而是同一 render 后续无偏移音符覆盖了单槽诊断值；探针现优先保留最近一次非零 shift。

这轮还暴露出整体平移裁剪窗口的退化路径：请求 `±1200 cents` 时，只有约 495 cents 跨度的源池可能完全落在新窗口之外，DSE 随后回退到原候选，导致“后级评分 scratch 已偏移但最终选样不变”。1A 裁剪现改为原窗口与偏移窗口的并集：负偏移只向下扩展下界，正偏移只向上扩展上界，0 保持原参数。这样保留原候选作为安全基线，同时允许目标方向的录音进入后级偏移评分；第二特征窗口仍完全不变。下一次宿主 A/B 应先确认 `pool shift` 非零，再检查最终选择指纹或 PCM 是否发生变化。

窗口并集版的首次 V5 宿主运行确认两点均已闭合：诊断保留了 `pool=-12`，且候选池仍为同一组 2 条录音；后续有效 render 中 `score` 从 12 增长到 18，`scratch/applied1A` 从 0 增长到 220。首次设置的第一音符 `-12` 正好发生在重复音符帧原点尚未收敛的冷启动阶段，日志为 `matches=1/1, misses=2, scratch=0`，因此该轮没有执行评分偏移。校准收敛后真正执行的是第三音符 `+12`；其目标约为 `-200 cents`，源层约为 `-681.92/-186.65 cents`，向上偏移时最近端点仍可能是原来的 `-186.65`，提交指纹不变不能判定 hook 无效。下一次无需改代码，应在同一已校准 Part 上把第一音符先回到 0，再设为 `-12`，并补测约 `-4/+4`；负向目标更可能越过两个源层的中点，可直接验证指纹是否从高层切到低层。

随后在同一已校准三音符 Part 上连续扫描第一音符，用户确认第二音符的听感采样在 `-3` 处发生离散变化；日志中的非零 `pool shift` 与 `-3/-2/-1/9/12` 操作逐次一致。当前两条源层约为 `-681.92/-186.65 cents`，中点约 `-434.28 cents`，相对于约 `-200 cents` 的目标，负向 2–3 半音正好会越过最近层分界，因此该现象是换层的强动态证据。REG 的 1A 路径可从“Applied”提升为“听感换层已观察到”，但最终 F0/PCM 对照仍未完成，尚不能标为完全 Verified。

这次也推翻了 ABI 11 指纹的原解释：在用户明确听到层切换时，所谓 current/target 指纹仍保持不变。结合 `FUN_1801a1800` 的调用现场，`FUN_1801ab850` 第 5 栈参数是本次输出容器，第 6 参数是已有输入候选；它们不是两个音符各自的最终选择。现有字段名称只能视为兼容 ABI 的历史命名，不能再用该指纹判定实际 sample 是否变化。下一探针应放在 `FUN_1801b0cf0` 提交之后，区分持续单元与跨音符连接单元，并读取实际提交 unit/sample 的 `+0x160` 或稳定标识。

用户还观察到第一音符回零会改变第二音符采样，而调整第二音符时第三音符没有同步变化。这与传统拼接库的跨音符连接单元相符：后一音符起音可能使用“前音符→本音符”的共享素材，前一音符的层选择因而能泄漏到后一音符开头；反向是否可见取决于连接是否存在备选层和本音符自身层代价。当前证据不足以把它直接判为一位偏移，也不足以安全交换 prepare 的两个音符角色。最终产品语义若要求严格的每音符归属，应在提交后探针确认连接段边界后，再决定将 incoming transition 归给后一音符，还是保留传统引擎的自然连接行为。

ABI 13 不再只依赖最后一次 selector 调用：每次 `FUN_1801ab850` 返回后，把输出容器和输入容器的 `{sample pointer, variant}` 指纹分别混入本 render 的聚合值，并记录 scope 调用数；每次发布新 epoch 时清零。托管日志新增 `sequence=output/input/calls`。它仍不记录歌词、路径或素材内容，但能覆盖同一 render 中较早的第二音符连接段，避免最后一个音符覆盖诊断。该聚合只用于同一进程内 A/B，不可跨进程比较指针型指纹。

ABI 13 的首次定向宿主 A/B 进一步区分了“跨音符归属”和“不同素材阈值”。第一音符从 `-1` 改到 `-4` 时整轮 sequence 从 `B694.../0C77...` 变为 `B41C.../FA33...`；其中 `-1` 仍在首次帧原点冷启动，仅有 `matches=1/1, misses=2, scratch=2`，因此数值对照不能单独使用，但与用户听到的 `-3` 离散分界一致。第二音符从 `-2` 改到 `-5` 时，两个完整 render 的聚合值严格相同，均为 `B1A4975CFA5380B9/E7EE80B56328B4AB/3`，而 `scratch/applied1A` 从 672 增长到 1122；这证明偏移进入了评分，但该范围没有改变任何提交的 sample/unit。更早同一进程中，第二音符从 `-10` 到 `+5` 时输入候选指纹曾从 `9191...` 切到 `E9A3...`，所以第二音符不是失效，而是其连接素材换层阈值不同于第一段。当前没有证据支持整体错后一位；传统连接单元允许前一音符影响后一音符起音，且每个音素/连接的离散分界可以不同。

随后执行了真正隔离的第二音符测试：第一、第三音符均回到 0，仅第二音符依次为 `-2/-3/-4/-6/-12`。五轮完整 render 的聚合序列始终严格等于 `71AFCE021BE7A90C/77D143B7499EE240/3`，但 `scratch/applied1A` 从 216 持续增长到 1088。结合此前第一音符在 `-3` 处改变第二音符听感采样，这已足以推翻“只是不同阈值”的解释：当前输入连接容器使用了 prepare 的前一音符 shift，导致 incoming transition 归属错后一位；第二音符自己的 shift 虽进入其它评分帧，却没有控制其起音连接。

归属修正只改变 1A 的输入连接角色，不交换整个 current/target：selector 输出容器继续使用当前（时间上较后）音符的 shift；已有输入容器代表进入当前音符的连接，也改用同一 current shift。这样每个音符同时控制自己的 incoming transition 与持续段，前一音符不再把 register 泄漏到后一音符起音。ABI 字段名称为兼容既有布局保留，但 `LastTargetShift` 现在报告输入连接实际使用的 current shift。下一轮应确认第一音符 `-3` 不再改变第二音符，而隔离扫描第二音符时 sequence 出现离散分界。

第一次归属修正版在宿主中表现为第一、第二音符可换层，但第三音符怎么调整都听不到变化。ABI 13 证明第三音符的 selector 序列其实在 `-4` 处从 `22AFFE.../B687B6...` 切换为 `E8EBE9.../82FB3D...`，且直到 `-11` 保持新序列；这说明被改变的是 `第三音符→静音` 的 terminal/release 单元，而不是第三音符有声主体。结合 `FUN_1801a1800` 的第二个 `0x1801A4515` selector 调用及其后续双 `FUN_1801b0cf0`/`FUN_18019c080` 消费路径，最终确认 DSE prepare 的 `current` 是时间上较早的一侧，`target` 才是进入的后一音符。前一版把两个容器都归 current，方向仍然反了。

最终归属改为 selector 的输出与输入容器都采用 target shift：`静音→第一` 归第一音符，`第一→第二` 归第二音符，`第二→第三` 归第三音符；`第三→静音` 因 target 没有 REG 而保持原始 release。ABI 字段名继续兼容保留，但两个 selection shift 均报告实际使用的 target shift。宿主验收必须同时覆盖首音、中间音和末音，确认末音有声主体换层且前一音符不再串改后一音符。

上述 target 方案随后被宿主直接否决：行为退回到“第一音符控制第二音符，第三音符无论如何都不动”。因此该方案不得保留。代码已回退到上一版能让第一、第二音符正常工作的 current 归属；末音问题不再靠整体交换角色处理。

ABI 14 为每轮前三次 `FUN_1801ab850` 调用增加有序 scope 轨迹，每项记录 `{current shift, target shift, output signature, input signature}`，日志格式为 `scopes=current,target:output/input;...`。这能把普通连接调用与 `0x1801A4515` 末音专用调用分开，确定第三音符有声主体在哪个 scope 被选择，再只修 terminal 分支。轨迹只含数值和进程内指纹，不含歌词、路径或素材内容。

ABI 14 的三音符宿主轨迹已经固定调用顺序。只调整第三音符时，前三个 scope 分别为 `0,0 / 0,0 / third,0`；同时调整第二、第三时则为 `0,0 / second,0 / third,second`；三音都非零时为 `first,0 / second,first / third,second`。因此 `current` 确实对应本音符，`target` 对应前一音符，回退到 current 归属是正确的，整体 target 方案不应再尝试。

第三音符在 current 归属下也确实发生了 selector 改选：`7/1` 时 scope 2 保持 `65BC.../3563...`，到 `-6` 时切换为 `690D.../E802...`，`-4/-9/-12` 均保持后一组；scope 0/1 完全不变。用户仍听不到第三音符采样变化，说明差异发生在末音专用 scope 的已选记录，却没有形成可听的有声主体变化。下一步先做无代码对照：在原第三音符后追加 REG=0 的第四音符，使原第三音符变成普通中间音。若此时可换层，问题严格限定在 terminal selector 之后的 `FUN_1801b0cf0 → FUN_18019c080 → FUN_1801b27c0` 消费链；若仍不可换，则转查该音素候选与后级声学处理，不修改尾音分支。

四音符对照推翻了“只坏在 terminal selector”的假设。添加第四音符后，第三音符固定为添加当时的低层，第四音符固定为高层；两者的 REG 后续怎么调整都不能改变听感。但 ABI 14 的 scope 和整轮 sequence 聚合都随第三、第四音符跨过负向分界而变化，`rawValid` 也在每轮从 true 变为 false。这说明 DSE selector 的确重新执行并改选，失效却没有传播到最终使用的后段合成单元。

托管重渲染路径随后发现一个与症状严格一致的不对称：`ForceNativeRender` 无论哪个音符发生 REG 变化，都只临时修改并恢复 `part.GetNote(0).NoteVelocity`。`UpdateScoreEdit(false, 0, duration)` 的整段范围不足以证明每个音符拥有的内部合成单元都已变脏；第一音符字段触碰可能只更新前段依赖，后续单元仍可在 selector 执行后复用旧结果。修正为在同一个 score-edit 事务中临时触碰 Part 内每一枚音符的 velocity，统一发送一次全段更新，恢复全部原值后再发送一次更新并提交。日志新增 `invalidatedNotes`，必须等于 Part 的实际音符数；任何音符缺失、临时值写入失败或恢复失败都会取消 score edit，不允许把临时 velocity 留进工程。该方案不改变 REG 的 current/target 归属，只验证并补齐 VSM 的逐音符合成缓存失效。

全音符失效版已在同一 V5 四音符 Part 中通过宿主验证，用户确认四枚音符均可独立控制采样层。日志中每轮均为 `begin=True, invalidate=True, invalidatedNotes=4, update=True, end=True, commit=True`，随后 raw/effective score 与 wave 有效位全部变为 false；每轮 render-complete 的 prepare 增量为 4，sequence 调用数也为 4。跨层操作时聚合指纹及对应有序 scope 指纹发生变化。由此可确认此前“后段音符锁死”来自逐音符合成缓存未完整失效，不是 current/target 角色错误，也不是 terminal selector 输出未被消费。传统 DSE 的 REG 主路径现达到 V5 多音符宿主内听感验证通过；仍需另行覆盖 V4、撤销/重做、保存重开和长 Part 性能，不把本次结论外推为这些路径已经验收。

随后撤销测试发现自定义时间线仍有独立故障。REG 从第二音符 `-4` 改到 `3` 时日志正确记录 `history push before=-4, after=3`；执行撤销后出现 `history apply` 并重新渲染，但没有对应的 `history snapshot`，且 render-complete 的有序 scope 仍报告第二音符为 `3`。因此这不是蓝柱单独漏刷，也不是 DSE 缓存复用：外部历史条目的 `AfterApply` 已执行，真正的 `ApplyBefore` 却没有把 before 快照写回 REG 值表。UI 与 DSE 随后都继续读取 `3`，行为彼此一致。修复应集中在 `CustomParameterHistoryCoordinator / BreathVolumeService.HandleHistory` 的外部条目回放边界：补充 undo/redo 方向、条目类型及回放前后值日志，并将外部 action 从 `BreathVolumeService.Sync` 锁内移出后再执行；在确认 `ApplyBefore/ApplyAfter` 实际落值前，不再把问题归因于面板刷新。

历史回放现改成两阶段：在 `BreathVolumeService.Sync` 内只移动时间线条目并选择 undo/redo action，释放锁后才执行外部参数回放，成功后再调用 `AfterApply` 刷新和重渲染。REG 快照日志记录 `direction`、`expected`、`actual` 和逐项校验结果；校验失败会抛出并把时间线条目原样移回来源栈，同时拦截本次原生 Undo/Redo，避免误撤销下面一条不相关的原生编辑。若只有刷新/渲染失败，已经落值的历史位置保持不变并安全记录错误。该修改仍需宿主确认蓝柱、scope 和声音在 undo/redo 时共同恢复。

两阶段版复测仍失败，但新版日志在点击撤销后既没有 `history snapshot`，也没有 `history apply`；运行观察同样没有进入 `WIVSMSequence.Undo`。编辑器的 `EditUndoCommand.ExecuteBody` 会在 `OverrideMouseCursor.IsWaiting` 时直接返回，而 REG 的重渲染及后续扩展拼音任务会维持该等待状态；结果是 `CanUndo` 已因补丁自有时间线返回 true，命令入口却在到达底层 Undo 前被静默丢弃。现为 `EditUndoCommand/EditRedoCommand.ExecuteBody` 增加最外层 Prefix：仅当对应栈顶是 external REG 或 BVL 条目时，抢在等待状态判断之前直接回放并拦截原命令；栈顶为空或是 native marker 时完全放行。底层 `WIVSMSequence.Undo/Redo` 补丁继续覆盖非 UI 调用。诊断新增 sequence handle、方向、栈深、栈顶类型和 handled 状态，用于区分命令未进入、补丁自有回放和原生历史放行。

命令边界版复测证明 Prefix 已进入且命中了正确 sequence，栈顶为 `external`、`handled=True`；但连续点击后 source 深度保持不变、destination 始终为 0，并且仍没有 snapshot/apply 日志。这正是 `HandleHistory` 外部 action 抛异常后执行 `RollBackHistoryMove` 的状态，不再是等待状态或错误栈顶。诊断现补充 snapshot 入口、逐项写入异常、回放异常类型与 timeline rollback 深度；快照实际值改用显式循环构造，减少 LINQ 枚举阶段隐藏异常。下一轮应先取得具体异常，再决定修 note identity 还是委托生命周期，不能再把相同失败归因于命令路由。

异常版复测记录到 `NullReferenceException`，同时 timeline 从 destination 正确移回 source，因此安全回滚有效；但 `history snapshot entered` 仍未出现，异常位于外部 action 调用边界或快照方法第一条格式化表达式。诊断改为先写入不解引用 snapshot 的入口标记，再显式 `ThrowIfNull`，随后才格式化内容；回放失败改为记录完整异常和托管堆栈。该日志只包含补丁方法、数值和匿名化原生句柄，不新增歌词、路径或工程内容。

完整堆栈进一步确认空引用就在 `BreathVolumeService.HandleHistory` 的外部 action 调用处，`ApplySnapshot` 的无解引用入口标记仍未出现；因此裸 `Action` 没有成功进入目标方法，与 note handle、快照字典及蓝柱刷新无关。外部时间线现不再保存三个独立委托，改为保存实现 `ICustomParameterHistoryEdit` 的明确对象。`RegisterShiftHistoryEdit` 自身持有 sequence、part、before 和 after，并通过实例方法实现 undo、redo 与落值后的全局刷新/重渲染。当前仓库只有 REG 使用该外部入口，故无需兼容其它调用方；BVL 自有 `ValueEdit` 路径保持不变。

对象式条目复测把异常明确为 `The custom parameter history entry is missing`：命令入口检查同一栈顶时 `External != null`，进入 `HandleHistory` 后局部 `externalEdit` 却没有赋值。最终定位到 C# 悬空 `else`：外层 `if (entry.Edit is { } edit)` 没有大括号，内部又有 `foreach` 和 `if (IsActiveNoteKeyCore)`，源码中的 `else externalEdit = entry.External` 实际绑定到了最内层 active-key 判断。对于 `Edit == null` 的 REG 条目，整个外层语句直接跳过，外部对象从未赋给局部变量。现为外层 BVL/External 两个分支补齐明确大括号。这个语法绑定错误同时解释了早期 `AfterApply` 会执行、真正 before snapshot 却从不执行的全部现场证据；对象式条目继续保留，以维持显式类型和可诊断性。

悬空 `else` 修正版已通过宿主撤销验证，用户确认问题修复。日志显示 UI 命令命中 `top=external, handled=True`，随后依次出现 `history snapshot entered direction=undo null=False`、四项 `expected/actual` 完全一致且 `matches=True`、`history apply`，并再次以 `invalidatedNotes=4` 完整触发渲染。由此撤销链已闭合为“时间线移动 → before 快照落值 → 蓝柱全局刷新 → 原生表发布 → 全 Part 缓存失效 → DSE 重渲染”。仍应单独验证 redo、多步交错原生编辑以及保存后历史重置，不从本次单步 undo 验收自动外推。

## 附录：VSM 6.13 的 S5API 函数表

`FUN_180070990` 写入的 slot 0 是 `S5API.dll` 模块句柄，slot 1–27 如下。名称和地址只用于当前样本的后续定位，不代表已经知道全部接口语义。

```text
 1  Func_4cd84bb7  0x180066760    15  Func_47a12a73  0x1800631a0
 2  Func_7b0b2f07  0x18006acf0    16  Func_2c6cd280  0x1800612d0
 3  Func_ab21a6d2  0x18006c740    17  Func_34c88d60  0x180061a20
 4  Func_9cbce37f  0x18006beb0    18  Func_b8d5dd29  0x18006cb40
 5  Func_cfc85a30  0x18006d0e0    19  Func_e7f8fafc  0x18006da20
 6  Func_5fd62874  0x180067b20    20  Func_b2d96da8  0x18006c750
 7  Func_68617beb  0x18006a870    21  Func_133d38ab  0x180060770
 8  Func_95d13855  0x18006be70    22  Func_6b47c126  0x18006a980
 9  Func_efe389ac  0x18006e240    23  Func_0425ac3f  0x1800606c0
10  Func_417eec3e  0x180061b10    24  Func_a5e8ddbb  0x18006c6a0
11  Func_236a1ecf  0x180060ef0    25  Func_92864e29  0x18006add0
12  Func_95031259  0x18006be30    26  Func_0ecc5890  0x180060720
13  Func_52f85f15  0x180067b10    27  Func_2c8b9800  0x180061300
14  Func_2db29822  0x180061330
```
