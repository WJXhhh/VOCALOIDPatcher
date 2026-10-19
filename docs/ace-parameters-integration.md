# ACE 仿生参数区接入说明

记录把四个仿 ACE Studio 的声学参数（力度 Energy、气声 Air、张力 Tension、假声 Falsetto）接入 VOCALOID 6 参数区的实现方式、宿主验证入口和维护约束。

理论背景见 [ace-studio-four-parameters-theory.md](ace-studio-four-parameters-theory.md)。本实现只覆盖参数区与发声映射，不包含音频自动分析。

## 1. 架构结论

不使用 BVL/REG 那样的独立覆盖层，而是让四个参数**走原生 ParameterView 管线**：

1. 用四个越界 `ControlParameterTypeEnum` 值（4CC：`AEN`/`AAR`/`AAT`/`AAF`）作为参数区条目。
2. 通过 Harmony 前缀覆盖 `MusicalEditorViewModel.CtrlParamTypeToVSMControllerType`，把自定义参数类型映射到**原生参数区不暴露的 VSMControllerType 载体**（`DynamicsHmm`、`PitchBendHmm`、`WeightHmm`、`Expression`）。宿主探针确认这些类型可插入/读回，但 HMM 三个载体不会被原生 `Project/sequence.json` 保存，因此曲线持久化由自定义 ZIP 条目负责（见 §3.2）。
3. 补丁 `GetMinControllerValue`/`GetMaxControllerValue`/`GetDefaultControllerValue` 为自定义类型返回 0/100/50。
4. 其余显示、命中、Arrow/Pencil/Line/Move/框选、双击输入、Undo/Redo、复制/粘贴数据都由原生代码完成，因为载体就是普通 VSM 控制器。
5. 编辑提交时（`WIVSMSequence.Commit` 前缀）由映射服务把载体曲线按参数折算写入真正发声的原生控制器：力度→`Dynamics`、气声→`Breathiness`（可切 `Air`）、张力→`Brightness`、假声→`Character`（实验性近似）。

这样做的关键依据：原生 `ParameterView` 对“非 Velocity/Mouth 类型”统一走 breakpoint 曲线，读取控制器 / 范围 / 默认值全部经过上述几个虚拟方法；只要映射到合法 VSM 类型并补范围，原生路径即可整体复用。

## 2. 参数定义

| 参数 | 参数类型 ID | 短名 | 值域/默认 | 载体（实现默认值） | 发声目标 | 映射 |
| --- | --- | --- | --- | --- | --- | --- |
| 力度 Energy | `0x41454E` | `ACE-E` | 0..100 / 50 | `WeightHmm` | `Dynamics` | 中性→64；0→0；100→127 |
| 气声 Air | `0x414152` | `ACE-A` | 0..100 / 50 | `PitchBendHmm` | `Breathiness` | 中性→0；0→0；100→127 |
| 张力 Tension | `0x414154` | `ACE-T` | 0..100 / 50 | `DynamicsHmm` | `Brightness` | 中性→64；0→0；100→127 |
| 假声 Falsetto | `0x414146` | `ACE-F` | 0..100 / 50 | `Expression` | `Character` | 中性→0；0→-64；100→63 |

映射采用“两侧独立缩放”：曲线上半段线性走到目标最大值，下半段线性走到最小值，保证 0/50/100 分别对应目标 min/default/max。公式在 `AceParameters/AceParameterMapping.cs`（纯逻辑，可离线测试）。

宿主探针结论（2026-10-19）：合法载体都能原生插入/读回并在参数区画出 breakpoint；伪造的 `(VSMControllerType)15/19/20` 被原生以 `InvalidControllerType` 拒绝，已排除；`S5Character` 只能存 ≤63 且探针显示插入其控制器会改变渲染 PCM，因此不使用；HMM 三个载体不进入原生 `sequence.json`，必须走自定义归档。当前四个载体即上表默认值。

## 3. 介入时机与排序

### 3.1 参数区条目顺序

