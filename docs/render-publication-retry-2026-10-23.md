# 渲染准备遇到锁竞争后挂起与蓝色进度停滞

2026-10-23。用户部署范围合并版本后反馈“渲染不活跃、完成区域不再填蓝”。已经修复并部署下次启动加载的版本；当前运行的 Editor 仍需保存、退出并重开。

## 现场证据

只读 MCP 查询到 Editor 6.13.3.1、PID 3792 的时值状态为 `suspended=true`，保存控制 49 条，已发布原生 Part 为 0，故障为 `TimeoutException: Native timing planner is busy or unavailable.`。诊断日志在 13:39:32、13:40:45 两次记录同一调用链：原生互斥锁等待 → `QueryPhonemes` → `QueryTransitions` → `Publish` → `Flush`。后一次故障前有 9 个 Part 开始渲染，之后输入变更停止了服务，重新准备时因锁忙而被永久挂起。

截图对应的轨道 Part 蓝色填充由原生 `UIMidiPart.Progress` 绘制，`OnRendererProgressed`／`OnRendererCompleted` 从原生 Part 读取进度并重画。这与钢琴窗波形分块节流是不同的通知路径。此次首先修复已证实的准备失败，未伪造进度或修改蓝色绘制规则。

## 修改

- 把原生规划器互斥锁的短暂等待超时标记为专门的 `TimingPlannerBusyException`，只重试这种明确可恢复的竞争，不吞掉任意 Timeout 或验证失败。
- 每个真实来源只保留一个 Dispatcher 重试计时器，按 25、50、100、200、250 ms 退避，后续最多 250 ms 一次；同一 revision 的重试预算为 5 秒，避免无限排队。计时器不抢占仍有 staged 编辑的事务，关闭来源时停止。
- 失效临时值还原、Commit 成功后立即从 Pending 移除对应目标；直到 Publish 成功才清除 NativeRestart。因此忙碌重试只重新准备和发布，不重复 Commit／显式刷新同一批范围。发布成功后重启原生服务并清理计时器。
- 真正的数据、执行或验证错误仍走原有拒绝逻辑，并取消尚未执行的重试；过期忙碌通知不能重新激活已经被拒绝的 revision。内部渲染副本沿用原有安全失败行为。

## 验证

真实 SDK 隔离进程中，工作线程实际占用原生互斥锁 350 ms。首次渲染准备被延后，期间未挂起、未提前开启未发布控制的渲染；锁释放后自动完成一次渲染，内部失效 Commit 始终只有一次。额外重复忙碌通知被合并。原生 Part 控件收到 26 次进度通知，200×40 的实际 WPF 渲染结果包含部分完成蓝色像素 7450、最终蓝色像素 7722。另验证真实错误会停止排队重试，原控制数据保留。

随后只读复制用户工程，在独立 SDK 进程冷渲染。初期低层载入夹具没有收到回调；最终夹具补齐原生分块尺寸默认值、master volume 插值和基线提交，并按既有有界 context 失效后重渲染。9 个 Part 完成、49 条时值控制保留、234 次进度通知，9 个原生 Part 控件各有 7722 个完成蓝色像素；源工程 SHA-256 前后相同，无挂起，原生服务保持开启。该夹具只安装部分产品补丁，出现 VoiceComp 源准备警告，不能宣称完整 Comp／BVL／ACE 后处理和导出通过。

整个解决方案 Debug／Release 与隔离程序集构建通过，0 警告、0 错误，Release ILRepack 成功；476 项托管测试通过。前轮范围合并探针的 30 组失效边界、11 组通知归属、7 组原生渲染、5 组通知／原生历史检查全部回归通过。新预算测试覆盖退避、到期、新 revision 和成功重置。

完整计数和摘要见 [验证证据](evidence/render-publication-retry-2026-10-23.json)。复现使用已有 `--transition-host-probe` 隔离入口，增加 `VOCALOID_PUBLICATION_RETRY_PROBE=1`；可选 `VOCALOID_PUBLICATION_RETRY_PROJECT` 指定仅复制、不修改的工程。探针代码仅编译进 `PatcherIsolatedValidation=true` 程序集。

## 部署与待确认

13:54 经既有部署授权切换安装目录的程序集符号链接，保留旧 payload、备份和回滚元数据。独立核对安装／payload 摘要及新类型；原生 DLL 不变。没有启动或关闭用户 Editor，也没有修改其工程、设置或播放状态。部署时 PID 3792 仍在运行，其已加载模块不会随磁盘链接切换而更新，必须保存后退出并重开。

[部署证据](evidence/render-publication-retry-deployment-2026-10-23.json)。完整宿主里的连续编辑、撤销／重做及导出仍待重开后验收。
