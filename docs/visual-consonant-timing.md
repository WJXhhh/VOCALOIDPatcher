# 采样声库辅音时长的直观显示与调节

2026-10-17：已扩展为逐音素全部边界控制，并按用户要求完成配套部署，尚待宿主实测。最新实现和验收范围见 [逐音素 timing](full-phoneme-timing.md)；以下保留此前版本记录。

2026-10-16 更新。Emotion 模式复用 AI Timing 的原生边界线和音素标签，**已接入局部时值查询驱动的拖动，完成部署并启动 Editor；用户实测反馈“已实测，还可以”。** 查询不合成音频，采样引擎无法实现的鼠标位置吸附到已验证的可达边界。[本次交付记录](intuitive-consonant-timing-deployment.md)区分构建、部署、启动与用户确认的验证范围。

## 当前行为

- 边界取自已经渲染的 `GetPhonemePositions()`，经 `GetAbsPositionFromNoteBaseTick()` 转为歌曲绝对坐标。
- 当前 Part 的辅音起点显示为亮线，内部边界为只读参考。控件池常驻，刷新合并，按视口筛选。
- 拖动目标位置按实际歌曲 tick 换算；增量反查 VEL，每个 Dispatcher 节拍最多查询一个缺失候选。忙锁立即跳过，后续节拍只处理最新目标，预览只显示已经查询过的真实候选边界及毫秒值。
- 松手将最后显示的候选 VEL 通过一条原生 `Transaction` 提交，正常渲染仍由编辑器负责。预测边界暂留至结果返回，随后核对并恢复真实边界显示。
- Ctrl+单击或双击恢复默认 VEL 64。Escape、丢失捕获、切换上下文取消候选；普通按下再松开不产生修改。
- 源音符的读取和查询都在 Dispatcher 上执行。提交、撤销、重做和关闭使旧手势及缓存失效；私有 metadata sequence 注册为内部 sequence，避免触发其它补丁的派生渲染。应用销毁 native manager 前释放查询会话。
- 设置“直观辅音时长”默认开启，关闭恢复原生显示。AI Timing 及其它 Emotion 表情操作保持原路径。

## 不可突破的性能边界

运行时的辅音覆盖层不得复制整个工程、调用 `StartAsyncRendering` 或 `Render` 来试探 VEL，不得扫描 128 档，也不得二分触发多次整 Part 合成。当前增量搜索只调用有限上下文的无音频时值规划；松手只进行一次原生正常提交。

全量音频校准类和旧音频二分求解已移到 `voicebank/tools/evec_render_harness/`，不编译进编辑器补丁。当前产品中的 `ConsonantTimingDragSearch` 是逐节拍消费无音频候选的状态机，不调用旧音频工具。

## 已否决的两条路径

第一条是将原版“鼠标移动量 → VEL”的灵敏度当作“VEL → 真实时长”。它会造成松手位置与渲染结果不一致。控制范围里的 30–320 ms 不是任意声库的实际时长映射，不能用于声学预览。

第二条是用隔离副本实际渲染后反查最近 VEL。其[140 次回放对照](evidence/consonant-timing-engine-validation.json)说明这些测试样例的结果一致，但每个新档位仍会渲染目标 Part，单目标最多十次。它还会先复制整个 sequence 再裁掉其它 Part，且缓存只活在一次手势内。去抖、异步、互斥与缓存没有解决这个主要成本，短片段约一秒的结果不能推广到长 Part。此方案已撤出产品。

传统采样轨的 `SetEditedPhonemePosition` 经既有离线探针确认返回 `InvalidTrackType`，不能直接套用 AI 的独立边界写入。

## 快速时值后端的下一步验收

首选沿采样引擎的时长规划/score 准备阶段取得边界，验证能否不进入波形合成。需要局部计算时，范围必须由引擎实际依赖的上下文确定，不能凭“前后各一颗音符”裁切后就宣称等价。

托管层存在 `WIVSMMidiPart.RenderPartiallyAsync`，调用方把 `VEAudioEngine.PartialRendererCallback()` 传入，用于局部试听。当前只确认这个接口是音频输出路径；尚未确认它是否仍准备整 Part 的时值、是否发布可查询的音素边界，不能据名称当作时值专用接口使用。

