# V6 MCP 阶段 3：Patcher 扩展参数

阶段 3 使用现有通用工具暴露 BVL 与 Register Shift，不新增专用 Tool，也不会把它们混入原生 `VSMControllerType`。

## 能力与 Schema

调用 `v6_get_catalog`，读取 `extension_parameters` 与 `capability_status`：

- `patcher.bvl`：按音符整数，范围 0～127，默认/清除值 127；持久化于 VPR 的 Patcher BVL 条目。
- `patcher.register_shift`：按音符整数，范围 -12～12 半音，默认/清除值 0；持久化于 VPR 的 Register Shift 条目。

capability 的 `implemented` 表示协议与适配器已实现；在完成对应 V6 宿主矩阵前，`host_verified` 保持 `false`。设置被关闭或本机不支持 DSE 原生挂钩时，availability 为 `temporarily_unavailable` 并带原因。

## 读取

```json
{
  "kind": "extension_parameters",
  "filter": {
    "parameter_id": "patcher.bvl",
    "track_index": 0,
    "part_index": 0
  }
}
```

响应逐音符返回稳定 `reference`、`source: patcher`、值、是否为默认值和缓存/渲染支持状态。可再用 `note_index` 缩小到单个音符。

## dry-run 与批量写入

以下请求先纯校验引用、范围与所有项目，再一次性写入一个自定义历史项：

```json
{
  "project_id": "<state 中的 project_id>",
  "expected_revision": 12,
  "dry_run": true,
  "operations": [
    { "domain": "extension_parameters", "op": "set", "parameter_id": "patcher.bvl", "track_index": 0, "part_index": 0, "note_index": 0, "value": 96 },
    { "domain": "extension_parameters", "op": "set", "parameter_id": "patcher.register_shift", "track_index": 0, "part_index": 0, "note_index": 0, "value": -3 }
  ]
}
```

确认 `valid: true` 后去掉 `dry_run`（并持有写租约）执行。`op: clear` 把值恢复到该参数的默认值。扩展参数批次不能与原生域 operation 混在同一请求中，因为两者使用不同的原子历史协调器；这种请求会在任何写入前失败。

## 事件与等待

写入和 UI 修改发布 `extension_parameter_changed`；昂贵派生工作排队时发布 `extension_parameter_rebuild_requested`。只有最新 generation 完成才发布 `extension_parameter_rebuild_completed`，可用 `v6_wait_event`：

```json
{
  "after_event_id": 100,
  "types": ["extension_parameter_rebuild_completed"],
  "timeout_ms": 30000
}
```

事件仅包含参数 ID、generation、revision 和安全摘要，不包含歌词、工程内容或 Yamaha 对象。

## 撤销、重做与验证边界

一次批量调用通过 `CustomParameterHistoryCoordinator` 形成一个撤销项；`v6_history` 的 undo/redo 沿现有补丁历史协调路径恢复两个参数，并重新合并 UI 刷新及去抖重建。

自动化测试覆盖注册表、范围、默认/清除与纯校验。真实 V6 中仍须逐项确认：写后 UI/读回、undo/redo、BVL 最新缓存 generation、Register Shift 原生/回退诊断、连续数十次写入无任务堆积，以及 Standard/AI Part 差异。在完成该矩阵前 capability 不宣称 `host_verified`。
