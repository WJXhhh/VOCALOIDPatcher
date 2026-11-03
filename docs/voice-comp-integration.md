# 单轨发音拼接

2026-10-22 实现记录。源码与隔离验证已完成，随后按用户指示部署到 VOCALOID Editor 6.13.3.1；宿主内交互、整句听感和各级导出仍需验收。离线证据见 [voice-comp-offline.json](evidence/voice-comp-offline.json)，部署文件校验见 [voice-comp-deployment.json](evidence/voice-comp-deployment.json)。

## 使用入口与三个动作

在 Emotion 视图的 timing 工具条选择“拼接”，选中音符后执行动作。音符保持在原旋律轨；后台独立渲染来源，最终合成为该 Part 的音频。

| 动作 | 默认行为 | 精调方式 |
| --- | --- | --- |
| 长音截取 | 生成至少原时长两倍、至少 1000 ms 的长音，裁回原发声范围 | 去头／去尾／取中段、实际源长、取用位置、左右裁切把手 |
| 辅元拼接 | 两份来源分别提供辅音与韵部，锁定原入韵点，保留完整辅音向前展开；自动隔离前音 | 来源歌词／音素、上下文、音素起点、连续取用窗口、0–30 ms 接缝 |
| 独立延音 | 解除与后音的合成接续，默认沿用原音符时长，不额外拉长发音 | 实际源长、输出终点、0–30 ms 收尾曲线；默认输出到本音符结束 |

动作可以组合，重复执行保留已设来源。已有辅元拼接再选“长音截取”时聚焦韵部来源，避免创建一份不会使用的长音。默认接缝 5 ms，不进行时间拉伸，也没有恢复选样时长偏置。

“从音符取源”只接受同声库，保存歌词、音素、力度／音符表现及 EVEC 的独立快照。音高、音高曲线和主轨控制器仍跟随所属主音符；它不是冻结的录音。可以分别为辅音、韵部修改前后上下文音素和上下文长度。

默认音素起点为自动（精调值 `-1`）；来源边界双击选择起点，Shift 拖动裁切边缘吸附真实音素边界。复合元音和韵尾通过连续取用窗口保留。无法确定入韵点、材料不足或无有效原生边界时显示失败状态，保留设置和上一次成品文件；输入改变后旧成品不再作为当前音频发布。

### 独立延音的源长修正

2026-10-22 用户反馈：独立延音把复韵母截成单韵母。原实现无条件把末音源长增加 250 ms，同时禁用了独立延音的源长输入框。较晚的元音过渡可能因此落到固定输出截点之后。

修正后，独立延音的“源长 · ms”可编辑：`0` 沿用原音符实际时长，仅在输出终点后移时相应延长；正数设置该音符的实际生成时长，支持比原来更短的来源，也可以主动增加尾部余量。输出截点仍单独控制，不移动音符、不拉伸音频。来源过短可能提前自然收尾，不会补造延音。悬停源长框可查看当前已完成来源的实际时长。

波形上同时提供“源长 ↔ 毫秒”拖动标签，位于来源波形上方，以独立的带箭头标签及长度标尺区别于矩形裁切把手。左右拖动直接改变生成时长，Shift 为十分之一速度精调，范围 10–60000 ms。拖动速度按当前源音时长计算，避免整段 Part 的静音填充使短音源长过于敏感。独立延音、长音截取及辅元各来源均可使用，窄视图无需进入“精调”。单来源视图使用完整来源区高度。

源长拖动只更新标签和候选标尺，不拉伸或伪造已有波形；鼠标松开后才提交一条共享历史与一次更新请求。Escape、丢失捕获或切换目标取消候选，输出截点不变。数值输入仍可用于输入确切时长。隔离验证及部署状态见 [voice-comp-source-length-drag.json](evidence/voice-comp-source-length-drag.json)。

波形源长拖动已通过两套 SDK 的 Debug／Release 构建和完整隔离探针，并已部署、核对七个文件哈希及保留旧版备份。离线检查覆盖窄宽视图、150%／200% DPI、候选不触发生成、Shift 精调、丢失捕获取消、单次提交及撤销；实际 Editor 鼠标与键盘操作仍待用户验收。代理未启动或关闭 Editor。

