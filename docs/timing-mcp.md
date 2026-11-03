# Timing MCP 接口

2026-10-22。通过三个独立工具接入现有 timing 数据模型；不依赖画布是否打开、是否选中音符或是否弹出精调框。Companion 与宿主补丁需要一起更新。

| 工具 | 功能 |
|---|---|
| `v6_query_timing` | 状态、设置、音符控制、原生音素位置、过渡布局、原生可达范围、Comp 配方和处理状态 |
| `v6_edit_timing` | 以下 14 种编辑／重置操作；一条请求为一条撤销记录 |
| `v6_timing_settings` | timing、音素音量、过渡、Comp 开关及全部过渡平滑设置 |

`v6_get_catalog.timing` 返回操作集合、查询种类、单位和限制。查询和写入均提供 `project_id`、`expected_revision`；写入还提供 `client_request_id`，实际写入沿用已有单写租约。`edit` 和 `settings` 是结构化对象，不接受包在字符串内的 JSON。未知字段、重复字段、互斥单位、错误索引及非有限数值会被拒绝。

## 查询

`kind` 支持：

- `status`：当前项目 timing 诊断、设置和能力。
- `settings`：全局偏好及契约。
- `note`：指定音符的系数、保存的音素偏移、当前缓存的受限信息、音素音量、Comp 配方和处理状态。
- `comp`：同一音符快照；包含配方、状态、错误、排队／运行标记和代次，不返回内部缓存文件路径。
- `phonemes`：原生重新计算的音素边界，单位为相对该音符起点的 tick；同时返回请求偏移／实际偏移不同时的 `limits`。
- `transitions`：整个 Part 的过渡布局，包含左右音素实例、stage／reverse／occurrence、原始／实际位置、音量、进度、覆盖方式、旧 onset 控制、共享端点及保护状态。
- `range`：指定 `transition_id`、`layout_fingerprint`、`endpoint`（`begin/split/end`），返回原生验证的 `reachable_frames`、最小／最大值和是否连续。不能只根据最小／最大值推断中间各帧可达。

除 `status/settings` 外，提供从零开始的 `track_index`、`part_index`；音符查询另提供 `note_index`。过渡列表使用 `offset/limit` 分页，默认 100、最大 200。端点中的 `minimum_candidate/maximum_candidate` 是几何候选范围；实际可达性应使用 `kind=range`。

原生音素、过渡与范围查询，以及 timing 原生编辑预演，以后台作业返回 `job_id`；使用已有 `v6_job(action=get)` 取得结果，可用 `v6_wait_event` 等待 `job_progress`，或 `v6_job(action=cancel)` 取消。一次宿主只接受一个 MCP 原生 timing 规划，忙时返回可重试的 `temporarily_unavailable`。普通快照与设置查询直接返回结果。

所有后台工作先在 Dispatcher 捕获不可变输入，完成后再次核对项目、timing 修订、设置和原生输入。音符／VEL／速度或设置在计算期间改变时，旧结果返回 `stale_project`。规划自身对临时原生工程的提交不增加当前项目修订号。

## 编辑

共同参数：`project_id`、`expected_revision`、`client_request_id`、`track_index`、`part_index`、`edit` 和可选 `dry_run`。每次新的逻辑操作使用新请求 ID；重试原操作复用 ID。**从 dry-run 改为实际写入时使用新的请求 ID**。

| `edit.op` | 字段与行为 |
|---|---|
| `boundary.set` | `note_index/boundary_index/offset_frames`，相对原生基线的绝对偏移 |
| `boundary.move` | `note_index/boundary_index/delta_frames`，从当前实际应用的位置相对移动 |
| `boundary.reset` | `note_index/boundary_index`，清除该物理边界的偏移及所有共享别名 |
| `note.reset` | `note_index`，重置该音符的系数与全部边界；关联边界同步清除，邻居的其它参数保留 |
| `coefficient.set/reset` | `note_index`；set 提供 0–1 的 `coefficient`；reset 恢复 VEL 的原生系数映射 |
| `phoneme_gain.set` | `note_index/phoneme_index/decibels`，范围 -60–12 dB；-60 为界面的静音值 |
| `phoneme_gain.reset` | `note_index`；可选 `phoneme_index`，省略时重置该音符全部音素音量 |
| `transition.set` | `layout_fingerprint/transition_id`，部分修改 `begin_frame/end_frame/decibels/phase/coverage/onset_frames` |
| `transition.reset` | 重置该过渡的端点、音量、进度、覆盖方式及旧 onset 控制；物理共享端点一起恢复 |
| `transition.reset_expression` | 仅清除进度与覆盖方式，保留时值和音量 |
| `transitions.reset` | `layout_fingerprint`，重置该 Part 的全部过渡控制 |
| `comp.set/reset` | `note_index`；set 提供完整 `comp` 配方，reset 删除配方 |

