# V6 MCP 阶段 5：Audio Part 原生离线处理核对

本文记录针对 VOCALOID 6.13.0.1 反编译代码的只读核对，以及 MCP Audio Part 能力采用的边界。反编译目录仅作行为依据，没有复制到仓库或修改。

## Normalize Wave

原生 `NormalizeWaveCommand.ExecuteBody` 建立 `Transaction(vsmSequence)`，随后通过 `OfflineController.ApplyVoiceChangeAndPitchShiftWithOriginalProcess` 调用 `OfflineProcessor.ApplyNormalize`，completion 最后才 `Dispose` 事务。

`OfflineProcessor.ApplyNormalize` 的实际顺序是：

1. 从 `GetOriginalWaveFilePath()` 读取原始媒体；
2. `NormalizeRenderer.RenderAudioFile` 同步输出临时 WAVE；
3. `SetOriginalWaveFile` 把结果提交给 Audio Part；
4. 删除 renderer 临时文件。

因此此前“Normalize renderer 异步，无法进入事务”的结论不正确。MCP 现在对没有 AI Voice Changer 的 Part 调用相同的 `ApplyNormalize`，再按 `OfflineController` 的非 AI 分支调用 `ApplyPitchShift`，整个过程留在调用方已有的一条原生 Transaction 中。失败会使外层事务回滚；成功产生一个 revision、标准 revision 事件和一个 undo 步骤，不另建 job。

## Time Stretch

原生 Track Editor 和 Wave Editor 的右边界 Alt 拖动都采用同一流程：

- 根据目标 tick 长度换算目标秒数，以当前 `DurationSec` 求 magnification；
- 用 `TimeStretchRenderer.MinBpmMag` / `MaxBpmMag` 限制范围；
- 在 Transaction 内调用 `OfflineProcessor.ApplyTimeStretch`；
- 非 AI Voice Changer 分支随后调用 `ApplyPitchShift`；
- completion 才提交或回滚事务。

`ApplyTimeStretch` 同步裁出当前 Region、渲染临时 WAVE、设置新的 original media，并把 Region 归一到新媒体的完整长度。MCP 的 `audio_time_stretch` 接受目标 `duration_tick`，沿原生算法换算 magnification；dry-run 只验证目标、白名单媒体、WAVE 头和原生范围，不创建 Region 文件或 renderer 输出。

在 `v6_apply_operations` 中，domain 已经表达 Audio Part，operation 名不带 `audio_` 前缀。精确请求形态如下：

```json
{
  "project_id": "…",
  "expected_revision": 12,
  "client_request_id": "audio-offline-1",
  "dry_run": true,
  "operations": [
    { "domain": "audio_parts", "op": "normalize", "track_index": 1, "part_index": 0 },
    { "domain": "audio_parts", "op": "time_stretch", "track_index": 1, "part_index": 0, "duration_tick": 3840 }
  ]
}
```

旧的专用 `v6_edit_structure` 则使用 `audio_normalize` 与 `audio_time_stretch`。两种入口最终进入同一个领域验证和执行器。目标时长等于当前 `DurationTick` 时按原生 UI 行为视为 no-op，不重新生成 pitch-shift 媒体。

## AI Voice Changer 边界

当 `AiVoiceBankID` 非空时，`OfflineController` 会进入 `ApplyVoiceChangeAsync`，在后台运行 `VoiceChangeRenderer`，并通过进度对话框完成取消和提交。当前统一 operation 是同步 Dispatcher 事务，不能在不长期占用 Dispatcher/写租约的情况下复用该异步 completion，也不能在 renderer 结束前诚实报告 transaction、revision 和 undo 状态。

所以 catalog 将 Normalize/Time Stretch 标为已实现，但查询逐 Part 返回 `offline_processing.*_supported`。启用 AI Voice Changer 的目标会在 dry-run 和执行时返回 `unsupported`，原因是 `ai_voice_changer_requires_async_rebuild`；不会只替换 original media 而遗留过期的派生播放文件。将来若增加可等待的异步事务协调器，再把该分支作为长 job 单独开放。

V6 接受外部媒体后会把工程所拥有的副本放在 `%LOCALAPPDATA%/VOCALOID6/VSM/VOCALOID6`，保存工程的缓存媒体也可能位于 `%LOCALAPPDATA%/VOCALOID6/VSMCaches/VOCALOID6`。这两个根仅用于解析目标 Part 已持有的路径，不接受请求中的 `source_path`；新建/替换媒体仍只经过用户配置和当前工程目录 allowlist。内部根同样经过 UNC、设备路径、ADS 和逐段 symlink/junction 检查，并收窄到 V6 应用子目录，而不是信任整个 `VSM`/`VSMCaches` 父目录。

## Fade 与 Gain

6.13 的 `AudioPlacementBuilder` 对 Audio Part 调用 `VEAudioEngine.SetAudioPartFade(placement)`。该 API 没有时长、曲线或数值参数，是播放 placement 的固定防爆音淡变，不是 VSM 工程中的可编辑属性，也没有对应 getter/setter/Transaction 数据。

Audio Part 可编辑增益存在于 Part 的 `WIVSMEffectManager` 效果链，类型是 `Gain` effect；它属于阶段 4 的 Part effect-chain 能力，不应再伪装成 `audio_gain` 属性。因此 `audio_fade` 与 `audio_gain` 继续明确为 `unsupported`。

## 验证

- `dotnet test VOCALOIDPatcher.McpTests/VOCALOIDPatcher.McpTests.csproj -c Release --no-restore`：55/55 通过。
- `dotnet build VOCALOIDPatcher/VOCALOIDPatcher.csproj -c Debug --no-restore`：0 warning，0 error。
- 新增契约测试覆盖 Normalize/Time Stretch 的 catalog required fields，以及纯请求形状校验。
- 尚未在宿主中执行真实媒体改写、undo/redo、UI 波形刷新和 mixdown 矩阵，所以 `host_verified` 保持 false。
