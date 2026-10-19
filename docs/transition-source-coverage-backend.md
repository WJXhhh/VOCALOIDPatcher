# 过渡完整源覆盖与框内推进：正式后端

2026-10-18。承接[复韵母内部推进研究](compound-vowel-source-progression.md)，已接入生产模型、局部规划、工程数据和统一 Part 发布。本轮没有新增前端入口，也没有部署或操作用户正在运行的编辑器。

## 控制语义

新增 `TransitionCoverageEdit(Edge, Sides)`，`Sides` 支持 Left、Right、Both；`None` 用于撤掉该项控制。它显式选择**已选 ART 素材、已选 stage 的完整源区间**。素材身份、stage、方向和音素身份仍由原生规划及兼容档案认证，控制本身不重新选样。

源区间来自素材 alignment 的两条 outer interval。正向按左右顺序读取；反向交换两侧并倒序推进。不是把包含其它 stage 的整份素材强行塞进一条过渡。两侧在各自当前分配中线性覆盖其完整源区间，再叠加已有 `TransitionPhaseEdit` 的非线性推进。Phase 原有的左右侧语义保持不变；未启用 Coverage 的旧工程继续使用原生已裁取区间。

尤其不能把 `edge.Original.Frames` 当作完整源：乐正绫「炽」的 `s\→iE_r` 在 240 tick 音符上，未调 timing 的原始规划已经只读到源帧 52，实际该 stage 元音侧仍为 26…61。正式实现直接读认证的 alignment，所以这个短音符也能覆盖到 61。

组合顺序为：音素 timing 和过渡接点确定输出分配 → 旧 Onset 机制确定其已有源规划 → Coverage 确定指定侧的完整源范围 → Phase 重排区间内推进 → 原生合成时施加过渡增益。Coverage/Phase 不增加或移动输出锚点，不改 Begin/Split/End、音符长度和帧标记，不做 WAV 后处理。

少于两帧的非恒定源区间无法同时表达两个源端点；缺少合法素材区间、身份变化、跨界或不完整帧表均在发布前明确拒绝。条件范围查询同样保留 Coverage，不能以丢掉控制的方式返回“可用”位置。

## 持续段接续

ART 与 STA 是不同素材。把 ART 的末索引 61 直接当成 STA 的源起点会混淆两份素材，正式实现没有这样做。完整源覆盖将 ART 推进至已选 stage 的真实终点，STA 保持原生分配、源起点、直接读取规则及原生重叠合成路径。映射通过已有原生路由在源帧消费前应用，持续段与过渡共享同一份不可变 Part 发布及缓存身份。

真实渲染逐项捕获 ART/STA 工作项，比较材料对象 ID、stage、方向、分配、STA `SourceStart` 和 `Direct`；覆盖起音、收尾和两端同时控制，各项均与相应未启用 Coverage 的几何对照一致。`s\→iE_r` 的短输出能够从 26…37 / 26…30 改为完整的 26…61，保护边界不动；240 tick 原短音符从 26…52 改为 26…61。推进早、晚和完整覆盖三组 PCM 分别不同。

这些检查确认控制没有重建、错移或错误寻址持续段。接缝的主观自然度和准确的 i/e 听觉分界仍需试听，不由 PCM 摘要或工作项相等代替。

## 保存、历史和缓存

- 工程新增可选 `Coverages` 集合，按过渡完整身份、所属音符稳定 ID 和 Part 存储。缺少该字段的旧数据视为空；旧 Phase/Onset 数值不重新解释。未知字段及不支持后端时继续保留原始数据。
- 增益修改、接点修改、Phase 修改和范围查询均携带 Coverage。逐 Part 编码保留其它 Part 的控制；删除/歌词替换的旧身份仍按既有生命周期为 Undo 保留，执行只绑定完整存活身份。
- `TransitionDocument` 和原生共享历史提交整份组合；音素 timing 修改仍通过原有组合事务提交。测试核对 Undo/Redo 后的音频和工程字节。
- 编译指纹显式包含 Coverage 侧别；生成的真实源帧映射进入现有原生快照和缓存指纹。保持原生 ABI 不变。相同查询命中局部结果缓存，增益不会使带源映射的结果冒充纯几何缓存。
- 双 Part 并发实际产生重叠渲染 scope，和分别重渲染结果一致。关闭测试源工程再打开后，原生缓存命中、无需进入合成 scope，音频保持一致。

## 验证结果

所有产物均使用独立 `--artifacts-path`，以最低 6.13.1.1 SDK 编译；分别在实际 6.13.1.1 和 6.13.3.1 DLL 下运行。

| 验证 | 每套 SDK |
| --- | ---: |
| 真实源覆盖/推进/音量/起音与收尾/短音符对照 | 36 次渲染 |
| 中文复韵母的保存、共享撤销、VEL/timing、音量、并发、缓存重开 | 15 个渲染步骤 |
| 既有组合回归，启用 Coverage 与 Phase，含复制、保存失败、删改、独立选样及共享接点 | 40 个渲染步骤 |

36 组源覆盖对照跨 SDK PCM 全部逐字节一致。每套的五组撤掉控制后恢复对照中，四组逐字节一致，一组只有一个 PCM16 采样相差 1 LSB；该差异落在此前独立测得的原生重复渲染范围内（峰值≤2 LSB、RMS≤0.1 LSB），没有加入生产音频兜底或重试。

纯查询区间断言没有 Start/Step/PCM 请求。含首次查询和同值缓存再查询的一对调用，中位耗时约 0.16 ms、最大约 2.63 ms，限于此夹具和设备；不含首次准备整个输入或实际音频合成，不能当作任意工程的性能上限。

托管测试 249 项、Rust 测试 47 项通过；完整 Debug/Release 解决方案构建通过。Release 有既有 Rust 链接器 stdout 编码警告，零错误。共享接点真实覆盖了 stage 0/1；反向区间映射有模型测试和已核对原生分支，当前这批实际声库夹具没有形成反向 ART，不声称其已完成真人声库全覆盖。

证据见 [transition-source-coverage-backend-20261018.json](evidence/transition-source-coverage-backend-20261018.json)。`voicebank/tools/summarize_source_coverage.py <临时根目录> <输出 JSON>` 可重新检查并汇总三套探针报告，WAV/VPR 夹具留在临时目录。此前延长音素末端时 raw score 坐标错误关联的研究记录仍有效，本轮没有修复或掩盖该另项问题。

## 复现入口

1. 独立构建 `evec_render_harness`，使用 `--source-coverage-probe --patcher-dse-initialization <输出目录>`。
2. 独立 Debug 主项目增加 `-p:PatcherIsolatedValidation=true`；通过 `VOCALOID_DURATION_HOST_ASSEMBLY` 指向该程序集，`--transition-host-probe` 运行生产宿主适配层。
3. `VOCALOID_TRANSITION_COVERAGE=1` 选择中文复韵母生命周期验收；`VOCALOID_TRANSITION_COVERAGE_GENERAL=1` 加 `VOCALOID_TRANSITION_PHASE=1` 运行既有完整组合验收。
4. 原生 DLL 使用已认证的 Release 构建，通过 `VOCALOID_TIMING_PROBE_NATIVE` 指定临时副本。`VOCALOID_PROBE_EDITOR` 与临时 harness 中的托管 SDK 必须匹配实际被验收版本。

安装目录的主补丁及原生 DLL SHA256 均与本轮开始前一致，见证据文件。

后续前端已接入菱形与精确面板，交互策略及独立验收见[完整源覆盖的菱形交互](transition-expression-diamond.md)。该前端改动尚未部署。
