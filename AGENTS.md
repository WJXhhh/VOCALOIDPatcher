# AGENTS.md

本文档适用于整个仓库，供在本项目中工作的自动化编码代理使用。

## 项目概览

VOCALOID Patcher 是加载到 Yamaha VOCALOID 6 Editor 进程中的 Windows 补丁。主程序集伪装为 `Microsoft.Xaml.Behaviors.dll`，通过模块初始化器启动，并使用 Harmony 对编辑器方法打补丁。项目还包含界面翻译、批处理工具、实时频谱和基于 LibreSVIP 移植的工程格式转换功能。

- 解决方案：`VOCALOIDPatcher.sln`
- 主项目：`VOCALOIDPatcher/VOCALOIDPatcher.csproj`，目标框架为 `net8.0-windows`
- 翻译检查工具：`ResourceTranslationComparer/ResourceTranslationComparer.csproj`
- 补丁入口：`VOCALOIDPatcher/VOCALOIDPatcher/Patcher.cs`
- Harmony 补丁：`VOCALOIDPatcher/VOCALOIDPatcher/Patch/Patches/`
- 设置与配置：`VOCALOIDPatcher/VOCALOIDPatcher/Config/`
- 界面与工具：`VOCALOIDPatcher/VOCALOIDPatcher/UI/`、`Jobs/`、`Utils/`
- 格式转换：`VOCALOIDPatcher/VOCALOIDPatcher/Formats/LibreSvip/`
- 外置翻译与硬编码映射：`translations/`、`HardcodedPropertyMap.xml`

`VOCALOIDPatcher/Microsoft.Xaml.Behaviors/` 是随项目包含的第三方兼容代码；`Formats/LibreSvip/` 主要移植自 LibreSVIP。修改这些区域时应保持改动聚焦，并同步检查 `THIRD-PARTY-NOTICES.txt`、相应 `NOTICE.md` 和许可证要求。

## 开发环境与构建

项目仅面向 Windows。主项目通过固定路径引用：

```text
C:\Program Files\VOCALOID6\Editor\VOCALOID6.dll
```

因此，完整构建要求该路径存在较新的 VOCALOID 6 Editor 安装。不要把 VOCALOID 的专有 DLL 复制进仓库，也不要为了绕过缺失依赖而提交本地路径或二进制文件。

主项目和 `evec_render_harness` 可用 `-p:VocaloidReferenceDirectory=<合法 SDK 所在目录>` 覆盖默认参考目录；发行脚本支持同名参数。需要同一产物兼容多个托管 SDK 时，以已认证的最低 SDK 编译并分别运行验证；仅增加原生哈希档案不能降低程序集引用的版本要求。当前采样控制链路已在 6.13.1.1 / 6.13.3.1 通过独立 SDK 验收，范围见 `docs/second-real-version-compatibility.md`，不代表全部 UI 已验收。

安装目录的主 DLL 可能是指向仓库普通 Release 输出的符号链接，普通 Release 构建也可能间接改变已部署文件。用户未授权部署时，Debug/Release 验证统一使用独立的 `--artifacts-path <临时目录>`，不要运行发行脚本的 `-Build` 路径。

VOCALOID 6.13.0.1 的反编译代码参考目录按以下规则定位（翻译检查工具使用相同规则）：

1. 优先使用本机环境变量 `VOCALOID_V6SRC`；空值或纯空白值视为未设置。相对路径以仓库根目录为基准。
2. 未设置时使用仓库同级的 `v6src` 目录，即相对于仓库根目录的 `../v6src`：

```text
development/
├── VOCALOIDPatcher/
└── v6src/
```

各设备可以使用不同的父目录；如果源码放在其他位置，只需在该设备设置用户级环境变量 `VOCALOID_V6SRC`，并重新打开终端或代理应用使其生效。不要为切换设备修改本文件或在源码中写入机器专用路径。代理读取参考代码前应按上述规则解析目录并检查是否存在。

当补丁涉及 VOCALOID 内部类型、私有方法、音素时值或原生绘制流程时，可优先在该目录核对实现。该目录不属于本仓库，只能作为只读参考；不要修改、复制或提交其中的专有代码。

## Ghidra MCP：跨设备与多实现约定

本仓库不绑定某一种 Ghidra MCP 实现、服务名称、版本、安装路径、端口或运行模式。GUI 插件桥接、stdio 服务、PyGhidra/headless 等均可使用；以当前设备的有效配置、实际工具 schema 和用户授权为准。个人 MCP 配置、Java/Python 路径和分析项目位置由本机维护，不应因切换设备修改仓库文档。

### 发现与验证

