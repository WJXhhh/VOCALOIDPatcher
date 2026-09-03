# VOCALOID 6 MCP 实施路线图

> 实施状态（2026-08-13）：阶段 0～8 已实现；阶段 3～8 的 V6 6.13.0.1 宿主关键路径验证见 [v6-mcp-stage-3-8-host-validation.md](v6-mcp-stage-3-8-host-validation.md)。阶段 0～2 的协议/宿主验证和性能基线见 [v6-mcp-stage-0-2-validation.md](v6-mcp-stage-0-2-validation.md)，可重复宿主记录格式见 [v6-mcp-host-validation-template.md](v6-mcp-host-validation-template.md)。

本文档用于安排 VOCALOID 6 MCP 后续开发。路线图以“完成后立即可独立使用的纵向切片”为单位，不再把读取、写入、Schema、撤销、UI 刷新和测试拆散到相距很远的阶段。

截至 2026-08-12，MCP 已覆盖工程与选区读取、轨道和 Part 编辑、音符与歌词/音素编辑、G2PA、原生歌唱参数、速度和拍号、播放定位、撤销/重做、工程文件、格式转换、混缩、revision guard、单写租约、幂等请求和基础 dry-run。核心工程编辑完整度约为 70%–80%，对 V6 Editor 全部操作的覆盖约为 40%–50%。

## 规划原则

### 1. 只交付完整纵向切片

一项功能只有同时满足以下条件才算完成：

1. 能力发现：catalog/capability 能准确说明当前版本是否支持。
2. 读取：写入前后都能通过 MCP 读回相关状态。
3. 写入：校验、权限、revision、幂等和危险操作确认完整。
4. 原子性：工程修改进入正确的原生 Transaction，并形成预期的撤销步骤。
5. 宿主同步：Editor UI、播放/渲染状态和必要的 Patcher 派生数据同步更新。
6. dry-run：复用执行路径的纯校验逻辑，不调用 setter、不写文件、不触发渲染。
7. 错误模型：能定位 operation、字段和失败原因，并明确是否已回滚。
8. 验证：协议测试、Bridge/Facade 测试和用户准备好的 V6 宿主内行为矩阵均完成。
9. 文档：Schema、最小示例、批量示例、版本限制和安全边界同步更新。

如果上述任一项暂时无法完成，该能力保持内部或 capability 为不可用，不以“实验性已支持”对外暴露。不得先发布只有 setter、没有读取/撤销/验证的半成品，再等待后续阶段补齐。

### 2. 先建立公共前提，再开发领域功能

后续功能共同依赖的实体引用、事务、结果、Schema、事件和测试夹具必须先稳定。领域功能不得各自实现一套临时 ID、分页、事件、错误或撤销协议。

### 3. 接口先行，但接口与首个实现同阶段落地

公共接口不能只设计不使用。每个新公共抽象必须在同一阶段由至少一个现有能力接入并经过宿主验证，否则无法证明接口足够且会造成后续返工。

### 4. 不用未来功能作为当前功能的完成条件

例如，BVL 接入不能等待完整 Mixer；效果器编辑不能等待录音；视图导航不能等待所有编辑工具。若一个切片确实依赖另一项能力，就把依赖项移到前置阶段，而不是留下跨阶段 TODO。

## 统一架构边界

为减少多人或多分支开发时的冲突，新增实现按以下边界放置：

| 层 | 职责 | 禁止事项 |
|---|---|---|
| `VOCALOIDPatcher.McpBridge` | DTO、错误码、分页/实体 ID/事件等传输协议 | 不引用 Yamaha/VSM/WPF，不包含领域实现 |
| `VOCALOIDPatcher.McpServer` | MCP Tool/Resource Schema、参数绑定、到 Bridge 的薄转发 | 不复制业务校验，不解释 Yamaha 对象 |
| `Mcp/Core/` | 实体解析、事务编排、结果映射、能力注册、事件总线、公共校验 | 不包含某一领域的具体 setter |
| `Mcp/Domains/<Domain>/` | Notes、Structure、Parameters、Effects 等领域的查询、校验和执行适配 | 不自行定义另一套权限、revision、ID 或事件机制 |
| `Patch/Patches/` | 仅捕获 V6 必需生命周期和模型事件 | 不在 Harmony 热路径构造大 DTO 或执行 MCP 请求 |
| `VOCALOIDPatcher.McpTests` | 协议、Companion、公共基础设施和假 Bridge 测试 | 不宣称替代真实 V6 宿主验证 |

