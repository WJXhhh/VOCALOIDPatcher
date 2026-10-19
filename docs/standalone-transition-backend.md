# 独立过渡音后端

2026-10-17。接续[实际引擎控制链](transition-timing-volume-chain.md)。本轮实现后端及独立 SDK 验证入口，未接编辑器 UI、工程归档/撤销事务，也未部署或操作 Editor。验收数字见[证据](evidence/transition-backend-20261017.json)。

后续[同音起音对照](onset-process-comparison.md)进一步区分了 ART 覆盖时长与完整起音的源帧进度：当前端点控制不能等同于主体起音控制，不能仅凭本页的 PCM 变化或映射一致性作该项功能验收。

## 接口与身份

代码在 `ConsonantTiming/Model/TransitionLayout.cs`、`Backend/PreparedTransitionTiming.cs`、`Backend/NativeTransitionSegments.cs` 和 `Backend/NativeTransitionGain.cs`；原生实现为 `native/playback-clock/src/transition_gain.rs`。

沿用 `PrepareCoefficient` 捕获输入、系数、现有音素 timing、上下文及坐标表。准备必须在来源模型线程且输入稳定时执行；后续查询只访问私有元数据与不可变快照，不重新读取源工程。发布/释放已发布的准备仍须遵守源模型线程与提交边界，sequence manager 必须活到准备释放之后。

依次调用：

1. `QueryTransitions(sourcePlan, revision)` 返回不可变布局：原始/当前 ART、左右音素身份、stage、方向、素材元数据摘要、源帧映射和共享接点。
2. `QueryTransitionRange(layout, endpoint, revision)` 枚举在其余接点固定时，原生无音频规划实际接受的整数帧集合。
3. `QueryTransitionControls(layout, controls, revision)` 编译多接点/多过渡增益，规划并核对全部 ART 身份、端点及受保护音素边界。
4. `PublishTransitions(sourcePart, epoch, result, revision)` 再次核对来源和输入，将系数、warp 与增益作为同一原生快照发布，失效当前 Part 的派生渲染结果。

过渡身份包含左右两侧的 **音符地址 + 音素序号/符号**、实际 from/to、stage、方向及重复序号。同名音素和同一素材的两个 stage 不会仅凭名称或素材指针混在一起。布局另带 preparation scope 与 revision；内容相同的其它 Part 不能借用它发布。

素材摘要只覆盖已读取的元数据，不宣称是独立录音的唯一 ID 或 PCM 哈希。精确组件档案、语义身份、全部 ART 序列与真实逐帧映射共同构成验证依据。

## 共享接点与范围

同一个原始帧坐标只创建一个接点，可以同时挂多个 ART 的 Begin/End/Split 别名。对别名提出矛盾坐标会被拒绝；改动共享接点会统一影响其全部别名。现有音素/context 锚点与 ART Split 保持固定，不能通过过渡内部 timing 偷改音素边界。

候选域来自相邻接点、固定 guard 和捕获窗口的输出起点。后端逐一向真实原生规划器验证候选，保存实际接受的集合；不将几何区间直接冒充可达范围，也不将超时当作不可达。返回范围允许有孔洞。组合编辑必须再次整体验证，各单轴范围不能直接做笛卡尔积。

布局、规划结果、范围分别有界缓存。单次范围扫描最多 4096 个候选，仍是相对昂贵的后台查询；再次查询同一范围不进入引擎。增益变化复用已有 timing 几何，无须重新规划。尚未决定未来 UI 的扫描调度与交互刷新策略。

## 坐标与音量

对外统一使用 Part 的绝对原生帧坐标。私有规划中对消息和锚点同时减去整数 origin，读取后再加回；不反插值现有取整 warp，不为后移一分钟的音符构造一分钟前置静音。原生真实渲染仍收到 Part 坐标。零改动保留原有锚点集，避免额外插点引入量化差异。

过渡音量为 -60…+12 dB（-60 表示静音，0 为中性）。增益在 DSE `0x141F50` 的原生帧贡献加入重叠相加前生效，乘已有 native gain。按当前 renderer/engine/Part/epoch 与 ART 的 from/to/stage/方向/实际范围匹配，同时处理主、辅助 analysis。它不是修改 DYN，也不是对最终 WAV 选一段缩放；重叠相加会使听觉边缘跨过接点。

