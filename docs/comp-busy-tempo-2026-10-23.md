# 合成源准备失败与 BPM 往返旧音频

2026-10-23，按用户截图红色标记核对：1 为 track 0 / part 0（tick 11520），2 为 track 1 / part 0（tick 18360），3 为 track 0 / part 1（tick 26880）。此前按 track 0 / part 2 查到的数据不代表截图第三段。

当前工程 revision 5、146 BPM、渲染空闲。主时值发布链未 suspended，pending 为 0，但三个标记 Part 的 VoiceComp 均为 Failed，错误为 `Native timing planner is busy or unavailable.`。第 1、3 段还保留了旧 Composition，第 2 段没有旧成品。这支持“私有源准备失败后仍发布旧 BPM 成品”的原因；缓存 UUID 文件与每个 Part 的对应关系尚未单独证实。

## 修改

- 私有 Comp 源在异步拥有者中预先发布时值，遇到明确的 planner-busy 异常时按现有 5 秒预算退避重试，保留取消与清理。
- Comp 的源观察者忽略同源内部失效提交，避免临时 velocity 写入引发自身 generation 变化与取消。
- 输入签名变化时撤下已发布 Wave、Composition、峰值及视图，刷新播放源；当前结果完成前使用原生回退。
- 上一次成品文件通过 LastCompletedWave 独立保留。新配方失败时不发布旧文件，也不允许将其作为当前导出结果。

## 验证

完整解决方案 Debug、Release 独立 artifacts 构建均为零警告、零错误，Release 合并成功；MCP 测试 476 通过。

隔离的合法 SDK 原生渲染探针使用生产 VoiceComp 钩子：连续注入 4 次私有源 planner-busy 后恢复；内部通知不使自身失效；146→73→146 输出帧数为 170963→297826→170963，往返 WAV SHA-256 一致且非静音。故意失败的配方保留上一次文件，同时拒绝旧音频发布和导出。

失效回归通过 30 个范围、11 个输入范围、7 个原生渲染、5 个通知/历史用例。最初的 64 音符严格 PCM 比较受到先前 staged/异常/Rollback 用例的夹具状态影响而失败；将音频比较移至这些操作前，保留原有严格断言后通过。该修正只调整探针执行顺序。

隔离探针不能代替用户工程中的宿主听感验收。14:15 已部署合并 DLL，保留原生 DLL；未启动或关闭编辑器，未修改用户工程。当前进程仍需用户保存工程并退出、重新打开后加载修复版。只读监控继续等待实际恢复或新的失败。

机器可读记录：[验证](evidence/comp-busy-tempo-2026-10-23.json)、[部署](evidence/comp-busy-tempo-deployment-2026-10-23.json)。
