# VOCALOID 6 MCP

VOCALOID Patcher 的 MCP 功能允许 Codex、Claude Desktop、Cursor 等本机 AI 客户端读取和操作当前 VOCALOID 6 工程。MCP 默认关闭，且不会在 VST 插件模式下启动。

## 架构

- `VOCALOIDPatcher.McpServer.exe` 是进程外 Companion，使用官方 C# MCP SDK 1.4.1。
- `stdio` 由 MCP 客户端按需启动；可选的 Streamable HTTP 只监听 `127.0.0.1`，不提供旧 SSE 端点。
- Companion 通过当前 Windows 用户专用命名管道连接 V6 内的 Bridge。每次 V6 启动都会生成新的实例 ID、管道名和握手密钥。
- Yamaha/VSM 对象只在 V6 的 WPF Dispatcher 上即时取得和使用，不会缓存到后台线程。
- 一个实例可被多个客户端读取，但只有一个客户端能持有写租约。写租约空闲五分钟后释放，活动作业期间不会过期。

## 启用和连接

在 `VOCALOID Patcher → 设置 → MCP` 中打开“启用本地 MCP 控制”。设置页可以直接复制 stdio 或 HTTP 配置、管理文件白名单、轮换 HTTP Token，以及撤销写入权限。

stdio 配置的基本形态如下，实际路径请使用设置页复制的值：

```json
{
  "mcpServers": {
    "vocaloid6": {
      "command": "C:\\Program Files\\VOCALOID6\\Editor\\VOCALOIDPatcher\\mcp\\VOCALOIDPatcher.McpServer.exe",
      "args": ["--transport", "stdio"]
    }
  }
}
```

HTTP 默认端点为 `http://127.0.0.1:39266/mcp`。请求必须包含设置页生成的 `Authorization: Bearer …`；Host、Origin、请求大小和并发数也会在 Companion 中校验。

当只有一个启用 MCP 的 V6 实例时，Companion 会自动选择它。存在多个实例时，每次调用都应提供 `instance_id`，也可以在启动 Companion 时传入 `--instance <id>`。

## 工具

| 工具 | 用途 |
|---|---|
| `v6_session` | 查询实例权限，取得或释放写租约 |
| `v6_get_state` | 工程、播放、渲染、活动对象、修订号和能力 |
| `v6_get_catalog` | 声库、语言、参数类型和转换格式 |
| `v6_query_project` | 分页查询轨道、Part、音符、参数、速度、拍号和选区 |
| `v6_edit_structure` | 以单事务批量编辑轨道、Part、速度和拍号 |
| `v6_edit_notes` | 以单事务批量编辑音符、歌词、音素、表情和颤音 |
| `v6_g2pa_candidates` | 查询通用语言调度或指定语言/扩展词典的候选发音 |
| `v6_g2pa_apply` | 通过 G2PA 层提交歌词、音素、Syllables 或范围重算 |
| `v6_edit_parameters` | 控制点、直接音高、轨道音量/声像和主音量 |
| `v6_apply_operations` | 在一个原生 Transaction 中混合 Structure、Notes、Parameters 与 G2PA operation |
| `v6_wait_event` | 按单调 event ID 等待有界事件流，不占用 WPF Dispatcher |
| `v6_wait_for` | 语义化等待 revision、render idle 或 playback 状态 |
| `v6_select_view` | 活动对象、选区和时间定位 |
| `v6_transport` | 播放、停止、定位和循环 |
| `v6_history` | 撤销、重做及状态 |
| `v6_run_job` | 歌词、时值量化、Swing 和和声任务 |
| `v6_project_file` | 新建、打开、保存、另存和 MIDI 导出 |
| `v6_convert_project` | 通过现有 LibreSVIP 桥接导入或导出工程 |
| `v6_mixdown` | 主输出、轨道或 Part 混缩 |
| `v6_job` | 查询、列出或取消当前客户端的长任务 |

Resources：

- `vocaloid://instances`
- `vocaloid://instances/{id}/state`
- `vocaloid://instances/{id}/project/summary`
- `vocaloid://instances/{id}/selection`
- `vocaloid://instances/{id}/catalog`
- `vocaloid://instances/{id}/schema`

`catalog`/`schema` 同时返回 operation ID、所属领域、必需/可选字段、最低 Editor 版本、错误码，以及每项 capability 的 `implemented`、`host_verified`、`minimum_editor_version`、`availability` 和 `unavailable_reason`。旧的大类 capability 布尔字段继续保留。