现有 `VocaloidMcpFacade.*.cs` 可渐进迁移，不做一次性大重写。新领域优先放入独立目录；公共 Facade 只保留 dispatch 和兼容入口。

### 对接契约

每个领域通过统一注册描述自身能力：

- query kind 与过滤器；
- operation discriminated union；
- capability ID、版本探测和不可用原因；
- 实体种类及稳定 ID 解析器；
- 纯校验器与事务内执行器；
- 结果投影器；
- 修改后事件及定向刷新策略；
- 宿主测试矩阵。

领域模块不得直接修改 Companion 工具清单。Tool 层只消费注册表生成或组合 Schema，以免每增加一个 operation 都同时争抢 `VocaloidTools.cs`、Facade switch 和 capability record。

## 总依赖顺序

```text
阶段 0 基线与契约冻结
  └─阶段 1 公共写入内核
      └─阶段 2 查询与事件内核
          ├─阶段 3 Patcher 扩展参数闭环
          ├─阶段 4 Mixer 与效果器闭环
          ├─阶段 5 Audio Part 闭环
          ├─阶段 6 原生语义编辑闭环
          ├─阶段 7 视图、选区与 Transport 闭环
          └─阶段 8 原生导入与工程生命周期
              └─阶段 9 录音及其它高风险控制
```

阶段 3–8 都依赖阶段 0–2，但彼此不设整体阶段依赖。公共内核稳定后可按资源并行开发，且每个切片必须独立完成。阶段 7 只依赖阶段 2 的事件/等待模型，不等待阶段 3–6 全部结束；阶段 8 只依赖已有长任务以及阶段 2 的文档替换事件和稳定错误模型。阶段 9 依赖阶段 7 的 Transport/设备状态语义和阶段 8 的文件生命周期安全，因此最后处理。

## 阶段 0：基线、契约和验证夹具

目标：先冻结现有行为和公共术语，避免开发新功能时无法判断兼容性或回归。

交付内容：

- 为现有工具建立 capability ID、operation ID 和错误码清单，保留当前工具名和参数兼容性。
- 记录当前 `EntityRef`、revision、page cursor、idempotency、write lease 和 job 的契约测试。
- 为现有 Structure、Notes、Parameters 和 G2PA 各选一个最小操作，建立 Facade 层可重复测试夹具。
- 建立宿主验证记录模板：V6 版本、前置工程状态、请求、读回、undo、redo、UI/渲染结果。
- 给所有能力增加 `implemented`、`host_verified`、`minimum_editor_version` 和 `unavailable_reason` 状态；未经宿主验证时不得宣称完整支持。
- 记录现有性能基线：1000/10000 个音符和高密度控制点下的查询时间、Dispatcher 占用和响应大小。

完成条件：

- 现有 MCP 自动化测试全部通过。
- 不启动或部署 Editor 的测试可以稳定复现协议与公共错误行为。
- 至少完成一次由用户准备 V6 的基线宿主回归，确认现有四类 mutation 的事务、revision 和 undo/redo。
- 后续阶段可以在不改动既有 Tool 名称的前提下扩展 Schema。

本阶段不开发新用户功能；它是唯一允许只形成开发基础而不增加能力的阶段。

## 阶段 1：公共写入内核

目标：一次解决稳定引用、跨领域事务、dry-run、逐项结果和幂等映射，后续领域只实现自己的校验与执行。

### 1.1 稳定实体引用

