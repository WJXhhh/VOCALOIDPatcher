# EVEC 在 VOCALOID 6 中的适配进度、反例与验收矩阵

本文是 EVEC 适配工作的权威进度账。它记录能复现的证据、被宿主实测推翻的假设、
当前实现状态和剩余验收项。`29_evec_complete_reverse_engineering_and_implementation_plan.md`
保留早期逆向资料，但不再作为“已经完成”的依据。

## 目标

在 VOCALOID 6 传统 DSE 轨道中，为支持 EVEC 的 Crypton V4X 声库提供接近 Piapro Studio
手感的 Voice Color、Voice Release 和 Consonant Extension：

- 不产生无声、错误 ART 路径、异常拖尾或选项互锁；
- 每个声库只展示其真实支持且在 V6 中可渲染的组合；
- 时机、力度感、短音符、连音和组合行为有明确契约；
- 多选、歌词重算、手动音素编辑、撤销/重做、复制、保存/重开均保持一致；
- 失败安全降级，不能让补丁拖垮编辑器或损坏用户原有表达参数。

## 证据等级

- **已证实**：来自官方配置、具体反编译调用链、实体 DDI/DDB 统计或宿主重复实测。
- **推测**：结构上合理，但尚未由目标声库和宿主行为共同验证。
- **待验证**：实现已存在，但缺少宿主矩阵结果。

## 2026-09-04 当前已证实事实

### Piapro 配置与数据模型

- PPS 的三槽顺序为 Slot 0 `CVV`、Slot 1 `VSil`、Slot 2 `CTop`，UI 名称分别为
  Voice Color、Voice Release、Consonant Extension。
- Miku EVEC 的 Common 配置是 divide `[45,45]` ms、limit `[30,60]` ms、min-v 45 ms；
  VSil 是 divide `[60,60]` ms、limit `[50,70]` ms、min-v 45 ms。这里的 limit 对是
  divide 插值公式的输入跨度，不是允许把 45/60 ms 分段任意压缩到 30–60/50–70 ms。
- Miku CTop 使用 302 `#2` 与 306 `#6`。
- Rin/Len Power EVEC 的 CTop 是 301，配置中没有 `phn-suffix`；映射明确写为
  `[306,301]` 与 `[302,-1]`。因此 Rin/Len 不能直接继承 Miku 的 CTop 菜单和后缀规则。
- Piapro 在 VSQX 中使用 `$pps(=)` 和 `$pps(/)` 保存拆分段。V6 当前“单音符多音素”模型
  与该结构不同，声学等价性必须逐项验证，不能由字符串相似性推出。

### V6 宿主反例

- Consonant Extension 开启后出现整音符无声。这证明“PHDC 中存在 `C#suffix`”不足以
  证明当前 V6 输入序列能命中可渲染 ART/STA 路径。
- Color、Release 与 Extension 曾互相锁死。直接原因包括：把时值分配失败当作整个状态
  事务失败，以及让 sidecar/cache 返回实际音素并未表达的组合。
- `SetPhonemes(..., isValidPhonemes, ...)` 的 Boolean 是音符有效状态。把 EVEC 写成
  `false` 会留下不可渲染/无效风险；它不是“跳过字典”的开关。

### 实体 DDI 的 CTop 路径（已证实）

- 使用固定版本公开解析器对本机已安装的 Miku Original EVEC、Rin Power EVEC、Len Power
  EVEC 做完整 PHDC/STA/ART 图统计：Miku 为 133 PHDC、36 STA、1,611 ART，其中 284 条
  三音素 ART；Rin 为 159 PHDC、36 STA、1,344 ART，其中 142 条三音素 ART；Len 为
  159 PHDC、48 STA、1,339 ART，其中 142 条三音素 ART。
- Miku 的 Mild/Accent CTop 不是把基础辅音替换为 `C#2`/`C#6`。实体 DDI 的内部三音素
  ART 键为 `C ^C#2 V` 与 `C ^C#6 V`；两组各 142 条，覆盖 35 个源辅音 token 与 5 个
  目标元音 token（按日语音拍稀疏分布，不是完整笛卡尔积）。
- Rin/Len 没有 `C#2`/`C#6` PHDC。其 142 条 CTop 三音素边统一为 `C ^C V`，与官方
  CTop 301“无 phn-suffix”配置相符。
- Miku 中虽能找到 `C -> C#2`/`C -> C#6` 二音素边，却找不到旧实现所要求的
  `C#2 -> V`/`C#6 -> V` 边。因此旧实现生成 `C#2 V`/`C#6 V` 会形成断路，足以解释
  “一开辅音延长整音符无声”。修正后的外部序列必须保留基础辅音。
- Rin/Len 的 PHDC 同时含有九组彩色元音/鼻音，但官方 Rin/Len EVEC 配置只暴露 Soft
  `#2` 与 Power `#6`。所以“PHDC 中存在某 token”不能作为 UI 能力判定；已确认旧扫描器
  会错误展示 Luka 专属颜色。
- 对当前实际暴露的组合又做了全图必要边审计：三库的 12 个可着色元音/鼻音均具备
  `V -> V#2`、`V -> V#6`，并且普通/`#2`/`#6` 三种结尾都能接 `*#1`、`*#2`；全部
  CTop 三音素条目也都有对应普通 `C -> V`。因此已知组合图中没有再发现 Color/Release
  断边，需安全限制的就是下述 Rin/Len 三条 plain consonant self-edge 缺口。
- 扩展到本机所有 Miku `_EVEC` 注册组件后，Original/Soft/Solid 三库分别为 1,611/
  1,610/1,607 条 ART，均含 284 条 `#2/#6` CTop、35/35 self-edge、两种 Release，能力
  配置可以共用。反例是注册名 `MIKU_V4X_Beta_EVEC`：其真实路径指向
  `MIKU_V4_Chinese.ddi`，只有 62 PHDC、0 CTop、0 Color 后缀边、0 Release token。
  因此名称中的 `_EVEC` 不能覆盖实体图反证；探测器改为实体 DDI 文件名优先后，该组件
  自然降级为不显示 EVEC，只有真实文件名也是 `MIKU_V4X_Beta_EVEC` 才采用官方 Beta 配置。

### CTop 强度与发音延长是两个独立维度（已证实数据语义）

- PPS 的 `PronunciationExtensionGroup` 另有 `ConsonantRepTimeBtn` / `consonantRepeatButton`，
  并存在独立的 `TopConsonantRepeatCountChangeCommand`。这不是 Miku 的 CTop 302/306 或
  Rin/Len 的 CTop 301 选择器。
- `FUN_101654b0` 在音符字段 `+0xF8` 与音符参数页字段 `+0xB8` 之间搬运该值，并创建独立
  撤销命令；`FUN_102255b0` 对写入值做换算和上限裁剪，最终内部计数范围为 0–3。
- `FUN_102216e0`、`FUN_10222cc0` 表明该计数参与首辅音的重组。调用对象实际是宿主 note
  对象 `+0xE4`，所以重组器读取的 `this+0x14` 正是 note `+0xF8` 的 0–3 重复计数；它先按
  该值追加普通首音素。若 Slot 2 的 CTop 指针非空，随后再把循环次数加一，并拼入 CTop
  `phn-suffix` 与剩余音素。因此“发音延长次数/长度”和“CTop 录音性格（Mild/Accent）”
  在 PPS 数据模型中确实是两个字段，不能继续共用一个下拉框。
- `FUN_10166b80` 取出的 CTop 值就是官方配置的 `phn-suffix`；`FUN_10222cc0` 先复制首
  辅音、再把该 suffix 直接附到最后一个副本。因此 PPS 写给音符的外部串是 Miku
  `C C#2/6 V`、Rin/Len `C C V`，而不是把 DDI 内部 caret 键直接写成音素。
