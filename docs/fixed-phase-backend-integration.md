# 固定 timing 范围：正式进度后端与菱形接入

后续修正：[修改发音后的目标生命周期与延迟映射](transition-pronunciation-render-lifecycle.md)。涉及主补丁和原生 DLL，验收范围及数值差异见该记录。

2026-10-18。承接 [固定范围同音研究](fixed-range-expression-comparison.md)。用户授权接正式后端和菱形，要求验证保存、撤销、缓存和并发，暂不部署。本轮生产源码接入完成，验证使用隔离 artifacts 和独立 SDK 进程；安装文件未改变，未启动或操作用户 Editor。

## 数据语义与实际执行

新增独立的 `TransitionPhaseEdit` / 工程 `Phases`，保存 ART 身份与无量纲 `Amount∈[-1,1]`。其数学语义为 `transition.fixed-phase.v1`：Split 两侧分别按 `u→u^(2^(2*Amount))` 读取本侧源进度。零值为原样；正值偏后展开，负值偏前展开。每侧首尾、Split、帧坐标与标志都保留，任何源位置均不离开原侧范围。不是输出 WAV 的时间拉伸或裁剪，也不改变增益参数。

`TransitionPhaseMapping` 是纯模型变换，不能生成或修改 timing anchors。`PreparedCoefficientTiming.QueryTransitionControls` 先查询完整原有控制组合（音素 timing、过渡端点、增益及旧偏移），再在该分配上计算 phase。返回 `RenderAnchors` 与原组合完全相同，只有对应 ART 的源位置变化。未编辑的 ART/STA 不复制新源位置；规划期 STA 相对游标与运行期不同的问题不会被扩散到持续段。

复用现有不可变 Part/epoch 发布：系数、warp、gain、源帧表一起提交。native mapper 完成时只更新新建区间，原生消费时核对行身份与源位置，源帧表已参与原生缓存身份、过期代次保护及多 Part 路由。本轮未改 Rust、导出结构或签名，保持 clock ABI 15 / transition ABI 2。

旧 `Onsets.OffsetSeconds` 仍是旧版的范围偏移，字段、解释和原有声音均保留。与 phase 同时存在时，先执行旧组合，再把 phase 映射到该组合已有范围；最终每个 ART 只发布一张源表。旧偏移不会被偷换为百分比进度，phase 归零也不会删除它。

对于只有端点帧、或者源位置恒定的片段，固定端点曲线在当前分辨率下自然为恒等。执行仍按该数学定义进行，已有设置保留；UI 禁止无意义的新拖动并说明原因。资料、拓扑或源帧不完整仍明确拒绝，不能以跳过控制冒充成功。

## 菱形交互

- 每个有可调内部进度的入音、收尾或其它 ART 都可显示选中菱形。菱形放在较长一侧的“读到该侧一半源进度”的位置，随参数在既定范围内移动。
- 横拖只改 `Phases.Amount`，越界限制为 ±100%；右移偏后展开，左移偏前展开。Shift 微调，Ctrl+单击只将该 ART 的进度归零。
- 位移始终从按下时的原值和原分配计算，不用上一帧候选叠加，避免来回拖产生累积变形。鼠标过程只提交最新候选查询，MouseUp 才提交一条共享撤销和一次音频失效。
- 精确面板增加“段内进度（%）”。只写用户实际编辑的字段，不因显示精度而顺带改动 timing 或 dB。旧起音偏移有保留提示，“重置过渡”明确清除全部该段控制。
- 左右端点仍是显式 timing 控制，与新菱形独立。调整 phase 不触发端点可达范围扫描，不进行任何音频合成。
- Escape、LostCapture、切换源和过期候选沿用现有取消／代次机制。短片段可经精确面板选择；没有内部可调帧时保留其存储值。

## 保存与共享撤销修正

工程仍使用版本 1 的有界扩展记录。`Phases` 和既有 GUID 音符身份、左右音素、stage、方向、重复序号绑定；增加取值、重复项、Part 归属与未知嵌套字段检查。删除音符时 phase 数据供撤销保留，但不能保存或发布到空位置、同位置新建音符。

