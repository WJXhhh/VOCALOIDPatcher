# 延音符边界归属导致渲染准备失败

2026-10-20。用户反馈部署后编辑器仍出现异常。本轮使用当前 VOCALOID MCP 和实际日志确认故障，随后独立复现、修复并部署；没有操作或覆盖用户工程。

## 现场证据

MCP 显示当前 Part 为两个连续的 240 tick 音符：`m u`、音高 62，后接 `-`、音高 61，声库为本机 V6 中文采样声库。编辑器报告 `is_rendering=true`，而音符诊断同时给出 `render_service_enabled=false`、`render_finished=false`、`timing_host.suspended=true`。屏幕保留的有效旧 score/wave 不能证明新渲染已经完成。

`renderer.jsonl` 在北京时间 22:10:39～22:12:36 记录 15 次 `preparation-rejected`。多数堆栈停在 `VerifyPhonemeBoundary → QueryPhonemes → QueryTransitions → Publish`。MCP 给出当前异常详情：

```text
Native sample mapping did not produce the requested phoneme boundary at 3302 (/-)
observed 3276..3302:u/@3302;3302..3324:u/@3324
following 3324..3347:u/Sil@3345
```

这证明失败发生在过渡规划验证阶段。`-` 不是新的采样音素，而是继续前一个音符的 `u`；原实现却要求原生工作项包含 `-`。改选前一个音符查询也会失败，因为其原始音素范围只到第一音符末端，而实际 `u→Sil` 在延音结束后，因而无法绑定归属。修复前独立重放六个查询全部失败，分别观察到上述边界拒绝或 `u/Sil` 归属歧义。

旧路由随后被撤下，工程中的既有过渡控制仍被保留并进入暂停状态。不能通过忽略错误、删除控制或放行一份无控制音频来宣称恢复。

## 修复

新增 `ContinuationPhonemes`，只在同一 Part、声库、连续音符序号、相接 tick 范围及精确原生共享帧都吻合时确认延音归属。所有原生 NOTE、音高变化和保护锚点继续保留；布局中把延音终点并入原来的最后一个音素，使尾部 ART 仍绑定原来的 `u` 身份。

当查询目标本身为 `-` 时，原生边界检查使用已经确认的来源音素，并要求接点两侧实际工作项严格相邻、声音身份一致。未把 `-` 当作通配符，也没有全面放宽普通音素的验证。独立修改延音符的“音素边界”仍明确拒绝，因为那是持续音中的音高分配边界；过渡与进度控制则可在原来的音素上继续执行。

工程归档格式、音素身份和原生 ABI 均未改变。原生 DLL 无需替换，前一轮 PCM 平滑修复也保留。

## 验证

`--continuation-timing-probe` 复现相同 `m u / -` 结构，分别把后一个音符设为 61、62、65，并从两个音符各自准备查询。六个查询均通过；其 Starts/Steps/Samples 均为零。每个查询再真实渲染中性值、进度 -0.203、三个端点位移与进度组合、恢复，共 24 次。全部已消费 ART 来源位置与计划一致，原生 timing/gain Failures 为零；中性和恢复 PCM 均与预热基准逐字节一致，修改后的声音确实变化。控制归档序列化及重新解析后的执行指纹也保持一致。

音高 65 的一次冷基准与后续温态输出有 273 个 PCM16 样本不同，最大差 2 LSB、差值 RMS 约 0.03474。重放保留冷、温两份原始文件，不设置新容差；所有中性／恢复比较严格采用显式重渲染得到的温态基准。STA 的原生持续段分块不作为 ART 来源逐帧一致性的验收对象。

另有既有矩阵 278 次真实渲染全部通过、15 个无效候选明确拒绝；308 项托管测试通过，完整解决方案 Debug/Release 隔离构建零警告、零错误。新增模型回归覆盖延音链、原归属不变、断开／缺失／错误共享帧拒绝，以及接点双侧证据。

证据见 [continuation-phoneme-render-rejection.json](evidence/continuation-phoneme-render-rejection.json)。原始 MCP 快照、故障日志和 WAV 保留在本机仓库外，没有把用户工程或二进制放进仓库。

## 部署

用户保存并关闭编辑器后，通过 Windows 管理员授权部署合并 Release 主补丁，SHA256 为 `F014A1DF3B00A2D1FE6B2230645380CE9BE935BA8BA53A26AF07341B7964B4E1`。旧版已备份，安装文件、链接目标及备份独立复核通过；原生 DLL 哈希保持不变。部署记录见 [continuation-phoneme-deployment.json](evidence/continuation-phoneme-deployment.json)。

尚待重新打开原工程后，通过 MCP 确认渲染服务恢复、暂停状态清除及新的波形实际完成；独立渲染通过不等于宿主现场验收。