- 在现有下标引用外增加 project generation 内有效的稳定 `entity_id`。
- 优先使用可安全读取且随对象移动保持稳定的 VSM 标识；若不存在，Bridge 维护弱引用或可重建映射，不持有跨线程 Yamaha 对象强引用。
- Track/Part/Note 插入、删除和移动后返回 index remap；工程替换后旧 ID 返回 `stale_project`。
- mutation 支持 `client_tag`，结果按标签返回，避免客户端靠数组顺序猜测对象。

### 1.2 统一事务与结果

- 新增 `v6_apply_operations`，可在一个原生 Transaction 中混合现有 Structure、Notes、Parameters 和 G2PA 操作。
- 支持请求内 `temp_id`，后续 operation 可引用刚创建的 Track、Part 或 Note。
- 返回逐项 `created`、`updated`、`deleted`、最新引用和安全摘要。
- 失败返回 operation index、字段、错误码、`rolled_back` 和 retryable 状态。
- 现有领域工具继续保留，并复用同一执行内核。

### 1.3 纯校验与 dry-run

- 将校验从 setter 分支中抽出；dry-run 与执行共享实体解析、范围和跨对象约束。
- 审计当前在 `execute == false` 时提前返回的 note update 等路径。
- dry-run 返回预计创建/修改/删除数量、危险确认需求和临时对象映射，不修改 revision。

完成条件：

- 单请求完成“创建 Track → 创建 Part → 添加 Note → 设置参数/G2PA”，只产生一个 undo 步骤。
- 任一中间操作失败后，模型、revision 和 UI 均不存在部分成功。
- 同一 `client_request_id` 重试返回相同对象映射且不重复执行。
- 现有四类 mutation 已至少各迁移一个 operation；公共接口有真实使用者，不是空壳。
- 插入、删除、移动和工程替换的稳定 ID 行为通过宿主验证。

## 阶段 2：查询、能力与事件内核

目标：让调用方能高效读取、等待并发现能力，为所有后续领域提供统一观察面。

### 2.1 查询执行器

- 在遍历模型前应用 Track、Part、绝对/相对 tick、音高、语言、声库和选中状态过滤。
- 支持字段 projection、歌词/音素文本搜索和 `changed_since_revision`。
- 参数查询支持范围摘要、时间桶降采样和显式原始点模式。
- 设置扫描数量、返回字节数和 Dispatcher 占用预算；超限返回可继续 cursor 或 `query_too_large`。
- 分页继续绑定 project ID 和 revision，不先构造全工程 DTO 再 `Skip/Take`。

### 2.2 自描述能力

- operation 使用强类型 discriminated union，或由领域注册表提供等价的完整 Schema resource。
- catalog 返回合法枚举、范围、默认值、单位、适用对象和版本要求。
- capability 从大类布尔值细化为具体 query/operation，并区分 unsupported、busy、permission denied 和 temporarily unavailable。

### 2.3 事件与等待

- 建立单调事件序号和有界事件缓冲，不在事件中携带完整歌词、音素或工程内容。
- 覆盖 `project_revision_changed`、`document_replaced`、`selection_changed`、`active_part_changed`、`transport_changed`、渲染生命周期、job progress 和 write lease revoked。
- 若 transport 不适合主动推送，提供可取消、有超时、带 `after_event_id` 的 `v6_wait_event`。
- 提供 `wait_for_revision`、`wait_for_render_idle` 和 `wait_for_playback` 的语义化封装；等待不得占用 WPF Dispatcher 或无限持有写租约。

完成条件：

- 大型工程分页只扫描满足过滤条件所需的模型范围，并有性能回归测试。
- 用户从 UI 修改工程后，客户端能收到 revision 事件并重新读取；旧 cursor 和旧引用正确失效。
- 修改参数后可以等待真实渲染稳定再 mixdown，无需高频轮询完整 state。
- 现有至少两个领域通过注册表发布 Schema 和 capability，证明对接模式可复用。