## 写入协议

写入前先调用：

```json
{ "action": "acquire_write" }
```

修改工具必须携带最近一次读取所得的 `project_id` 和 `revision`，以及调用方生成的唯一 `client_request_id`。工程已被用户或其他客户端修改时会返回 `stale_project`，调用方应重新读取，而不是在旧引用上继续写入。同一个 `client_request_id` 的重试会返回缓存结果，不会重复执行事务。

批量音符示例：

```json
{
  "project_id": "…",
  "expected_revision": 12,
  "client_request_id": "9f6d…",
  "dry_run": false,
  "operations": [
    {
      "op": "add",
      "track_index": 0,
      "part_index": 0,
      "position": { "bar": 2, "beat": 1, "tick": 0 },
      "duration_tick": 480,
      "note_number": 60,
      "lyric": "あ"
    },
    {
      "op": "update",
      "track_index": 0,
      "part_index": 0,
      "note_index": 0,
      "lyric": "ら",
      "expression": { "accent": 64 },
      "vibrato_depth": 48
    }
  ]
}
```

每个批量编辑调用只产生一个 V6 `Transaction` 和一个撤销步骤。任何子操作失败都会回滚整个调用。使用 `dry_run: true` 可以先做引用、范围和操作类型验证；dry run 不要求用户授予写入权限。

稳定引用新增 `entity_id`，在当前 project generation 内不随 Track/Part/Note 的插入、移动和下标变化而改变。写入可以继续使用旧下标，也可以提供 `entity_id`、`track_entity_id`、`part_entity_id` 或 `note_entity_id`。工程替换后旧 ID 返回 `stale_project`/`invalid_reference`。创建 operation 可带 `temp_id`，同一 `v6_apply_operations` 中后续项用 `track_temp_id`、`part_temp_id`、`note_temp_id` 引用；`client_tag` 会原样回到逐项结果。

混合事务示例：

```json
{
  "project_id": "…",
  "expected_revision": 12,
  "client_request_id": "…",
  "operations": [
    { "domain": "structure", "op": "add_track", "type": "midi_ai", "temp_id": "t" },
    { "domain": "structure", "op": "add_part", "track_temp_id": "t", "duration_tick": 1920, "temp_id": "p" },
    { "domain": "notes", "op": "add", "track_temp_id": "t", "part_temp_id": "p", "duration_tick": 480, "note_number": 60, "lyric": "あ", "temp_id": "n" },
    { "domain": "parameters", "op": "add_controller", "track_temp_id": "t", "part_temp_id": "p", "parameter_type": "Dynamics", "value": 72 },
    { "domain": "g2pa", "action": "set_lyrics", "track_temp_id": "t", "part_temp_id": "p", "note_temp_id": "n", "lyrics": "ら" }
  ]
}
```

查询可提供 `projection`、`changed_since_revision` 和预算字段。Notes 过滤在 DTO 投影前支持 Track/Part、绝对或相对 tick、音高、语言、声库、选中状态及歌词/音素文本；参数查询的 `parameter_mode` 为 `raw`、`summary` 或 `buckets`。cursor 绑定 project ID 与 revision，并记录扫描位置；预算耗尽时返回可继续 cursor，单项/页面无法满足字节预算时返回 `query_too_large`。

事件只包含标准 ID、revision 和安全摘要，不包含完整歌词、音素或工程内容。典型调用先从 state 读取 `latest_event_id`，再调用 `v6_wait_event`；需要“revision 至少达到 N”“真实 render idle”或“播放进入指定状态”时使用 `v6_wait_for`。

时间位置可使用绝对 `absolute_tick`、Part 内的 `part_relative_tick`，或 `{ "bar": 1, "beat": 1, "tick": 0 }`。小节和拍从 1 开始；读取位置还会同时返回绝对 tick 和秒数。

## G2PA 调度

`v6_g2pa_candidates` 默认调用 Editor 自身的多语言调度。提供 `language_id` 时则直接选择对应 manager；此时可以同时指定 `use_extension_dictionary`。语言 ID 为 `JPN=0`、`ENG=1`、`KOR=2`、`ESP=3`、`CHS=4`。`is_ai` 不接受调用方覆盖，而是始终从目标音符取得，避免把传统音符伪装成 AI 音符调用语言插件。