1. 先检查当前会话实际可用的工具，包括可搜索或延迟加载的工具。不要仅因没有某个预期工具名就断言“没有 Ghidra MCP”；不同实现的同类能力可能使用完全不同的名称。
2. 如需排障，检查客户端中实际配置的服务名称、启动命令、参数、环境变量和加载错误。Codex 可使用 `codex mcp list`，再用 `codex mcp get <实际服务名> --json` 查看对应服务；服务不必名为 `ghidra`。只读取必要配置，不把完整用户配置或凭据复制到仓库或日志中。
3. 先区分传输方式与执行模式：stdio、HTTP 等描述客户端如何通信，不能据此判断服务是否连接 GUI、是否启动独立 JVM 或是否共享项目。检查实际启动器及当前实现说明，按执行模式选择检查方式：
   - GUI 桥接：确认其要求的 GUI、插件和目标 Program 已准备好；仅在配置使用网络端点时检查该端点，不固定检查某个端口。
   - 独立后台（如 headless）：检查启动程序、依赖、项目目录和服务错误；不要求 GUI 或 HTTP 监听。客户端按既有配置正常启动服务是该模式的正常流程。
   - 需要显式连接或选择实例的实现：依据实际工具说明操作；不要把某个实现的握手步骤套到其他实现。
4. 使用当前实现提供的只读能力列出程序/二进制并查询目标元数据，确认文件名、版本或哈希、架构和映像基址。后续查询按 schema 显式指定目标名称、路径或标识；不支持显式目标时先确认当前选择。空项目或未加载目标应与服务未连接区分。
5. 工具列表出现、端口监听、实例的缓存状态或“已连接”字段都不能单独证明目标可用。以一次成功的目标元数据查询和所需只读查询为验证依据。
6. 仅在配置已修正、客户端仍未加载工具时，按客户端支持的方式刷新或重新建立会话；重新建会话不能替代依赖安装、配置修复或目标准备。失败时报告具体层次：工具未加载、服务启动失败、连接失败、目标缺失或查询不支持，不无限重试。

### 会话切换与实例复用

- 新会话不应假定继承了上一会话的工具、连接或目标选择。先核对当前有效配置与实际工具 schema；配置刚切换时，注意客户端可能仍缓存旧实现的工具列表。仓库约定描述通用行为，本机路径、端口、实现名称及迁移记录留在本机配置或说明中。
- 用户说“已经打开”时，先确认其所指的 GUI、项目和 Program 与 MCP 查询对象是否一致。后台返回空列表只说明该服务所见项目为空，不能据此声称用户没有打开 DLL。报告时区分 GUI 项目、后台项目、程序标识和源文件路径；工具未提供的信息应明确说明未知。
- 优先复用当前配置及用户指定的已有实例。再次导入或启动分析前，先核对是否已有目标、分析任务或可复用连接；不要因为列表暂空、分析未完成或查询失败就重复导入、另建项目或启动另一套服务。
- 不假定任何实现都支持附着到外部 GUI，也不假定其 GUI 启动模式就是附着模式。用户要求连接已有 GUI 而当前实现不支持时，先查明能力限制，再在授权范围内调整连接或实现；不要把 GUI 项目路径直接交给另一个进程打开来模拟附着。
- 多客户端或重连可能产生独立后台进程。出现重复分析、内存明显增加或项目锁冲突时，核对进程归属和项目占用；清理须限于已确认且获授权的实例，不误停用户 GUI、不删除真实项目，也不在仍有持有者时强删锁文件。
- 调整连接后，以目标元数据和一次实际只读查询验证是否落在预期实例上；仅更改配置、插件加载成功或端口监听均不算完成。涉及已有项目时保留其数据和用户修改，迁移所需配置备份放在本机，不写入仓库。

### 分析用法与兼容性

- 按能力选择工具：程序/二进制列表与元数据、函数/符号搜索、反编译、交叉引用、字符串、导入/导出、内存读取。不要求实现提供固定名称的工具，也不因缺少某个工具而否定其他可用能力。
- 先定位目标和函数，再按地址或实现支持的标识反编译；用交叉引用和少量原始数据交叉验证。导出符号可能只是标签，不一定是可反编译函数。
- 所有搜索、列表和批量反编译使用合理的分页、数量或范围限制。避免无界 p-code、全库反编译和巨量调用图输出。
- 工具可能返回 JSON、文本或逐项错误；先检查实际返回结构。分析状态、函数长度和类型推断等单项结果不应独立作为结论。
- 旧环境曾出现分析状态不准、函数长度被报告为 1 字节、搜索词不可省略等问题。这些是特定实现/版本的历史现象，不是所有 MCP 的通用缺陷；遇到类似现象时以当前查询结果及交叉验证为准。
- 研究文档里的工具名、端口、已打开项目、绝对地址和分析环境是当时记录，不是新设备的配置要求。复用结论前核对目标二进制版本、哈希或相关字节特征及映像基址。