## 阶段 3：Patcher 扩展参数闭环

目标：让 Agent 能操作用户实际看到的增强版 V6，而不只操作原生 VSM 数据。

前置依赖：阶段 1 的统一实体/事务/结果，阶段 2 的注册表、查询和等待模型。

先建立扩展参数注册表，每个参数声明：

- 稳定 ID、`source: patcher`、作用域和值类型；
- 范围、默认值、是否可清除和持久化来源；
- 读取、纯校验、写入和撤销协调器；
- UI 刷新、Changed 合并和昂贵派生物重建策略；
- capability 与宿主验证状态。

按以下顺序交付两个独立切片：

1. BVL：按音符读取、批量写入、清除、默认值、缓存与重建状态、undo/redo、等待最新 generation 完成。
2. Register Shift / DSE Pitch Layer：读取、设置、清除、渲染支持状态、undo/redo 和原生/回退路径诊断。

每个切片完成后即可单独发布；BVL 不等待 Register Shift，Register Shift 也不反向修改 BVL 协议。后续自定义参数只注册新适配器，不新增专用 Tool。

完成条件：

- MCP 写入后 UI 立即反映，派生波形按 Part 去抖重建，等待工具只等待最新 generation。
- 一次批量写入形成一个符合现有自定义历史协调器语义的撤销步骤。
- 原生 Controller 与 Patcher 参数不会因名称相同而混淆。
- 连续数十次写入没有视觉树重挂、重复刷新或后台重建任务堆积。

如果产品目标优先是“增强版 V6 的实际可控性”，本阶段应紧接阶段 2；如果只追求原生 V6 对齐，可与阶段 4–6 并行但不能拆散自身闭环。

## 阶段 4：Mixer 与效果器闭环

目标：完整操作一个明确层级的原生效果链，而不是一次铺开 Master、Track、Part 后都只完成一半。

按三个独立切片顺序推进：

1. Track Mixer 静态状态：读取和编辑静态音量、声像、Mute/Solo，明确区分静态值与自动化曲线。
2. Track 效果链：查询顺序、类型、旁路和参数；插入、删除、移动、清空及批量参数编辑。
3. Part 与 Master 效果链：复用相同协议，只增加目标解析和 V6 版本能力。

每个切片都同时提供 effect catalog，包括 Gain、De-Esser、Compressor、EQ、Distortion、Chorus、Phaser、Tremolo、AutoPan、Delay、Reverb 等当前版本实际存在的效果、参数范围、默认值、单位和 GUID/版本能力。

完成条件：

- 每个层级写后可读回、可 undo/redo，UI、播放引擎和 mixdown 结果同步。
- 效果链移动和批量参数修改分别只有一个撤销步骤，失败全部回滚。
- 未安装、GUID 不匹配或版本不支持时返回 `unsupported`，不写入猜测值。
- Track 切片完成即可发布，不等待 Part/Master；后两者只能复用协议，不能返工 Track 契约。

## 阶段 5：Audio Part 闭环

目标：从“能插入音频文件”提升为“能完整读取和编辑 Audio Part”。

交付内容：

- 查询源文件安全标识、源 Region、采样率、声道、时长、Part 位置和适用增益属性。
- 创建、替换源文件、移动、裁剪 Region、调整长度及删除。
- 按实际 V6 能力支持 Normalize Wave、淡入淡出、增益或时间伸缩；无法原生事务化的功能不纳入本切片。
- 丢失媒体、格式不支持和文件不可访问的结构化诊断。
- 文件路径继续经过 V6 侧 allowlist、重解析、UNC/设备路径/ADS/符号链接/junction 逃逸校验。

完成条件：

- 一个 Audio Part 从查询、替换、裁剪到 mixdown 可形成独立工作流。
- dry-run 能验证文件与 Region，但不打开写句柄、不改变媒体缓存。
- 替换或裁剪失败不留下半更新 Part；undo/redo 可恢复原媒体引用和 Region。

## 阶段 6：原生语义编辑闭环

