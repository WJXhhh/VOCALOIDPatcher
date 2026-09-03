# V6 MCP 阶段 7：视图、选区与 Transport

阶段 7 继续复用 `v6_select_view`、`v6_transport`、`v6_get_state` 和阶段 2 事件等待模型。所有已实现的选择、导航、面板和编辑工具操作都是 UI 状态操作：需要匹配当前 project/revision 以避免作用于错误文档，但不取得写租约、不增加 revision、不创建 undo 项。

## 状态与 capability

`v6_get_state.view` 返回当前编辑工具、参数类型、参数面板、Lower Zone、Mixer、Inspector/Media Browser 可见性、水平/垂直 zoom 和钢琴窗绝对 tick viewport。`v6_get_state.playback` 与 `v6_transport(action=status)` 返回播放、位置、循环/Start-End Marker 状态；`playback_rate_editable` 为 `false`。

catalog 中以下入口已按 V6 6.13 API 实现，但在宿主矩阵完成前 `host_verified=false`：selection、viewport、参数面板、Lower Zone、Inspector/Media Browser/Mixer、pause/resume、markers、grid step 和编辑工具。V6 6.13 的 `MainWindow` 为面板提供了公开的语义方法和 `Is...Shown` 读回属性；playback rate 在 `MainViewModel`、`AudioPlayer`、`UserSettings` 及音频引擎包装中都没有可确认入口，仍明确为 `unsupported`。

## 选择与导航

`request.mode` 支持 `replace`、`add`、`toggle`。可指定 Track、Part、音符索引，或在活动 MIDI Part 中使用 `note_range`：

```json
{
  "project_id": "<project>",
  "expected_revision": 10,
  "request": {
    "mode": "replace",
    "track_index": 0,
    "part_index": 0,
    "note_range": {
      "absolute_tick_begin": 1920,
      "absolute_tick_end": 3840,
      "pitch_min": 48,
      "pitch_max": 72
    },
    "viewport_absolute_tick": 1920,
    "horizontal_zoom": 0.5,
    "vertical_zoom": 0.4
  }
}
```

范围按音符与时间区间相交选择。响应同时返回 selection、view 和未改变的 revision。操作发布 `selection_changed`、`active_part_changed`、`view_changed`，可通过 `v6_wait_event` 等待。

V6 6.13 的 `Sequence.SetActivePartAndTrack` 返回值只表示活动轨道是否发生变化，不是“Part 激活成功”标志：目标已经 active，或在同一轨道内切换 Part 时都可能返回 `false`。MCP 因此先跳过已 active 的重复调用，其余情况调用后以 `ActivePart` 读回作为成功条件，不把该 change flag 误报为失败。

## 参数面板与编辑工具

同一 request 可设置 `parameter_panel_visible` 和 V6 `ControlParameterTypeEnum` 名称。`edit_tool` 支持：`arrow`、`pencil`、`line`、`scissors`、`pitch`、`vibrato`、`expression`、`timing`。这些调用使用 `EditorModeME.ChangeMode`，不合成鼠标输入。

`lower_zone` 支持 `hidden`、`musical`、`wave`、`mixer`、`empty`；`right_zone` 支持 `hidden`、`inspector`、`media_browser`。写入分别调用 `MainWindow.ShowLowerZone/HideLowerZone` 和 `ShowInspector/ShowMediaBrowser/HideRightZone`，读回包含 `lower_zone_visible`、`mixer_visible`、`inspector_visible` 和 `media_browser_visible`。这些纯 UI 操作不获取写租约、不增加 revision，也不创建 undo 项。

## Transport 与 Marker

`v6_transport` 支持 `play`、`pause`、`resume`、`stop`、`seek`、`grid_previous`、`grid_next`、`set_markers`、`set_loop`、`set_loop_enabled`、`set_start_mode`、`status`。`set_markers` 使用与 V6 Start/End Marker 相同的 `loop_begin_tick`/`loop_end_tick` 范围语义；grid step 使用当前 Musical Editor quantize，Off 时回退到四分音符。`set_start_mode` 接受 `song_position` 或 `begin_loop`，直接使用与 V6 原生命令相同的 `MainViewModel.StartMode`，并在状态中读回。状态变化发布 `transport_changed`。

## 宿主验证边界

自动化测试只验证 capability 开关与协议契约。真实 V6 仍需验证：三种选择模式及范围边界、UI 手动选择后的事件、viewport/zoom 读回、参数面板、Lower Zone 五种状态、Inspector/Media Browser 切换、八种编辑工具、播放中 pause/resume、循环 Marker、不同 quantize 的 grid step，以及每项操作前后 revision、CanUndo/CanRedo 和写租约均不变。