- 传统 MIDI 轨：原生 14 项 → `ACE 力度, ACE 气声, ACE 张力, ACE 假声` → `BVL` → `REG`。
- AI 轨：原生 6 项 → `BVL` → `REG`；ACE 参数不出现。
- 同步点：`ParameterHeaderControl..ctor` 后置（早于 `OnLoaded` 恢复上次选择）、`UpdateControlParameterComboBox` 后置（轨道切换/初始化）、设置开关刷新。同步是幂等的：先移除全部自定义类型，再按 registry 顺序和开关状态追加，并赋新的 List 给 `ControlParameterTypes`。

### 3.2 启动与持久化

- 参数类型选择沿用原生 `UserSettings.ControlParameterType` 持久化。若上次停在 ACE 条目，`AceParameterComboInitPatch` 在初始化时自行同步列表并跳过后置里 `SelectedIndex=(int)自定义值` 的越界写法。
- 编辑器启动时必须先在 `ParameterHeaderControl` 构造后置里完成列表注册，再让 `OnLoaded` 读取选择。
- 工程持久化：`.vpr` 内新增 `VOCALOIDPatcher/ace-parameters.json`，保存时由 `Sequence.SaveSequence` 后置写入四条载体曲线；加载时在 `Sequence.LoadProjectSequenceFile`/`Sequence.Load` 前读取、后置应用。
- 加载应用时以归档为唯一来源：先清空各 Part 上四个 ACE 载体类型控制器，再按 `track/part/absPosTick` 定位 Part 并重建曲线；随后 `Commit(false)` 作为无历史基线，保证第一次用户 Undo 不会删除刚恢复的曲线。
- 归档带版本号、条目/点数上限与坐标校验；读取失败只记录日志并降级为“无归档”，不影响工程打开。

### 3.3 编辑、撤销与渲染

- 原生 Behavior 在 `Transaction` 内写载体控制器；`Transaction.Dispose` → `WIVSMSequence.Commit(true)`。
- `AceParameterCommitMappingPatch` 在 `Commit` 前缀执行，**不依赖**原生 update observer（观察者通知在 Commit 体内才到达，早先版本因此出现"第一次编辑不发声、需要先划一次预热"的不稳定行为）。实现改为：维护每个 `(Part, 参数)` 的载体曲线快照；Commit 前缀重新计算快照并比较，只对发生变化的 `(Part, 参数)` 重写目标控制器。首次提交（新工程或未覆盖的加载路径）先对已有曲线补一次映射，再建立快照基线。
- 派生写入发生在同一次 Commit 内，因此 Undo/Redo 同时恢复载体曲线与目标控制器；Undo/Redo 后重新取快照基线。
- 结果与现有目标曲线相同时不写，避免无意义历史；映射关闭时只更新快照、不写目标控制器。
- 设置窗口里的"按 ACE 曲线重建原生映射"按钮在切换映射目标或外部修改后主动重算（作为一条可撤销事务提交）。

### 3.4 与 BVL/REG、延迟刷新共存

- 共享的参数头补丁从 `BreathVolumePatch.cs` 抽到 `CustomParameterPatch.cs`，由 `AceParameterRegistry` 统一枚举 BVL/REG/ACE，避免三套硬编码白名单。
- ACE 参数不是覆盖层类型：`BreathVolumeUi.IsCustomPanelActive` 对 ACE 返回 false，BVL 覆盖层在切到 ACE 时隐藏，原生参数区接管。
- `ParameterViewDeferPatch` 把 ACE 当原生类型处理（不加入 BVL 排除名单）；如宿主验证发现延迟刷新与载体映射有冲突，再按 BVL 方式排除并同步刷新。

## 4. 原生批量命令兼容

原生命令里有一批 `Enum.GetValues(typeof(ControlParameterTypeEnum))` 循环，不会包含越界自定义 ID。补丁对这些入口补充了 ACE 处理：

- `HasSelectedBreakPoint()`：让 Copy/Cut/Delete 命令可用。
- `RemoveSelectedParameter()`：删除选中 ACE 载体控制器。
- `CopySelectedParameter(clipboard)`：把选中载体控制器推入剪贴板。
- `WIVSMClipboard.HasCopyMidiController()`：HMM 载体不被原生跳过，保证 Paste 命令可用。
- `PasteParameter()`：**仅支持纯参数剪贴板**（`NumNote == 0`）的 ACE 曲线粘贴；音符+参数的混合粘贴目前只由原生处理音符，ACE 曲线不会随粘贴复制，属已知限制。
- `Duplicate()`：跟随选中音符偏移复制选中 ACE 载体控制器。
- `ResetControlParametersToDefault()`：原生会把第一个选中控制器设成载体的原生默认值，后置修正为 50。