既有音素音量仍按真实音素范围由 `PhonemeGainWaveProcessor` 处理；验证覆盖两者组合及零音量改动的精确恢复。

## 缓存与并发生命周期

缓存数字身份包含原有系数/warp 及按规范顺序编码的过渡增益，排除指针和 epoch。0 dB 不额外更改旧缓存键。内容相同的控制重开后可复用缓存，受控与普通结果不会互读。

VSM 在关闭源 Sequence 时发布缓存。可以先关闭源再释放准备，让原生在控制仍存在时计算正确键；若提前释放，则先将源 Part 标记为需重渲染，再撤掉自己的快照。`HasValidRenderedWave` 不能单独用作输入是否已失效的判据，验收以关闭、重开后的 PCM 为准。

移除操作比较 epoch，旧准备不会撤掉新准备的发布。原生持有不可变 `Arc` 快照，替换或移除的增益代次标为 retired；旧渲染作用域不能继续以成功状态输出。匹配失败会计入诊断并使当前渲染失败，不改为无控制音频冒充成功。

查询入口用现有后端锁和有界原生 mutex 等待；渲染绑定来自实际 renderer scope。没有进程全局“第几个音符”的可变游标。四个内容相同的 Part 中三组控制与一组普通渲染并行，每组与自身顺序参考逐 PCM 一致；同时运行真实私有规划与缓存查询。

## 验证与边界

独立 harness 参数为 `--transition-backend-probe --patcher-dse-initialization OUTPUT`，原生库通过 `VOCALOID_TIMING_PROBE_NATIVE` 显式传入。`VOCALOID_TRANSITION_EXISTING_TIMING=1` 同时加入浮点系数 .375 与现有音素末边界偏移；默认无既有控制。两套 SDK 使用同一最低 SDK 编译产物，分别加载真实 6.13.1.1 / 6.13.3.1 托管与原生组件。

八组夹具覆盖中文单元音、la、多音素、相邻音符、短音符、擦音、MIKU ka 和三音素 stage 1；核对原生实际消耗的 ART 坐标、素材元数据及逐帧来源，不只比较波形外观。验证包含增益反复发布/恢复、多增益、共享接点、可达两端、音素音量组合、关闭/重开缓存、提前释放、外来布局/Part、过期 revision、未提交输入和 VEL 变更拒绝。另测 Part 内后移一分钟的坐标恢复。

当前明确边界为完整且有界的 Part 上下文（最多 33 个音符，捕获窗口相对跨度最多 22 秒）、44100 Hz、256 samples/frame、0.5 秒 presend；不支持 DVQM、AI 或混合副声库。本轮未将有限上下文的可达域宣称为任意长工程的全局最大自由度。

运行时验收使用 Release 原生构建。Debug 原生出现空规划的问题已用改动前的原生/托管组合复现，尚未修正；因此 Debug 构建成功不等于 Debug 原生运行链已通过。未知组件版本按精确档案拒绝，不套用已认证版本的地址。

四轮（两版 × 无/有既有 timing）合计 **524 次逐 ART/源帧核对的渲染、96 次缓存重开、48 次并发渲染**，范围扫描合计 4510 次无音频规划。四轮冷范围查询中位数分别约 5.3、8.2、6.4、7.2 ms，最慢单次约 48.3 ms；这是本机八组夹具的测量，不是任意工程时限保证。重复范围查询为零次原生规划。另通过两版后移一分钟的夹具、提前释放、清空源音符后释放和退休作用域修正后的并发回归。

全套托管测试 208 项、Rust 测试 44 项通过；以最低认证 SDK 完成整个解决方案 Debug/Release 的隔离构建。安装目录主 DLL、原生 DLL 的 SHA256 及主补丁链接均保持原样。

尚未接 UI、工程控制数据归档、共享撤销及宿主渲染调度；本轮 SDK 缓存保存夹具用于验证原生缓存，不能替代后续 VPR 保存和宿主内验收。