### 操作边界

- 普通查询默认只读。用户已有的 MCP 配置可以正常使用；不要求所有设备采用同一白名单格式，也不因为服务暴露写工具就调用它们。
- 重命名、注释、类型修改、导入、保存、删除、调试和修改二进制等操作应在用户请求授权范围内。不要为验证连通性修改或重新导入用户项目。
- 不擅自启动、重启或关闭用户的交互式 Ghidra GUI，不为排障另起重复实例或切换实现。已配置的 headless 服务正常启动不属于擅自切换；涉及用户正在使用的项目时先确认不会造成锁冲突或覆盖。
- 尊重现有客户端工具过滤与权限设置，不通过直接 HTTP、额外脚本或修改白名单绕过限制。配置排障应聚焦已确认的问题，不顺手修改全局 Java/Python 环境或替换用户选择的 MCP 实现。
- 仓库中的 `VOCALOIDPatcher.McpBridge/`、`VOCALOIDPatcher.McpServer/` 和 `VOCALOIDPatcher/VOCALOIDPatcher/Mcp/` 面向 VOCALOID Editor，与 Ghidra 分析服务不同。未经用户明确要求，不得部署或启动 VOCALOID Editor；Ghidra 查询成功也不能代替 VOCALOID MCP 的宿主内验证。

常用命令：

```powershell
dotnet restore VOCALOIDPatcher.sln
dotnet build VOCALOIDPatcher.sln -c Debug
dotnet build VOCALOIDPatcher/VOCALOIDPatcher.csproj -c Release
dotnet build ResourceTranslationComparer/ResourceTranslationComparer.csproj -c Debug
```

Release 构建通过 ILRepack 生成可部署文件：

```text
VOCALOIDPatcher/bin/Release/net8.0-windows/out/Microsoft.Xaml.Behaviors.dll
```

`bin/Debug/net8.0-windows/Microsoft.Xaml.Behaviors.dll` 和 Release 目标框架目录根部的同名 DLL 都是未合并主程序集，尺寸约 1 MiB，不能当作单文件发行版本。ILRepack 会把 `0Harmony.dll`、`ToolGood.Words.Pinyin.dll`、`YamlDotNet.dll` 和 `ZstdSharp.dll` 合并进 `out/` 下约 5–6 MiB 的 DLL。合并后程序集引用列表中不再出现独立的 `0Harmony`，但 `HarmonyLib` 类型应仍存在于最终 DLL 中。部署和打包必须使用 `out/` 文件。

发行包可用以下命令生成：

```powershell
./scripts/build-release.ps1 -Build
```

项目还包含 `native/playback-clock/` 下的 Rust 播放时钟。MSBuild 默认从 `%USERPROFILE%\.cargo\bin\cargo.exe` 调用 Cargo，因此当前 PowerShell 的 `PATH` 中没有 `cargo` 也不代表构建不可用。Release 构建应同时产生：

```text
VOCALOIDPatcher/bin/Release/net8.0-windows/VOCALOIDPatcher/native/v6patch_clock.dll
```

发行脚本会把它打包为 `VOCALOIDPatcher/native/v6patch_clock.dll`。不要提交 `native/playback-clock/target/`；Rust 单元测试可显式运行：

```powershell
& "$env:USERPROFILE\.cargo\bin\cargo.exe" test --manifest-path native/playback-clock/Cargo.toml
```

`scripts/deploy.ps1` 会申请管理员权限，并修改或链接 VOCALOID 安装目录中的文件。除非用户明确要求部署，否则不要运行它。也不要在验证过程中启动或关闭 VOCALOID Editor。

## 验证要求

仓库当前没有自动化测试项目。修改后至少执行与改动范围相符的构建：

- 普通源码或项目配置改动：构建整个解决方案。
- 仅格式转换代码改动：至少构建主项目，并对涉及的导入/导出格式做可用的针对性往返或样例验证。
- 翻译检查工具改动：单独构建并运行该工具的相关路径。
- 发布脚本改动：先做 PowerShell 语法检查；只有在已有合法 Release 输出时才生成发行包。

若因缺少 `VOCALOID6.dll` 无法构建，应清楚报告该环境限制，不得声称验证通过。不要把 `bin/`、`obj/`、`release/`、生成的 DLL、ZIP 或 VOCALOID 工程样例作为普通源码改动提交。

## 代码约定