本轮修正旧数据未含新字段时的两个问题：

1. 空操作按规范化后的语义比较，必须在停止渲染前返回；不能因为补出 `Phases:[]` 或调整 JSON 排版而创建撤销或停止异步服务。
2. `TransitionDocument` 的共享撤销持有原始不可变归档。未发生音符重定位时 Undo 恢复原字节及 saved 状态；发生原生移动时才按已有稳定 GUID 重定位。未知字段和原始数据不能因接入新参数而丢失。

实际旧格式 VPR 测试覆盖无后端打开、原字节保存、空操作保持异步服务、加入 phase、撤销恢复旧字节和旧音频。其无 phase 音频还与先前已部署后端的同夹具 PCM 比较，完全一致。

## 验证结果

证据汇总：[正式接入验证](evidence/fixed-phase-integration-20261018.json)。

- 使用最低受支持 6.13.1.1 SDK 编译，分别加载真实 6.13.1.1 / 6.13.3.1。固定范围同音对照每版 16 次，包括原始 timing 和预先编辑过 timing：基准、越界参考、纯进度入音快慢、纯进度收尾快慢、归零。两版本对应完整 PCM 相同，新正式后端与已认可研究原型的对应 PCM 相同。
- 两版本各 40 个宿主渲染结果核对（包含缓存命中），对应 PCM 相同。覆盖 phase-only、相同值不生成历史、共享 Undo/Redo、原生移动/删除/同位置重建、旧偏移、浮点 timing、dB、独立选样、保存失败回滚、保存重开及序列复制。
- 缓存重开验证 scopes=0 且读取过缓存 key；修改一个 Part 后另一个 Part 的 PCM 和缓存保留。两 Part 实际并发并与各自串行 PCM 比较相同。
- 日语 `n n#2 a` 夹具覆盖共享接点、多个 ART 的独立 phase 与独立增益、保存重开；不能把共享接点合并为一个进度控制。
- 隔离 WPF 检查生产菱形命中、与固定 Split 区分、Shift 微调、极端越界、回到原点无累积偏移；实际绘制菱形并检查对应像素。它不是用户真实 Editor 连续鼠标操作验收。
- 每组 51 个连续进度候选确认零次额外原生规划、零次 DSE Start/Step/PCM 输出。预热后中位查询约 0.016–0.038 ms；首次 JIT 等开销下单点最大约 4.2 ms。不能将该夹具数字推广到所有大 Part。
- 232 项托管测试通过，整个解决方案 Debug / Release 隔离构建通过，无警告、无错误；四语种 31 个过渡键一致。未改原生源码，无需新 ABI 迁移。

当前完成正式代码与独立宿主验证，尚未部署，也未在用户真实编辑器中操作新菱形。不宣称所有声库、极端参数或未知版本的主观音色都已验收。

## 复验入口

```powershell
# 主补丁验证仍须 Debug + 独立 artifacts + PatcherIsolatedValidation=true；不要写到已部署的 Release 符号链接目标。
$env:VOCALOID_FIXED_EXPRESSION_BACKEND = '1'
dotnet <harness.dll> --fixed-expression-probe --patcher-dse-initialization <独立输出目录>

$env:VOCALOID_DURATION_HOST_ASSEMBLY = '<隔离验证主程序集>'
$env:VOCALOID_TRANSITION_PHASE = '1'
$env:VOCALOID_TRANSITION_ONSET = '1' # 同时测试旧偏移
dotnet <harness.dll> --transition-host-probe --patcher-dse-initialization <全新独立输出目录>

# 只重验 WPF 绘制及拖动计算时，加 VOCALOID_TRANSITION_UI_ONLY=1。
```

宿主探针目录须全新，不能拿上次 VPR 输出目录重跑；否则会碰到既有文件，不能据此判断生产保存有问题。`VOCALOID_PROBE_EDITOR` 和 `VOCALOID_TIMING_PROBE_NATIVE` 仍分别选择合法真实组件与已认证原生库，研究 WAV、SDK、生成 DLL 不入库。