- 独立延长计数为 N 时，无 CTop 是 `C` 后再重复 N 次；有 CTop 时会额外增加一份用于
  CTop 的副本，即 Miku `C [C×N] C#2/6 V`、Rin/Len `C [C×N] C V`。Miku 的 35 个
  CTop 源辅音都有 plain self-edge；重新按实体 ART 全量 35 个源 token 核对后，Miku 全部
  有 self-edge，Rin/Len 明确缺少 `Z Z`、`h\\ h\\`、`z z` 三条。后者的直接 `C ^C V`
  仍可承载一次 CTop/普通重复，但第二份及之后的额外重复需要不存在的 self-edge。
- 这也给出一个不能靠字符串消除的 V6 歧义：Rin/Len `CTop=301, repeat=0` 与
  `CTop=Normal, repeat=1` 都是 `C C V`。当前 live cache/sidecar 保存明确用户意图；没有
  元数据时采用确定性回退——第一份普通重复优先解释为官方 301，剩余份数才计入延长。
  这不是声称两个 PPS 字段相同，而是承认 V6 单一音素串丢失了原始字段身份。

### PPS 时值公式与标准 Accent 的边界（已证实）

- `FUN_10219630` 的汇编完整确认 `UEVECDivideInfo` 算法：先计算
  `divide0 + (divide1-divide0)/(limit1-limit0) * duration`，上限裁到 `divide1`；若结果
  低于 `divide0` 则返回 0。随后要求剩余时长至少为 `min-v`，不足时只允许把 divide 缩到
  `duration-min-v` 且仍不得低于 `divide0`，否则返回 0。
- 官方 Common/VSil 的两个 divide 端点分别相等，因此长音符的结果固定为 45/60 ms；
  `limit` 不会让 Short/Long 随音符长度变成比例值。Common 至少需要 90 ms，VSil 至少需要
  105 ms（含 45 ms 最小元音）；再短时 PPS 返回 0，即不建立该拆分，而不是强挤边界。
- V6 适配已改成相同短音符策略：所选录音和四项 EVEC 状态照常保存，仅跳过不成立的
  `SetEditedPhonemePosition`，保留 VSM 原生边界。可接受范围不包含精确目标时也不再回退到
  任意合法边界，因此时值失败不会回滚状态或把多个选项重新锁在一起。
- `CVCLDNoteEVECPage::CreatePanes` 只建立 CVV/VSil/CTop 与独立发音延长控件；PPS 的
  `SliderAccent`、`Vocaloid3NoteSetAccentCommandDetail` 与 EVEC command 是另一条调用链。
  当前没有证据表明 EVEC 存在额外连续“力度”字段，补丁不得借 CTop/Color 偷改标准
  Note Accent、Velocity 或 Decay；Mild/Accent 的力度感来自所选录音 ART ID。
- 继续闭合 UI 后确认延长控件的精确手感：`CreatePanes`（`0x10168C70`）构造
  `EVECConsonantExtension`（`0x101679F0`），固定循环 0–3 创建四个
  `ConsonantRepTimeBtn`（`0x10167830`）；第一档使用本地化资源，后三档文字为 `x %d`。
  数据提交函数 `0x10167E20` 写的是同一个离散计数，没有连续延长强度或毫秒滑杆。

### VSM 离线结构验证（已证实范围有限）

- 直接调用已安装 V6 的 `VIS_VSM_WIVSMNote_setPhonemes`，官方外部形式 `k k#2 a`、
  `k k#6 a`、`k k a`、四份首辅音组合以及 Color/Release 组合均可原样写回并保持
  `isValidPhonemes=True`。此前 caret 串也能通过，反过来说明 VSM 数据层不做强声学校验。
- 离线序列没有绑定实体 Voicebank/Renderer，因此这些探针的 phoneme positions 为空。它只
  证明 VSM 数据层接受 caret 序列，不能证明 DSE 已成功选中录音或宿主内有声。
- 重写后的生产重组器与独立逻辑 harness 已覆盖 Miku Mild/Accent、Rin/Len 301、0–3 档
  延长、鼻音、四维组合、任意切换、早期 caret/错误 suffix 迁移和全部清除；当前断言全部
  通过。该结果验证字符串和状态机，不替代宿主可听验证。
- 进一步把所有产品状态做成全有向切换矩阵：Miku 108 个状态共 11,664 条、Rin/Len 72 个
  状态共 5,184 条、Luka 30 个状态共 900 条。每一条都从源物理串切到目标物理串、剥离回
  同一基础音素并重新解析；Miku/Luka 精确回读，Rin/Len 的同形组合按设计交给 sidecar
  消歧。全部通过，证明字符串层没有“从某个组合进入后无法清除”的有向死角。
- 针对宿主复测暴露的“互相锁住”，已移除按“历史物理串→单个逻辑状态”保存的
  `Realizations`：同一 `C C V` 曾被后一次组合覆盖，撤销或回写时会复活错误组合。现在只在
  当前音素串仍与 live state 精确一致时使用缓存；不一致立即重解析并采用上述确定性回退。
  对 `C C V`、`C C C V`、五份 C 的上限组合新增回归，分别恢复为 301+延长 0/1/3。
- 同形逻辑切换若移除了 CTop，会先重置该 CTop 曾拥有的元音起点边界，再应用新状态时值；
  避免音素串虽未改变，旧 45 ms CTop 边界却残留，造成听感仍像旧选项的“声学互锁”。
- `EvecProjectArchive` 现有 `.vpr` sidecar 又做了真实临时 ZIP 写入—读取—清除回归，包含
  Rin/Len 301 + 延长 2 + Color + Long Release 的组合；原项目条目保留、全部字段回读一致，
  清空状态后 EVEC entry 正确移除。该测试补强持久化，但不替代 Editor 自身保存/重开。

### V6 `ConsonantOffset` 候选载体（已证实为当前传统 DSE 同步渲染的非载体）

- 6.13 的 `WIVSMNote` 暴露独立 `ConsonantOffset`，并发送 `DidUpdateConsonantOffset`；它不是
  Note Velocity、Accent 或手工 phoneme position。
- 新增离线 native probe 后确认：初值为 0，负数 -1/-10/-100/-1000 均被拒绝且不产生
  staged change；0、1、10、100、1000 均可写入并进入 VSM transaction。以 history commit
  写入 100 后，原生 Undo 恢复 0、Redo 恢复 100，说明它至少是一个原生可撤销字段。
- 当前离线 DSE 授权阻断使 `GetPhonemePositions()`/`GetOriginalPhonemePositions()` 仍为空，
  无法证明数值单位、饱和范围及它是否真正改变传统 DSE 辅音时长。因此尚未把发音延长改成
  `ConsonantOffset`，也不拿该字段暗中编码 sidecar。
- 进一步从运行时 note vtable 锁定实际 getter/setter RVA 为 `0xED980`/`0xED990`。getter
  只是读取 note 对象 `+0x118` 的 32 位整数；setter 进入 `0xEDFA0`，只完成非负校验、旧值
  撤销命令、字段写入和 `DidUpdateConsonantOffset` 通知，不在此处重算音素边界。6.13 托管
  `MusicalEditorViewModel` 收到该通知后也直接返回。
- 已继续闭合传统 DSE 同步渲染链：`WIVSMMidiPart_render`（RVA `0x1FD90`）→ `0x68C20` →
  `0x59B40` → score builder `0x5A620` → note event builder `0x575D0`。最后一个函数会解析
  note `+0xA0` 的音素字符串并写入时值、音高、Velocity/Expression、Vibrato 等 score 数据，
  但不读取 `+0x118`，也不调用 vtable `+0x2A8` 的 `ConsonantOffset` getter。结合全托管源码与
  VSM 直接引用筛查，可确认它不是当前传统 DSE 同步渲染的 EVEC 发音延长载体；除非以后在
  另一条实际启用的渲染模式中找到相反证据，否则按遗留/仅存储字段处理，不接入生产 EVEC。