- 保持现有 C# 风格：文件作用域命名空间、启用 nullable、4 空格缩进，公开类型和成员使用 PascalCase，私有字段使用 `_camelCase`。
- 优先做小而明确的修改；不要顺手格式化或重写无关的移植代码。
- 主程序集在宿主进程内运行。补丁失败不应拖垮编辑器：在 Harmony prefix/postfix 和 UI 注入边界延续现有的防御式空值检查、异常隔离及调试日志模式。
- 新 Harmony 补丁通常继承 `PatchBase`，准确声明目标类型、方法名和参数类型，并在 `Patcher.ApplyPatches()` 中注册。受设置控制的补丁应使用对应的 `Settings` key 归组，使运行时保护可以禁用故障功能。
- 反射或补丁目标依赖 VOCALOID 内部实现。改变签名时要考虑不同受支持编辑器版本；找不到目标方法时应安全降级并记录原因。
- WPF UI 访问须在正确的 Dispatcher/UI 线程上执行。缓存 WPF 画刷、几何等可冻结对象时，沿用 `Freeze()` 做法。
- 不引入无必要的新 NuGet 依赖；确需引入时，说明用途并同步第三方声明。

## BVL 参数面板

独立呼吸音量（BVL）面板主要实现在 `VOCALOIDPatcher/VOCALOIDPatcher/BreathVolume/BreathVolumeOverlay.cs`，数值、缓存和波形重建生命周期在 `BreathVolumeService.cs`。维护时应以按上述规则定位的反编译代码目录中的 `VOCALOID6/MusicalEditor/ParameterView.cs` 及其 Behavior 反编译代码为行为依据，不要只凭截图模仿外观。

### 自定义参数面板的实现方法

这轮实践确认，新增参数面板不能简单理解为“画一层长得像原版的 Canvas”，也不能只把自定义枚举塞给整个原生 `ParameterView`。原生参数面板由显示控件、`ParameterView` 调度、多个 Behavior、VSM 数据写入和撤销事务共同组成；其中只有一部分能安全复用。

1. **先确定数据语义。** 明确参数是连续 Controller、音符属性，还是像 BVL 一样由补丁单独持久化的“每音符派生值”。同时确定取值范围、默认值、选择单位、命中区域、撤销粒度和修改后是否触发重渲染。数据语义没定之前不要开始仿 UI。
2. **沿原生调用链研究，而不是只看控件类。** 至少核对 `ParameterView.OnLoaded`、`EditModePropertyChanged`、`DrawControlParameters`、`UpdateMouseCursorGuide`、`UpdateToolTip`、`BeginDragRectangle/DragRectangle/EndDragRectangle`，以及 Arrow、Pencil、Line、Move、Selection、SongPositionJump 对应的 Behavior。还要阅读 `UIControlParameters`、`UINomineeControlParameters`、`Bar`/`BreakPoint`、`NomineeBar`/`NomineeBreakPoint` 的 `Render` 和 `IsHit`。
3. **把原生代码分成“可复用显示原语”和“必须适配的业务行为”。** 网格、Bar 绘制、候选轨迹、原生画刷、光标、提示 Label、播放位置线和拖选框可以复用。原生 Behavior 会按 `ControlParameterType` 直接修改 `NoteVelocity`、`Opening` 或 VSM Controller，并使用原生 Selection/Transaction；对于 BVL 这种独立数据模型，直接挂原生 Behavior 会写错数据。正确方式是保留显示原语，按原生状态机重建一层很薄的数据适配与提交逻辑。
4. **不要把“补齐 Min/Max/Default 枚举分支”误当成完整接入。** 即使原生控件能画出柱，后续的命中、选择、候选建立、拖动提交、双击输入、撤销和刷新仍可能在不同方法中按 Velocity/Mouth/Controller 分流。只有逐条审计所有分支后才能决定是否复用整条原生路径；否则应使用隔离覆盖层。
5. **覆盖层只创建一次。** 在 `ParameterView` 构造完成后找到 `xPanel`，建立一个与歌曲坐标系一致的根 Canvas，并一次性加入网格层、真实参数层、候选层、空状态和输入框。以后刷新只改 `Width`、`Height`、数据列表、Visibility 和 `InvalidateVisual()`，不要 `Children.Clear()` 后重建。
6. **保持原生层级关系。** 参数层顺序应为背景/网格 → 实际参数 → 候选轨迹；原生 `xOutsideActivePartCanvas`、`xTempObjectCanvas`、`xGuideCanvas` 在自定义面板激活时按需要提升到覆盖层上方，并关闭命中，退出时恢复原始 ZIndex 和 `IsHitTestVisible`。不要复制播放线或提示 Label，否则容易出现双线、低频更新或两套边界算法。
7. **统一歌曲坐标系。** 根 Canvas 宽度至少为 `max(SongWidth, ParameterView.ActualWidth)`，横坐标统一由绝对 tick 与 `WidthPerTick`/`CalcTickToViewPosition` 得出；命中测试、轨迹点、柱位置和播放线不能混用 viewport 局部坐标、Part 相对 tick 与歌曲绝对 tick。所有提示和输入框再用 `ParameterViewer.ViewportRect()` 做可视范围裁剪。
8. **把交互写成显式状态机。** 推荐最少包含 `None`、`SongPositionJump`、`Rectangle`、`MoveWait`、`Move`、`PencilWait`、`Pencil`、`LineWait`、`Line`。MouseDown 只捕获上下文和原值；首次 MouseMove 才从 Wait 进入真实拖动；MouseUp 负责唯一一次提交；LostCapture/Escape 只取消候选并恢复 Idle。不要把这些逻辑分散成互相触发的即时值更新。
9. **拖动期间数据与显示分离。** 用独立 preview 字典保存候选值，用 `UINomineeControlParameters` 呈现；不要在每个 MouseMove 中写服务层、触发 Changed、刷新真实柱或启动音频重建。MouseUp 时把 preview 一次写入服务层，再创建一条撤销记录和一次重建请求。
10. **刷新入口必须幂等且合并。** 后台通知先切回 Dispatcher；用 pending 标记把同一消息循环内的多次 Changed 合成一次刷新；用 refreshing 标记阻止重入。刷新代码不得反过来修改会再次发送 Changed 的业务状态。只更新播放头、提示或候选轨迹时，不要刷新网格和全部柱。
11. **重计算与 UI 刷新分开限流。** 参数显示应立即更新，但 WAV、缓存、分析结果等昂贵派生物应按 Part 去抖。generation 只有在昂贵工作开始前和结束前都核对才有效；如果所有旧任务仍会排队进入串行锁，generation 检查得太晚，仍会造成卡死。
12. **诊断只放在状态边界。** 可记录 Attach、Refresh 合并次数、MouseDown/状态转换/MouseUp、提交值数量、重建 generation 和耗时。不要给 `OnRender`、`CompositionTarget.Rendering` 或每像素/每 MouseMove 路径挂全局 Harmony 日志；结构化日志也会产生分配、序列化与后台写盘压力。
13. **按行为矩阵验收。** 至少覆盖 Arrow 单击/拖选/Ctrl/Shift/双击/右键、Pencil 单击/正向拖动/回拉/越界、Line 单击/左右两个方向/越界、Move 单个/多选/上下限/Alt、Escape/LostCapture、播放中点击、水平滚动与缩放、空 Part、切换参数类型，以及连续快速操作几十次后的 UI 响应和后台任务数量。