`transition.set/reset/reset_expression` 均要求刚查询取得的布局指纹与过渡 ID。ID 由布局及两侧真实音素实例共同生成，同名音素的不同过渡不会共用 ID；每次工程编辑后重新查询。`begin_frame/end_frame` 是查询返回的原生 Part 帧坐标，split 是受保护的音素锚点。完整边界关系和原生执行结果均通过后才提交；不通过修改 split 伪造辅音时长。

一帧为 `256/44100` 秒，约 5.805 ms，正值往右。原生过渡帧坐标含内部 3087 帧基准与 0.5 秒 presend；相对 Part 起点的秒数为 `(frame - 3087) * 256/44100 - 0.5`。音素接口采用相对原生边界的帧偏移，不能把过渡的绝对帧坐标当作音素偏移。

`phase` 范围 -1–1，只重新分配当前源范围内的进度，不自动启用完整来源；`coverage` 可为 `none/left/right/both/auto`，省略时保留现状，`none` 可与非零 phase 同时提交。需要完整来源时显式设置 `coverage`（`auto` 选择可用的一侧）。旧 `onset_frames` 为 -4096–0，仅支持有明确右侧音素的入场 Sil 过渡。过渡 gain 为 -60–12 dB，左右端点和音量等字段可以在一条精调请求中一起验证与提交。

超限的音素旧值继续按现有策略投影至最近合法边界，保存请求值，返回请求／应用差异。`boundary.move` 从应用值移动，避免从过时超限请求值继续累加后看似“拉不动”。共享边界按原生基线帧识别，不按相邻音符或屏幕像素猜测。已有过渡控制在音素编辑时会按完整 Part 验证并保留，与界面使用同一个预演／提交适配器。

例如，将第三段中某个音素边界往左两帧，先查询并确认当前音符索引，再调用：

```json
{
  "project_id": "当前工程 ID",
  "expected_revision": 123,
  "client_request_id": "preview-dan-boundary-001",
  "track_index": 0,
  "part_index": 1,
  "dry_run": true,
  "edit": {"op": "boundary.move", "note_index": 17, "boundary_index": 1, "delta_frames": -2}
}
```

取得作业结果并确认后，以当前修订号、新请求 ID 和 `dry_run=false` 实际提交。若要延长前一个音素，应修改两音共享的交界，而非误改后一个音内的辅音／元音交界。

## Comp 与设置

Comp 支持 `body/consonant/vowel/isolation/seam_ms/bypass`。两个拼接源 consonant/vowel 必须成对提供。每个源支持 `lyric/phonemes/length_ms/trim/offset_ms/vowel_index/trim_begin_ms/trim_end_ms`，以及完整的 `evec/context/expression` 快照；trim 为 `keep_head/keep_tail/middle`。isolation 支持 `after/end_offset_ms/fade_ms/source_length_ms`。与界面共用 `VoiceCompPlan.Validate` 和宿主可编辑性检查；无效或未绑定的旧配方不覆盖。`comp.set` 是完整配方替换，先读原配方再改所需字段即可保留其它参数。

`v6_timing_settings.settings` 为部分更新，包含：

| 字段 | 范围 |
|---|---|
| `phoneme_timing/phoneme_volume/transitions/voice_comp/smoothing/phase_alignment` | boolean |
| `volume_threshold_db` | 0.1–30 |
| `crossfade_ms` | 1–50 |
| `loudness_window_ms/analysis_range_ms` | 20–300 |
| `timing_offset_ms` | -50–50 |

设置属于全局偏好，不加入工程撤销；dry-run 不改设置。实际修改沿用设置窗口的刷新入口。读操作和所有 dry-run 不写控制值、不推历史、不停止当前渲染、不发布候选。实际控制提交沿用既有归档、历史与按 Part 去抖重建；Comp 的最终合成状态从 `kind=comp` 读取。播放、试听工程和导出沿用 `v6_transport/v6_mixdown`。

已撤回的“时长选样”保留旧工程数据兼容，不提供新的执行入口。界面悬浮框和鼠标手势由界面负责，MCP 直接操作参数。

## 验证范围

419 项 MCP／模型／传输回归通过，包含结构化 schema 和 stdio 实际往返。独立真实 VSM/DSE 的 14 组验收直接调用生产 facade，覆盖查询无历史／无修订变化、原生范围、五类控制及设置 dry-run、实际写入、重置、幂等、撤销重做、取消、过期修订以及计算期间原生修改的失效处理。Debug／Release 在独立输出目录构建。

这是源码、Companion 传输与独立 SDK 进程验证；尚未部署到交互式 Editor，因此 `host_verified=false`。没有启动／关闭 Editor，没有修改用户当前工程。详细记录见 [timing-mcp.json](evidence/timing-mcp.json)。
