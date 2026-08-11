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

## 开发验证

```powershell
dotnet test VOCALOIDPatcher.McpTests\VOCALOIDPatcher.McpTests.csproj -c Debug
dotnet build VOCALOIDPatcher.sln -c Debug
dotnet publish VOCALOIDPatcher.McpServer\VOCALOIDPatcher.McpServer.csproj `
  -c Release -f net8.0 -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:PublishTrimmed=false
```

集成测试会用假的 V6 Bridge 完成真实的 stdio 和 Streamable HTTP 初始化、工具枚举、调用及 HTTP 认证失败验证，不会启动或关闭 VOCALOID Editor。