目标：调用 V6 自己的业务语义，避免客户端用底层 setter 复制邻接、连音、参数迁移和边界规则。

每个命令作为独立可交付切片，推荐顺序：

1. Transpose、起始位置量化、时值量化。
2. Split/Join Note、Staccato、Normalize、Insert Rest/Silence。
3. 歌词 Shift Left/Right、Reset、Extract、批量插入、音素锁定和音标转换。
4. Join Parts、Half/Double Tempo、Duplicate Track。
5. 参数选区重置、区间删除、区间平移/缩放和值域限制。

切片只在找到对应原生入口、完成版本探测并验证原生选区/边界/撤销语义后发布。优先扩展 `v6_run_job`；需要与其它 operation 同事务组合时，再注册为统一 operation。

完成条件：

- dry-run 返回实际影响范围、对象数量、冲突和裁剪信息。
- Split/Join、歌词移动和音素操作保护跨音符 G2PA 上下文。
- 每个命令完成后即可独立调用，不依赖列表中后续命令。

## 阶段 7：视图、选区、标记与 Transport 闭环

目标：支持 Agent 与用户在同一个 Editor UI 中协作，并提供可靠的播放/渲染同步原语。

按独立切片推进：

1. 选区：Track、Part、Note、控制点、Tempo、Time Signature、Master Volume，支持替换、追加、切换和范围语义。
2. 导航：确保对象/时间范围可见、滚动、缩放、Zoom to Selection/Full、查询当前 viewport。
3. 面板：参数区与参数类型、Lower Zone、Mixer、Inspector、Media Browser 的可见性。
4. Transport：pause/resume、循环、Start/End Marker、网格前后移动、播放起始模式和适用的播放速率。
5. 编辑工具：Arrow、Pencil、Line、Scissors、Pitch、Vibrato、Expression、Timing；只通过语义状态切换，不模拟鼠标。

纯 UI 导航不取得写租约、不增加工程 revision、不产生 undo；模型选区和 Marker 是否影响 revision 必须跟随原生行为。每个切片使用阶段 2 的事件/等待机制验收，不能在后续 Transport 阶段才补导航状态读回。

## 阶段 8：原生导入与工程生命周期

目标：补齐 V6 原生工程合并/替换行为，并与 LibreSVIP 转换明确区分。

按独立切片推进：

1. Revert，以及脏工程的保存/放弃/取消确认结果。
2. VPR、VSQX、PPSF、MIDI 原生导入当前工程。
3. Tempo/Time Signature 专项导入。
4. 标准音频原生导入工作流。
5. 最近工程只读查询。

每个导入切片都必须使用长任务、进度、取消、document/revision 事件和文件安全策略。原生导入不得复用 LibreSVIP 转换结果冒充 V6 原生合并语义。

完成条件：

- 导入失败或取消不会留下半提交工程。
- 工程 replacement 立即使旧 project ID、entity ID 和 cursor 失效。
- 长任务准确区分 cancel requested、native canceled、completed after cancel 和 failed。

## 阶段 9：录音及其它高风险控制

目标：在前述权限、事件和长任务模型稳定后，谨慎增加会影响外部设备、文件或应用生命周期的操作。

候选能力：

- 录音轨道准备、输入设备状态、count-in、开始、停止和结果文件状态。
- Playback Guide Sound、节拍器和全局 Audio Play 状态。
- Close Project、Exit 等应用生命周期操作。

这些能力默认 capability 关闭，必须逐项确认，且不得为了自动化验证自行启动、关闭、部署 V6 或开始真实录音。每项能力单独安全评审和宿主验收，不以“阶段整体完成”为由批量开放。

## 并行开发与冲突控制

阶段 0–2 由单一集成分支顺序完成，因为它们会改变公共协议和核心文件。进入阶段 3–8 后可并行，但遵守以下规则：

