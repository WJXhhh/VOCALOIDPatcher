# VOCALOID Patcher

[<img src="./res/lang_en.svg" alt="Read this in English" height="28">](./README_EN.MD)
[<img src="./res/lang_ja.svg" alt="日本語で読む" height="28">](./README_JA.MD)

> [!IMPORTANT]
> 本仓库是 [`IzumiiKonata/VOCALOIDPatcher`](https://github.com/IzumiiKonata/VOCALOIDPatcher) 的功能扩展分支，现已包含独立的原生引擎补丁、参数、播放与 MCP 功能。功能、设置和发行包组成均已与上游不同，请以本文档为准，不要混装两个仓库的二进制文件。

VOCALOID Patcher 是一个直接加载进 `VOCALOID 6 Editor` 的功能补丁。本分支在上游的多语言界面、钢琴窗工具、批处理和项目格式转换基础上，继续扩展了声库兼容、逐音符参数、波形渲染、播放同步、性能优化和本地自动化控制。

- 补丁版本：`2.3.1`（本分支持续开发）
- 翻译条目版本：`6.13.1.1`
- 当前开发环境：`6.13.1.1`；新增的原生补丁主要面向 `6.13.x`
- 问题反馈：[本仓库 Issues](https://github.com/WJXhhh/VOCALOIDPatcher/issues)

部分功能依赖 VOCALOID 内部实现。补丁在找不到目标或原生组件不可用时会尽量安全回退，但旧版本并未完整验证。反馈问题时请附上编辑器版本、启用的相关设置、调试日志和复现步骤；格式转换问题最好再提供一份能复现问题的精简工程。

## 安装

1. 从[本分支 Releases](https://github.com/WJXhhh/VOCALOIDPatcher/releases) 下载最新版。
2. 本分支只提供 `.NET 8` 构建，不再提供上游旧版的 `_NET6` 构建；VOCALOID 6 `6.3.0` 及更早版本不受支持。
3. 将压缩包中的文件 (`README.txt` 除外) 复制到 `C:\Program Files\VOCALOID6\Editor` 并覆盖原文件。
4. 启动编辑器并打开任意工程。菜单栏出现 `VOCALOID Patcher` 即表示加载成功。

* 如果编辑器不在默认目录，请将文件复制到实际的 Editor 目录。切换上游版与本分支时，请使用同一个发行包中的完整文件，不要只替换其中一部分。

![VOCALOID Patcher Menu](./res/screenshot.png)

## 功能

### 本分支相对上游的主要变化

- **独立呼吸音量 (BVL)**：增加原生风格的参数面板，可在 `0–127` 范围内单独调节每个已生成的自动呼吸音。数据随工程保存，支持复制、撤销和重做；它不会额外创建呼吸音。
- **音区偏移 (REG)**：为 AI 与传统声库增加 `-24–24` 的逐音符音区参数，只改变合成所用的声源音区，不改变目标音高。数据同样随工程保存并进入编辑历史。
- **中文歌词与扩展拼音**：结合词组上下文处理多音字，使用 V6 原生中文 G2PA 生成音素，并支持自由组合的拼音声母/韵母、独立音素以及不区分大小写的 `Asp`、`Sil`。
- **AI 声库兼容扩展**：可让 AI 声库使用中、英、日、韩、西五种语言，并允许选择任意 AI 声库作为 VOCALO CHANGER 目标。相关功能可在设置中单独关闭。
- **波形与播放增强**：SV 编辑器样式会按真实音素边界标注音符波形；重新渲染时保留稳定波形并按实际进度替换。平滑播放指示条使用原生播放时钟，并可按 DirectSound 游标或 ASIO 驱动延迟自动校准；原生组件不可用时会回退到托管实现。
- **载入和编辑性能优化**：包括并行预加载 DSE、延后初始钢琴窗/参数面板/检查器刷新、可视区域虚拟化、合成预览节流、PCM 与波形缓存管理等，可在“性能”设置中分别控制。
- **DSE 运行时兼容补丁**：可在内存中调整 DSE 授权检查。此功能仅用于已合法持有的软件和声库环境，不授予任何额外许可或使用权。
- **本地 MCP 控制**：可让 Codex、Claude Desktop、Cursor 等客户端读取和编辑工程、控制播放、转换工程及执行混缩，并提供本地 Token、目录白名单、写入租约和危险操作确认。

这些扩展大多可以在 `VOCALOID Patcher 设置` 中独立开关。若当前编辑器版本不支持某个原生补丁，对应功能会隐藏、禁用或保留原始编辑器行为。

### 界面翻译

编辑器资源和部分硬编码文本都可以翻译。译文保存在外置 XML 文件中。

### 钢琴窗

- 在当前钢琴窗中显示其它轨道的音符，并可跳过静音轨道。
- 在音符上显示 `C4`、`F#3` 这样的音高名。
- 圆角音符、歌词垂直居中和始终显示波形；可选带真实音素边界的 SV 编辑器样式。
- 重新渲染时保留旧波形，并随实际渲染进度逐步更新，减少波形闪烁或短暂消失。
- 平滑播放指示条，并可根据 DirectSound 或 ASIO 的实际输出延迟自动校准。
- 在右下角显示当前声库封面；每个声库都可以单独设置立绘、大小和透明度。
- 立绘支持静态图片、`GIF` 和 `MP4` 等视频。
- 为触控板补充钢琴窗横向滚动。

![Show notes from other tracks](./res/show_other_notes.png)
![Show note pitch](./res/show_note_pitch.png)
![Rounded notes](./res/rounded_notes.png)
![Centered lyrics](./res/centered_notes_b.png)
![Always show waveform](./res/always_show_wave.png)

### 批处理工具

- 批量替换选中音符的歌词。
- 按当前网格量化音符时值。
- 按调式生成上、下三度至八度的和声，可写入现有轨道或新轨道。
- 钢琴窗右键菜单提供八分音符、十六分音符的 Swing 和 Shuffle 预设。

### 项目格式转换

除了编辑器原生支持的格式，补丁还可以直接导入和导出：

`USTX`、`UST`、`SVP`、`S5P`、`VSQ`、`VSQX`、`VPR`、`CCS`、`TSSln`、`ACEP`、`VVProj`、`PPSF`、`DV`、`TLPX`、`TLP`、`SVIP`、`DS`、`AISP`、`Y77`、`VOG`、`MusicXML`、`OpenSVIP JSON` 和 `UFData`。

也可以将歌词导出为 `LRC`、`SRT` 或 `ASS`。

这部分代码移植自 [`LibreSVIP`](https://github.com/SoulMelody/LibreSVIP)

导入含有中文字符的工程时，也可以自动将中文字符转换为拼音。

不同编辑器的数据模型并不完全相同，转换后仍建议检查音高曲线、音素、换气和伴奏位置之类的东西。PPSF 目前支持新版 ZIP/JSON 工程，不支持旧版二进制 PPSF。

### MCP 控制

可选的本地 MCP Companion 允许 Codex、Claude Desktop、Cursor 等 AI 工具读取工程，并以可撤销事务编辑轨道、Part、音符、歌词、扩展参数、音频 Part 和混音效果，也可控制播放、选择对象、转换工程和执行混缩。MCP 默认关闭；连接、文件白名单、写租约和 HTTP Token 均在设置窗口的 MCP 分类中管理。

架构、客户端配置、工具列表和安全规则见 [VOCALOID 6 MCP 文档](./docs/mcp.md)。

### 其它

- 按设定间隔自动保存当前工程。
- 在编辑器中显示实时频谱；使用 ASIO 时频谱可能无法正常工作。

## 调试

启动 VOCALOID 6 时按住左 `Shift`，补丁会额外打开一个控制台并输出调试日志。反馈启动失败、转换异常或兼容性问题时，请一并附上相关日志。

## 开发

需要在默认目录 `C:\Program Files\VOCALOID6\Editor` 安装一份较新的 VOCALOID 6。主项目只构建 `net8.0-windows`，普通开发验证可构建整个解决方案：

```powershell
dotnet build VOCALOIDPatcher.sln -c Debug
```

Release 构建会同时编译 `native/playback-clock` 下的 Rust 播放时钟，并将托管依赖合并到：

```text
VOCALOIDPatcher\bin\Release\net8.0-windows\out\Microsoft.Xaml.Behaviors.dll
```

`out` 目录中的 DLL 才是包含依赖的可部署主程序集。生成包含原生播放时钟、MCP Companion、翻译和第三方声明的完整发行包时运行：

```powershell
./scripts/build-release.ps1 -Build
```

`voicebank/` 和 `voicebank_doc/` 保存的是声库格式研究工具及记录，不是发行包中的用户功能。开发时可以用 `mklink` 把合并后的 DLL 链接到编辑器目录，省去每次构建后手动复制。

### 更新翻译

运行 `ResourceTranslationComparer` 可以检查：

- 新增但尚未翻译的资源条目；
- 编辑器没有实际使用的资源；
- 需要加入 `HardcodedPropertyMap.xml` 的硬编码文本；
- 多余或已经失效的翻译键。

使用 `--write-map` 会另外生成整理后的 `HardcodedPropertyMap.generated.xml`。
