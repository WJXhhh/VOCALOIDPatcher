# EVEC 渲染修复真实宿主回归（2026-09-12）

## Codex 后续修复（源码验证，不覆盖下文宿主证据）

更新：该修复已部署，并由 Codex 在真实 V6 重复验证首次失败后第二次成功、双 Part 共享门
及重试等待取消；详见 [后续宿主复验](36_evec_retry_host_verification_20260912.md)。下文保留修复前证据。

- C3：第二次尝试前在 UI 线程创建并提交新的 EvecRenderCopy，替换旧副本并交由共享关闭
  路径回收；仍保留原 job ID、两次尝试预算及取消状态。重建发生在获取渲染门之前，
  重建失败或任务已过期时不进入第二次原生渲染。新增 retry-copy-prepared / retry-copy-failed 日志。
- notes.update 增加 velocity 字段，范围 0–127，dry_run 同样校验；通过现有事务提交，
  不新增绕过交互确认的入口。catalog 的操作契约同步更新。
- 全解决方案 Debug 构建 0 警告 0 错误，109 项 MCP 测试通过；新增回归覆盖重试前更换
  资源、重建失败、重建期间取消。模拟资源测试不能证明原生渲染事件必然到达，仍须
  重跑 C3 的 score 锁释放场景，确认 attempt=1 实际 wave-copied/published 且其他 Part 不再等 90 秒。
- 未部署本轮修复（安装主 DLL 链接 Release out，本轮仅构建 Debug）。
- P1 全工程重建仍保留为已知开销，未声称解决。删除/关闭、中文共存与人工试听仍按
  下文未验证项保留，不以单元测试替代宿主验收。

本轮在用户已运行的 VOCALOID 6 6.13.1.1 宿主上，对 2026-09-12 部署的 EVEC 渲染修复执行 A/B/C/D 矩阵。
所有编辑通过现有 V6 MCP（stdio `dsh-mcp-client`）真实事务执行，不是 dry_run、不是离线 harness。

## 0. 环境与安装核对（宿主实测）

| 项 | 值 |
| --- | --- |
| Editor | 6.13.1.1，PID 6796，启动于 2026-09-12T12:40:29（在 12:33:47 部署之后） |
| 安装主 DLL | `C:\Program Files\VOCALOID6\Editor\Microsoft.Xaml.Behaviors.dll`，符号链接指向 Release `out/` |
| 安装 SHA-256 | `1677B57B1A17F1DE11CB3B00FFE8B08F013E4C14E9433481F475B3BF2DF5A5F6`（与给定值一致） |
| out 产物 | 7,285,248 bytes，哈希与安装端一致 |
| state/catalog | `notes.evec` = implemented/host_verified，`evec.render_timing` = host_verified，`timing_renderer_integrated=true`，catalog 含 `notes.set_evec` |
| 测试工程 | 独立未保存工程；Miku Original EVEC（4 音）、Rin Power EVEC（2 音）、SCRATCH（1 音）三个 EVEC Part |

结论：宿主确实加载了本轮部署的 DLL（启动时间晚于部署时间），状态/目录能力与预期一致。

## 证据分级

- **宿主实测**：通过 MCP 在真实工程上写入并观察 `%APPDATA%\VOCALOIDPatcher\evec-diagnostic.log`、`%TEMP%` 产物与进程状态。
- **故障注入**：在宿主机上对文件加锁 / 目录 ACL 实现的可恢复注入，非产品正常路径。
- **静态判断**：源码/反编译阅读，未在宿主执行。
- **人工试听**：本轮**未做**。下文“播放更新”指 placement 重新登记与产物变化，不代表听感验收。

## A. 声音输入失效（宿主实测）

每个输入都触发新一轮：`scan-queued → override-released → prepared → wave-copied → published → placement`。

| 输入 | job | 产物变化 | 结论 |
| --- | --- | --- | --- |
| 声库 Original→Soft EVEC | 8（+9 Rin） | peak 5052→2654，score 83620→83618 | 通过 |
| 撤销声库 | 10 | peak 恢复 5052，score 哈希还原 | 通过 |
| 重做声库 | 12 | peak 2654，score 哈希与第 8 轮一致 | 通过 |
| tempo 120→100 | 16/17 | Miku 891436→变长，Rin 同步变化 | 通过 |
| 新变速点 60@2400 | 18/19 | Miku 1,279,532 bytes | 通过 |
| Part 移到 1200 跨变速点 | 20/21 | Miku 1,368,108 bytes，peak 5214→5111 | 通过 |
| 表情 accent=80 | 22/23 | WAV 哈希改变（accent 已落库：query 返回 80） | 通过 |
| 控制曲线 Dynamics=90 | 24/25 | peak 5111→8264 | 通过 |
| Velocity | — | **MCP `notes.update` 无 velocity 字段**（仅 `add` 支持），本轮无法驱动 | 未验证（测试面限制） |

