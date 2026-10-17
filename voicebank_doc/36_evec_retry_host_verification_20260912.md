# EVEC 原生重试修复：Codex 宿主复验

## 环境和范围

2026-09-12 用户授权本代理启动 V6 并自行测试。先备份再构建完整 Release，
通过既有安装符号链接部署 ILRepack out 主 DLL。构建 0 警告、0 错误；109 项 Release 测试通过。

- 安装 SHA-256：`5E267079EB8B6534EE1DAD4D40555578E557038A5C3CDDAB4DAAAF910D0E4EE5`。
- 备份：`C:\Users\WJC\.codex\backups\evec-retry-test-20260912-134247`。
- Editor 6.13.1.1，PID 21944，启动时间 13:43:27，晚于部署。
- MCP 新建独立未保存工程 `16bbeffbadbd43148bece9e25dd019e0`，只创建测试轨道和两个 Miku EVEC Part，未打开或修改用户原工程。
- 全部变更经真实 MCP 事务执行。未做人工试听、混缩或中文分段共存验证。

## C3：首轮失败后第二次成功（真实宿主、文件锁注入）

只在当前测试期间扫描新出现的 VSM 源 score，以 FileShare.None 暂时锁定；检测到首条
score-copy-failed 后立即释放全部锁并退出，最长 30 秒自动退出。没有改 ACL、MCP 权限或全局配置。
初次注入只锁中了原生源、未影响副本，不能作为阳性证据；修正 watcher 后得到以下有效结果。

| job | 首轮 score 失败 | 新副本准备 | 第二次成对发布 | 结果 |
| --- | --- | --- | --- | --- |
| 5 | 13:47:00.748 | 13:47:02.804 | 13:47:03.158 | 通过 |
| 8 | 13:48:21.003 | 13:48:22.999 | 13:48:23.327 | 通过 |
| 10 | 13:49:11 附近 | 13:49:13 附近 | 13:49:13.655 | 通过 |

均先有 wave-copied，随后 score-copy-failed / attempt=0 / retry-scheduled，
再有同一 job 的 retry-copy-prepared / wave-copied / published / placement。
失败尝试没有单独发布 WAV；成功发布均 scorePresent=True。没有原先复现的 90 秒重试超时。

双 Part 情况下，job 8 释放门进入重试等待后，邻接 job 9 在 13:48:21.339 成功发布，
早于 job 8 的新副本准备；说明重试延迟不占共享门。这仅证明两个 EVEC Part，不能替代中文共存验证。

## 重试等待期间取消

job 14 在 13:50:07.210 retry-scheduled；脚本检测注入释放后立即用 MCP 提交新 Velocity。
13:50:07.480，job 14 superseded（约 270 ms）；job 15 同时被替代并报 reason=superseded。
job 14 此后没有 retry-copy-prepared 或 published；最终 job 17 在 13:50:07.797、
job 16 在 13:50:08.108 成功发布。第一次取消测试触发过晚、旧 job 已成功，因此仅此轮计为取消阳性证据。

## Velocity 接口

notes.update velocity=45/50/55/60/65/70/75/80 均真实提交并触发重建；
velocity=128 在 dry_run 与真实调用均返回 invalid_request。
当前 notes 查询未暴露 velocity 投影，本轮不声称通过查询完成数值回读，也未试听确认辅音听感。

## 清理与剩余项

注入进程均结束，锁句柄随 finally 释放；已释放 MCP 写租约。V6 保留打开的独立测试工程，
供用户查看；未保存、删除或关闭用户工程。

本机证据：`C:\Users\WJC\.codex\tools\evec-retry-host-evidence.log`，
`evec-fault-result3.json` 至 `evec-fault-result6.json` 和 `evec-score-fault-once.ps1`（同目录）。

本轮未发现需要追加代码修复的 C3 问题；报告 35 的重试不渲染缺陷已得到宿主阳性回归。
全工程失效重建仍为已知开销；删除/关闭/设置禁用时的取消、持续两次失败、中文共存、
人工试听与混缩不在本轮新增阳性证据内。既有单元测试不能替代这些宿主路径。