- render harness 已能在 `Render` 后摘要 `HoldingScoreList`/`RenderingScoreList`；本机全部传统
  DSE 授权无效或过期时，所有用例均为 `render_result=NoError` 但 `score_frames=0`，因此该探针
  可带到合法授权环境复用，当前不能提供音频或 score 阳性对照。

### 绑定实体声库的离线 DSE 渲染诊断（授权阻断，不能判定 EVEC 声学结果）

- 新增 `voicebank/tools/evec_render_harness`，按 Editor 6.13 的真实初始化顺序创建 VDM、DSE、
  VSM，绑定实体 CompID、插入 tempo/time signature、重置 HMM weight/AI vibrato、提交序列并
  调用 `WIVSMMidiPart.Render`。它会解析实际输出 WAV 的 PCM peak/RMS，而不是只检查文件存在。
- Miku Original EVEC、Rin Power EVEC、Len Power EVEC 均可成功 `SetVoiceBankID`、插入普通
  `k a` 和 EVEC 音素、提交并得到 `Render=NoError` 的 44.1 kHz/16-bit WAV；但普通 `k a`
  与所有 EVEC 例都为全零 PCM。因此这批 WAV 不能用于比较 CTop/Extension/Release。
- 同一 DSEManager 直接报告三库授权状态：Miku Original EVEC 为 `InvalidKey`，Rin/Len Power
  EVEC 为 `InvalidTrialKey`。全机 35 个已注册传统 DSE 声库没有一个 `NoError`（7 Expired、
  7 InvalidTrialKey、21 InvalidKey）；唯一 `NoError` voice license 是 DNN 声库，不能作为传统
  DSE harness 的阳性对照。普通 `k a` 也全零与该授权状态一致。
- 因此当前离线渲染只证明完整 native 链已跑到 WAV 输出，并把阻断点收敛到 DSE 声库授权；
  它既不能证明修正后的 EVEC 一定有声，也不能把全零归因于音素重组。若要继续做自动可听
  A/B，需要至少一套在 DSEManager 中返回可合成授权状态的传统声库；否则仍以用户宿主内
  已授权环境的复测为准。不得研究或实现授权绕过。

## 已实施但仍待宿主重新验证

- 实际 `note.Phonemes` 是声学事实来源；sidecar/live cache 只在当前物理串仍精确一致时用于
  区分 Rin/Len 301 与一次普通重复，已删除会跨撤销/回写复活旧组合的历史 realization cache。
- 四个选项的状态提交不再被时值边界失败整体否决；多选中不适用音符不会回滚其它音符。
- CTop 强度与发音延长拆成两个控件、两个状态字段和独立更新入口；延长 0–3 以重复首辅音
  实现，CTop 再追加最后一份带 suffix（或 Rin/Len 无 suffix）的副本。
- EVEC 直写使用 `isValidPhonemes=true`，仍绕开 G2PA 的标准歌词重算路径并核对写回字符串。
- Release 目标锚定逻辑音符末端；`*#1/*#2` 是不同录音单元，而不是按音符长度比例缩放。
- Common/VSil 目标分别固定为 45/60 ms；不足 `divide + min-v` 的短音符只保留原生边界，
  不改变用户选择，也不把 VSM 可接受区间内的任意位置误当成 Piapro 时值。

上述项目只有编译和静态调用链验证，尚不能替代实际渲染。Extension 无声的 ART 断路根因
已经由实体 DDI 证实，但修正序列能否被 V6 `SetPhonemes`、边界编辑和渲染链完整接受仍待
宿主验证，不得标记完成。

## 当前硬问题与下一步证据

| 优先级 | 问题 | 当前判断 | 完成证据 |
| --- | --- | --- | --- |
| P0 | Extension 导致无声 | 已证实旧 `C# V` 断路；PPS 外部表达已锁定为 `C C# V` / `C C V` | 对每个可见 CTop 选项验证写回、边界与宿主可听输出 |
| P0 | Rin/Len CTop 301 | 配置、内部 ART 与 PPS 外部 `C C V` 已交叉证实；代码已接入 | 在双子声库逐元音验证切换与可听输出 |
| P0 | 组合互锁 | 已删除同形串历史状态复活路径，加入 Rin/Len 确定性回退和同形时值边界清理，待复测 | 四项任意顺序设置/清除均可逆，连续操作与撤销/重做不复活旧组合 |
| P0 | 发音延长计数 0–3 | 已接入独立状态/UI/sidecar，并通过字符串矩阵 | 验证四档可逆、听感递增、与 CTop 强度正交 |
| P1 | Release 时长与拖尾 | PPS 公式已逐指令复现；单音符模型仍可能与 Piapro 拆分段听感不同 | 多 BPM、变速、90/105 ms 临界、空隙/连音矩阵的边界与听感记录 |
| P1 | Color 切入与力度感 | 45 ms 规则仅是配置输入，不等于 V6 最佳声学结果 | 各元音、音高、力度和 Color/Release 组合的宿主 A/B |
| P1 | 歌词、手工音素、撤销与持久化 | 已有补丁但曾出现双事实源 | 逐项 round-trip，实际音素、UI、sidecar 三者一致 |
| P1 | V6 原生 ConsonantOffset | 已闭合同步 render→score→note event 链并确认不读取该字段；判为当前传统 DSE EVEC 非载体 | 仅在发现另一条实际启用且读取该字段的渲染路径时重开；否则不再作为发音延长方案 |
| P1 | 离线 DSE 可听回归 | native 渲染链已跑通，但本机全部传统 DSE 声库授权无效/过期，普通音也全零 | 在合法授权的 DSE 声库环境得到非零普通音阳性对照后再比较 EVEC |
| P2 | UI 手感 | Color/CTop/Release 使用下拉，延长已按 PPS 改为固定四段；本轮取消由当前 CTop 反向禁用延长的互锁 | 混合多选、最后操作优先、错误反馈、键盘操作与批量提交验收 |

## 工作原则

1. 先验证声库的完整可渲染路径，再把选项暴露给 UI；不能只扫描 PHDC 名称。
2. Miku、Luka、Rin/Len 与 release-only 声库分别建能力配置，不再用一个名称启发式兜底。
3. 状态修改和派生时值是两层：时值失败可以降级，但不能造成选项互锁或无法恢复标准。
4. 不再擅自修改 Accent、Decay、Velocity 等用户参数。若 PPS 证据表明确有力度控制，先建立
   可撤销、可恢复原值的独立适配层，再进入宿主验证。
5. 每条新结论都记录其证据来源和验证状态；商业声库只记录结构/计数/边关系，不提交文件、
   音频或专有载荷。

## 变更记录

- **2026-09-04**：建立进度账；登记 Extension 无声、组合互锁、Rin/Len CTop 301 无后缀、
  `isValidPhonemes` 语义和旧文档过度结论。开始扫描本机新安装的 Rin/Len 实体声库。
- **2026-09-04**：完成 Miku/Rin/Len 实体 DDI 全图核对。推翻“把首辅音替换为后缀辅音”
  的旧模型，确认 Miku 使用 `C ^C#2/6 V`、Rin/Len 使用 `C ^C V`；同时确认 PHDC-only
  能力扫描会给 Rin/Len 暴露不属于官方产品定义的颜色，进入代码修正阶段。
- **2026-09-04**：能力判定改用官方产品配置，新增 Rin/Len 301；状态提交前按当前声库
  归一化，避免残留的不支持项锁死其它槽。随后进一步反编译证明早期 caret 直写仍不对：
  caret 是 DDI 内部 ART 键，PPS 外部音素采用重复辅音 + suffix，代码进入第二次纠正。
- **2026-09-04**：从 PPS 独立撤销命令与音符字段确认“首辅音重复计数 0–3”。完成官方
  外部重组、第四维状态、UI、sidecar 向后兼容、同形状态安全回读和逻辑矩阵。V6 原生数据
  层接受全部新串；完整 Debug 解决方案 0 警告通过。翻译检查器因仓库外 `V6src` 缺失未能
  运行，但四份 XML 均可解析且 1,280 个键集合完全一致。Release/部署与宿主试听随后执行。
