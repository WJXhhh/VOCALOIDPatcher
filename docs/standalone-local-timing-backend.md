# 独立局部时值查询后端

本报告保留首次实现与测量。后续[并发与生命周期验证](local-timing-concurrency.md)发现并修正了 Start 等待及渲染饥饿问题；**当前代码仅在会话构造时 Start，查询只重置规划状态**。最新准确性和性能见该报告，本页逐候选 Start/Stop 的描述与数值属于历史版本。

2026-10-16。本轮完成独立的输入准备、有限上下文查询和最终 tick 边界转换，查询不再借助 `StartAsyncRendering`，也不需要先渲染基线。实现位于 [Backend](../VOCALOIDPatcher/VOCALOIDPatcher/ConsonantTiming/Backend/LocalConsonantTimingBackend.cs)，尚未注册到拖动功能，也没有部署或启动 Editor。

本机隔离进程验证结果：**142 组完整边界列表与正常音频渲染逐 tick 相等；另有 512 个 VEL 起点与既有独立渲染参考相等。候选查询的 PCM Step、输出采样数、引擎帧推进均为 0。** [数值证据](evidence/standalone-local-timing-backend.json)包含对照、耗时、二进制和源码哈希。

## 输入与会话

`LocalConsonantTimingBackend.Query(target, velocity, cancellation)` 返回目标音符的相对 tick 边界、上下文数量、规划次数、缓存命中和完整查询耗时。返回数组为独立副本，调用者修改数组不会污染缓存。

后端只创建一次私有 DSE engine 和两个私有 metadata sequence，后续复用。每个窗口只清除、插入最多 33 个私有音符并 `Commit(false)`；没有 DuplicateSequence、整 Part 复制、VSM renderer 启动、波形文件或音频输出。DSE 的 Start/Stop 用来重置私有规划状态，Start 仍会分配部分合成状态，所以“不合成音频”并不等于零初始化开销。

输入直接交给原生 VSM 的单音符 NRPN 编码器，复制音高、VEL、音素、语言、Accent、Decay、BendDepth、BendLength、Opening、RisePort、FallPort。编码使用窗口内 tempo 及起始 tempo，不复制工程中的完整 tempo 表。声库头使用 `NativeLangID` 与 `SingerID`，不能把 `NPIndex` 误作 singer ID；RIN 的 singer ID 为 6，MIKU 为 0。

每个事件保留原工程计算出的采样位置及 256 采样块内偏移。私有输入 Part 保持原 Part 绝对起点、音符保持原 tick 坐标，使编码器的时长取整与原工程一致。输入编码不是回放某个 VEL64 的已捕获事件，第一次查询便可查询任意 VEL。

## 必要上下文

窗口依次取目标前后各 1、2、4、8、16 个音符；每次扩张后重新规划。窗口覆盖完整 Part，或连续两次扩张的目标完整边界相同时，才返回并缓存。最大 33 音符、5 次规划；未稳定、窗口超过 22 秒等情况明确失败，不回退全量渲染。

这是一条经过本轮样例检验的停止规则，**不是“任何声库的依赖都不超过 33 音符”的证明**。100 音符参考链中，部分候选需要 17 音符、4 次规划；不能固定只取直接前后邻居。完整上下文中若原生 VSM 无法给重复音素音符分配独立边界，返回空列表；不完整窗口出现空结果则继续扩张。本轮 3 个空结果与正常渲染一致，UI 将来必须把它视为不可拖动。

缓存最多 128 项，包含 bank、Part 坐标、目标索引、音符及表情、tempo、物理时间与候选 VEL。特别纳入实际物理时间，避免窗口之前的 tempo 修改改变帧相位后仍复用旧值。九种失效检查覆盖邻居 VEL、音高、表情，目标音素、时长、位置，窗口之前及窗口内 tempo，以及 bank。对局部窗口外的其它 Part 编辑，调用者仍须 `ClearCache()`；当前未接入宿主变更通知。

## 坐标转换

直接把窗口归零会改变原生 256 采样帧的取整相位，产生 1 tick 差异；在很远的目标之前逐帧填充 score，又会重新引入长工程成本。后端采用整数帧平移：

1. 用原 sequence 的时间接口求窗口各音符相对 Part 的实际采样位置。
2. 原点取首音符前约 0.5 秒处的整数帧；所有事件只减整数帧，不丢弃块内采样偏移。
3. 从 DSE 规划表构造有限的边界 score，保留原生 72 字节 stride、音素名和两项音素标志。托管 `VSMScore` 形状遗漏的标志不能直接忽略，否则 EVEC 映射会出错。
4. 在第二个私有 metadata Part 上，以 60000 tick/秒的密集时间轴运行原生 VSM 音符与音素匹配，保留重复音素和 EVEC 的原生匹配行为。
5. 将映射结果恢复为整数原生帧，要求误差小于 0.01 帧，再加回原点并用原 sequence 的 `GetTickFromTime` 换回目标相对 tick。