实现新面板时，优先复制原生的行为契约和生命周期，而不是复制反编译源码本身。最终结构应当是“原生绘制与交互素材 + 自定义数据适配器 + 明确状态机 + 合并刷新/去抖重建”，而不是一套凭外观猜出来的平行 UI，也不是把不兼容的数据硬塞进原生业务分支。

- BVL 的网格、柱和编辑候选层分别复用 `UIControlParameterGridLine`、`UIControlParameters`、`UINomineeControlParameters`。这些控件应在覆盖 Canvas 中常驻，只更新数据和调用 `InvalidateVisual()`；不要在每次刷新时清空并重挂整棵视觉树。歌曲画布可能宽达数十万像素，反复拆装会在连续操作后阻塞 WPF UI 线程。
- 播放位置线复用 `ParameterView.pathSongPos` 及原生 `songPosTranslate`。项目的平滑播放头会在 `CompositionTarget.Rendering` 中逐帧更新该变换；不要用低频 `SongPosition` 通知另算一套 BVL 播放线。
- Pencil、Line 和 Move 的拖动过程只更新候选层，鼠标松开后才一次性写入 BVL 数值并提交历史。Pencil 回拉时要删除被折返覆盖的轨迹点；Pencil/Line 对柱值按音符绝对 tick 在线段间插值，Line 越过 Part 边界时先裁剪端点并保持原斜率。
- 原生交互包含 Wait→Insert/Move 状态。只按下再松开不应误修改柱值；首次 MouseMove 才建立候选轨迹。拖动期间同步维护 `EditorMode.IsIdleMouseOperation` 和 `HorizontalDragScroll`，Escape 或丢失鼠标捕获必须清理候选层、拖选框和临时状态。
- 光标和提示沿用 `VocCursors.Pencil`、`VocCursors.LinePencil`、`Cursors.Hand` 和 Alt 拖动的 `VocCursors.Duplicate`。数值提示、双击输入框采用原生 57×17 尺寸与水平 14、垂直 7 像素偏移，并按 `ParameterViewer.ViewportRect()` 翻转或裁剪。
- 空白 Arrow 单击先进入 SongPositionJump；发生拖动后才切为 RectangleSelect，并使用 `ParameterView.BeginDragRectangle/DragRectangle/EndDragRectangle`。未拖动松开时才跳转歌曲位置，播放或录音期间不跳转。
- 一次 BVL 提交可能同时产生 Display 和 Values 通知，覆盖层刷新必须合并排队，不能在鼠标松开后再额外同步全量刷新。不要在 `UIControlParameters.OnRender` 等 WPF 热路径上挂全局 Harmony 诊断或生成大字典日志。
- 同一 Part 的派生波形重建必须去抖并丢弃过期代次。连续编辑不能为每次鼠标操作都排队一个最终会串行执行的完整 WAV 重建；当前 `RequestRebuild` 在进入 Part 重建锁前等待短暂稳定期并核对最新 generation。
- 截至 2026-08-12，原生播放线、候选轨迹、数值提示、选择生命周期和输入框已按 6.13.0.1 原生代码接入；连续操作卡死已针对视觉树反复重挂、重复刷新和波形重建任务堆积修正，并通过 Debug/Release 构建，尚待用户再次做宿主内连续操作确认。

