# 单轨拆轨／拼接 MCP

2026-10-22。这里的拆轨指“长音截取、辅元拼接、独立延音”：前台主旋律不拆轨，后台私有发声组生成并拼合。接口直接接入现有 `VoiceCompHost`，不要求打开 Emotion 画布或源音面板。

## 工具

| 工具 | 用途 |
|---|---|
| `v6_query_voice_comp` | 读配方、旁路、合成代次与错误、输出切片、音素边界、缓存源音时长和波形峰值 |
| `v6_edit_voice_comp` | 三个快捷动作、多选、完整配方／单一来源替换、同声库音符快照、旁路和重置 |
| `v6_preview_voice_comp` | 源音单听、当前拼接结果、整句原生播放及停止 |

现有 `v6_query_timing(kind=comp)`、`v6_edit_timing(comp.set/reset)` 继续可用；新工具补齐快捷动作和工作流。`v6_get_catalog.voice_comp` 返回工具和操作集合。

所有调用使用 `project_id/expected_revision`；修改及试听还使用 `client_request_id` 和已有单写租约。`edit` 是具体对象 schema，未知／重复字段和错误数值不会被默默忽略。试操作使用 `dry_run=true`；实际提交使用新的请求 ID，重试同一次提交复用原 ID。

## 查询与批量选择

提供 `track_index/part_index`。可用 `note_indices` 明确选择最多 128 个不同音符，也可省略，按 `offset/limit` 分页查询，默认 50、最大 100。`include_sources=true` 返回已经完成的源音数据，每份来源最多 128 个波形峰值，不触发合成，不暴露内部 WAV 路径。

返回的 `state` 区分 `Dirty/Queued/Running/Status/Error/Generation`；每个音符带完整配方、实际输出 `spans` 和缓存源音的音素边界。切片和边界单位是采样点，包含 Part presend。缓存不保证已对应当前参数，使用前检查 `ready`。

## 编辑操作

共同字段为 `edit.op` 和 `edit.note_indices`。所有音符必须属于指定 Part。整批先计算并验证，再调用一次共享历史提交；最后一个目标无效时，前面的目标也不会改变。

| 操作 | 参数与语义 |
|---|---|
| `long_slice` | 可选 `length_ms/trim/offset_ms`，默认源长度为 `max(音符长度×2, 1000 ms)`；已有辅元拼接时编辑韵部源，保留辅音 |
| `phoneme_splice` | 创建辅音、韵部来源；可选 `seam_ms`，默认 5 ms |
| `independent_sustain` | 可选 `length_ms/end_offset_ms/fade_ms`；length 是真实来源长度，end_offset 是输出终点，两者独立 |
| `replace` | `recipe` 完整替换，结构与 timing 的 `comp.set` 一致 |
| `replace_source` | `source_role` 和完整 `source`，替换 body／consonant／vowel 中已存在的一份来源 |
| `copy_source` | `source_role` 和 `donor={track_index,part_index,note_index}`，保存 donor 歌词、音素、EVEC、VEL 及源内表现快照 |
| `bypass` | `bypass=true/false`，不删除配方 |
| `reset` | 删除选中音符配方 |

`trim` 为 `keep_head`（去尾）、`keep_tail`（去头）、`middle`（取中段）。源长度范围 0–60000 ms，0 为自动；offset 为 ±60000 ms，seam／fade 为 0–30 ms，输出 end_offset 为 ±1000 ms。材料可用性及最终声音仍由实际合成检查，写入成功后要继续查询合成状态。

来源快照复用界面“取用音符”的同一个捕获方法：目标旋律、音高与主轨数据不变，来源长度、取用位置和上下文保留原配方值；快照不保留 donor 指针或长期依赖。同声库传统采样才可取用，AI、双声库混合和跨声库来源会拒绝。

例如批量独立延音：

```json
{
  "project_id": "当前工程 ID",
  "expected_revision": 123,
  "client_request_id": "sustain-preview-001",
  "track_index": 0,
  "part_index": 1,
  "dry_run": true,
  "edit": {
    "op": "independent_sustain",
    "note_indices": [16, 18],
    "length_ms": 1800,
    "end_offset_ms": 0,
    "fade_ms": 5
  }
}
```

用 `replace_source` 精调歌词、音素、语境、源内表现、裁切或入韵点：先查询完整 source，仅修改需要的字段再替换，避免覆盖其它设置。完整 `recipe/source` 字段见 [timing-mcp.md](timing-mcp.md)。

## 试听

`action=source/result` 提供 `track_index/part_index/note_index`。源音另选 `source_role=body/consonant/vowel/isolated`；独立延音使用 isolated。返回可取消的 `job_id`，通过 `v6_job` 取结果。音频在编辑器所在电脑播放，`play=false` 只生成和检查预览片段，不出声。预览不写入工程，不产生撤销记录，不接受任意文件路径。

只有当前完成、未过期的合成可以试听。片段制作期间再次检查工程修订和合成代次，取消或发生修改的结果不能开始播放。试听片段最长 120 秒；源音单听按缓存源音实际音素范围裁取，结果按目标输出 spans 裁取，并保留短前后余量。临时文件在停止、替换预览或退出时清理。

`action=phrase` 从所属 Part 起始附近调用原生播放器；`action=stop` 停止本工具的试听。整句播放和通常的播放／导出仍共用原生 transport 和 mixdown。更新失败保留配方及既有成品，但接口会拒绝把旧成品当作当前预览。

## 验证与部署

结构化 schema、stdio 调用、模型与全套回归通过。真实 VSM/DSE 隔离验收覆盖 timing 原有 14 组以及拼接查询、三个动作、批量撤销重做、来源快照、旁路、错误目标原子拒绝、试听契约和过期音频拒绝；这不代替真实 Editor 的整句听感验收。主补丁与 Companion 必须同时更新；已打开的 MCP 会话在重连后刷新工具列表。
