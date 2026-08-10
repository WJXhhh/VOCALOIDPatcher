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

本机另有 VOCALOID 6.13.0.1 的反编译代码参考目录：

```text
E:\Users\Administrator\CLionProjects\v613
```

当补丁涉及 VOCALOID 内部类型、私有方法、音素时值或原生绘制流程时，可优先在该目录核对实现。该目录不属于本仓库，只能作为只读参考；不要修改、复制或提交其中的专有代码。

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
- `OnRendererBlockRendered` 在通知钢琴窗前已经把新的 `AudioBufferList` 放进 `audioSourceDictionary`。只有真正通过 `RendererPreviewThrottlePatch`、即将执行重画的分块事件才能推进遮罩；被节流掉的事件不能先清除旧背景。
- `OnRendererCompleted` 会先移除分块音源，再尝试读取最终波形文件。最终文件仍被占用或尚未可读时，应保留“旧背景 + 已完成分块”的合成快照，等缓存加载成功并主动刷新后再替换，不能在完成事件到来时直接清背景。
- 渲染通常复用相同的 `WaveFilePath`。只按“Part + 路径”缓存会返回旧 PCM；必须在 `MusicalEditorViewModel.OnRendererStarted` 时通过 `RenderedWaveCacheRenderStartedPatch` 使该 Part 的缓存失效。
- 删除音符后的空白区域依靠渲染进度遮罩真正裁掉旧快照；不能仅在旧波形上覆盖一层颜色。扫过效果保持为窄幅、低透明度的旧波形残影，不使用发光效果。

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
