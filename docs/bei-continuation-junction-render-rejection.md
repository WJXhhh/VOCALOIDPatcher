# bei 过渡端点与延音接点重合导致渲染暂停

2026-10-20，延音归属修复部署后，用户再次报告当前工程在 `bei` 处无法完成渲染。本轮通过 VOCALOID MCP、宿主日志及已保存工程的独立副本定位，没有修改用户现场工程。

## 现场与复现

MCP 报告编辑器 6.13.3.1、工程 revision 952、无未保存修改。第一个 Part 的第 4 个音符（索引 3）为 `bei [p ei]`，相对位置 2160 tick、长度 120 tick、音高 63；后接同音高、长度 120 tick 的延音 `-`。渲染状态仍为进行中，但 `render_service_enabled=false`、`render_finished=false`，timing host 已暂停。22:36:04～22:36:13 的 5 条 `preparation-rejected` 堆栈停在 `TransitionControlPlan.Compile → TransitionProjectBinding.Resolve`，异常为 `Requested transition endpoint is outside its native geometry domain.`。

独立读取已保存工程的 18 音符 Part，保留其 11 份音素计划和全部已保存过渡控制。修复前查询重现相同异常：`p→ei` 的 End 为帧 3509，归档要求移动到 3515（+6 帧）。3509 同时是延音内部的音高分配接点，被上一版布局当作保护锚点，因而任何非零位移都会失败。这与前一次把字面 `-` 当作录音音素的归属错误不同。

## 修改

`TransitionLayout` 同时保留物理音符列表和延音合并后的逻辑音素列表。只有已证明属于延音的内部接点、且不与真实音素边界或布局首尾保护点重合时，才解除该点对 ART/STA 几何的锁定。ART Split 仍保持保护；普通锚点、真实音素边界、单调映射和原生输出范围检查不变。原生 NOTE 消息、音符位置、长度和音高数据继续保留，未删除或裁剪工程控制值，也未修改原生 ABI。

前一份延音文档中的“所有保护锚点继续保留”描述对应首次修复；本轮针对其中错误分类的内部接点进一步修正。这里的接点分类不等于移除原生音高事件。

## 验证与部署

新增 `--stored-transition-replay`，对工程副本使用原有归档执行规划和真实渲染。原音高以及将延音临时降低／升高 2 个半音的三个副本，分别执行预热、中性、原控制、恢复，共 12 次。全部已消费 ART 来源与计划一致，原生 timing/gain Failures 均为零；每次渲染前后的音符位置、长度和音高一致。原控制确实改变 PCM，恢复输出与相应预热后中性基准逐字节一致。所有规划查询 Starts/Steps/Samples 均为零。音高变化只在独立副本发生；未据此宣称声学 F0 曲线不变。

边界模型回归覆盖可移动内部接点，以及与真实音素边界重合时仍拒绝移动。310 项托管测试通过，完整解决方案 Debug/Release 隔离构建零警告、零错误。再次执行延音矩阵 24 次受控渲染及既有声库矩阵 278 次渲染，全部通过；15 个无效候选继续拒绝。

用户保存并关闭编辑器后部署合并 Release 主补丁，SHA256 `5D6557FEF8308C9EA95559119C72CA4E75629830CA9B208A7F212CDE626ECD47`。合并程序集含 Harmony 类型且无独立 `0Harmony` 引用。旧补丁已备份，安装文件、持久链接和备份哈希独立复核通过；原生 DLL 未变。

汇总见 [复现及回归证据](evidence/bei-continuation-junction-render-rejection.json)、[部署记录](evidence/bei-continuation-junction-deployment.json)。原始 MCP、日志、工程副本及 WAV 只保留在仓库外。本轮尚待重新打开原工程后的 MCP 宿主验收，独立渲染不替代现场验证。
