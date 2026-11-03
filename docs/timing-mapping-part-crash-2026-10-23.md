# 临时时值 Part 复用崩溃修复

2026-10-23 14:49:52（北京时间），VOCALOID 6.13.3.1 的 Windows Application Error / .NET Runtime 记录了 `0xc0000005` / `AccessViolationException`。调用链为：

```text
SampledTimingHost.Changed → Flush → Publish → PrepareTransitions
→ SampledTimingSession.Prepare → PrepareCoefficient
→ RentCoefficientMapping → WIVSMMidiPart.setDuration
```

现场日志最后包含连续撤销，以及一个音符 REG 从 0 改为 22 后的发布。此顺序只能说明触发上下文，不能证明 REG 数值有误。故障模块是 .NET 8.0.30 的 `coreclr.dll`，该模块名也不足以证明运行时本身是根因。现有小型转储确认了调用栈，尚不能证明原生对象究竟在哪里失效或受损。

## 修复边界

原对象池缓存整个 `Sequence + Part`，下一次租用时删除音符并对已映射过私有 score 的 Part 调用 `SetDuration`。修复取消这条旧 Part 复用路径：归还时移除整个临时 Track 并 `Commit(false)`，仅缓存没有 Track、没有待提交事务的 Sequence 外壳；下一次租用创建新的 Track 和 Part。继续保留最多五个外壳，避免每次查询都走较昂贵的 Sequence.Close。

租用及归还均先核对 Sequence 仍由原 manager 持有，再访问子对象；清理失败的外壳交给既有关闭/重试机制，不进入池。准备对象在执行任何原生清理前先清空自身的窗口所有权，重复或同步回调重入 Dispose 会立即返回，避免同一 Sequence 被归还两次。

这是一项针对已确认崩溃路径的隔离修复，并非已经证实原生内存损坏的最初来源。未修改用户工程、配置、REG 值或原生兼容档案。

## 验证

独立 SDK harness 的 `--timing-preparation-benchmark --patcher-dse-initialization` 增加了对象生命周期回归：

- 96 轮交替 1/3/9/18 音符、两种 V4X 声库及长短窗口，并周期性强制 GC。
- 在原生归还通知中重入 Dispose，共发生 192 次回调；另有显式重复 Dispose。
- 每次归还核对空 Track 列表、已提交状态、manager 所有权、无重复 Sequence 和最多五个外壳。
- 另一个准备对象始终保持存活，验证回收其他窗口不会改变它的音素边界。
- 原有 24 组边界及完整过渡帧与修复前产物完全一致；DSE 步进和合成样本计数均为零。

本机 6.13.3.1 SDK 上的解决方案 Debug / Release 隔离构建均为 0 警告、0 错误；Release 已完成 ILRepack 合并。现有 McpTests 共 482 项全部通过。本轮未重新验收 6.13.1.1。

修复前原有的短基准也能通过，因此不将新增压力验证描述为复现了现场致命异常。还需要用户在实际 Editor 中重复原工程的 REG 编辑、连续撤销/重做和跨 Part 操作确认。验证使用隔离 artifacts 目录，不部署或启动 Editor。