## ACE 仿生参数区（力度/气声/张力/假声）

四个仿 ACE Studio 的连续参数接在原生参数区。实现在 `VOCALOIDPatcher/VOCALOIDPatcher/AceParameters/`，补丁在 `Patch/Patches/AceParameterPatch.cs` 与共享的 `CustomParameterPatch.cs`，设计文档见 `docs/ace-parameters-integration.md`。

- **不要为四个参数新增覆盖层**。做法是用越界 `ControlParameterTypeEnum`（4CC）作参数区条目，再通过 Harmony 覆盖 `MusicalEditorViewModel.CtrlParamTypeToVSMControllerType` 映射到原生 VSM 控制器载体 `VSMControllerType`（当前 `WeightHmm`/`S5Air`/`XSynth`/`Expression`；伪造的 VSM 值会被原生以 `InvalidControllerType` 拒绝）。原生 `ParameterView` 因此可以完整复用折线绘制、Behavior、Undo/Redo。
- **载体不等于惰性存储**：载体是引擎可见的原生控制器。离线渲染探针（`voicebank/tools/evec_render_harness --ace-carrier-audibility-probe`，覆盖 V4X/V5 中文/V6 天依）已证明 `DynamicsHmm`、`PitchBendHmm` 会被渲染消费，把 ACE 曲线写进去会额外改变响度/音高；且 ACE 中性 50 不等于这两种控制器的引擎默认值，所以曲线回到 50 后仍会持续影响后续音符（2026-10-20 用户报告的“张力影响后边几个音符”）。`WeightHmm`/`S5Air`/`XSynth`/`Expression` 在三种声库的探针中逐字节中性，可作载体。**不要在没有重跑该探针的情况下换回或新增载体**；若使用原生参数下拉里也存在的类型（`S5Air`/`XSynth`/`Expression`），用户编辑同名原生参数会与 ACE 曲线共享控制器数据。
- 载体控制器必须通过 `.vpr` 内自定义归档 `VOCALOIDPatcher/ace-parameters.json` 持久化；加载时以归档为唯一来源清空并重建载体控制器，再 `Commit(false)` 作为无历史基线，避免第一次 Undo 删除恢复数据。归档按参数种类存点、不记录载体类型，因此换载体后旧工程会自动迁移到新载体（旧 HMM 载体曲线本来就不会写入原生 `sequence.json`）。
- 自定义类型必须在 `GetMinControllerValue/GetMaxControllerValue/GetDefaultControllerValue` 补丁里返回 0/100/50。参数选择沿用 `UserSettings.ControlParameterType`；`AceParameterComboInitPatch` 负责跳过原生 `SelectedIndex=(int)自定义值` 的越界写法。
- 2026-10-20 起发声使用 `AceAcousticHost` 本地 PCM 后端，见 `docs/ace-acoustic-backend.md`：力度为独立增益、正向气声按整帧有声能量合成塑形气流噪声、负向气声衰减分带噪声、张力使用 LF 源谱比、假声调整源谐波形态。气声分析频带不能作为正向合成的唯一能量预算，否则暗音色拉满仍几乎无效；重叠帧的噪声须按绝对采样位置保持一致。`AceParameterMappingService` 只负责归档与生命周期；**不要恢复成向 Dynamics/Breathiness/Brightness/Character 派生写回的映射**。假声仍是声学近似，不代表录制假声采样或 ACE 云端模型。
- 中性曲线必须精确透传；后台只使用 Dispatcher 捕获的不可变曲线，保留取消、去抖、generation 和单工作限流。渲染开始使缓存失效，渲染完成重建；按 BVL→音素音量→过渡平滑→ACE 顺序组合，导出必须等待处理完成。旧版已保存的原生映射曲线不得擅自删除。
- 原生命令里遍历 `Enum.GetValues(ControlParameterTypeEnum)` 的入口（选中判断、删除、复制、粘贴、Duplicate、Reset）不会包含自定义 ID；已用定点补丁补齐。纯参数剪贴板复制粘贴可用，音符+参数混合粘贴不复制 ACE 曲线（已知限制）。
- 共享参数头补丁（列表同步、名称、范围）由 `AceParameterRegistry` 统一枚举 BVL/REG/ACE；新增自定义参数类型必须在该 registry 注册，否则会从下拉列表或开关逻辑中遗漏。
- 改载体类型或声学后端后重跑离线载体探针（`voicebank/tools/evec_render_harness --ace-carrier-audibility-probe`）、`AceAcousticOfflineTests`、`AceParameterOfflineTests` 和现有 BVL/REG 回归；宿主探针（`VOCALOID_ACE_PROBE=1`）和宿主交互验收仍须在用户授权的 Editor 中完成。2026-10-19 的 5/5 宿主验收针对旧映射版，新 PCM 后端尚待宿主验收；AI 音轨目前不提供这四个参数。