补充观察：
- 纯元音音符上设置 CTop/辅音延长会被正确拒绝（`unsupported`，整事务回滚），与既有结论一致。
- WAV 字节哈希不稳定（同输入的两次渲染哈希不同），score 哈希稳定；判断“内容变化”以 peak/score 为主。
- 每次提交都会 `override-released` 并重建**该工程全部** EVEC Part（保守策略）：改 Miku 时 Rin 也被重渲染（job 9/11/13，产物字节完全相同）。

### A6 快速连续编辑（宿主实测）

7 次连续 `set_evec`（3+3 快速 + 1 最终，间隔 0/260ms）：

```
prepared job=26/27 → superseded job=26/27; render-failed attempt=0 reason=superseded
prepared job=28/29 → superseded job=28/29; render-failed attempt=0 reason=superseded
prepared job=30/31 → wave-copied → published（仅最终世代）
```

最后一次编辑之后只有 job 30/31 发布，旧任务全部 `superseded`，无 `render-stale` 之外的回退；`%TEMP%` 始终 4 个产物，无堆积。

## B. 成对发布与失败回退（故障注入，宿主实测）

部署二进制没有测试注入缝隙，且 WAV/score 目标是 `%TEMP%` 下的随机 GUID 名，无法按路径定向。
本轮采用**隔离且可恢复的宿主机文件锁注入**：后台 watcher 只对内部副本序列目录
`%LOCALAPPDATA%\VOCALOID6\VSM\VOCALOID6\*\*.score` 中新出现的源 score 加 `FileShare.None`，
WAV 源不受影响；观测到目标后立即释放（本轮已确认进程结束、无残留句柄）。

关键日志（job 40）：

```
12:57:42.559 wave-copied job=40; peak=9439; bytes=1368108   ← WAV 成功且非静音
12:57:52.567 score-copy-failed job=40                        ← score 复制失败
12:57:52.568 render-failed job=40; attempt=0; reason=score   ← 整次尝试失败
12:57:52.568 retry-scheduled job=40
```

结论：
- WAV 成功但 score 失败时**没有发布单独 WAV**，也没有 `published`；整次尝试按失败处理。
- `override-released` 在准备替代副本前发生，且失败后没有登记新覆盖，`WaveFilePath`/`ScoreFilePath` 均回退原生，**不存在新 WAV 混用原生 score**。
- 失败尝试复制出的 WAV 被删除；`%TEMP%` 产物数量未增长（本次注入锁的是 VSM 源文件，不产生 TEMP 残留）。
- 同批次的 Rin（job 41）在锁释放后于**第 0 次尝试内**成功发布，说明注入只影响被锁期间。

## C. 有限重试与取消（宿主实测 + 故障注入）

### C1 同一 job 最多两次尝试

用 deny-read VSM 目录制造持续渲染失败（job 36/37）：

```
12:53:12.504 render-failed job=36; attempt=0; reason=canceled
12:53:12.505 retry-scheduled job=36
12:54:44.498 native-timeout job=36; seconds=90
12:54:44.503 render-failed job=36; attempt=1; reason=timeout   ← 第二次即终止
```

无新外部触发后保持静止，未出现第三次尝试；覆盖已释放，播放回退原生。注入结束后一次新编辑即恢复发布（job 38/39、46）。

### C2 取消（三个阶段）

| 阶段 | 触发 | job | 观测 |
| --- | --- | --- | --- |
| 去抖等待 | 连续提交 | 26–29 | 多次 `scan-queued` 合并，仅最终世代 `prepared`；旧任务 `superseded` |
| 重试等待 | 新编辑 | 60 | `retry-scheduled` 后 `superseded job=60; started=True`，**无 attempt=1、无 90s 超时** |
| 正在渲染 | 新编辑 | 52 | `superseded job=52` → `render-failed attempt=1; reason=superseded`（+0.14 s），随后 job 53 正常发布 |
| 正在渲染 | 新编辑 | 61/62 | `superseded job=61`；`render-stale job=62`（发布前签名复核丢弃过期结果） |

取消后任务不复活、不发布旧结果，新编辑能立即取得共享门并发布；副本与临时文件在后续发布中回收（`%TEMP%` 稳定为 Part 数×2）。

### C3 首次失败、第二次成功 —— **未达成（缺陷）**