```json
{
  "project_id": "…",
  "expected_revision": 12,
  "track_index": 0,
  "part_index": 0,
  "note_index": 0,
  "lyrics": "hello",
  "language_id": 1,
  "use_extension_dictionary": false
}
```

返回的每个候选都包含 `language_id`、`data_size`、拼接后的 `phonemes`，以及可原样提交给 `set_syllables` 的 `syllables` 数组：

```json
{
  "project_id": "…",
  "expected_revision": 12,
  "client_request_id": "…",
  "action": "set_syllables",
  "track_index": 0,
  "part_index": 0,
  "note_index": 0,
  "language_id": 1,
  "syllables": [
    { "syllable": "hel-", "phoneme": "h e" },
    { "syllable": "lo", "phoneme": "l @U" }
  ],
  "reset_phonemes": true
}
```

`v6_g2pa_apply` 的 `action` 可为 `set_lyrics`、`set_phonemes`、`set_syllables` 或 `reset`。它遵循与其它修改工具相同的写租约、工程修订、幂等请求 ID、事务和 dry-run 规则。`set_syllables` 返回 `next_note_index`，表示 native `length` 沿 `.Next` 推进后得到的排他上下文音符；没有后继音符时为 `null`。

## 文件与确认

文件操作只允许访问当前工程目录和设置页加入的目录。UNC、设备路径、NTFS 备用数据流、目录穿越以及通过符号链接或 junction 逃离白名单的路径会被拒绝。

以下操作始终由 V6 逐次确认：

- 删除轨道或 Part；
- 一次删除超过 32 个音符；
- 新建或打开工程以替换当前工程；
- 覆盖已有文件。

首次普通工程写入也会显示客户端名称、版本和传输方式；授权仅持续到本次 V6 退出。Tool Annotations 仅供 MCP 客户端展示，实际权限始终由 V6 Bridge 再次检查。

### Mixer 与效果器

`v6_query_project` 的 `mixer`、`effect_chains` 和 `effect_catalog` 查询以及
`v6_apply_operations` 的 `mixer_effects` domain 用于控制 Track 静态值和原生效果链。
`set_track_static` 接受 `volume`、`pan`、`mute` 和 `solo`。其中音量、声像及效果链
修改进入原生编辑历史；Mute/Solo 则沿用 V6 自身的非历史状态语义，通过
`MixerViewModel` 同步 Mixer UI 与播放引擎，查询会返回
`mute_solo_undoable: false`。需要恢复时应先读出旧值，再显式写回，不能依赖
`v6_history`。

### Audio Part

使用 `v6_query_project` 的 `kind: "audio_parts"` 读取 Audio Part。响应包含稳定 Part 引用、位置、Region、时长，以及不暴露完整路径的源文件 ID、原始名称、采样率、声道、秒数和结构化媒体诊断。白名单外的现有工程媒体仅报告 `media_outside_allowlist`，不会由 MCP 主动打开。

Audio Part 写入复用 `v6_edit_structure` 的写租约、`project_id`、`expected_revision`、`client_request_id`、原生事务和事件机制。当前 operation 为：

- `audio_create`：需要音频轨道 `track_index` 和 `source_path`，可提供位置、名称和 Region/时长；
- `audio_replace_source`：原子替换源媒体；
- `audio_move`：移动到绝对 tick，可同时移动到另一音频轨道；
- `audio_trim_region`：设置 `region_tick_begin`/`region_tick_end`；
- `audio_set_length`：保持 Region 起点并调整 `duration_tick`；
- `audio_normalize`：调用 V6 原生 Normalize renderer，并重建非 AI Voice Changer 的 pitch-shift 媒体；
- `audio_time_stretch`：以目标 `duration_tick` 调用 V6 原生 Time Stretch renderer，并重建非 AI Voice Changer 的 pitch-shift 媒体；
- `audio_delete`：删除 Audio Part，属于危险操作。

最小 dry-run 示例：

```json
{
  "project_id": "…",
  "expected_revision": 12,
  "client_request_id": "audio-check-1",
  "dry_run": true,
  "operations": [
    {
      "op": "audio_create",
      "track_index": 1,
      "source_path": "D:\\AllowedMedia\\guide.wav",
      "absolute_tick": 0
    }
  ]
}
```