- **2026-09-04**：Release 主项目 0 警告构建完成；ILRepack 合并产物为 7,140,352 bytes，
  SHA-256 `CF08C8E3215C3482763AA33165721335EA87130A965E3A1E8D57CE52E01BE2AC`，最终
  程序集不再引用独立 `0Harmony` 且仍包含 `HarmonyLib.Harmony`。已在确认 VOCALOID6
  关闭后部署；Editor 端托管 DLL 与 `v6patch_clock.dll` 均和 Release 源哈希一致，未启动宿主。
- **2026-09-04**：补建实体声库绑定的离线 DSE 渲染 harness。官方初始化、音符提交和 WAV
  输出均成功，但普通音与 EVEC 全部为零；交叉检查确认目标三库分别处于 `InvalidKey` /
  `InvalidTrialKey`，且本机没有授权有效的传统 DSE 阳性对照。将该结果登记为授权阻断，
  不误判为 EVEC 音素失败，继续等待合法授权环境的宿主可听矩阵。harness Release 与完整
  Debug 解决方案均以 0 警告、0 错误构建；生产重组逻辑矩阵再次全部通过。
- **2026-09-04**：逐指令核对 PPS `FUN_10219630`，纠正早期把 limit 当作边界上下限的
  解释。新增可独立测试的 `EvecTimingMath`，精确复现 divide/min-v/返回 0 规则；V6 时值
  分配取消短音符和不可达目标的“任意合法范围回退”。Common 90 ms、VSil 105 ms 临界和
  长音固定 45/60 ms 测试均通过。另确认 PPS 标准 Accent slider 不属于 EVEC 状态，继续
  保留用户原有 Accent/Velocity/Decay。完整 Debug 解决方案与 Release 主项目均为 0 警告、
  0 错误；新 ILRepack 产物为 7,140,864 bytes，SHA-256
  `5BAFEACDBB18AD9D7A719B151D5F97CCEB72BF0D278168D16BCCD01DA2A7315D`。确认 Editor
  未运行后，安装目录符号链接读取到相同哈希；native clock 哈希也与 Release 一致。
- **2026-09-04**：补齐 17,748 条跨产品全有向状态切换与 `.vpr` sidecar ZIP round-trip；
  全部通过。修正时值变更判断，使 Rin/Len 同形串优先采用与当前物理串一致的逻辑缓存状态，
  不再因重新解析为“另一个同形组合”而误判其他控件也发生变化；SetPhonemes 异常写回时
  增加原音素/有效位/语言/边界恢复，避免批量部分成功时留下半写状态。另建立原生
  `ConsonantOffset` 范围与 Undo/Redo 探针，确认存储行为但尚未宣称声学语义。完整 Debug、
  两个 harness Release 和主项目 Release 均为 0 警告、0 错误；新合并 DLL 为 7,140,864
  bytes，SHA-256 `135A45E1C19265AD11FA02EE114FB1770D5A118C87986309579E5749E46FDF27`，
  Editor 未运行时安装端符号链接哈希一致。
- **2026-09-04**：继续逆向 `VSM.dll` 的 `ConsonantOffset`。运行时 vtable 与静态汇编交叉
  锁定 getter/setter/helper，确认字段位于 note `+0x118`，setter 不直接改音素边界，只做
  校验、历史、写值和通知；6.13 托管通知分支无刷新/重渲染动作。随后闭合传统 DSE 的
  `render → score builder → note event builder` 链，确认实际 score 构造读取音素字符串但不读
  `+0x118`/getter，故将其从“待证候选”降为当前同步渲染的非载体。render harness 同时增加
  score 摘要，但无效授权下所有用例仍为 0 帧。本轮只更新研究证据与 harness，不改运行时
  代码，也未重建/替换已部署 Release。
- **2026-09-04**：把 PPS `FUN_102216e0 → FUN_10222cc0` 的末段与汇编逐条闭合：确认
  `this+0x14` 等于宿主 note `+0xF8` 的重复计数，Slot 2 CTop 非空会在循环数上再加一，
  `FUN_10166b80` 的 suffix 随后直接拼入。由此确认 Rin/Len 301 与 repeat=1 在 V6 中确实
  同形。生产状态机删除会按同形字符串复活旧组合的 `Realizations`，无 sidecar 时优先把第一
  个重复解释为 301，并在同形移除 CTop 时清理旧时值边界。另增加按 EVEC 原生事务记录的
  轻量逻辑历史：Undo/Redo 后以实际音素/边界确认命中，再恢复该事务的 before/after 四维
  状态，解决 `Normal+延长1 ↔ 301+延长0` 无法从 `C C V` 反推的问题。新增 3 个歧义上限回归；
  逻辑 harness 的 17,748 条全有向切换与 sidecar round-trip 继续通过，完整 Debug 解决方案 0 警告、
  0 错误。主项目 Release 与 render harness 也均为 0 警告、0 错误；ILRepack 合并 DLL 为
  7,151,616 bytes，SHA-256 `219A20433F3300556DFB554C9A07B5E9EA3BC5839B86282FD35A27F42DB6C67B`。
  最终程序集含 `HarmonyLib.Harmony` 且无独立 `0Harmony` 引用；确认 Editor 未运行，安装端
  符号链接读取到相同哈希，native clock 两端哈希仍为
  `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。
- **2026-09-04**：通过 VDM 只读 `VoiceBank.Path` 找到三库真实 DDI，再用固定公开解析器
  重跑完整 ART inventory。纠正文档早先的 29/27 近似统计：35 个 CTop 源 token 中，Miku
  全有 plain self-edge；Rin/Len 共同缺 `Z Z`、`h\\ h\\`、`z z`。生产能力层因此按音符
  限制安全重复总数：无 CTop 时三类辅音最多延长 1；启用无后缀 301 后延长必须为 0，仍
  保留直接 `C ^C V` 的 Accent。切换 Accent 时会自动收回不再可达的延长值；Inspector 只
  展示当前选择共同可用的档位，避免形成无声串或“谁都解不开”的死组合。逻辑 harness
  17,748 条切换/sidecar 回归及 `h\\`/`Z` 辅音识别继续通过，完整 Debug 解决方案、render
  harness Release 与主项目 Release 均为 0 警告、0 错误。ILRepack 合并 DLL 为 7,152,640
  bytes，SHA-256 `F7E0D87A7CEE3DBAAFF7E2F42BEC7EC33D3B39A71757255F86EB48F14DA09D18`；
  最终程序集含 `HarmonyLib.Harmony` 且无独立 Harmony/合并依赖引用。Editor 未运行，安装
  目录符号链接读取到相同哈希，native clock 两端仍为
  `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`；待宿主试听。
- **2026-09-04**：把 PPS 延长 UI 从构造到提交继续闭合，确认官方手感是常驻的关闭/×1/×2/×3
  四个互斥按钮，而非下拉或连续滑杆。Inspector 已换成四段 ToggleButton：实体声库不可达
  档位保持可见但灰显，关闭档始终可操作；多选存在不同值时 Color/CTop/Release 下拉留空、
  四段按钮不伪装成第一颗音符的状态。右键菜单采用同一多选共同上限并禁用不可达档位，
  消除第二条绕过入口。完整 Debug 与主项目 Release 均为 0 警告、0 错误；ILRepack DLL 为
  7,155,200 bytes，SHA-256 `C2E8AF0776B3C5E192685F2E85EAC63D6AD74AE2D6C34309D47D2B1CBDB72C82`。
  最终程序集含 Harmony 与 EVEC Inspector 类型；Editor 未运行，安装符号链接哈希一致。
- **2026-09-04**：将 DDI 审计扩展到 Miku Soft、Solid 与注册为 Beta_EVEC 的中文组件。
  Soft/Solid 与 Original 的 CTop/Color/Release/self-edge 能力一致；Beta_EVEC 的实际中文 DDI
  没有任何 EVEC 声学 token，推翻原先仅凭组件名暴露 Soft/Power 的配置。生产探测器对该
  组件返回 None，防止生成不可渲染的 `V V#2/#6`。随后把判定泛化为实体 DDI 文件名优先：
  `VoiceBank.Path` 为 `.ddb` 时解析同目录 `.ddi`，VDM 名称仅在无实体路径时回退。该修正还
  解决 VDM 把 `LEN_V4X_Serious.ddi` 错报成 `RIN_V4X_Serious` 而漏掉 Voice Release 的问题。
  四个 release-only 双子库的实体 DDI 均确认含 `*#1/*#2` 及各 6 条入口边。完整 Debug 与
  主项目 Release 继续为 0 警告、0 错误；最新合并 DLL 为 7,155,200 bytes，SHA-256
  `D2E6EEDE2B92F0C8B341FAA02D2C73013F324663DEC1D71E7CF1B5610D1AA1B3`，Editor 未运行，
  安装符号链接哈希一致。