新值存入 `ContextBreak.SourceLengthMs`，参与缓存、共享撤销及 VPR 持久化；修改终点或收尾曲线不会丢失源长设置。旧归档缺少该字段时按 `0` 处理，重新生成时采用修正后的自动行为。源长未改变的隔离来源继续保留完整的 pure timing 音素调整，而不是一律按已拉长来源处理。长音截取的两倍／1000 ms 自动规则不受影响。

此修正的隔离构建、真实引擎及来源时长证据另见 [voice-comp-sustain-source-length.json](evidence/voice-comp-sustain-source-length.json)。两套 SDK 的 Debug／Release 构建及完整探针通过，384 项托管测试通过；中文复韵母夹具验证自动 400 ms／显式 650 ms 的源长、输出截点不变及撤销复用来源。另修正了缓存满后因 Dictionary 复用空槽而可能淘汰新来源的问题，改为显式最近使用顺序。

检测到 Editor 已由外部关闭后，按此前用户部署授权安装了此修正，七个部署文件哈希均已核对，旧版文件已备份。代理未启动或关闭 Editor，实际复韵母听感仍待用户试听。

## 界面

`AcousticControlStyle` 从已有 timing 分段按钮提取共享样式。工具条现在使用 `AcousticEditMode` 枚举，包含音素、过渡和拼接，切换模式取消候选、不创建历史。

编辑面板位于 Emotion 视图底部，默认 272 DIP，可调 200–360 DIP。宽视图右侧保留 216 DIP 检查区；不足 840 DIP 或面板低于 250 DIP 时，检查区移入“精调”浮层。低高度会压缩刻度与次要标签，保留两份来源波形。收起后保留音符标记，选择或悬停标记可展开名称。

- 蓝灰底色、浅蓝与暖灰金来源、线性动作图标和小型波形预设沿用统一资源。
- 结果波形、源音全长波形及交叠包络来自真实 PCM；生成期间显示状态，不补画虚构波形。
- 来源裁切矩形把手、入韵菱形、接缝曲线和输出终点把手各自命中；悬停提供源时间、输出时间及音素。
- 拖动仅更新候选，松手一次提交；Escape、捕获失败、丢失捕获取消候选。输入数值使用等宽字体。
- 面板、波形绘制层和标记层常驻；冻结画刷／画笔、裁剪绘制、合并刷新。绘制时不读取原生音符，悬停只命中已经捕获的可见标记。
- 四份翻译同步提供全部拼接文本；展开和状态反馈为 140 ms。

源音单听播放完整的独立来源；试听结果截取当前组合成品（含 ACE）。这两种本地 PCM 试听不经过原轨效果器。整句试听走 Editor 原生播放器及原轨混音效果。

## 数据与音频链路

主要文件位于 `VOCALOIDPatcher/VOCALOIDPatcher/VoiceComp/`。

| 文件 | 职责 |
| --- | --- |
| `Model/VoiceCompPlan.cs` | `SourceRecipe`、`ContextBreak`、不可变 `CompRenderPlan`／`SlicePlacement`、采样级对齐 |
| `Model/VoiceCompArchive.cs` | VPR 内 `VOCALOIDPatcher/voice-comp.json` 的版本化读写与校验 |
| `CompNativeSource.cs` | 私有工程、发声组、上下文、EVEC／扩展拼音、真实边界与源音处理 |
| `CompSourceFingerprint.cs` | Dispatcher 捕获的发声组语义输入摘要 |
| `CompWave.cs` | 有界流式裁切、互补余弦接缝、叠加、峰值、取消和原子发布 |
| `VoiceCompHost.cs` | 配方生命周期、历史、调度、缓存、成品发布、导出准备 |
| `CompEditorPanel.cs` | WPF 面板、真实波形及候选手势 |
| `ConsonantTimingOverlay.Comp.cs` | Emotion 入口、选择、来源拾取、音符标记、面板布局 |
| `Patch/Patches/VoiceCompPatches.cs` | 原生命令／渲染／播放范围／关闭边界 |
| `VoiceCompValidation.cs` | 仅隔离构建包含的真实引擎与 WPF 探针 |

普通连续音符保留连唱。被替换的音符从普通组移除，隔离边界把相邻组断开；每份有声材料仅输出一次。私有工程复制 tempo、调律、主 Part 控制器和目标音符，显式注册为内部工程，不持有主 Part 的 score 修改权。EVEC 的子音符和扩展拼音 carrier 在这个所有权内编译，最终边界重新归属主音符。

