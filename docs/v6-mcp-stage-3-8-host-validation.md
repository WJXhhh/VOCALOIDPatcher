# V6 MCP 阶段 3～8 宿主验证

验证日期：2026-08-13<br>
宿主：VOCALOID 6 Editor 6.13.0.1<br>
工程：仅使用临时新建并保存的可丢弃工程；未打开或修改用户最近工程。

## 结果

| 阶段 | 宿主验证结果 |
|---|---|
| 3 扩展参数 | BVL 与 Register Shift 批量写入、读回、事件、Undo/Redo 通过。BVL 重建事件保持 latest-generation 语义；Register Shift 在当前音符上的持久值可恢复，原生支持诊断为 unavailable。 |
| 4 Mixer/效果器 | Track volume/pan 写入、读回、Undo/Redo 通过；Mute 写入可读回且不进入历史，Undo/Redo 其它 Mixer 值时保持不变。读取到 11 个宿主实际安装效果；既有 Part Gain 参数写入和读回通过。 |
| 5 Audio Part | 外部路径白名单、创建、查询、安全媒体标识、Normalize、Time Stretch、Undo/Redo 通过。保存并重开后，V6 管理的 `VSM/VOCALOID6` 媒体仍可读取和离线处理。 |
| 6 原生语义 | Transpose、Insert Lyrics Batch、Half Tempo 的 dry-run、执行、模型读回和单步 Undo 通过；Transpose Redo 通过。失败的无语言音符插入未留下部分模型。 |
| 7 UI/Transport | 选区、编辑工具、参数面板、lower/right zone、缩放、seek、start mode 的状态读回通过；工程 revision 与 Undo 栈不因视图操作变化。Mixer 与 Inspector 在 UI 中实际可见。 |
| 8 原生导入/生命周期 | Recent 查询、Import Project dry-run/执行、Import Audio dry-run/执行、Job get 终态、Revert(discard) 通过。Revert 后 project generation 改变，旧 project ID 的写入返回 `stale_project`。 |

所有已执行 mutation 均使用 write lease、最新 revision 和唯一 `client_request_id`。测试结束后强制关闭了经进程路径核对的验证宿主，并删除临时工程目录。

## 宿主验证中发现并修复

1. Audio Part 的源媒体在导入后由 V6 移入自身 VSM 目录，不能再次按客户端外部路径白名单拒绝。现在仅对 Part 模型返回的原始媒体路径，额外信任 `%LOCALAPPDATA%/VOCALOID6/VSM/VOCALOID6` 与 `VSMCaches/VOCALOID6`，同时继续执行逐段 reparse/junction、UNC、device、ADS 和 RIFF 校验；客户端提供的 create/replace 路径仍只走普通 allowlist。
2. Time Stretch 目标长度等于当前长度时按原生 UI 语义直接 no-op，不再额外重建 pitch-shift 媒体。
3. V6 `SetActivePartAndTrack` 的返回值只表示 Active Track 是否改变。选择接口改为读取 `ActivePart` 后置状态判断，已激活或同轨 Part 不再误报失败。
4. Revert 结果不再直接序列化内部 ValueTuple，改为标准 `ProjectContext`，返回 `instance_id`、`project_id` 与 `revision`。

## 自动化回归

- `dotnet test VOCALOIDPatcher.McpTests/VOCALOIDPatcher.McpTests.csproj -c Release --no-restore`：70/70 通过。
- `dotnet build VOCALOIDPatcher.sln -c Release --no-restore`：0 warning、0 error；Rust release 构建与 ILRepack 合并成功。

Fade、Audio Part 直接 Gain、Transport playback rate、音符时值量化、参数平移/缩放/clamp 等经 6.13.0.1 代码核对不存在相应稳定原生业务入口的能力，继续保持 `unsupported`；本次验证没有为它们猜测 setter 或 GUID。