合成配置为 44100 Hz / 256 样本，presend 为 500 ms。边界 score 去除原生调度的 3087 帧前置偏移；该偏移与 500 ms presend 是两个不同层次的量。不会按音符长度均分音素，也不对真实边界作插值或平滑。

## 准确性与性能

完整对照含孤立、前邻、后邻、连音、休止、短音符、重复音素、EVEC 多辅音与尾音、MIKU/RIN、非零 Part 起点、20–300 BPM、局部变速、随机音高/邻居 VEL/音符表情和 PB/PBS/POR/BRE/BRI 控制器。查询在参考渲染之前执行，参考渲染的 PCM 计数同时作为探针正对照。142 次查询及缓存复查均保持源 VEL、音素边界、staged 状态和 score valid 状态不变。

另以 MIKU 的 `k a` / `n a`、孤立/连音四条曲线验证全部 128 档 VEL，共 512 个起点；包含平台区和非线性响应。

以下为本机 Debug 隔离进程的完整查询耗时，包含输入准备、必要的多轮上下文、Start/Stop、score 映射和坐标转换。它们不是鼠标到屏幕的端到端延迟。

| 场景 | 数量 | 中位数 | p95 | 最大值 |
| --- | ---: | ---: | ---: | ---: |
| 四条 VEL 曲线，未命中缓存 | 512 | 6.60 ms | 7.61 ms | 19.58 ms |
| 连续未命中缓存，三音符上下文 | 300 | 6.87 ms | 7.65 ms | 17.24 ms |
| 连续命中缓存 | 2000 | 0.003 ms | 0.005 ms | 0.68 ms |
| 100 音符 + 百万 tick 空尾 | 20 | 19.62 ms | 20.96 ms | 21.73 ms |
| 1000 音符 + 百万 tick 空尾 | 20 | 20.13 ms | 21.25 ms | 23.38 ms |
| 10000 音符 + 百万 tick 空尾 | 20 | 20.14 ms | 21.69 ms | 24.18 ms |

长工程查询均使用 9 音符、3 次规划。音符数量增加 100 倍没有使查询成本增加 100 倍，但不能据一次测试写成完全与工程规模无关；前一轮中位数约为 20/21/26 ms，也说明运行条件会影响结果。1000/10000 音符样例只做性能测量，没有为此生成整 Part 音频真值；100 音符另有完整边界参考链。

后端冷初始化约 143 ms，一次性创建并保留会话。300 次未命中加 2000 次缓存查询前后，私有提交内存下降约 18.7 MB，句柄和线程增量均为 0；这是一次前后观测，不是无泄漏证明。另完成 12 次会话创建、查询和释放。预先取消的查询没有进入 DSE Start，非法 VEL 被拒绝；后端自身没有任务队列，重叠调用直接返回 busy。

## 限制与接入条件

- 只允许证据中的精确 DSE/VSM 哈希、Windows x64 和 44100 Hz。AI、双声库、重叠音符、DVQM、手动音素边界，以及超出窗口/输入限制的情况安全拒绝。实际测过的声库为 MIKU/RIN，不能据此声称所有采样库都已验证。
- 同步查询期间源模型必须稳定，manager 必须比后端活得久。当前直接读取 source 的时间接口；尚未封装为跨进程快照协议，也未接入编辑器撤销/关闭生命周期。
- 直接规划显式持有 DSE 元数据包装器使用的同一原生 mutex，等待上限 50 ms，随后释放。**与 Editor 正常渲染并发、宿主锁顺序及 UI 请求调度尚未验收**；添加互斥不能替代这些验证。
- 尚需在宿主中验证生命周期与正常渲染的共存，接入最新请求覆盖/取消、Part 变化失效及拖动坐标。先保留现有数值预览，不能把同步查询直接放入每次 MouseMove，也不能一次扫描 128 档 VEL。

## 复现

研究 harness 链接与产品相同的 Backend 源码。为避免把专有程序集复制到仓库，使用仓库外的临时项目/输出目录构建运行；不要运行部署脚本或启动 Editor。

- `--standalone-timing-probe`：[StandaloneTimingMatrix.cs](../voicebank/tools/evec_render_harness/StandaloneTimingMatrix.cs)，先局部查询再独立渲染参考，输出 `standalone-timing.json`。`--timing-case=名称` 可限定调试样例。
- `--standalone-timing-benchmark`：[StandaloneTimingBenchmark.cs](../voicebank/tools/evec_render_harness/StandaloneTimingBenchmark.cs)，不渲染音频，输出 `standalone-benchmark.json`。`VOCALOID_TIMING_GOLDEN` 指向仓库中的参考 JSON。
- 两个模式都使用本机已授权声库，以及 `VOCALOID_TIMING_PROBE_NATIVE` 指定的 ABI14 隔离计数器；启动参数与已有探针相同。
- [summarize_standalone_timing.py](../voicebank/tools/summarize_standalone_timing.py) 校验完整矩阵、逐点参考、PCM 正负对照和边界上限后，生成可留在仓库的数值证据。

本轮整个 Debug 解决方案构建通过，0 警告、0 错误；现有 123 项测试通过。未部署、未运行宿主内拖动验收。