2026-10-15 的[局部时值研究](local-consonant-timing-feasibility.md)进一步验证：直接调用 DSE 事件规划，33 组短上下文的有效调度与正常渲染一致，候选 PCM 调用与输出采样均为 0。核心规划及读取中位数 1.15 ms，但当前 VSM 查询包络中位数 73.41 ms；独立输入准备、通用上下文和最终 tick 边界转换仍未完成，尚未接回拖动。局部 `Commit` / `UpdateScoreEdit` 本身只保留旧边界；`RenderPartiallyAsync` 的准备链仍遍历 Part 音符和控制器。

2026-10-16 的[独立后端](standalone-local-timing-backend.md)已补齐输入准备、窗口扩张与帧相位保留、原生音素匹配及最终 tick 转换。142 组完整边界与正常渲染一致，512 个 VEL 起点与独立参考一致，候选 PCM 为 0；最多 33 音符的窗口不扫描长空尾。缓存、长工程及连续查询测量见报告。当前仍未接入拖动；窗口稳定规则的通用性、与宿主正常渲染共存、变化通知及请求调度需要继续验证。

后续[并发验证](local-timing-concurrency.md)完成同进程正常原生渲染与局部查询对照，包含正在渲染的同一 Part、不同声库、取消、关闭和重建；PCM 与完整边界均相等。当前会话常驻，只重置规划状态，万音符样例查询中位数约 0.63 ms。不限频请求仍影响正常渲染，60 Hz 节奏试验已记录；真实 Editor/WPF 生命周期及最新请求调度仍未接入或验收。

启用新后端前至少需要：

1. 与正常渲染的边界一致，覆盖连音、多辅音、非零 Part 起点、tempo 变化及实际表达配置。
2. 请求不生成完整 Part PCM，不先复制整个工程；无关音符增加或 Part 尾部拉长不应放大拖动成本。
3. 相同上下文缓存跨手势复用，键包含声库、相关音符/音素/表达、时序及 tempo 等实际依赖；上下文变化必须失效。
4. 同时最多一个待处理请求，旧请求可取消；拖动期间不修改工程，松手一次提交。
5. 在长 Part 中测量等待时间、内存和任务数量；后端不可用时不回退到全量渲染。

## 实现与验证

### 2026-10-16 显示与手势修正

用户报告 Emotion 音符内部的 `l`、`a` 等音素文字只在松手瞬间可见。覆盖层原先隐藏原生 `UIEmotionPhoneme`，而替代标签排列在音符上方；本次将常驻的替代标签放回各真实音素区间的中央，使用音符行高，并在候选边界变化时同步移动。没有保留上下两套重复标签，也不按音符长度平均切分。

用户同时报告提示里的毫秒和 VEL 拖动时不变。本次在手势开始时立即显示经过查询的候选值；仅在钢琴窗自身真正丢失鼠标捕获时取消手势，忽略子控件冒泡的 `LostMouseCapture`。覆盖层刷新后重新绑定悬停对象，避免沿用旧音素时长或反复关闭提示；刷新中仍有有效手势时继续显示同一搜索对象的候选。后端尚未准备好时消费该声学手柄的点击并提示不可用，不落入原版 VEL 拖动路径。

辅音悬停填充与音符主体之间的细缝按显示几何处理：原生音符框从音乐 tick 内侧一个 pen 宽处开始，填充延伸到该框；真实 `AbsPosTick`、`EndPosTick`、`SignedWidth` 和音素标签中心仍采用引擎边界。该改动不更改发音数据、搜索结果或提交值。

以上属于源码修正；Debug 构建通过。当前运行的 Editor 有未保存内容，本次不关闭它，不覆盖其已加载版本。新版本的鼠标交互与缝隙显示仍待部署后宿主确认，不能用搜索器的自动测试代替这项验收。

随后用户报告下方 VEL 已设为 0，悬停提示仍显示 64。此前修正只覆盖上方手柄及画布刷新，下方参数提交只清理查询缓存，没有主动通知覆盖层；工具提示是生成后保留的字符串，因此可能继续显示旧内容。本次 `ConsonantTimingRuntime.SourceChanged` 通知对应覆盖层关闭旧提示、失效反馈缓存并合并刷新，覆盖原生提交、撤销和重做。缺失音符时不再以默认 64 冒充当前值。