- **2026-09-04**：render harness 直接链接生产 `EvecVoicebankDetector`/重组器源码并以真实
  VDM VoiceBank 运行能力探针。结果与预期一致：本机中文 Beta 名称组件 `supported=False`；
  Miku Original/Soft/Solid 为 Color 0/101/105、CTop 0/302/306、Release 0/201/202；Rin/Len
  Power 为 CTop 0/301；Warm/Sweet/Serious/Cold 仅 Release。生产限值实测 Miku 的 `k/h\\/z`
  均为 3，Rin/Len 的 `k` 为 3、`h\\/z` 在 Normal/Accent 下分别为 1/0。该探针验证了真实
  对象路径、错误 VDM 名称和能力归一化的整条代码，而不只验证独立脚本。
- **2026-09-04**：针对第二次宿主反馈“重音仍切不过去、选项互锁后无法解开”，把图约束
  从“当前 CTop 反向禁用延长按钮”改成最后操作优先。Rin/Len 的 `Z/h\\/z` 上，选择延长 1
  会退出冲突的 301；选择 301 会把延长收回 0。两条路径均可随时操作，仍不会生成缺少
  `C C` self-edge 的无声组合；Miku 与普通 Rin/Len 辅音继续保留 CTop+延长的正交组合。
  同时修正批量更新中状态对象被原地修改的问题：过去传给 updater 的 `current` 也被当成
  before snapshot，导致历史/诊断拿到假“修改前”状态；现先克隆再修改。live cache 新增
  `VoiceBankID` 身份，换声库后即使音素文本同形也不会复活上一声库的逻辑状态。Debug
  解决方案、逻辑 harness 和真实 VDM 能力/交互策略探针已 0 警告通过；新增有界
  `%APPDATA%/VOCALOIDPatcher/evec-diagnostic.log`，只记录四维 ID、提交结果和 token 数，不
  记录歌词、音素正文、工程或声库路径，供下一次宿主反弹时定位请求/归一化/写入哪层失败。
  随后新增 native mutation probe，在实体 Miku/Rin Part 上按生产公式和事务顺序验证
  `Mild→Accent→Normal`、301/延长同形往返、组合与全部清除；每步实际音素、保护位和事务
  结果均通过。主项目 Release 0 警告、0 错误；ILRepack 产物为 7,158,272 bytes，SHA-256
  `C49E2DF97FEC930C66CD40AA6BE9917551A020B917FFC73BC1F97D8AA8081ACE`。最终程序集包含
  `HarmonyLib.Harmony`、`EvecDiagnosticLog` 和新 Inspector，引用列表没有独立 `0Harmony`；
  确认 Editor 主进程未运行，安装端符号链接读取到同一哈希。native clock 两端仍为
  `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。
- **2026-09-04**：继续审计歌词/G2PA 生命周期并找到另一条确定性“锁回旧状态”路径。
  原补丁在 `G2PAMultiLingualManager.SetLyrics` 前只保存 EVEC state，没有临时解除
  `IsProtected`；新增的真实 G2PA probe 证明，受保护的 `k k#2 a` 把“か”改为“き”时虽然
  返回成功，音素仍保持旧串。解除保护后同一调用生成 `k' i`，再按 Mild 重组为
  `k' k'#2 i`，完整通过。生产 prefix 现只对确有 EVEC 的受保护音符临时解锁，postfix 在
  原 G2PA 成功后于同一外层事务内重贴原四维状态；失败或异常恢复原保护位。直接
  `SetNoteEvec` 也接入同一有界诊断日志，使歌词重贴/复制等非 Inspector 路径可观测。
  Debug 解决方案和带 `--lyrics-probe` 的 render harness 均为 0 警告、0 错误。确认 Editor
  主进程未运行后完成 Release；ILRepack DLL 为 7,159,808 bytes，SHA-256
  `5542714E88861981ED188CC622548E1A401BD9E2672B9425469AF4619CDAC530`。最终程序集包含
  `EvecLyricsEditState`、`EvecDiagnosticLog` 与 `HarmonyLib.Harmony`，引用列表没有独立
  `0Harmony`；安装端符号链接读取到相同哈希。
- **2026-09-04（进行中）**：开始闭合复制/粘贴与拆分/合并生命周期。已确认普通复制、
  拖拽复制和整音符粘贴都会经过 `WIVSMMidiPart.DuplicateNote` 或
  `WIVSMClipboard.PushNote`，现有 `CloneState` 能带走双子同形逻辑状态；但原生“粘贴音符
  属性 → 歌词与音素”直接在 `WIVSMClipboard.CopyNoteProperty` 中写 `Lyric/Phonemes`，
  绕过上述两条钩子。其单源多目标和多源逐项映射已按 `Pair(Target, Source)` 核实；这会使
  Rin/Len 的 `301+延长0` 与 `Normal+延长1` 在同为 `C C V` 时丢失来源语义。正在为公开
  `CopyNotePropertyTo` 增加同一批次的 before/after 捕获、归一化重贴与单条逻辑历史，避免
  多选粘贴的撤销被拆成数条而错位。拆分与合并仍在继续按原生事务语义审计，尚未部署新
  Release；当前安装版本仍为 SHA-256
  `5542714E88861981ED188CC622548E1A401BD9E2672B9425469AF4619CDAC530`。
- **2026-09-04（复制/拆分修正已完成源码验证）**：新增 native clipboard probe，实测
  `PushNote` 返回音符及 `GetNotes` 包装器句柄相同，且 `Parent` 仍是来源 MidiPart、
  `VoiceBankID=BKKP765AEHXWSKDB`；因此排除“剪贴板没有声库而被归一化为空”的中途假设。
  真正缺口由阳性反例闭合：Rin 来源逻辑态为 `Normal+延长1` 时物理串 `k k a` 经原生
  `CopyNotePropertyTo(LyricsAndPhonemes)` 正确写到目标，但脱离 sidecar 后生产解析规则只能
  得到 `Attack=301/Extension=0`。新增 `EvecClipboardPropertyPatch` 在公开批次边界复刻原生
  单源多目标/多源 Zip 映射，先捕获全部 before，再于调用者现有事务内统一按来源逻辑态重贴
  与分配时值；全部成功后才写 cache，并把多选作为一条逻辑历史，任一步失败则把返回值置
  false 交给原事务整体回滚。无 EVEC 的手工保护音素只清旧 cache，不改原生保护位。
  另一个 native structure probe 确认 `DivideNote` 保留左句柄但新建同形右句柄，`JoinNotes`
  保留左句柄和原音素；因此新增 divide clone，而 join 不做猜测性写入。Debug 全解决方案与
  render harness Release 均为 0 警告、0 错误，clipboard/structure probe 均 `valid=True`；
  还未构建/安装本轮 Release。
