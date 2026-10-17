# EVEC 排布修正与真实 V6 时序验证

2026-09-10。继文档 32、33，区分生产功能、独立渲染实验与尚未完成的宿主接入。

## 已修正的生产行为

- Consonant Extension 的持久化仍为 0–3；界面显示 none、×2、×3、×4，
  对应原辅音加 0–3 次重复。CTop 是独立选项，不计入这个界面倍数。
- Luka EVEC 的辅音扩展能力不再依赖是否有 CTop 列表。其已安装 Piapro
  articulation 配置没有 CTop 选项，但不能因此隐藏独立辅音扩展。
  仅支持 Voice Release 的声库仍不会获得辅音扩展能力。
- 解析截图 `t t#2 e|e#2|*#1` 为 Mild、Soft、Breath-Short，扩展值为 0。
  仅规范事件排列的竖线可转换为分段摘要；不任意删除自定义串中的竖线。
- 保留 `a a`、内部收音标记及未知 caret 内容。元音不能当作可重复辅音；
  非末尾声核的自定义 coda、残留未知标记不能继续强行套用 Color/Release。
- 90/105/150 ms 分段门槛容忍浮点舍入噪声，同时保持精确逻辑终点；
  89.999 ms 等实质不足的时长不会被容差放行。

产品语义参考 [Crypton V4X 说明](https://sonicwire.com/product/virtualsinger/special/v4x)
和 [Piapro EVEC 操作说明](https://piaprostudio.com/?p=5386&lang=en)。
具体编号与分段时刻依据文档 32、33 的本机 articulation/PPS 证据，
不是声称产品网页公开规定了 45/60 ms。

## 异步原生 score 推翻了过强的振幅推断

同步 `part.Render` 可生成 WAV，但本轮其 HoldingScoreList 为非空对象、0 帧。
新增 `--async-score` 走 StartAsyncRendering 并泵送 observer；要求 score 非零，
另存每音符 GetPhonemePositions 及真实时间映射。不能用空 score 宣称时序验证通过。

Miku 与 Rin 的 isolated / Short 分段样例均有 1466 帧，帧长约 5.805 ms。
以下是歌曲绝对时间；对应 WAV 还包含 500 ms presend：

| 事件 | 逻辑起止 ms | 原生音素范围 ms |
| --- | --- | --- |
| 起音段（Miku `k k#2 a`） | 250–294.792 | 202.083–289.583 |
| Soft `a#2` | 294.792–1189.583 | 289.583–1183.333 |
| Short `*#1` | 1189.583–1250 | 1183.333–1241.667 |

起音辅音预发声不同于内部事件边界；后两段边界误差约 5–8 ms。
这些样例不支持“分段后气声一定提前 110–170 ms 被截断”的旧推断。
Miku 样例的后段仍存在很低幅度 PCM；统一 -50 dB RMS 活动阈值漏掉了部分气声尾部。
分析工具现在同时输出最后非零样本，但它也可能包含噪声，仍不是真实音素边界。
试听拼接保留这些低幅度尾部，不用 RMS 阈值直接裁剪。

## 渲染副本实现与边界

新增 `EvecRenderCopy`：预检声库、语言、音素和 tick 分段后复制 sequence，
只在副本中用原生 DivideNote 构造起音/Color/Release 事件。
保留原生分割表达行为；续接段 velocity=64、Accent/Decay=50、关闭两端 portamento，
其他表达依据原音符。按 PPS 同语言后继间隔 ≤100 ms 省略收音。
任一不支持排列或量化后零长段使整个副本创建失败，原工程不变。

**尚未接入编辑器正常渲染的发布、失效与取消链路。**
当前 `SegmentedPhonemeRenderCoordinator` 专用于中文的两次 carrier 合成，
带原生会话互斥、延迟关闭、WAV/score 覆盖与 AudioPlayer placement 刷新。
不能只复用路径 getter 或调用同步 Render 就宣布接入成功；还需处理关闭、撤销、
声库/速度修改、过时代次、音频与 score 配对以及原生对象生命周期。
本轮不更改这条中文生产链，也未部署或启动 Editor。

## 验证

- 纯逻辑状态转换：Miku 11664、Rin/Len 5184、Luka 14400，全部通过。
- 两个实际声库各 90 次异步原生渲染，共 180 次非静音且有 score；
  包含截图 `t e`、鼻音 `n a`、力度 32/96、短音、连唱、100 ms 间隔和变速样例。
  这组矩阵由 harness 构造分段，并不等于编辑器已采用 EvecRenderCopy。
- `--render-copy-probe` 单独验证新副本类：3 段、非零 Part 偏移、真实 score、
  源音符/保护/表达/撤销状态不变；连唱省略 Release；自定义 coda 回退。
- `--evec-contract-probe` 验证 Luka 独立辅音扩展与不支持声库边界。
- 完整 Release 解决方案构建通过；没有宿主交互和人工听感验收。

复现实验输出应放临时目录，不提交 WAV、score 或声库文件。