处理顺序为：

```text
原生合成与 pure timing（含 REG、EVEC、扩展拼音）
  → 每份来源的 BVL
  → 每份来源的音素音量
  → 每份来源的原生过渡平滑
  → 截取／辅元接缝／独立延音混合
  → ACE PCM
  → 原轨播放与混音效果
```

因此前元音与后辅音的重叠分别保留各自音高、音量，再混合。裁切改变源到输出的时间映射时，会按歌曲实际时间映射控制器、直接音高及颤音；材料不够时拒绝隐式拉伸。音素拓扑改变的来源保留 pure timing 系数，不把旧音素逐项偏移硬套到新拓扑。

播放器、波形和导出通过同一成品路径及边界元数据读取结果。屏蔽原生渐进分块覆盖组合成品；重叠边界不写回主 Part 的单声部 score。输出延伸超出 Part 时，文件播放范围随成品延长，主 Part／音符长度不变。

## 调度、缓存、历史与失败

- 每 Part 一个运行请求、一个可替换的最新请求；120 ms 去抖，取消和 generation 在准备及发布前检查。私有来源使用共享单工作限流及原生渲染门。
- 源音缓存按实际发声组输入计算，不依赖不相关音符的 Part revision。裁切、接缝、旁路只影响拼合；已认证的惰性 ACE 载体不触发原生源音重建。发音、源长、tempo、有效控制器、pure timing 等变化重新合成相关组。
- 同一生成条件的辅音／韵部共享原始来源，自动源长先解析为真实长度再比较。每 Part 最多缓存 32 份原始来源；当前成品引用的文件保留到替换或关闭。
- 配方进入现有共享历史；一次手势对应一条撤销记录。音符移动依附音符身份，复制通过已有 NoteCopied 链路传递，删除／原生撤销重建身份时重新绑定。
- VPR 只保存配方和稳定定位信息，不保存 PCM、指针或临时路径。归档最多 4 MiB／100000 条，限制深度并验证数值、枚举和重复定位。不能重新绑定的条目保留；包含激活的未绑定条目时拒绝导出。
- 保存沿用原有整体事务，注入写入失败时恢复旧 VPR 且保持脏状态。源音生成失败保留旧成品文件，但不替代当前输入的音频（尤其不能沿用旧 BPM）；导出等待当前版本，失败或取消则中止，不能静默使用旧版本导出。
- 私有源 Dispose 统一排队关闭；管理器销毁前停止请求、泵送 Dispatcher 并等待源工作／原生关闭窗口安静，避免后台原生释放与管理器释放竞态。

## 两项必要的原生集成修正

新建独立纯元音源暴露了原 timing native hook 的返回值声明错误。只读核对已认证 DSE 二进制后，将内部 candidate scope／prune 返回值从 `u64` 改为 `u8`，selector 的原生 char 参数也保留为 `u8`。目标只定义 AL，高位并非有效布尔返回值；经 TLS 搬运错误的宽返回值会污染中性候选。没有修改导出 ABI、结构体或引入依赖。两套 SDK 的冷启动独立源、pure timing 及 52 项 Rust 回归均已执行。

扩展拼音的独立来源同时收紧了边界与释放契约：要求两段 carrier 的真实音素边界，允许原生 presend 内的负预发声，不使用名义音符长度伪造边界。其专属任务在返回前同步移除 observer、停止并释放渲染门，源工程只交给 `CompNativeSource` 的统一关闭路径，避免重复异步关闭导致管理器退出崩溃。

## 已执行验证

同一份按最低 SDK 编译的隔离补丁分别加载两套真实 SDK。隔离验证期间没有启动、关闭或部署 VOCALOID Editor；后续部署使用按最低 SDK 编译的合并 Release 产物，保留旧版备份并逐项校验安装文件，没有启动或关闭 Editor。

| 验证 | 6.13.1.1 | 6.13.3.1 |
| --- | --- | --- |
| 完整解决方案 Debug | 通过，0 警告／0 错误 | 通过，0 警告／0 错误 |
| 完整解决方案 Release | 通过，0 警告／0 错误 | 通过，0 警告／0 错误 |
| 真实引擎拼接探针 | 通过 | 通过 |
| 私有来源清理及进程正常退出 | 通过 | 通过 |
| WPF 四语言 × 三宽度 × 三高度 × 三 DPI | 108 份渲染 | 108 份渲染 |