dry-run 只用共享读打开解析 RIFF/WAVE 头，不打开写句柄、不调用 Yamaha setter、不创建 Part，也不触碰 V6 媒体缓存。当前仅接受可验证的 PCM 或 IEEE-float WAVE；丢失、不可访问、截断和不支持格式分别返回结构化错误。

V6 6.13 的 Normalize 与 Time Stretch renderer 本身同步执行；原生 UI 也先建立 `Transaction`，在其中调用 `OfflineProcessor.ApplyNormalize`/`ApplyTimeStretch`，完成后才提交，因此 MCP 对未启用 AI Voice Changer 的 Part 提供相同事务与 undo/redo 语义。dry-run 会验证媒体白名单、WAVE 头、目标时长和原生伸缩范围，但不会创建临时文件或调用 renderer。启用了 AI Voice Changer 的 Part 需要原生 `OfflineController` 异步重建派生媒体，无法加入当前同步统一事务，操作会明确返回 `unsupported`。

通过 `v6_apply_operations` 调用时使用 `{ "domain": "audio_parts", "op": "normalize", ... }` 或 `{ "domain": "audio_parts", "op": "time_stretch", "duration_tick": 3840, ... }`；通过旧的 `v6_edit_structure` 调用时 operation 名分别是 `audio_normalize` 和 `audio_time_stretch`。

V6 的 `SetAudioPartFade` 是播放引擎对所有 Audio Part 自动施加的固定防爆音淡变，并不是工程中可编辑的 fade 属性；可编辑的 Part 增益则属于 Part effect chain 的 `Gain` 效果器，而不是 Audio Part 属性。因此 `audio_fade`、`audio_gain` 仍保持 `unsupported`，不会伪造 setter；增益应通过效果链能力操作。

### 原生导入与工程生命周期

`v6_project_file` 除既有的 `new`、`open`、`save`、`save_as`、`export_midi` 外，还提供以下 V6 6.13 原生 action：

- `revert`：`options.dirty_action` 必须表达 `save`、`discard` 或 `cancel`；结果分别为 `saved_then_reverted`、`discarded_then_reverted` 和 `cancel`。成功替换文档后 project generation 会变化，并发布 `document_replaced`，旧 project ID、entity ID 和 cursor 随即失效。
- `import_project`：仅调用 `MainViewModel.ImportProjectFile` 导入 VPR、VSQX 或 PPSF；`options.import_tempo_time_signature` 控制是否同时导入速度和拍号。
- `import_midi`：调用 `MainViewModel.ImportMidiFile`；可设置 `track_type: ai|standard`、`encoding: shift_jis|utf8`、`absolute_tick` 和 `import_tempo_time_signature`。
- `import_tempo_time_signature`：调用 V6 的专项 MIDI 速度/拍号导入，不创建 MIDI Track。
- `import_audio`：调用 V6 的标准 WAVE 导入；单文件可用顶层 `path`，多文件用 `options.paths`，`options.placement` 可为 `one_track` 或 `different_tracks`。
- `recent`：只读返回最近工程。白名单内条目含可再次提交的路径；白名单外条目只返回安全 ID、文件名和 `accessible: false`。

原生导入示例：

```json
{
  "action": "import_project",
  "project_id": "…",
  "expected_revision": 21,
  "client_request_id": "native-import-1",
  "path": "D:\\AllowedProjects\\chorus.vsqx",
  "options": { "import_tempo_time_signature": true }
}
```

这些 action 通过 V6 自身的导入方法和内部 Transaction 工作，不经过 LibreSVIP。路径必须通过 V6 allowlist；dry-run 会验证路径、存在性、扩展名和选项，但不调用原生导入方法。Job 取消是协作式的：进入原生调用前取消会得到 `Cancelled`；V6 6.13 的同步导入没有中途取消入口，如果取消请求在原生调用期间到达而导入最终成功，状态为 `CompletedAfterCancel`，不会错误声称模型未改变。

## 开发验证

```powershell
dotnet test VOCALOIDPatcher.McpTests\VOCALOIDPatcher.McpTests.csproj -c Debug
dotnet build VOCALOIDPatcher.sln -c Debug
dotnet publish VOCALOIDPatcher.McpServer\VOCALOIDPatcher.McpServer.csproj `
  -c Release -f net8.0 -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:PublishTrimmed=false
```

集成测试会用假的 V6 Bridge 完成真实的 stdio 和 Streamable HTTP 初始化、工具枚举、调用及 HTTP 认证失败验证，不会启动或关闭 VOCALOID Editor。
