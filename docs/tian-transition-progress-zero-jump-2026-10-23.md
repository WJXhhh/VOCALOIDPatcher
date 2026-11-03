# tian 过渡菱形首次拖动的来源跳变

现场：第一轨、第一 Part 的第三个音符 `tian / t_h iE_n`。用户报告原位与左右轻拖听感差异大，而左右轻拖彼此相近。

MCP 在工程 revision 19 读取到该边界的 phase 为 0、coverage 为 none，当前输出范围为 3380 / 3385 / 3400。对 phase = -0.001、+0.001 的只读候选查询均返回 coverage = right；输出边界保持原值。所有现场操作都是查询或 dry-run，结束时工程仍为 revision 19，未修改未保存内容。

根因是 `TransitionExpressionControl.Progress` 自动调用 `Enable`。菱形第一次移动同时启用完整来源，精调面板修改进度时也自动勾选完整素材，MCP 沿用同一策略。这是一个离散的来源范围切换，叠加在原本连续的进度曲线上。

在已保存工程的独立 SDK 副本中，保留该 Part 的音符、音高曲线和参数，恢复目标边界的现场几何并只清除其 phase/coverage。右侧原取用范围为素材帧 14–28，完整素材范围为 14–53。旧行为无论向左还是向右轻拖，都先多取到后面 25 个素材帧；这一变化远大于轻微进度重排。

修正内容：

- 菱形、精调输入和 MCP phase 仅调整当前来源范围内的进度，不再隐式改变 coverage。
- 完整来源继续由精调复选框或 MCP coverage 显式启用。已有工程中保存的完整来源设置保持有效，不迁移或清除。
- MCP 允许 coverage=none 与非零 phase 一起提交；省略 coverage 时保留已有值。
- 原裁切进度表平坦时禁用菱形；完整来源仍可单独开启，开启后有进度可调时恢复菱形。
- 更新四种语言的提示，保留 Ctrl+单击同时重置进度及完整来源的显式操作。

验证：

- 484 项托管测试通过，含原位往返、微小正负进度、显式完整来源、平坦来源和 MCP 两轴独立性回归。
- 完整解决方案 Debug（6.13.3.1 SDK）及 Release（6.13.1.1 SDK）构建通过，均无警告或错误；使用独立 artifacts 目录。
- 两套真实 SDK 各完成 8 个最终原生渲染：预热、基线、左右微调、旧行为左右微调、完整来源零进度、恢复原位。逐帧捕获验证 DSE 实际消费了目标进度表，原生 timing/gain failure 均为 0。
- 修正后 ±0.001 的最大素材帧变化约 0.007137；旧行为为 25。回到 0 后 PCM 与基线逐样本一致。
- 在目标过渡及随后 10 个输出帧内，差值 RMS / 基线 RMS：新左右微调约 0.000707 / 0.000994；旧左右微调约 1.2545 / 1.2524，旧左右彼此约 0.01337。该指标是波形差异，不是音量变化或主观听感评分。两套 SDK 结果一致。
- 四份翻译 XML 可解析且键集合一致。

复现入口：harness `--transition-progress-continuity --allow-empty-key-catalog --patcher-dse-initialization <输出目录>`，`VOCALOID_GENG_PROJECT` 指向保存的工程副本，`VOCALOID_TIMING_PROBE_NATIVE` 指向已认证原生模块，必要时以 `VOCALOID_PROBE_EDITOR` 选择合法 SDK。探针有意固定本次现场目标和输出边界，输入不符会中止。

当前仅完成源码修正和隔离验证，尚未部署，也未在实际 Editor 中验收拖动和试听。用户当前未保存工程保持原状。完整工程副本、MCP 捕获、WAV 与构建日志保留在仓库外；摘要见 [验证记录](evidence/tian-transition-progress-zero-jump-2026-10-23.json)。