## SV 波形样式

SV 编辑器样式的音符波形实现在 `VOCALOIDPatcher/VOCALOIDPatcher/Patch/Patches/WaveformPatch.cs`：

- 波形按渲染帧重排到音符组下方，并在音高切换处绘制淡出残影。
- 音素覆盖范围必须使用 `WIVSMNote.GetPhonemePositions()` 的真实边界；该列表通常包含“音素数 + 1”个位置。通过 `WIVSMNote.GetAbsPositionFromNoteBaseTick()` 转为绝对时间后，再用 `MusicalEditorViewModel.CalcTickToViewPosition()` 转换为画布坐标。不要按音符长度平均切分音素。
- 音素标签使用白色；音素起止边界和覆盖线使用 `LightSkyBlue`，线宽为 `0.5`。
- 编辑器会把渲染波形拆成 512 像素宽的多个 `UIRenderedWave`。跨分块的音素范围只能在真实起止位置绘制竖线，不能在分块边缘制造伪边界；标签中心不在当前分块时也不要重复绘制。
- 音素数据、音符或渲染分数不可用时应安全跳过并保留原生波形回退，不能让绘制异常影响编辑器。

## 波形保留与渐进更新

波形生命周期和重新渲染过渡实现在 `WaveformSnapshotPatch.cs`，PCM 缓存补丁在 `RenderedWaveCachePatch.cs`，分块刷新节流在 `RendererPreviewThrottlePatch.cs`。维护这些代码时应保留以下已经核实的约束：

- `FastCanvas.ClearElement()` 会清空 `VirtualChildren` 和 `Children`，但不会清空 `Background`。旧波形只能以冻结的 `DrawingBrush` 快照暂存在背景层；把保留层放回 `Children` 会在下一次重画时一起被删除。
- 稳定快照应“数据常驻、背景按需显示”。无渲染任务时，正常 `UIRenderedWave` 子元素成功挂载后必须隐藏快照背景，否则首次第二次重画会叠成双重波形，水平或垂直缩放期间也会产生旧坐标残影。
- `PianorollView.UpdateHorizontalOrVerticalZoomed(MusicalEditorViewModel)` 会同步重建各画布并调用 `UpdateViewport()`。非渲染状态下的缩放应走 `WaveformSnapshotZoomPatch` 的无快照背景路径，同时仍可更新内存中的稳定快照。
- `PianorollView.OnRendererStarted` 会先调用 `DrawRenderedWaveCanvas()`；对应的 `MusicalEditorViewModel.OnRendererStarted` 是先通知订阅者、再移除旧的临时音源。开始回调中若旧波形子元素成功重新挂载，可以隐藏背景；若没有挂载成功，必须继续显示背景，不能重新引入消失。
- `OnRendererBlockRendered` 在通知钢琴窗前已经把新的 `AudioBufferList` 放进 `audioSourceDictionary`，但此时屏幕波形尚未改变。只有真正通过 `RendererPreviewThrottlePatch` 的事件才能记录待提交进度，并且必须等新挂载的 `UIRenderedWave` 成功完成 `OnRender` 时才能推进遮罩；被节流掉或尚未实际绘制的事件不能先清除旧背景。
- `OnRendererCompleted` 会先移除分块音源，再尝试读取最终波形文件。最终文件仍被占用或尚未可读时，应保留“旧背景 + 已完成分块”的合成快照，等缓存加载成功并主动刷新后再替换，不能在完成事件到来时直接清背景。
- 渲染通常复用相同的 `WaveFilePath`。只按“Part + 路径”缓存会返回旧 PCM；必须在 `MusicalEditorViewModel.OnRendererStarted` 时通过 `RenderedWaveCacheRenderStartedPatch` 使该 Part 的缓存失效。
- 删除音符后的空白区域依靠渲染进度遮罩真正裁掉旧快照；不能仅在旧波形上覆盖一层颜色。分块进度必须直接采用当前重画事件的 `VSMRendererProgress` 区间，不做定时插值或旧波形亮带；`BlockRenderingEnabled == false` 时 `SecondEnd` 只是总体百分比，不能映射为横向渲染位置。
- 删除 Part 内最后一个音符时不保证还会收到有效的分块或完成事件。`NumNotes == 0` 必须在 `DrawRenderedWaveCanvas` 路径中同步清除稳定快照、波形子元素和该 Part 的 PCM 缓存，并跳过 `InsertRenderedWave`，不能继续等待渲染回调。