独立验证还包括 383 项托管测试、52 项 Rust 测试，以及 `BreathVolumeOfflineTests`、`AceAcousticOfflineTests`、`AceParameterOfflineTests` 全部通过。

真实引擎探针覆盖三种长音取用产生不同 PCM、25 次快速编辑及原始来源复用、前后上下文隔离、Part 首尾／超尾播放范围、后辅音修改后前音 PCM 与来源缓存保持不变、辅元对齐、共享撤销、来源快照字段、失败保留／导出拒绝、保存失败回滚、保存重开、原生删除撤销、pure timing、音高与变速、主轨控制器、音素增益、过渡、ACE、EVEC 和扩展中文拼音。独立手工副本通过原生异步渲染生成的参考源与生产来源 PCM 逐字节一致；裁切叠加的采样值另外由纯测试与人工期望数组比对。

BVL 探针证明来源经过 BVL 生命周期且未丢失有效声音，但本夹具未产生可识别呼吸材料（`changed=false`），不能据此声称呼吸增益已完成听感验收。窄屏和宽屏的实际 WPF 截图已人工检查；截图矩阵不是宿主内鼠标／键盘／滚动验收。

## 复现隔离验证

使用合法 SDK 目录和已授权声库，所有输出指向独立临时目录。探针默认夹具使用 MIKU V4X Original EVEC 与洛天依 V4 中文的已安装声库；缺失时明确失败。不要把 SDK DLL 或临时 WAV 放入仓库。

```powershell
$compSdk = $env:VOCALOID_COMP_SDK
$compOutput = Join-Path $env:TEMP 'v6-voice-comp-verification'
dotnet build VOCALOIDPatcher.sln -c Debug --artifacts-path "$compOutput/build" "-p:VocaloidReferenceDirectory=$compSdk"
dotnet build VOCALOIDPatcher.sln -c Release --artifacts-path "$compOutput/build" "-p:VocaloidReferenceDirectory=$compSdk"
dotnet build VOCALOIDPatcher/VOCALOIDPatcher.csproj -c Debug -p:PatcherIsolatedValidation=true --artifacts-path "$compOutput/isolated" "-p:VocaloidReferenceDirectory=$compSdk"
dotnet build voicebank/tools/evec_render_harness/EvecRenderHarness.csproj -c Debug --artifacts-path "$compOutput/harness" "-p:VocaloidReferenceDirectory=$compSdk"

$env:VOCALOID_PROBE_EDITOR = $compSdk
$env:VOCALOID_DURATION_HOST_ASSEMBLY = "$compOutput/isolated/bin/VOCALOIDPatcher/debug/VOCALOIDPatcher.IsolatedValidation.dll"
$env:VOCALOID_TIMING_PROBE_NATIVE = "$compOutput/isolated/bin/VOCALOIDPatcher/debug/VOCALOIDPatcher/native/v6patch_clock.dll"
dotnet "$compOutput/harness/bin/EvecRenderHarness/debug/EvecRenderHarness.dll" --voice-comp-probe --patcher-dse-initialization "$compOutput/probe"

dotnet test VOCALOIDPatcher.McpTests/VOCALOIDPatcher.McpTests.csproj --artifacts-path "$compOutput/tests"
& "$env:USERPROFILE/.cargo/bin/cargo.exe" test --manifest-path native/playback-clock/Cargo.toml --target-dir "$compOutput/rust-tests"
```

做跨 SDK 验证时保留最低 SDK 编译的隔离补丁，仅切换各 SDK 对应的 harness 和 `VOCALOID_PROBE_EDITOR`。并行探针使用各自的外置运行数据目录，避免翻译文件复制竞争。

## 尚待宿主验收与边界

- 部署后，在真实 Editor 中验证整句听感，尤其爆破音、复合元音／韵尾、三个动作组合、多音交叠和包含呼吸材料的 BVL／REG。
- 验证拖动／取消／键盘焦点、来源拾取、滚动缩放、切换 Part、窄视图遮挡和连续快速操作；核对播放器、波形及各级导出。
- 验证混合原生命令撤销重做、移动／复制／删除／重开，以及全部旁路或恢复后回到原输出。
- 首版只支持传统采样、同声库来源；AI、双声库混合、跨声库取样和自动搜索最佳发音不在范围内。
- 私有源保留原 Part 时间布局；超大 Part 的生成耗时、内存与长期交互尚未压测。源音最长 60 秒、上下文单音 50–5000 ms，材料不足会显式失败。