在 4 次独立注入中（job 36/37 deny-VSM、job 40 与 job 45 score-lock、job 50 score-lock），
只要 attempt=0 失败，attempt=1 都**没有产生任何渲染**：没有 `wave-copied`，
而是在 90 s 后 `native-timeout` + `render-failed attempt=1; reason=timeout`。
即使注入窗口在 attempt=1 开始前已经释放（job 45、job 50），结果相同。5/5 复现。

复现步骤：
1. 让一个 EVEC Part 的 attempt=0 因 score 复制失败（对 VSM 源 score 加排他锁）或渲染取消（deny-read VSM 目录）。
2. 释放注入。
3. 观察：`retry-scheduled` 后无 `wave-copied`，+90 s 出现 `native-timeout`；期间共享渲染门被占用。

定位（静态）：`EvecRenderCoordinator.TryRenderCopy` 对同一个 `EvecRenderCopy.Sequence` 先 `StopAsyncRendering()`，
重试再 `StartAsyncRendering()`，但从未重新 `Commit`/重建副本；内部副本的渲染产物已存在时，第二次 `StartAsyncRendering` 不产生完成事件，`WaitForNativeRender` 只能等到 90 s 超时。
单元测试 `BoundedRenderRetry` 只覆盖重试策略，未覆盖“同一副本能否再次原生渲染”。

影响：任何首轮渲染失败后，该 Part 的覆盖被释放并回退原生，同时重试占用共享门 90 s，阻塞同工程其它 EVEC Part（job 37 因等待 job 36 的门而随后自身也超时）。

## D. 共存与负载（宿主实测）

- **多 EVEC Part 连续编辑**：恢复 3 个 EVEC Part（Miku/Rin/SCRATCH），10 次连续 `set_evec` 共 199 ms（单次最大 38 ms），UI/MCP 全程响应；去抖后只产生 jobs 57/58/59 各一次发布。
- **产物与内存**：`%TEMP%` 稳定为 6 个产物；V6 工作集 307.6→335.6 MB，线程 48–49，句柄 +21；无持续增长。
- **按工程代次重建全部 EVEC Part**：已确认存在（改一个 Part 会重渲染所有 EVEC Part，产物可能字节相同），是本版保守开销。
- **中文分段渲染共存 / 共享门无死锁**：本轮未构造中文分段 Part，因此**未验证**；共享门与内部序列表为静态审阅（`NativeRenderSession`/`SegmentedPhonemeRenderCoordinator.IsInternalSequence` 让位判定）。

## 未能验证项（如实标记）

| 项 | 原因 |
| --- | --- |
| Velocity 编辑使覆盖失效 | MCP `notes.update` 不暴露 velocity，仅创建时可设 |
| 删除 Part / 删除轨道 / 关闭工程 / 重开 时的取消 | 这些是 dangerous 操作，`McpConfirmWrites` 默认 true，会弹 V6 交互确认框（dry_run 返回 `confirmation_required=true`），本轮不能代替用户点击 |
| 设置中关闭 EVEC | MCP 无修改设置的工具入口 |
| 人工试听 / A/B 波形 | 本轮未监听；“播放更新”仅为 placement 重新登记 + 产物变化 |
| 中文分段 + EVEC 共存 | 未构造中文分段 Part |
| 首次失败第二次成功 | 见 C3：宿主复现出重试不渲染的缺陷 |

## 遗留缺陷与建议

1. **P0 重试第二次不渲染**（C3）：失败后对同一内部副本再次 `StartAsyncRendering` 不产生完成事件，导致 90 s 门占用。建议重试前重建 `EvecRenderCopy`（或重新 `Commit`/触发一次原生渲染），并增加“同一副本二次渲染”的宿主/单元覆盖；否则建议在确认无法重渲染时直接放弃重试，避免占用共享门 90 s。
2. **P1 全量重建开销**：每次提交重建全部 EVEC Part，且期间覆盖被释放，连续编辑时播放可能短暂回退原生；建议按精确失效或按 Part 签名（而非工程代次）重建。
3. **测试面缺口**：velocity 编辑与删除/关闭类取消路径需要有可自动化的宿主入口，或在允许注入的构建上验证。

## 本轮 job ID 索引（诊断日志）

- A1 声库 8/9、撤销 10/11、重做 12/13；A2 tempo 16/17；A3 18/19、20/21；A4 22/23；A5 24/25；A6 26–31。
- B 基线 32/33；注入 40（WAV 成功/score 失败）、41（锁释放后发布）、44（窗口过早释放，attempt=0 内成功）。
- C 持续失败 36/37；45、50 同为 attempt=0 失败后 attempt=1 超时；取消 52/53、60/61/62 与 63/64/65。
- D 负载 57/58/59；恢复 38/39、46。
- 日志：`%APPDATA%\VOCALOIDPatcher\evec-diagnostic.log`；注入脚本与日志：`%TEMP%\evec-regression\`。