- 每个分支只拥有一个 `Mcp/Domains/<Domain>/` 和对应测试目录。
- 公共 DTO、错误码、事件类型和注册接口只能通过小型“契约变更”先合入，再由领域分支消费。
- 不在领域分支直接修改 Facade 大 switch、Tool 清单或 capability 大 record；使用注册表接入。
- 一个 PR 不同时重构公共内核和实现两个领域功能。
- 新字段保持向后兼容；删除或改义必须引入协议版本和迁移期。
- 领域事件只发布标准化实体 ID、revision 和安全摘要，不向事件总线泄漏 Yamaha 对象。
- Patcher 自有参数、原生 Controller、效果参数分别使用不同 namespace/source，避免 ID 和 operation 名冲突。

推荐 PR 拆分方式不是“先协议 PR、再读取 PR、再写入 PR”，而是：

1. 必要且最小的公共契约 PR；同 PR 带一个现有能力的适配与测试。
2. 单个完整领域切片 PR；含 query、operation、capability、事件、测试和文档。
3. 宿主验证修正 PR；只修复实际 V6 行为差异，不顺带扩展下一项能力。

## 横向质量要求

所有阶段持续满足：

- Yamaha/VSM 对象只在 V6 WPF Dispatcher 上即时取得和使用，不跨线程缓存。
- 工程修改通过原生 Transaction 提交；失败安全回滚，一次批量调用只形成预期的撤销步骤。
- 修改后依赖原生模型通知和必要的定向刷新，不调用会重复附加 observer 的通用刷新路径。
- 版本敏感入口先做能力探测，找不到目标时安全降级并返回结构化原因。
- 文件访问继续执行 allowlist、UNC、设备路径、ADS、符号链接和 junction 逃逸检查。
- 取消是协作式的，并准确报告最终状态；不得让播放、渲染、录音或文件操作拖垮 Editor。
- 不在热渲染路径、MouseMove 或逐控制点循环输出高频结构化日志。
- 不记录歌词、音素、工程内容或完整路径，除非定位错误确有必要且已最小化。

## 验证门禁

每个纵向切片必须通过三层验证：

1. Companion 层：stdio 与 Streamable HTTP 的工具枚举、Schema、认证、结构化结果和错误映射。
2. Bridge/Facade 层：权限、revision、幂等、分页、稳定 ID、dry-run、回滚、事件和路径策略。
3. V6 宿主层：真实原生事务、UI 同步、undo/redo、播放/渲染影响和版本兼容。

宿主行为矩阵至少包含：

- 成功、边界值、无效引用、setter 拒绝和事务中途失败。
- mutation 后立即读回、undo 后读回、redo 后读回。
- 用户通过 UI 修改后 revision/事件变化，旧 cursor/引用失效及重新读取。
- 连续数十次 mutation 后 ModelChanged、Renderer 和 AudioPlayer 回调没有重复订阅或倍增。
- 渲染中、播放中、脏工程、空工程、无活动 Part、Standard/AI Part 和受支持版本差异。
- Patcher 数据与原生事务之间的撤销顺序、刷新合并和派生重建去抖。

假 Bridge 集成测试只能证明 MCP 协议与 Companion 行为，不能替代宿主验证。代理不得为了测试自行启动、关闭、部署 VOCALOID Editor。

## 阶段完成检查表

每完成一个阶段或独立切片，在合并前逐项确认：

- [ ] 所有硬依赖已合入并有稳定契约，没有引用尚未开发的未来能力。
- [ ] Query、Mutation、Capability、Schema、Event/Wait、Undo/Redo、dry-run 已形成闭环。
- [ ] 现有客户端仍可调用旧工具，协议变化向后兼容。
- [ ] 自动化测试和对应 V6 宿主行为矩阵均通过。
- [ ] 文档示例可以从一次 state/query 开始独立跑通，不要求读者猜测隐藏步骤。
- [ ] capability 只开放已经完整实现且完成宿主验证的能力。
- [ ] 没有遗留“等后续阶段完成才能正常使用”的 TODO；若仍有依赖，本切片不得标记完成。