## 5. 已知限制

1. 传统 MIDI 轨专用；AI 轨不显示 ACE 条目（AI 侧应映射 `S5Air`/`S5Character`/`Expression`，需要单独 A/B）。
2. 假声没有连续的 VOCALOID 原生参数，`Character` 只是实验性代理；EVEC `#F`/REG 阈值方案未实现。
3. 混合剪贴板（音符+参数）粘贴不复制 ACE 曲线；纯参数复制粘贴可用。
4. `PianorollViewNoteMoveBehavior` 移动音符时附带的参数曲线移动仍只遍历原生枚举，ACE 曲线不会一起移动；后续可补。
5. ACE 曲线在映射启用时"拥有"对应目标控制器；用户随后直接修改目标原生参数，会在下一次 ACE 编辑时被整 Part 覆盖。
6. 没有音频分析/自动 baseline；ACEP 导入导出的双向打通列为后续。
7. 行为验证依赖宿主；离线测试覆盖纯映射数学、归档读写与版本校验，不覆盖 WPF/VSM 交互。
8. 旧构建保存的工程没有 `ace-parameters.json`：其中 Expression 载体（假声）可能以原生控制器残留，HMM 三个载体（力度/气声/张力）从未被保存。首次用本构建打开旧工程时后三者仍是平线，需要补画一次并重新保存。
9. 映射目标的既有原生控制器在首次 ACE 编辑时会被整 Part 重算；这是有意的所有权策略。

## 6. 宿主探针与验收

- 探针入口：`VOCALOID_ACE_PROBE=1` 环境变量；`AceParameterHostProbe.Queue()` 在编辑器启动后运行。要求新建、未保存、空工程；结果写入 `%TEMP%\VOCALOIDPatcher\AceParameterProbe\<guid>\result.json`。
- 每个候选载体记录：原生插入/读回、原生 ParameterView 生成 breakpoint、保存 `.vpr` 后 `sequence.json` 与 `VOCALOIDPatcher/ace-parameters.json` 的内容。
- 探针所有 JSON 写入都有兜底，不再序列化原生包装对象（此前曾因 `WIVSMNote.CppObjPtr` 不可序列化导致 `async void` 未处理异常并终止编辑器；该问题只存在于探针，产品路径不受影响）。
- 功能验收矩阵：参数条目顺序/切换、四种编辑工具、双击输入、框选/移动、撤销重做、复制粘贴（纯参数）、Duplicate、保存重开、缩放滚动、播放中编辑、BVL/REG 回归、设置开关、无补丁编辑器打开工程。
- 映射听感验收：逐参数编辑后渲染 PCM 应变化且方向符合公式；Undo 恢复；关闭映射后编辑不再改声。
- **2026-10-19 人工宿主验收通过（5/5）**：四参数条目出现且可正常编辑；保存关闭重开后控制点恢复（不再平线）；重新打开后第一次编辑立即发声（不再需要预热操作）；加载后第一次 Undo 不会删除恢复的曲线；打开工程不显示为已修改。

## 7. 维护注意

- 参考反编译源码是 6.13.1.1（文档中常见 6.13.0.1）；所有反射/私有字段访问保持 `AccessTools` 判空，失败时记录并让 `AceParameters` 功能自动禁用（`PatchRuntimeGuard`）。
- 调整载体类型时必须重新跑宿主探针；不要假设 HMM/Expression 在传统音轨渲染中永远中性。
- 映射目标常量与 `WIVSMMidiController.GetMinValue/GetMaxValue/GetDefaultValue` 保持一致；改映射公式后更新 `AceParameterOfflineTests`。
- `AceParameterMappingService` 依赖 `WIVSMUpdateObserver`；若某版本观察者不可用，会退化为每次 Commit 全量扫描，正确但更慢。
- 修改四个参数的显示名/短名/设置项时必须同步四份 `translations/*.xml` 并跑 `ResourceTranslationComparer`。
