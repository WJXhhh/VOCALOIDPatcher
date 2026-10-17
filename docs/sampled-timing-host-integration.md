# 浮点时值宿主接入记录

2026-10-17 更新：逐音素执行器、全部边界手势及 ABI 3 已实现，原生缓存身份和关闭时机已修正，最新验证及仍待完成的宿主验收见 [逐音素 timing](full-phoneme-timing.md)。下文为此前阶段记录。

2026-10-16。本轮已接入共享历史命令、工程存储和原生 Part 路由的代码，浮点拖动尚未启用。不要将隔离进程通过与真实编辑器全部验收通过混为一谈。

## 实现

- `SampledTimingHost` 为源 Sequence 管理独立会话，提交使用现有 `CustomParameterHistoryCoordinator`。源提交、撤销和重做失效旧 revision，停止旧任务并移除旧 Part 路由，避免删除对象的地址被后续对象复用。
- `SampledTimingProjectLoadPatch` 在 `LoadProjectSequenceFile` 返回源之后、外层初始化开始渲染之前恢复扩展。`SampledTimingProjectSavePatch` 在原生 VPR 保存后写入扩展，失败返回保存失败并恢复原文件。临时备份不把源标记为已保存。
- `DuplicateSequence` 捕获不可变工程扩展快照，后台自动备份只写该快照，不跨线程读取源音符。此副本尚不支持应用时值的音频渲染，进入该路径会拒绝启动，防止忽略系数。
- 新原生 ABI `v6_timing_*` 版本 1，Note 为 24 字节、Status 为 32 字节；原有 clock/register-shift 布局和 ABI 不变。托管端核对精确 DSE/VSM 哈希以及上述 ABI/尺寸后才能安装。
- VSM render core 进入时捕获该 Part 的不可变 epoch 快照；DSE parser 绑定拥有快照，后台 NOTE 规划仍能识别所属 Part。匹配使用源时间、音高和当前哈希的 3,087 帧原生 pre-roll，不按进程级 NOTE 序号推测目标。歧义、旧 epoch 或不匹配会记录失败并使 DSE Step 返回失败，不能静默输出默认系数音频。
- 真实系数提交才按每个目标最多 33 个上下文音符使原生缓存失效。候选查询仍走无 PCM 的局部规划。正常最终渲染的 PCM 工作范围由 VSM 决定。

## 已验证

隔离 native 验收入口为 `--timing-routing-probe`，使用生产原生路由，不安装旧的系数序号探针。

- Part 起点 0/17,003 tick，音符时长 120/480 tick 的四组场景，每组同时控制三个相邻音符，完整边界全部与局部查询一致，路由失败数为零。
- 同一 Sequence 的四个 Part，含 MIKU/RIN、相同音高/节奏与不同系数，原生 render core 峰值同时活动 **4 个任务**。四个目标完整边界一致，没有 Part 间系数串用，失败数为零。
- Debug/Release 整体解决方案构建通过；147 项托管测试、26 项 Rust 测试通过。

## 真实 Editor 验收状态

已备份旧安装文件，并在用户允许 UAC 后安装宿主验证构建、启动 Editor。自动宿主验收器要求“未修改的空白新工程”，该保护条件未满足，结果为 **failed / validation requires an unmodified empty new document**，尚未执行共享 UI 撤销、宿主保存重开和后台备份的验收步骤。没有绕过保护或清除现有工程。

用户随后反馈操作测试可用，并报告音素文字、提示数值和交界细缝问题；这些修正另见 [显示与手势记录](visual-consonant-timing.md)。用户操作反馈不等同于自动验收矩阵通过。本轮在该反馈之后不关闭有未保存内容的编辑器；未对该运行实例替换新的 UI 修正版本。

后续还须验证：真实 `EditUndoCommand`/`EditRedoCommand` 的混合时间线、宿主保存与加载、后台备份、取消和缓存发布。受控音符发生移动、改长、改音高或拓扑变化后当前严格绑定会拒绝旧定位，自动迁移和复制仍未完成。逐元音边界与逐过渡控制继续保留存储结构，尚无声音执行器。通过这些门槛后才接回浮点拖动。