本次没有添加 NuGet／Rust 依赖，没有修改第三方移植区域，也没有运行发行部署脚本。

## 2026-10-22：含连音的来源生成超时

现场只读检查确认，`geng` 所在独立来源包含六个音符，其中有两个 `-` 延续音。原生主轨已有有效音频及边界，但私有来源在准备阶段被拒绝；Harmony 安全降级阻止了原生任务启动，来源模块仍等待完成事件，90 秒后误报超时。用独立夹具带入相同 timing／过渡设置复现了这一过程。

修正包含三个边界：

- 前音后接 `-` 时，其最后边界是延续音的音高锚点，不能当成元音真正结束。局部规划同时验证前后两份原生采样行确实延续同一音素；不接受仅凭名称或名义时长的边界。
- 私有发声组隔离后，原生起音／收尾素材及可用过渡区间可能改变。继承的过渡偏移在新来源中按最近合法整数帧投影，固定未编辑接点、音素锚点及已有原生重合关系，保留增益、相位等其他控制。该投影仅用于拼接来源，普通主轨的严格校验和用户归档原值保持原样。
- 来源启动及 observer 泵送后检查 timing 准备故障，立即返回准备失败原因，保留旧成品；不再把已拒绝启动的任务等到超时。

按最低 SDK 编译的同一隔离补丁在 6.13.1.1／6.13.3.1 完成完整拼接回归，包括六音连音来源、故意拒绝启动后的快速失败、归档原值保持及原生清理。当前工程的只读副本另外完成目标 Part 的原生基线与全部五个拼接来源；未写回用户工程。托管测试增加两项来源过渡适配回归，共 386 项通过。

本次发行构建在独立源码快照完成，保留上一已部署版本的 MCP 路由，排除同一工作区正在开发的 MCP timing 接口；工作区改动全部保留，工作区本身另通过完整解决方案 Debug 构建。两套 SDK 的快照 Debug／Release 构建均为零警告、零错误。真实 Editor 内重新打开该工程后的听感及交互仍需验收。

## 2026-10-23：独立源长改变了同组其他音符的 timing

现场选择为第 18 小节第 3 拍的 `zhong`（`ts` + `UN`），其独立来源包含同 Part 的前 17 个音符。`CompNativeSource` 原先把“来源末音改变长度”作为整个来源组的布尔状态传入 `BindCompSource`，后者因此给组内所有音符只保留 timing 系数，漏掉了其他音符已有的音素边界偏移。主工程的归档数值没有被删除，但重新生成的来源回到了未调整的边界。

现在传递的是具有独立内部 timing 的所属主音符身份。只有该音符及其 EVEC 派生事件按独立来源语义处理；其他音符完整继承原来的 timing 计划，包括共用的物理边界。音素拓扑改变时仍保留原有的安全过滤。

新增 `CompSustainTimingValidation` 隔离探针，以真实中文原生渲染比较源长 148／350／650 ms 和恢复自动长度后的每个邻音边界，同时检查复制后的 timing 计划指纹、主轨 timing／过渡归档及音符几何保持不变。当前工程的只读副本在修复前有 9 个非目标音符发生边界变化；修复后两套 SDK 下其余 16 个音符的边界逐项一致。小型 `uo`／`k_h 7`／`t 7`／`ts` + `UN` 夹具也复现并验证修正。所有文件输出位于独立临时目录，没有修改现场工程。

探针在现有 `--voice-comp-probe` 的隔离补丁下通过 `VOCALOID_COMP_SUSTAIN_TIMING_PROBE=1` 启用；默认使用小型夹具。可额外设置 `VOCALOID_COMP_SUSTAIN_PROJECT` 为只读 VPR 路径、`VOCALOID_COMP_SUSTAIN_PART` 和 `VOCALOID_COMP_SUSTAIN_NOTE` 为零起始索引，检查指定 Part 的目标及其前置来源组。`VOCALOID_COMP_SUSTAIN_PROJECT` 只读，不保存、重开或替换用户 Editor 中的工程。