排查症状时可优先按以下对应关系判断：

- 修改后波形立刻消失，滚动后才出现：通常是波形子元素被清空但没有稳定背景，随后由 `UpdateViewport()` 重新挂载。
- 渲染完成后仍显示旧波形：优先检查同路径 PCM 缓存是否在渲染开始时失效。
- 缩放过程中出现残影、缩放结束消失：快照背景和新坐标下的子波形同时可见。
- 首次第二次渲染变粗或重影、之后不继续累加：稳定背景在正常子波形挂载后没有隐藏。

截至 2026-08-10，用户已经在 VOCALOID Editor 中确认“首次波形在重新渲染和操作期间不再消失”。渐进更新、删除区域清除、缓存失效已经实现；之后报告的缩放残影和首次二次重影已通过按需隐藏背景与缩放专用补丁修正，并通过 Debug/Release 构建，但该最后一轮修正尚待用户再次做宿主内确认。

## Rust 播放时钟

- 原生实现位于 `native/playback-clock/src/lib.rs`，托管加载和 ABI 封装位于 `Utils/Audio/NativePlaybackClock.cs`，使用者主要是 `SmoothPlayheadPatch.cs` 和 `PlaybackLatencyCalibrator.cs`。
- 原生 DLL 不存在、加载失败或 ABI 不匹配时必须安全回退到托管时钟，不能影响编辑器播放。
- 修改导出结构或函数签名时同步更新 Rust 与 C# 两侧，并递增 ABI 版本；结构体布局、调用约定和数值边界必须保持一致。
- Rust 标准库分发声明已经记录在 `THIRD-PARTY-NOTICES.txt`；更改 Rust 依赖或许可证时同步复核 `Cargo.lock`、项目许可证和第三方声明。
- 当前 Rust 测试覆盖时钟单调投影、回跳重同步、反馈过期、播放速率边界和相关性延迟检测。修改时钟或相关算法后应运行全部 Rust 测试，并再构建整个 Release 解决方案。

## 翻译与用户可见文本

补丁自有翻译键以 `VOCALOIDPatcher` 开头。新增或修改用户可见文本时：

1. 通过 `TranslationManager.Tr(...)` 使用稳定、语义明确的键，不在代码中新增可翻译的硬编码文本。
2. 同步检查 `translations/English.xml`、`中文 (简体).xml`、`中文 (繁體).xml` 和 `日本語.xml`；至少保证四个文件的键集合一致。
3. 若处理编辑器自身的硬编码字符串，同步维护 `HardcodedPropertyMap.xml`。
4. 可运行：

```powershell
dotnet run --project ResourceTranslationComparer/ResourceTranslationComparer.csproj
```

该工具默认读取已安装编辑器的 `VOCALOID6.dll`。`--write-map` 会生成 `HardcodedPropertyMap.generated.xml`，它是供人工审阅的候选结果；不要未经检查直接覆盖正式映射。

## 配置、兼容性与安全

- 用户配置位于 `%APPDATA%/VOCALOIDPatcher/config.json`；外置运行数据位于编辑器目录下的 `VOCALOIDPatcher/`。不要在开发或测试中删除真实用户配置。
- 保持已有配置键和序列化格式向后兼容。新增设置应提供安全默认值，并检查设置窗口、运行时刷新和翻译键是否完整。
- 格式转换会处理不可信的外部工程文件。解析器必须校验边界、长度、枚举值及空值，避免无限分配、路径穿越或覆盖源文件。
- 不记录工程内容、歌词、文件路径或其它用户数据，除非它们是定位错误所必需的信息；错误日志应尽量简洁。

## 提交前检查

- 查看 `git diff`，确保没有无关格式化、生成物、机器专用路径或专有文件。
- 构建受影响项目，并如实记录未执行的运行时验证。
- 改动补丁时检查失败回退、设置开关和多版本兼容性。
- 改动用户可见文本时检查四份翻译及硬编码映射。
- 改动第三方移植区域或依赖时检查许可证和 notices。
