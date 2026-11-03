# Part 失效范围合并与内部提交通知隔离

2026-10-23，承接 [失效范围研究](render-invalidation-research-2026-10-23.md)。本轮已实现并完成隔离回归；随后经用户授权，于 13:35 部署到本机 6.13.3.1 Editor，尚待宿主交互验收。

## 实现

`SampledTimingHost.InvalidateTargets` 仍逐一核对目标音符的位置、时长、音高、音素与声库身份；无效地址跳过，并补上 Track 越界检查。每个有效目标仍取自身及前后各 16 枚音符，随后按原生 Part 标识分组。

`SampledTimingVsmBinding.InvalidateRenderedContexts` 先验证全部输入 context，各自保留 1–33 枚、同 Part、无重复的边界；再按音符原生标识求精确并集。每枚音符只临时修改、还原一次，全部还原后为该 Part 执行一次 `Commit(false)`。之后显式调用一次 `InvalidateAndRefreshPart`。分离范围之间的空隙不会被补齐，跨 Part 不合并。原先单 context 入口保留并转调新实现。

新增 `TimingCacheInvalidationScope`，标记只覆盖当前来源、当前线程上的临时写入和 Commit，支持嵌套与异常退出。它不是由 `updateHistory == false` 推断而来，普通无历史提交仍完整通知。

八个业务 Commit 补丁识别该标记：采样时值、ACE、MCP revision、EVEC 渲染、分段音素渲染、EVEC tempo/history、BVL history、VoiceComp。内部提交不再派发为新的用户编辑，也不清理外层输入变更记录。时值原生输入 observer 同样忽略这类临时通知，避免回填 dirty Part。诊断观察仍可记录真实 Commit。

原生 Commit、原生缓存失效与渲染服务照常执行；实际 Part 渲染开始／完成的后处理生命周期仍保留，音素音量、平滑和 ACE 仍由渲染事件清缓存、重建。未改变导出等待入口、Undo/Redo 补丁或 ACE 载体。

## 计数对照

真实 6.13.3.1 SDK、隔离夹具，调用实际生产 `InvalidateTargets`。与前轮证据逐音符比较，16 组既有选择的覆盖集合完全一致。

| Part 音符数／目标 | Commit，前→后 | 显式刷新，前→后 | Velocity 写入，前→后 | 覆盖音符 |
| --- | ---: | ---: | ---: | ---: |
| 12／中间 1 枚 | 1→1 | 1→1 | 24→24 | 12 |
| 12／相邻 3 枚 | 3→1 | 3→1 | 72→24 | 12 |
| 12／全部 | 12→1 | 12→1 | 288→24 | 12 |
| 33／全部 | 33→1 | 33→1 | 1634→66 | 33 |
| 64／全部 | 64→1 | 64→1 | 3680→128 | 64 |
| 64／首尾各 1 枚 | 2→1 | 2→1 | 68→68 | 34 |

刷新列统计补丁显式调用次数，不代表完整宿主中的总 WPF 绘制次数。最后一行保持两个分离范围的精确覆盖，未把中间 30 枚音符一起失效。所有正常失效用例均恢复原数值，且准备阶段 DSE Start/Step 为 0。

## 回归证据

- 30 组失效／边界检查：3、12、33、64 枚音符的中间、相邻、全部、首尾、重复目标；非法超长 context、context 内重复、混入其它 Part、已有 staged 用户编辑、空目标、过期音高、Track／Note 越界、两个 Part，以及注入 setter 异常。覆盖 Velocity 0／127，异常后原值与内部作用域恢复；失败夹具的 staged 数据由探针显式 Rollback 清理。
- 11 组实际原生输入 observer 检查：普通编辑保持原归属，tempo／未知归属删除保留全局回退；内部缓存失效不产生 dirty Part 或输入通知，之后真实编辑仍只标记所属 Part。
- 7 组真实异步原生渲染：保留前轮音符／四种 ACE 载体检查，新增 12／64 音符的整 Part 合并失效。新增两例均只有 Part 0 开始、完成各一次，Part 1 无渲染；前后 WAV SHA-256、全部音素位置不变，无取消事件。
- 安装八个实际生产 Commit 补丁，将 13 个下游入口替换为计数 stub。两次内部失效消费者调用数均为 0；普通 `Commit(false)`、`Commit(true)` 均保留全部入口，共 14 次调用（含两次既有 EndInputMutation）。真实原生 Undo 回到 81、Redo 回到 82，内部失效未插入额外原生历史步骤。这是通知边界检查，没有启动完整 UI／DSP 消费链。
- 新增作用域单元测试覆盖嵌套、来源隔离、线程隔离、异常与重复 Dispose。`VOCALOIDPatcher.McpTests` 474 项全部通过，0 失败、0 跳过，包含现有 BVL／REG、时值、过渡与归档回归。
- `AceAcousticOfflineTests`、`AceParameterOfflineTests`、`BreathVolumeOfflineTests` 全部通过。
- 整个解决方案 Debug／Release、隔离程序集与探针 Release 构建通过，0 警告、0 错误；Release ILRepack 成功。全部使用独立临时 artifacts 路径。

完整计数、音频摘要、源码／构建摘要与基线对照见 [JSON 证据](evidence/render-invalidation-batching-2026-10-23.json)。复现使用研究文档中的 `--render-invalidation-probe` 命令；探针只接受 `PatcherIsolatedValidation=true` 程序集，通知 stub 仅存在于该独立进程。

## 验证边界

隔离回归期间未部署；之后已按用户要求切换安装目录的程序集符号链接到经过验证的独立 Release 合并产物，并独立核对 SHA-256、合并类型、旧版本备份与原生 DLL 未变。部署前 Editor 未运行，代理未启动或关闭 Editor，未修改用户工程或设置。部署记录见 [部署证据](evidence/render-invalidation-batching-deployment-2026-10-23.json)，本次程序集参考 SDK 为 6.13.3.1。

真实 SDK 验证覆盖原生音频、音素位置与原生历史；完整自定义参数历史、连续编辑的 UI 响应以及导出等待／成品音频仍需宿主验收。计数下降证明失效准备与重复通知减少，不能据此声称端到端合成速度提高相同比例。ACE 载体本身仍可触发原生重算，本轮未优化该路径。