- **2026-09-04（复制/拆分修正版已安装）**：再次运行 17,748 条 Miku/Rin-Len 全有向
  切换、Luka 900 条切换、sidecar archive round-trip、真实 VDM 能力/最后操作优先、native
  mutation、lyrics G2PA、clipboard 与 structure probes，全部通过。完整 Debug 解决方案、
  render harness Release、主项目 Release 均为 0 警告、0 错误。确认 VOCALOID6 Editor 未
  运行后生成 ILRepack 单文件 7,166,464 bytes，SHA-256
  `2DE8AE1A46432F96BB6C47117C97773CA2203B01E742EB922DEF44864DE602DD`；安装目录符号链接
  读取到同一哈希。最终程序集包含 `EvecClipboardPropertyPatch`、`EvecDivideNotePatch`、
  `EvecService.ClipboardPropertyTransfer`、`EvecDiagnosticLog` 与 `HarmonyLib.Harmony`；
  native clock 源/安装哈希继续一致为
  `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。待宿主交互/试听。
- **2026-09-04（时长重锚修正，源码验证完成）**：沿用户原始“Short/Long 仍和音符长度
  有关”反馈继续查到新的生命周期缺口。选择 Release 时虽按当前 note end 写入固定 60 ms
  边界，但之后右侧缩放走 `WIVSMNote.SetDuration`、左侧缩放走 `ResizeLeft`，两者此前都不
  重新运行 EVEC 时值分配；旧相对 Tick 会留在原处，使拉长后的 `*#1/*#2` 区段吸收新增
  时长。现新增两条 geometry postfix，在原编辑事务中重新锚定 Common/VSil；底层
  `DivideNote` 会同时重锚左右音符并 clone 同形状态，`JoinNotes` 会重锚实际 survivor、清除
  被合并句柄的缓存。它不改变 Short/Long ID，也不碰 Accent、Decay、Velocity。
  逻辑 harness 新增 240 ms 与 1000 ms Release 回归，两者仍严格返回 60 ms；105 ms 成功、
  104.999 ms 跳过拆分的临界保持不变。完整 Debug 解决方案 0 警告、0 错误；本段尚未生成
  新 Release，安装端仍是 SHA-256
  `2DE8AE1A46432F96BB6C47117C97773CA2203B01E742EB922DEF44864DE602DD`。
- **2026-09-04（BPM 重锚修正，源码验证完成）**：固定毫秒边界写成 Tick 后，后续修改
  tempo 点、点位、全局 tempo 或 ARA/global tempo 模式也会让 45/60 ms 漂移。生产层现对
  Insert/Duplicate/Remove/Move tempo、`WIVSMTempo.Value` 以及三个全局 tempo setter 只做
  sequence dirty 标记；在同一原生 `Commit(bool)` prefix 中至多扫描一次所有受保护音符并
  重锚实际 EVEC，避免画 tempo 线时每个点都全工程扫描。Rollback 清标记；边界修改与 tempo
  修改进入同一原生历史，所以 Undo/Redo 可一起还原，不额外制造撤销步。普通音符由
  `IsProtected` 快速过滤，单个 stale wrapper 失败不会中断其它音符。Debug 全解决方案继续
  0 警告、0 错误；尚未生成新 Release。
- **2026-09-04（几何/BPM 重锚修正版已安装）**：完整 Debug、render harness Release 与
  主项目 Release 均为 0 警告、0 错误；重新运行 17,748+900 全切换、archive round-trip、
  真实声库能力、mutation、lyrics、clipboard、structure probes 全部通过。确认 Editor 未
  运行后生成 ILRepack 单文件 7,173,632 bytes，SHA-256
  `F4D370327DB7B9834BDCB5CDEAC82070EB1E4D50A4B95C37A03F6DF6D311B2AD`；安装目录符号链接
  哈希一致。最终程序集可见 `EvecSequenceTempoCommitPatch`、`EvecNoteDurationPatch`、
  `EvecNoteResizeLeftPatch`、`EvecJoinNotesPatch`、`EvecClipboardPropertyPatch`、
  `EvecDiagnosticLog` 与 `HarmonyLib.Harmony`；native clock 两端哈希仍为
  `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。待宿主对
  先选 Release 后拉伸音符、左缩放、拆分/合并、再改 BPM 的实际边界和听感复测。
- **2026-09-04（换声库时受保护 EVEC 遗留的无声路径，源码修正完成）**：6.13 的用户换声库
  路径为 `WIVSMMidiPartExtension.SetVoiceBank`：先改 VoiceBankID，再调用 part-wide
  `ResetPhonemes`。新增 `--voicebank-switch-probe` 用真实 JPN G2PA 对同样的 Miku
  `k k#2 a` 做阳性/阴性对照：保持 `IsProtected=true` 时切到 Rin Power，Reset 返回 true，
  但音素和保护位仍为 `k k#2 a/true`；先解锁的对照则正确生成 Rin 的 `k a/false`。
  因而已确认“原生返回成功”并不代表受保护 EVEC 已按新声库重算，旧声库专用 `#2/#6`
  token 会被带入新声库，是确定的无声风险。生产新增 `EvecVoiceBankChangePatch`：只在目标声库
  确实变化且 Part 内实际存在 EVEC 时，在原换声库事务内临时解锁这些音符，让原生 G2PA
  真正重建基础音素；成功后清掉旧 sidecar/cache，并把 before→base 作为一条批量逻辑历史，
  使 native Undo/Redo 能恢复双子 `C C V` 的正确歧义态。普通受保护手工音素不动；失败或
  异常只在旧物理串仍完整时恢复原保护位。反射核对目标签名为
  `Boolean SetVoiceBank(WIVSMMidiPart, VoiceBank)`；完整 Debug 解决方案 0 警告、0 错误，
  probe `protected_skipped=True`、`unlocked_regenerated=True`、`valid=True`。本段尚未构建或
  安装新 Release，当前安装端仍为
  `F4D370327DB7B9834BDCB5CDEAC82070EB1E4D50A4B95C37A03F6DF6D311B2AD`。
- **2026-09-04（Part 属性粘贴/整段复制的 EVEC 生命周期，源码修正完成）**：新增
  `--part-property-probe`，实测 `PushMidiPart` 会保留 Rin VoiceBankID、`k k a` 和保护位；
  但 `CopyPartPropertyTo(VoiceBank)` 只把目标 ID 从 Miku 改为 Rin，原 Miku
  `k k#2 a/IsProtected=true` 完全不变，也不调用 G2PA；`Note|VoiceBank` 则复制出物理
  `k k a`，但脱离 sidecar 后仍无法区分双子 301 与延长1。probe 三项均为 true，证明这是
  两个独立缺口。现新增 `PushMidiPart`、DuplicatePart/Track/Sequence 的逐音符 state clone；
  Part 属性批次 patch 复刻原生单源多目标/多源 Zip 映射。只粘贴 VoiceBank 时，在同一原生
  Transaction 内剥掉旧声库 EVEC token；粘贴 Note 时，等最终目标声库写完后再按来源逻辑
  state 统一重贴，避免“先按旧目标声库归一化”误清或误映射。整个批次成功后才发布 cache，
  任一步失败返回 false 交给原生事务回滚。逻辑历史由同句柄 before/after 扩展为两个快照
  集合，因此整 Part 替换时 Undo/Redo 可按旧/新 native handle 恢复各自 sidecar，数量变化
  也不再要求强行一一配对。反射核对 5 个 Harmony 目标签名，完整 Debug 解决方案再次为
  0 警告、0 错误。尚未构建/安装本轮 Release。