反馈按“音符句柄 + 当前 VEL + 源 revision”缓存；当前悬停音符在音频尚未返回时使用局部无 PCM 查询，等待锁的预算为 0，并对忙状态按 30 Hz 限流重试。不查询全部可见音符。渲染完成后明确失效预测反馈，改读真实 score 边界；忙、准备中及不支持状态显示当前 VEL 和状态文字，不混用旧毫秒数。新状态文本已同步四份翻译。关闭工程后不刷新已关闭源。

`--timing-note-identity-probe` 在私有 MIKU `k a` 样例中检查了 VEL 64→0→撤销→重做：旧视觉引用、参数引用及重新取得的源音符均读取同一个正确值，完整边界及毫秒数跟随实际值改变，四次查询 Start/Step/Samples 均为 0。因此该样例排除了“音符引用自动保留旧 VEL”的猜测；它不代替真实 Editor 参数面板到工具提示的 UI 验收。VEL 0 本身也不表示辅音时长为 0 毫秒。

### 2026-10-16 宿主复现：提示文本被翻译缓存复写

用户再次报告无效后，在实际 6.13.1.1 Editor 的临时单音符 `l a` 工程中复现：参数柱、源音符、覆盖层音符均为 VEL 0，`ToolTip.Content` 已为“116.7 毫秒 · VEL 0”，但屏幕依旧为“58.3 毫秒 · VEL 64”。此前的通知和查询修改没有解决这一实际显示原因。

根因为全局 `WpfTranslationPatch` 在 ToolTip Opened/Loaded 时遍历自动生成的 TextBlock；`GetOriginal` 对同一控件保留第一次文本，重开提示时又将旧数字写回。覆盖层提示已由 `TranslationManager.Tr` 完成翻译，因此在首次打开前用现有的可继承 `UntranslatableProperty` 标记 ToolTip，跳过重复翻译，不改变全局翻译规则。

本轮同时检查真实参数输入入口、下方面板实际拖动、撤销、重做，以及提示视觉树中 TextBlock 的实际文字。VEL 64 为 58.3 ms，0 为 116.7 ms，拖到 123 后为 29.2 ms；撤销回 0、重做回 123，屏幕与 Content、参数柱、音符数值保持一致，渲染完成后也一致。[宿主证据](evidence/timing-tooltip-host-validation.json)记录范围及数值。Debug/Release 整个解决方案构建均为零警告、零错误，已部署合并 DLL；此次无需修改原生 DLL 或申请管理员权限。

`VOCALOID_TIMING_FEEDBACK_VALIDATION=1` 仅用于显式开发验证：等待标准 New 流程完成且取消默认 Add Track 对话框，再在新的未保存空工程建立临时样例。正常启动不运行。`notes` 查询新增 `velocity` 字段；显式请求 `timing_diagnostics` 才读取有界覆盖层/提示视觉树诊断，正常绘制及 MouseMove 不写诊断日志。本轮不代表浮点拖动或多 Part 基础设施的完整验收。

- [ConsonantTimingOverlay.cs](../VOCALOIDPatcher/VOCALOIDPatcher/ConsonantTiming/ConsonantTimingOverlay.cs)：实际边界、原生标签替换、命中和 VEL 数值预览。
- [ConsonantTimingMath.cs](../VOCALOIDPatcher/VOCALOIDPatcher/ConsonantTiming/ConsonantTimingMath.cs)：原生控制灵敏度与整数取整，不作声学推断。
- [ConsonantTimingPatches.cs](../VOCALOIDPatcher/VOCALOIDPatcher/Patch/Patches/ConsonantTimingPatches.cs)：原有七个显示与交互补丁，不再拦截最终提交。
- [离线校准](../voicebank/tools/evec_render_harness/ConsonantTimingCalibration.cs)、[离线求解](../voicebank/tools/evec_render_harness/ConsonantTimingSolver.cs)：仅研究工具。

撤出全量试算并部署安全版本时的交付记录：Debug/Release 构建零警告、零错误，全套 123 项测试通过。离线工具另在临时目录构建通过，该次交付没有运行引擎试算。最终合并 DLL 经只读元数据核对，不含校准类、二分求解方法或替代提交补丁；覆盖层及内嵌 Harmony 保留。安装 DLL、播放时钟及四份翻译已更新并逐项核对哈希。宿主连续操作、缩放及听感尚未验收；用户退出编辑器后完成部署，代理没有启动或关闭 Editor。本轮局部规划研究的隔离试验与限制见上方链接。