- **2026-09-04（换声库/整段复制修正版已安装）**：在新增 clipboard Clear 句柄清理后，
  完整 Debug 解决方案、render harness Release、主项目 Release 均为 0 警告、0 错误；
  11,664 条 Miku、5,184 条 Rin/Len、900 条 Luka 全有向切换、archive round-trip、真实
  VDM 能力与交互策略、mutation、lyrics、Note clipboard、structure、voice-bank switch、
  Part property probes 全部通过。Part probe 进一步确认 native Undo 恢复旧 target handle，
  Redo 恢复新 copied handle，两个 handle 集合历史契约成立。确认 Editor 关闭后生成 ILRepack
  单文件 7,191,040 bytes，SHA-256
  `745B453503F229B3C152D08C80754BC8E3FF4A9489C0C50C1F0610DC95AD5F9E`；安装目录符号链接
  读取到同一哈希。最终程序集包含 `EvecVoiceBankChangePatch`、
  `EvecClipboardPartPropertyPatch`、`EvecClipboardPartPatch`、DuplicatePart/Track/Sequence、
  geometry/tempo patches、`EvecDiagnosticLog` 与 `HarmonyLib.Harmony`，程序集引用中无
  `0Harmony`。native clock 源/安装哈希仍一致为
  `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。当前诊断日志尚
  不存在，表示安装该日志版本后还没有可读取的宿主 EVEC 写入；仍待用户在 Editor 内试听和
  交互复测。
  最终构建还补充了换声库失败回滚的 cache 重播：若 ResetPhonemes 已触发 UI 刷新、但后续
  secondary voice 步骤失败，先按旧 VoiceBankID 暂存 exact before state，等调用者 native
  Transaction 回滚后重新命中；非事务调用不回滚时因声库/音素不匹配会自然失效。
- **2026-09-04（缺失声库自动替换的受保护音素链路，源码修正完成）**：继续审计工程加载与
  Track 导入，确认二者都先调用 `ReplaceVoiceHelper.ReplaceVoice` 以 raw
  `SetVoiceBankID` 替换缺失声库，随后依次 `ResetLyrics`、`ResetPhonemes`。而 EVEC sidecar
  在更早的 `LoadProjectSequenceFile` postfix 应用；旧 Voice Bank 未安装时，原实现会用
  `VoiceBank()==null` 的空能力先把 sidecar 归一化为空。即使物理 `k k#2 a` 仍在，后续
  part-wide G2PA 也会因保护位跳过，最终形成“新声库 ID + 旧声库 token + 无法解锁”的确定
  无声风险。现改为两阶段交接：加载期只在 sidecar 可精确重组当前物理串时暂存原逻辑状态，
  可拒绝 stale/伪造 sidecar；`ReplaceVoice` 只捕获、不提前解锁，使紧随其后的
  `ResetLyrics` 仍尊重原保护；到实际 `ResetPhonemes(WIVSMMidiPart)` prefix 才解锁，成功后
  以新声库能力恢复仍受支持的 Color/CTop/Release/延长，不支持的维度清空。G2PA 失败、
  ReplaceVoice 部分改库后失败或异常时不再重新保护跨库旧 token，而是清理 cache 并保持可
  编辑，避免“失败但锁死”。正常用户主动换库仍保持原有 clear-all 语义，不受此自动替换
  策略影响。新增 exact-realization 回归覆盖 Miku 后缀、Rin/Len `C C V` 双义两态以及 stale
  sidecar 拒绝；完整 Debug 解决方案 0 警告、0 错误，11,664/5,184/900 切换、archive、
  voice-bank switch 与 Part property probes 已通过。本段记录时尚未生成新 Release，安装端
  仍为 SHA-256 `745B453503F229B3C152D08C80754BC8E3FF4A9489C0C50C1F0610DC95AD5F9E`。
- **2026-09-04（缺失声库自动替换修正版已安装）**：完整 Debug 解决方案、render harness
  Release 与生产 Release 均 0 警告、0 错误；11,664 条 Miku、5,184 条 Rin/Len、900 条
  Luka 全有向切换、archive round-trip、4 条 exact-sidecar 正反例以及 7 个真实 VDM/VSM
  probes 全部通过。构建前以精确进程名/路径确认 Editor 已关闭；安装文件仍为 Release 输出
  符号链接，新 ILRepack 单文件为 7,194,112 bytes，源与安装端 SHA-256 同为
  `9EB8818C7F8086AE2459B0553433E3EFBAED5041A31CAF661485F1EA0005CF7E`。最终程序集已核对包含
  `EvecAutomaticVoiceBankChangePatch`、`EvecG2paResetPhonemesPartPatch`、
  `EvecVoiceBankChangePatch`、`EvecClipboardPartPropertyPatch`、`EvecService`、
  `EvecDiagnosticLog` 与 `HarmonyLib.Harmony`，引用列表无 `0Harmony`。native clock 源/安装
  哈希继续一致为 `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。
  `%APPDATA%/VOCALOIDPatcher/evec-diagnostic.log` 仍不存在，表示安装日志版后尚无可读取的宿主
  EVEC 写入。自动缺失库替换难以在已安装完整声库的离线 harness 中真实触发，故两阶段
  Harmony 串接仍需宿主用缺库工程/导入场景复测；声音与 UI 手感同样仍以宿主试听为准。
- **2026-09-04（歌词左移/右移与直接音素写入，源码修正完成）**：6.13 的
  `MusicalEditorViewModel.LyricMoveLeft/Right` 不通过剪贴板或 DuplicateNote，而是逐音符 raw
  复制 Lyric、Phonemes、IsProtected，最后做范围 G2PA。EVEC 的保护位使 G2PA 跳过目标，但
  原生没有逻辑 sidecar；Rin/Len 的 `Normal+延长1` 物理 `k k a` 因而会在移动后按确定性
  fallback 重判成 `Accent301+延长0`，直接表现为两个控件串值/似乎互锁。新增 native
  `--lyric-move-probe` 已复现：copy/reset 均 true、物理串与保护位保留，naive 语义却变为
  `301/0`；原 `0/1` sidecar 仍可精确重组同一物理串。生产层现按原生不对称规则传递逻辑态：
  单选左/右移动到行尾，多选仅移动连续选区，空出的端点清空状态；在 raw SetPhonemes 返回后、
  尚处于原 Transaction 内时立即重挂目标 sidecar 并按目标音符几何重设 EVEC 时值。历史快照
  新增 Lyric 判据，使 Undo/Redo 不会把纯歌词移动误配给下一条 EVEC 历史。所有成功的直接
  `WIVSMNote.SetPhonemes` 还会先清掉旧同形 cache，再由 EVEC 自有写入/歌词移动上下文重新发布，
  同时封住手工音素编辑、ResetLyrics、SplitNote 特定音素等“字符串相同但语义已变”的复活
  路径。6 组单选/多选/边界方向规划、17,748+900 全切换、archive 与 8 个真实 probes 全部
  通过；Debug/render harness 均 0 警告、0 错误。本段尚未生成新生产 Release，安装端仍为
  SHA-256 `9EB8818C7F8086AE2459B0553433E3EFBAED5041A31CAF661485F1EA0005CF7E`。
- **2026-09-04（歌词移动/直接音素失效修正版已安装）**：确认 Editor 本体关闭后完成生产
  Release，ILRepack 单文件 7,202,816 bytes；Release 源与安装符号链接 SHA-256 同为
  `221F6CB47B08F87A2988888430778933C52A4874D8FDAADB9BCDDC40862177C6`。最终 metadata 已核对
  包含 `EvecRawPhonemeWritePatch`、`EvecLyricMoveLeftPatch`、`EvecLyricMoveRightPatch`、
  `EvecLyricMovePlanner`、既有自动/显式换声库补丁及 `HarmonyLib.Harmony`，assembly
  references 无 `0Harmony`。native clock 两端继续一致为
  `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。当前仍待宿主验证
  双子 `Accent301`/`Normal+延长1` 混合序列左右移动、撤销/重做后控件是否保持来源逻辑态。
- **2026-09-04（音符/Part 位移的固定毫秒边界，源码修正完成）**：EVEC 的 45/60 ms 最终
  存为当前 tempo map 下的相对 Tick。新增真实 VSM `--position-timing-probe`：120 BPM 的
  60 ms 为 58 tick，60 BPM 为 29 tick；把旧 58 tick 随音符移到 60 BPM 区域会变成
  120.833 ms，而按新绝对位置重算仍为 60.417 ms。现补齐 `WIVSMMidiPart.MoveNote` 与
  `WIVSMMidiTrack.MovePart` postfix，在调用者原事务内按新 AbsPos 重锚每颗 EVEC 音符；覆盖
  钢琴卷帘拖动、Quantize、InsertRest、Part Double/Half Tempo、轨道 Part 拖动及工程插入空间，
  不改变所选 Short/Long 录音 ID，也不触碰 Velocity/Accent/Decay。
- **2026-09-04（Part 拆分/合并的逻辑 handle 迁移，源码修正完成）**：新增
  `--part-structure-probe` 实测 Rin 两颗 `k k a/true`：Divide 后左音符保留原 handle、右音符
  获得新 handle；Join 后第一颗仍保留、第二颗再次换 handle。物理与保护均有效，但旧 cache
  无法跟随，`Normal+延长1` 会回退成 Accent301。现对 `DividePart`、`JoinParts` 在 native
  调用前按绝对 Tick/音高/稳定顺序捕获 source state，返回后在同一 Transaction 内按最终
  音符顺序归一化重贴并清理消失 handles；`RemovePart` 同步释放全部 note generations。
  结构 API 可能在 InsertSilence 等一次 Transaction 内串联 Divide→Duplicate→Join，因此新增
  pending transition accumulator：连续转换以首次 before 和最终 after 合成一条 Undo/Redo，
  多个互不相干 Part 则并集合并；Commit 成功才发布历史，Rollback 清除。纯 composer 回归已
  覆盖连续 A/B→A/C→A/D 与并行 E/F→E/G。Debug 解决方案、logic harness 与两个新 native
  probes 均通过；尚未生成包含本段的生产 Release，安装端仍为
  `221F6CB47B08F87A2988888430778933C52A4874D8FDAADB9BCDDC40862177C6`。
- **2026-09-04（位移时值/Part 结构修正版已安装）**：完整 Debug、logic、render harness 与
  10 项 VDM/VSM probes 全部通过，构建前再次确认 Editor 本体关闭。新 ILRepack 单文件为
  7,213,568 bytes，Release 源与安装符号链接 SHA-256 同为
  `0B094C0B290D2C1F9D4C473D311E79A1114DFC1043CA3D9E9377E7F4FBFE78D6`。最终 metadata 已核对
  包含 `EvecMoveNoteTimingPatch`、`EvecMoveMidiPartTimingPatch`、`EvecDividePartPatch`、
  `EvecJoinPartsPatch`、`EvecRemovePartPatch`、`EvecTransitionAccumulator`、歌词移动补丁与
  `HarmonyLib.Harmony`，引用中无 `0Harmony`；native clock 两端仍为
  `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。宿主仍需验证跨变速
  点拖动及 Part 拆并 Undo/Redo 的实际声音与选项保持。
- **2026-09-04（跨分割音符与对象销毁生命周期，进行中）**：扩展真实
  `--part-structure-probe`，用一颗横跨 Part 分割点的 Rin `k k a/true` 音符验证 V6 行为。
  Divide 后该音符只留在左 Part，不复制到右 Part；原 note handle、绝对位置、1440-tick
  时长、物理音素与保护位全部保持。因此当前 Divide/Join 的稳定顺序迁移不会把一份逻辑态
  错贴成两份。进一步审计发现 EVEC 已清理 RemovePart，但漏掉 `WIVSMSequence.RemoveTrack`
  和 `WIVSMSequence.Close`：删整轨会遗留 note generation/cache，关工程还会遗留该 sequence
  的 Undo/Redo、pending transition 和 tempo 标记。现已增加删除 transfer（只为实际 EVEC
  snapshots 记录 before→empty 历史，成功后释放全部 note handles）、删轨补丁及 Close 成功
  后的 sequence 定向清理；不全局清空其它仍打开 sequence 的状态。扩展 probe、render harness
  Release 和完整 Debug 解决方案均 0 警告、0 错误。本段仍需删除→Undo/Redo 句柄实测和生产
  Release；安装端暂仍为 SHA-256
  `0B094C0B290D2C1F9D4C473D311E79A1114DFC1043CA3D9E9377E7F4FBFE78D6`。
- **2026-09-04（删 Part/轨道/关闭工程生命周期修正版已安装）**：新增
  `--removal-lifecycle-probe` 对 Rin 两颗物理同为 `k k a` 的音符实测：RemovePart 和
  RemoveTrack 均成功；Undo 都恢复原来的两个 note handles，Redo 都再次删除，证明
  before→empty 逻辑历史能与 native 历史精确对齐。完整 logic harness（Miku 11,664、
  Rin/Len 5,184、Luka 900 全有向切换等）、扩展后的 11 项 VDM/VSM probes、render harness
  Release、完整 Debug 和生产 Release 均通过且 0 警告、0 错误。构建前确认 Editor 本体关闭；
  新 ILRepack 单文件为 7,218,688 bytes，Release 源与安装符号链接 SHA-256 同为
  `5EF51D0D2C9036104C6979F27112ABFDF8619134641BAF6CA413D20FDFB34266`。最终 metadata 已核对
  包含 `EvecRemovePartPatch`、`EvecRemoveTrackPatch`、`EvecSequenceClosePatch`、
  Divide/Join/transition accumulator 与 `HarmonyLib.Harmony`；46 项程序集引用中无
  `0Harmony`。native clock 源/安装哈希仍一致为
  `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。仍待宿主重点复测
  双子 `Accent301`/`Normal+延长1` 在删除 Part/轨道后 Undo/Redo，以及关闭/重开工程后的
  选项与声音一致性。

## 本轮宿主复测最小矩阵

1. Miku EVEC：同一 `k a` 音符依次选择 Mild、Accent、Normal，确认三者都能切换且有声；
   音素应依次表现为 `k k#2 a`、`k k#6 a`、`k a`。
2. Rin/Len Power EVEC：Accent 与 Normal 往返，确认 Accent 为 `k k a`、Normal 为 `k a`，
   且界面不会自动跳回或锁死。
3. 发音延长保持 CTop=Normal，依次选关闭、1、2、3；再保持 2 不变切换 CTop 三档，确认
   两个控件互不改值、辅音长度总体递增且不出现整音符无声。
   Rin/Len 的 `Z`、`h\\`、`z` 是实体图特例：Normal 只应出现关闭/1，Accent 只允许关闭；
   切回 Normal 后“1”应重新可选，不能锁死或生成三份以上相同辅音。
   四个延长按钮应始终占据固定位置；不可达档位只灰显，不应从界面消失或跳动。
4. 多选具有不同 EVEC 值的音符时，三个下拉和四段延长不得显示第一颗音符的值；选择一个
   明确选项后再统一写入全部可用音符，右键菜单中的不可达延长档位也应同步灰显。
5. 任意顺序组合 Soft/Power、Short/Long、CTop、延长 0–3，再逐项恢复 Normal/None/关闭；
   各项必须单独可清除，Short/Long 的录音选择不随音符长度自动互换。
6. 在 120 BPM 附近分别用短于/等于/长于 105 ms 的音符切换 Short/Long；选项都应保持所选
   录音，短音符不得把边界强挤到不同长度，也不得连带清除 Color、CTop 或发音延长。
7. 对上述任一组合执行撤销/重做、改歌词、保存/重开，确认界面、音素与声音一致。
8. 在双子 Power 上分别给连续音符设置 `Accent301` 与 `Normal+延长1`（物理均为 `k k a`），
   执行歌词左移/右移、撤销/重做；逻辑选项必须跟随歌词来源移动，不能全部变成 Accent。
9. 在 120→60 BPM 变速点两侧拖动带 CTop/Color/Release 的音符及整个 Part；45/60 ms 听感
   不应翻倍或减半。随后拆分/合并 Part 并 Undo/Redo，双义选项与声音都应保持。
