# 研究日志

## 2026-09-04：录音 capture 机器预检

- 扩展 session QA 合同，显式记录时长容差、最大 peak、最小发声 RMS、最大 DC、边界 SNR、音高容差和最小自相关置信度；配置变更会进入输入哈希。
- 新增只读 `validate_recording_capture.py`：逐 take 核对 44.1 kHz/mono/PCM16、时长、削波、peak、DC、RMS、首尾静音 SNR、SHA-256 和目标 F0；全量模式同时找 missing 与 unexpected WAV。
- ART 在每音节 72% 处、STA 在持续区 65% 处取 2048-sample 窗，只在目标 `±150 cent` 内做归一化自相关和亚采样 lag 插值。该结果是目标音高预检，不冒充最终完整 F0 contour。
- 用三层 manifest 的第一条 ART 与第一条 STA 生成完全自有的约 266.661 Hz 正弦 WAV：两条均通过，12+1 个窗的 F0 误差绝对值低于 0.004 cent，相关度约 0.9999。
- 负向 WAV 使用 int16 极限幅度，检测到 461 个 clipping samples，同时触发 `clipping` 与 `peak_level`，验证器按约定返回退出码 3。
- 自动通过不写回 approved；真实人声阈值、发音、音色、完整 F0、强制对齐和 outer/inner 边界仍需校准/人工 QA。

## 2026-09-04：恢复音高层模板并生成三层录音 manifest

- 新增 `analyze_reference_layers.py`，只读七个 DDI，逐项聚合 836 个 STA 和 56,051 个 ART 的 `pitch1/pitch2/duration/dynamics/tempo/unknown2`；不读取 DDB、PCM 或 FRM2。
- 2/3/4 层稳健模板分别约为中心 `±2.503` 半音、`-3.670/+0.335/+3.335` 半音、`-5.377/-1.372/+1.624/+5.125` 半音。全部满层 ART 与 STA 对应层的中位坐标最大只差 6.7443 cent。
- 七库合计 96 个单层 ART 全部是 `Sil→unvoiced`，两个 pitch 字段全部为 float32 `-FLT_MAX` 无音高哨兵；其时长中位数为 `3072/44100 s`，少量更长。这闭合了“为何单层”的数据语义，修正了仅凭层数推断的不足。
- 全部 56,887 个 STA+ART 样本的 `dynamics≈0.6`、`tempo=90`、`unknown2=0`；样本时长按有声/无声类别有明显差异，但不能当作原始录音句长。
- 新增 `plan_recording_sessions.py` 和配置示例，把 190 条 ART、38 个 STA、层模板、歌手舒适区与显式 QA/时长展开成逐 take manifest。三层示例得到 570 个 ART take、114 个 STA take，共 684 个 pending WAV，净计划时长 71.25 分钟。
- 示例层点为 MIDI `60.3301/64.3348/67.3351`（约 C4/E4/G4）；任何层超出配置的舒适区即失败。所有 take 有稳定相对路径、输入哈希和待填写 provenance，输出拒绝覆盖；两次生成完全一致。
- 这仍不是录音完成：0.5 s/音节、1.5 s STA、中心音高、SNR/F0 阈值和 carrier 易读性必须由真人短试录确认，随后才可做强制对齐与批量 DRS。

## 2026-09-04：中文长提示达到 190 段理论下界

- 新增 `plan_chinese_long_prompts.py`，把 2,090 条音节间 ART 边分成 190 条固定长度路径；每条路径补成 `<sil> + 12 个原生验证拼音音节 + <sil>`。
- 下界来自每个 12 音节片段最多容纳 11 条音节间边：`ceil(2090/11)=190`。实际输出正好 190 段，因此在该离散模型内最优，不是贪心近似。
- 带下界 circulation 保证 1,900 个内部连接位置至少各使用一次全部 407 个规范音素音节，并为 55 条 `Sil→onset` 与 38 条 `coda→Sil` 预留片段边界。
- 全量回放 190 条提示后，2,090 条音节间边全部恰好一次，2,556 条 ART 边全部有 trace，漏边和图外边均为零。规范提示哈希为 `014dea6ae502f5495cc427ee3299129cdbe381d5ebfff8f8a1068efc5a343796`。
- 两个独立 Python 进程生成完全相同的 JSON；`--max-syllables 8` 也生成达到自身下界的 299 段结果。负向测试确认非法上限和已有输出文件均被拒绝。
- 新增独立 `verify_chinese_long_prompts.py`；它不导入规划器，重新从图、G2PA 和逐提示音素展开验证 2,556/2,090/407 计数、逐边 trace、下界与规范哈希，实测全部通过。
- 该输出仍是合法拼音链而非自然汉语句子；汉字、声调、秒数、换气、采样音高层和 forced-alignment provenance 继续保留为 M4 后续工作。

## 2026-09-04：许可证来源闭合与官方发行入口

- 扩展 `license_harness` 同时枚举传统 DSE 与 AI/DNN voice banks。本机 VDM 得到 29 个 DSE、12 个 DNN；DSE manager 的 41 个 Voice license 按 CompID 精确匹配为 29+12，未匹配数为零，另有 2 个 Application license。
- DNN 共 25 个 license descriptor，key/serial 均非空；11 个库有 2 个 descriptor，1 个库有 3 个。DNN 结果为 `Expired=10`、`InvalidTrialKey=1`、`NoError=1`，再次证明 descriptor 数量与字段非空不代表授权成功。
- 6.13.0.1 托管引用显示，`SpliceResult` 只由 `GetSpliceResultForApplicationCompID` 读取并用于 Editor 主组件的 analytics 状态；声库 UI/可用性读取普通 `Result`。这不排除其它原生宿主或版本的消费者。
- Yamaha 官方当前说明终端用户的 serial 需由 VOCALOID Authorizer 向服务器换取本机 authorization info，Voicebank 未授权宽限期为 14 天；非 Yamaha Voice Bank 由与 Yamaha 签有许可协议的 partner company 支持，考虑 VOCALOID business 的法人走企业咨询入口。
- 官方页面未公开个人自助的 CompID 分配或 voicebank license 签发 API。正式发行的可执行结论仍是向 Yamaha 企业渠道咨询，不能把本地元数据生成或逆向格式等同于合法签发。
- 核对 6.13.0.1 默认声库路径后确认：`Name`/`BankName`/`GroupName` 是显示元数据，组件身份和许可证匹配使用 CompID；Editor 的默认传统声库是用户设置 `defaultVoiceCompID`，不是组件注册中的“默认名称”。传统 V5 支持语言由 CompID payload 的语言位恢复，版本由 `Version\Major/Minor/Revision` 读取并另做可合成性判断。

## 2026-09-03：建立传统声库基线

### 工作区与样本

- 用户指定的记录目录是仓库根目录下完整名称 `voicebank_doc/`，不是 `voicebank/_doc/`；此前误建的嵌套路径已在提交后纠正。
- 工作区已有与本研究无关的未提交补丁和翻译改动。本轮未修改这些文件。
- 64 位注册表 `HKLM\\SOFTWARE\\VOCALOID5\\Voice\\Components` 中发现 7 个传统声库组件，均指向 `C:\Program Files\Common Files\VOCALOID5\Voicelib`。
- 每个组件目录均包含一个 `.ddi`、一个 `.ddb` 和一个 约 230 字节的 `_v4compatible.vvd`，另有图标/EULA 等安装资源。

### 文件头实测

- `Luo_Tianyi_Ning.ddi`：偏移 `0x08` 为 ASCII `DBSe`；首个 `PHDC` 位于偏移 `0x1c`。
- `Luo_Tianyi_Ning.ddb`：偏移 `0x00` 为 ASCII `FRM2`。
- `_v4compatible.vvd` 不是明文配置；直接按文本读取呈现为短的可打印混淆数据。其编码/校验语义尚未研究。

### 公开实现审计

只读审计了两个公开源码仓库，检出到系统临时目录，没有拷入本仓库：

- `yuukawahiroshi/ddb-tools`，提交 `2e78b11f0f1fd63535f1c67e8aa5096bf4ac94a7`，MIT。
  - `DDIModel` 按 `PHDC`、`TDB `、`DBV `、`STA `、`ART `、`VQM ` 等块解析索引。
  - `extract_wav.py` 把 `SND ` 负载解释为 16-bit PCM，读取采样率与声道数。
  - `pack_ddb.py` 依赖已经存在的 `singer.tree` 和包含 `FRM2/SND ` 块的分解目录，只重写 DDI 内的 64 位 DDB 偏移。
  - 因此它是提取/重打包器，不是从 WAV 开始的训练器。
- `shmishtopher/VAU`，提交 `e1286ead0bac01487bb97d2cb23a489d72c29195`，Apache-2.0。
  - 独立实现也把 `SND ` 头解释为 18 字节：magic、块长、采样率、声道数、4 字节索引，随后为 PCM。
  - 当前实现只做 DDB 中 `SND ` 的发现与导出，没有 DDI 或训练路径。

来源：

- https://github.com/yuukawahiroshi/ddb-tools
- https://github.com/shmishtopher/VAU

### DDI 只读解析

使用 `ddb-tools` 的解析器直接读取本机文件、只输出聚合统计，没有导出商业声库音频。一个代表性库 `Luo_Tianyi_Ning` 的块区间为：

| 块 | 起始偏移 | 解析结束偏移 |
| --- | ---: | ---: |
| PHDC | `0x0000001c` | `0x00001c46` |
| TDB | `0x00001d4e` | `0x000036c7` |
| DBV | `0x000036c7` | `0x000036e3` |
| STA | `0x000036e3` | `0x0002a2a5` |
| ART | `0x0002a2a5` | `0x00340da9` |
| VQM | `0x00340e6b` | `0x00341248` |

该库 PHDC 共列出 62 个音素：42 个被标为 voiced、20 个被标为 unvoiced。TDB 只覆盖 42 个有声音素；STA 覆盖 38 个可持续音，共 76 个样本；ART 有 2556 个双音素转接单元，共 5099 个样本；VQM 有 1 个 growl 样本。

### Ghidra/DSE 实测

- 8089 端口监听正常。
- 当前打开的 Program 有 `VDM2.dll`、`VDM.dll`、`DSE.dll`；所有 DSE 查询均显式指定 `program="DSE.dll"`。
- DSE 6.13.1.1 的 RTTI/字符串显示传统库对象与分析对象：
  - `DSE5::CDBSinger`
  - `DSE5::CDBVoice`
  - `DSE5::CDBVArticulation`
  - `DSE5::CDBVStationary`
  - `DSE5::CSMSFrame` / `CSMSCollection`
  - `DRS::CSMSAnalysis` / `CSMSRegionAnalysis`
- `DSE5::CDBSinger` 构造函数（当前地址 `0x18010c5b0`）设置块 ID `DBS `、采样率、语言、声库路径和名称。
- 虚方法 `0x18010ddb0` 与 `0x18010e2c0` 会把对象树序列化到 `<base>.tree`；低于版本 3 时会提升到 3 并设置 `DBS ` 块 ID。
- `0x18010df80` / `0x18010e060` 从声库根目录读取 `singer.inf` 与 `epr_templates.txt`。
- 核心树序列化函数 `0x1801080a0` 会遍历子 Chunk，写回块长度，并处理 `EMPT` 占位与外部数据流偏移。
- 加载路径 `0x18010d490` 会打开两个后缀文件；后缀常量的精确文本和它与 `.ddi/.ddb` 的对应仍需进一步核对。

### 当前判断

传统声库的“训练”更准确地说是：录音设计与标注、音素转接切分、音高/动态等样本属性估计、SMS/EpR 声学分析、静态音/转接/VQM 树构建、块序列化与 DDB 偏移重定位。它不是现代神经网络权重训练。

现阶段可以有把握做到格式读取和已有树重打包，但还不能有把握从自有 WAV 生成正确的 `FRM2/EpR`。下一步优先逆向 DRS 的 SMS 分析入口与参数文件格式，再验证能否对一段自有录音生成 DSE 可读取的分析块。

## 2026-09-03：锁定 FRM2/SND 写入器

- 通过立即数 `0x324d5246`（`FRM2`）和 `0x20444e53`（`SND `）定位了 DSE 的构造、读写函数。
- `DSE5::CSMSFrame` vtable 为 `0x180238d30`；写入器 `0x180138560`，读取器 `0x1801363a0`。
- `DRS::CSMSFrame` 也有平行的读写实现：`0x18006a400` / `0x180087750`。两套字段掩码和量化分支高度对应，表明 DRS 分析结果与 DSE5 声库帧共享同一 SMS 数据模型，但对象基类布局不同。
- 通用块写入器 `0x1801071f0` 证明 DDB 顶层长度包含 8 字节 magic/size 头。
- `SND ` 写入器 `0x180048960` 证明负载中的 `u32` 是 PCM 值数量，总长严格为 `18 + count * 2`。
- `DRS::CSMSAnalysis` 确实会创建 frame/region，但当前直接调用图落在运行时 `wbhsm_getAudioChunk`，尚未发现可直接接收 WAV 的官方训练入口。后续不把“类名中有 Analysis”误当成训练工具已恢复。
- 新增独立、无依赖、只读探针 `voicebank/tools/probe_ddb.py`，准备对 7 个本机 DDB 做顶层完整性与字段分布验证。

## 2026-09-03：完成 7 个 DDB 全量验证与 DDI 引用闭包

- 独立探针完整遍历 7 个 DDB，共验证 30,731,926,266 字节、2,727,146 个顶层块；所有文件都由合法的 `FRM2`/`SND ` 块无缝覆盖，无越界、尾随字节或 SND 长度错误。
- 合计 2,670,252 个 FRM2、56,894 个 SND。所有 SND 都是 44,100 Hz、单声道、16-bit PCM；PCM 值总数 800,089,088，约 5.040 小时。
- 使用公开 DDI 解析器取得引用集合，再由独立 DDB 扫描结果逐项验证：
  - 每个 EpR 偏移都位于文件内并指向 `FRM2`；所有偏移唯一，集合恰好等于全部 FRM2 块起点。
  - 每个样本都唯一归属一个 `SND `；归一后的集合恰好等于全部 SND 块起点。
  - STA 的 SND 指针不是块首：本批 836 个 STA 样本全部严格等于 `SND 块首 + 2066`，也就是跳过 18 字节头和 1024 个单声道 PCM samples。ART/VQM 的解析结果则指向 SND 块首。未来写入器必须保留这种语义差异。
- FRM2 的固定前缀在全部库中只有一个 frame kind 值 `1`，字段掩码只有三种：
  - `0x0000002000e00207`：2,573,791 帧；普通主帧。
  - `0x0000000000000200`：95,002 帧；只包含掩码位 9 的轻量帧。
  - `0x00000000000e22b7`：1,459 帧；VQM 帧。
- 对 `Luo_Tianyi_Ning` 按 DDI 样本类别反查每个 EpR：全部 18,129 个 STA 帧使用主掩码；VQM 的 105 帧全部使用 `0xe22b7`；ART 同时包含 265,135 个主帧和 6,648 个轻量帧。
- Ning 的 6,648 个轻量帧，其位 9 对应的单精度数值全部严格为 `0.0`，且只集中在静音、清辅音、送气音、塞擦/摩擦音等 ART 区段。DSE 读取端会对非压缩的该值做对数域变换；“它表示无有效 F0 的无声帧”目前是强推断，仍待运行时消费者确认。
- 同一 EpR 内相邻普通 FRM2 的时间值严格相差 `256 / 44100 = 0.005804988662...` 秒，确认分析 hop 为 256 个采样点。公开脚本使用 512 点窗口，但窗口长度仍需由 DSE 配置或数组维度独立确认，不能仅由 hop 反推。

## 2026-09-03：确认普通单元的窗口关系与 STA 对齐点

- DDI 中曾被公开解析器命名为 `snd_identifier` 的 `u32`，实际是 SND 的 PCM 值数量。对全部 56,894 个样本逐项比较后，它与所属 SND 头的 `pcm_value_count` 全部相等，零例外。
- 全部 56,887 个 STA/ART 样本都严格满足 `pcm_value_count = epr_count * 256 + 2048`；7 个 VQM 样本则严格满足 `pcm_value_count = epr_count * 256`。
- 普通 STA/ART 因而具有 2048-sample 的固定边缘扩展；结合 STA 有效指针位于 `PCM payload + 1024 samples`，它与 2048 点分析窗的半窗对齐一致。该跨全部样本的不变量推翻了公开提取脚本中写死的 512 点窗口假设。
- Ghidra 中 `DSE5::CDBVStationaryPhUPart` 构造函数为 `0x1801139d0`，vtable 为 `0x1802276b8`，读写负载函数为 `0x180113cc0` / `0x180113dc0`。
- 外部 EpR/SND 装载函数 `0x18010abb0` 要求 STAp 选择索引为 1 且 EpR track 至少有 3 个 region；`CSMSGenericTrack` 的 `+0x150` 由 `0x18013aff0` 维护为 region 累计起始时间数组。装载器把 `regionStart[1]` 按 `round(sampleRate * time)` 换算为 PCM 字节地址，保存为有效区指针。
- 主 FRM2 掩码的正确位集合是 `{0,1,2,9,21,22,23,37}`。先前把十六进制 `0x00e00000` 误读成位 17–19，已在文档中更正；VQM 的 `0x000e0000` 才包含位 17–19。

## 2026-09-04：恢复普通 FRM2 全布局与音高字段用途

- 普通帧位 0–2 的物理顺序已确认：位 1 为 Hz 谐波频率，位 0 为对数型谐波幅度，位 2 为弧度相位。频率数组从 F0 的整数倍开始，到 Nyquist 后以 0 填充；幅度未用槽为 `10000.0`，相位未用槽为 0。
- 位 9 已直接确认为线性 Hz 的 F0：它与第一谐波相同，其余频率是整数倍。此前文档中的“高度怀疑”已改为确定结论。
- 普通帧可以精确走到块尾：三组数组、F0、位 37 `ENV `、位 21 主共振列表、位 22 三参数头与第二共振列表、位 23 `ENV `，没有未解释的尾字节。
- `ENV ` 格式为 28 字节头加 `float32` 点数据。代表性位 37 包络的 x 网格按 `F0/22050` 排列，位 23 包络按 `30/22050` 排列。
- DSE 的 `CResonance` 消费者支持把主三元组解释为中心频率、对数幅度和带宽；带宽会被限制到 20–1500 Hz。位 22 的三个头参数和第二共振列表角色仍未完成。
- 新增只读工具 `voicebank/tools/probe_frm2.py`。Ning 的代表性普通帧与无声帧均完成“完整解析 → 重新序列化 → 逐字节一致”；VQM 被识别但明确标为未支持。
- 对全部 56,894 个样本验证 `duration = pcm_value_count / sample_rate`，零例外，最大误差约 `2.7e-13 s`。
- DSE 运行时把样本 `+0x160` 用作层选择/插值坐标，把 `+0x164` 通过 `440 * 2^(value/1200)` 转成参考频率。两者均是相对 A4 的音分，但不能解释成 ART 的两个音素音高。
- 对全部 836 个 STA 样本，第二音高字段与 EpR F0 的平均音分全部吻合，最大误差 `0.000266 cent`；第一字段有 799 个相同，其余 37 个最多调整 `2.049 cent`，符合“层坐标与实际参考音高分离”的用途。
- DSE 中 `epr_templates.txt` 的解析/写入键已定位为 `epr_resonances_templates`；另有 `phonetic_group` 与 `default_epr_resonances_templates`。该文本和 `singer.inf` 由 CDBSinger 的构建/保存路径处理，属于比最终 `.ddi/.ddb` 更靠前的声库源输入。
- VQM 掩码 `0xe22b7` 已从 `CSMSFrame` 读取器完整走通：350 个正弦分量、2049 个幅度/相位谱 bin、F0、flags 驱动的位 13 特征块、两个控制字节、一个 `u32` 和两个 ENV。全部 1,459 个 VQM 帧逐字段解析后均能逐字节回环。
- `probe_frm2.py --scan` 已对 7 库 30.73 GB、2,670,252 个 FRM2 做内部边界全量验证；三种掩码全部精确走到块尾，未发现未知掩码、尾字节或子结构越界。

## 2026-09-04：恢复 DRS 内存 PCM 批处理驱动

- `DRS::CSMSAnalysis` 构造函数 `0x1800cdde0` 的 vtable 为 `0x1805bef20`；`+0x08/+0x10/+0x18/+0x20` 分别是取下一帧、初始化输出、提交帧和 finalize。`CSMSRegionAnalysis` 在 `0x1805bebd8` 提供对应的 region 包装。
- 定位到批处理驱动 `0x1800d9b10`。它可以从 `CSoundIO` 的 16-bit PCM 读取，也能从 `{float *pcm, u32 sample_count, u32 sample_rate}` 描述符读取；每次送入 128 samples，然后 drain 所有当前可产生的 `CSMSFrame`。
- 真正的 PCM push 是 `0x1800d1140`：写入 `+0xae0` float 环形缓冲，`+0xaf4` 是写指针，`+0xaf8` 是累计输入秒数。主分析函数使用 `+0xb00` 读指针，并在每帧后前进 `+0xbb0` samples。
- 构造函数按 `round(sample_rate / config[0x18])` 计算 hop，并把 `hop/sample_rate` 保存为帧时间步。参考库的 256-sample hop 对应配置值 `172.265625`；128 只是输入分块，不是分析窗。
- 批处理驱动使用配置 `+0x1c/+0x20` 裁剪归一化分析区间。大型配置对象的来源、F0/region 外部约束及 DRS→DSE5 转换仍待恢复。
- 该驱动不在 DSE 导出表中，当前也没有直接代码 caller，只有数据表引用；它证明内部离线数据流成立，但不能包装成稳定的官方训练 API。详细记录见 `06_drs_offline_analysis_driver.md`。

## 2026-09-04：DRS SMS2 首次实际生成与回读闭环

- 建立 `voicebank/tools/drs_harness`，直接从程序生成的 44.1 kHz float PCM 调用配置构造、`DRS::CSMSAnalysis`、批处理驱动和 collection writer；另建 `probe_sms2.py` 独立扫描嵌套 FRM2。
- 配置构造器的 `+0x18` 默认值是 `86.1328125`，对应 512-sample hop；参考库的 256-sample hop 必须覆盖为 `172.265625`。harness 输出时间步严格闭合到 `256 / 44100`。
- 找到外部 F0 分支：动态参数 `0x14 == 0` 时，参数 `0x0d` 直接提供逐帧 F0。动态槽是 envelope；必须同时写归一化时间 0 和 1，单写起点会向默认终点插值。
- 发现 writer 的字段筛选：输出掩码是 `frame.mask & stream[+0x20]`。全零 stream 会把丰富分析帧错误过滤成 28 字节小帧；把该字段设为全 1 后得到完整结果。
- 两秒 220 Hz 谐波信号的自动模式得到 345 帧，其中 128 帧有声、217 帧无声；外部 220 Hz 模式得到 345/345 有声帧，probe 解码 F0 为 `219.708`–`220.969` Hz。
- 0.5 秒外部 F0 实验写出 641,288 字节、87 帧；DSE reader 消费同样 641,288 字节并重建 1 个 generic、87 帧，形成“PCM→DRS 分析→SMS2 写出→DSE 回读”的闭环。
- DRS 原始有声掩码 `0x801c6fa6`（位 `{1,2,5,7,8,9,10,11,13,14,18,19,20,31}`）与最终传统库主掩码 `0x2000e00207` 明显不同。下一硬缺口是 DRS→DSE5 的后处理/字段映射，当前 SMS2 不能直接复制进 DDB。详见 `07_drs_sms2_harness.md`。

## 2026-09-04：最终普通帧、单元 DDB 与最小 DDI 原生闭环

- 后续定位并调用了 DRS 原始帧到最终 DSE5 普通帧的转换链。`DrsHarness` 可从合成 PCM 或外部 44.1 kHz PCM16 WAV 写出目标主掩码 `0x0000002000e00207`；110/220/440 Hz 与外部 WAV 实验均由 `validate_main_sms2.py` 逐字段通过。
- `build_unit_ddb.py` 把最终帧与匹配 PCM 组装为一个 STA DDB：52 个 FRM2 后接一个 SND，PCM 数量严格为 `52*256+2048=15360`，STA SND 指针为 chunk 起点加 2066。
- DSE 的 stationary factory 已闭合：STA/STAu/STAp 三种 type 0 构造分别位于 `0x180113960`、`0x180113990`、`0x1801139d0`。紧凑 STAp 用两个名为 `SND`/`EpR` 的 `EMPT` 子对象保存源单元位置。
- SND 的 canonical 源偏移为 `0x3d`；EpR 偏移严格为 `0x3d + snd_chunk_size + 7`。商业首样本的 139,794 字节 SND 对应 DDI 值 139,862，与公式完全一致。
- STAp 缓存读取器 `0x18010a950` 依次读取帧数/偏移表、采样率、声道数、PCM 数量、DDB SND 指针和四个整数。四个整数的消费者 `0x18010cfb0/0x18010d1f0` 证明它们是分散保存的库级载荷，而非波形循环/拼接边界；最小库可全部写 `-1`。
- `.tree` 根 `DBS ` 与最终 `DBSe` 的关键差异是 PHDC 后的 0x104 字节名称摘要。`DBSe` 读取器 `0x18010d8e0` 使用 `MD5("K2ho" + upper(base_name) + "nF")` 的 32 字节十六进制文本，后补 228 个零；遗漏该块会让后续对象错位。
- 新增 `finalize_stationary_ddi.py`，负责插入 STAp 缓存、DBSe 摘要和最终 materialized/source 标记。用名称 `one` 生成的 DDI 为 1,554 字节，配套 DDB 为 636,058 字节。
- 公开 `DDIModel` 成功解析该自有库：`a`、一个 STAp、52 个 EpR、duration `0.348299319727891`、pitch `-1200.0`、fs 44100。duration 使用完整 SND PCM 数量（含两侧 1024-sample 边缘）除以采样率，与 56,894 个商业样本的不变量一致。
- DSE 原生 `.ddi/.ddb` 加载入口 `0x18010d490` 回读同一文件并得到：根认证 1、STA/STAu/STAp 数量均 1、52 帧、44100 Hz、单声道、15360 PCM、SND 指针 607386、四个载荷整数均 `-1`，`load.valid=True`。
- 当前最小库仍没有 ART 转接，也没有部署或启动编辑器。下一阶段从 ART/ARTu/ARTp 工厂和紧凑缓存开始，目标是补齐 `Sil↔a` 后再做隔离宿主渲染。

## 2026-09-04：ART/ARTu/ARTp 诊断单元原生闭环

- ART、ARTu、ARTp 构造器分别定位为 RVA `0x110b00`、`0x110b30`、`0x110b70`，对象大小为 `0x168/0x178/0x268`。在已初始化的源 ART 下加入目标 ARTu 和名为 `default` 的 ARTp 后，DSE 可写出完整中间树骨架。
- 单转接源单元的 ARTp magic 位于 `0x33`，SND magic 位于 `0x6c`，EpR 位于 `0x6c + snd_chunk_size + 7`。商业首样本的 `0x33/0x6c/0xae85` 与其 79 帧、22,272 PCM 的 SND 长度精确闭合。
- ARTp 版本 3 紧凑读取器 `0x180110d50` 在 EpR 表后读取采样率、声道、PCM 数量、SND payload/core 两个指针和 alignment 四元组 vector。Ning 全部 5,099 个 ARTp 的两个 SND 指针严格相差 2048 字节。
- 全部 5,099 个 ARTp 都有两组 alignment；outer 两段无缝分割 `[0,epr_count]`，inner 始终位于对应 outer 内。75 个样本存在 inner 裁剪，且 `o→uei` 的裁剪样本仍全部是有声主帧，因此 inner 是稳定/可用子区间而非简单 voiced mask。
- 新增 `build_bank_ddb.py`，把两个独立验证的自有单元合并为 1,272,116 字节 DDB，并输出绝对偏移 manifest；新增 `finalize_minimal_articulation_ddi.py`，生成 2,204 字节的一个 STA + 一个诊断 `a→a` ART DDI。
- 公开解析器读回 ARTp source `0x33`、SND source `0x6c`、EpR source `0x7885`、52 个第二单元帧、两个 SND 指针和两组 alignment。DSE 原生加载器也读回 ART/ARTu/ARTp 数量均 1、52 帧、15360 PCM、指针差 2048、alignment `[0,26]/[26,52]`，根认证为 1，`load.valid=True`。
- 同时修正 STA/ART duration：它等于完整 SND PCM 数量除以采样率，包含两侧各 1024 samples；52 帧单元为 `15360/44100 = 0.348299319727891` 秒，而不是只按核心帧数计算。
- 回读 harness 的初始 phonetic dictionary 会由 DBSe loader 销毁并替换；加载模式停止二次释放这些指针后，退出阶段不再出现 `0xc0000374`。
- 当前 ART 是重复自有 220 Hz 单元，只证明结构，不证明真实过渡音质。下一步必须用带 outer/inner 标注的真实 `Sil↔a` 录音。

## 2026-09-04：闭合 VDM 发现、组件 ID 与 DSE 配对加载

- `VDM2.dll` 的现代组件读取器 `0x1800dcc30` 已完整核对。传统 voice 模式读取 `Path` 后拼接 16 字符组件 ID 子目录，只用 `FindFirstFileW(...\\*.ddb)` 找到数据文件；`VDM_VoiceBank_path` 返回该完整 DDB 路径。
- V5 风格最小元数据门槛已确定：有效组件 ID、可找到 DDB 的 `Path`、完整 `Version/Major/Minor/Revision`、6 字符 `DRP`、非空 `Name`、16 字符 `Date` 和非空 `BankName`。`IsInstalled` 缺失时默认 1；空 `DefaultStyleID` 回退到固定 UUID，缺失 `GroupName` 回退到 `BankName`。
- DSE `0x18017fd50` 从 VDM voice bank vtable `+0x30` 取得 `.ddb` 路径，`0x180180180` 用 `_splitpath` 提取目录与 stem，随后构造 `CDBSinger` 并调用 vtable `+0x18 = 0x18010d490`，固定打开同 stem 的 `.ddi` 与 `.ddb`。注册表无需也没有单独 DDI 路径。
- 新增 `vdm_harness`，直接调用 6.13.0.1 的公开 VDM C API且不启动 Editor、不写注册表。本机成功枚举 29 个 V3/V4/V5 传统库；7 个 V5 中文库的路径、版本、DRP、语言 4、名称、参数数和可合成版本标志均与注册表/反编译一致。
- 组件 ID 解码器 `0x1800d9de0` 已恢复：16 字符 ID 解为 14 位 base-28 payload，两位校验选择两轮置换，payload 索引 3 编码 native language。新增 `compid_codec.py` 从用户自己的 VDM.dll 读取并验证 codec 表，不把专有表固化进仓库。
- `BD79E492NWWK3DDF` 被解为 `00L415D0050000`，语言 digit 为 4；测试 payload `00A40000000000` 编成 `BCB8AXEZKKTHYCAF`，再由 VDM 6.13.0.1 自身内部函数验证为有效并原样解回。
- 许可证门槛与格式加载明确分离：VDM 对象可暴露 default/bundle license descriptors，但托管 `VoiceBankExtension.IsValidLicense` 仍要求 DSE license 列表中同 CompID 的有效结果。任意注册表 `Key` 不能产生合法授权，不能借用商业组件 ID。
- 本轮仍未写注册表、未部署、未启动 Editor。下一宿主步骤必须使用独立组件 ID和可撤销隔离注册，同时单独处理研究库的许可证/UI 判定。

## 2026-09-04：`Sil`/`a` 双音素与双向边界原生闭环

- `tree_harness` 现可构造两个 PHDC 音素项：末字节 1 的 unvoiced `Sil` 与末字节 0 的 voiced `a`；公开解析器精确读回相同分类。
- DSE 初始化后的 articulation ARR 已分别含 `Sil` 和 `a` 两个源对象。harness 在其中加入 `Sil→a` 与 `a→Sil` 两个 ARTu/ARTp，1,244 字节骨架的 ARTp 分别位于 `0x2bb` 和 `0x38a`。
- 新增 `finalize_sil_a_ddi.py`。它验证 PHDC 类型和每个 ARTp 后的 `default/target/source` 名称，按从后到前的顺序插入两份可变缓存，避免偏移漂移或转接错绑。
- 三个自有单元合并后的 DDB 为 1,908,174 字节，最终 DDI 为 2,926 字节。公开 `DDIModel` 得到 STA `a`、ART `Sil a`/`a Sil`，每条转接均有 52 个 EpR 和两组 alignment。
- DSE 原生回读得到 `articulation.count=2`，按名称找到的两条转接各有一个 target、一个 part、52 帧、44.1 kHz 单声道和两个相差 2,048 字节的 SND 指针；payload 分别为 1,241,396 与 1,877,454，确认没有共用错误单元，最终 `load.valid=True`。
- 当前三份单元仍重复同一自有 220 Hz 合成数据，只证明完整最小拓扑、缓存和指针。下一硬缺口是显式 outer/inner 标注与 DRS region 约束，然后才能用真实 `Sil↔a` 录音替换诊断 ART。

## 2026-09-04：ART 边界标注实际驱动 voiced/unvoiced 帧

- `finalize_minimal_articulation_ddi.py` 与 `finalize_sil_a_ddi.py` 现支持显式 outer/inner alignment；两条 ART 分别使用 `[0,26,2,24]/[26,52,28,50]` 与 `[0,26,3,25]/[26,52,27,51]` 的裁剪测试均被 DSE 原生回读，`load.valid=True`。旧单 ART 输出 SHA-256 保持不变。
- `CSMSRegionAnalysis` 构造器、vtable、初始化/提交/finalize 位于 `0x1800956b0`、`0x1805bebd8`、`0x180095780/0x1800957f0/0x1800959b0`。配置 `0x2f` 默认 0；设为 1 后会按 frame `+0xf0` 的正负切 region。
- 该 region 切分不能直接代表 voiced/unvoiced：DRS 原始 bit31 帧的 `+0xf0` 是相对 A4 的音分，220 Hz 与自动分析得到的约 110 Hz 都为负。实测仍只有一个 type 7 region，尽管 48/52 帧有 F0。
- 外部 F0 动态参数 `0x0d` 的 envelope 坐标已由实验确认为录音内 0..1 归一化位置。把 0.15 秒误写为位置 0.15 会得到 8/44；按 `0.15/0.30=0.5` 写入后，`Sil→a` 精确得到 26 unvoiced + 26 voiced，`a→Sil` 精确得到相反顺序。
- 两份边界 SMS2 均通过 DSE writer/reader 与独立 probe。`build_unit_ddb.py` 新增 split-frame 和两侧 voicing 强校验；错误反向期望报告 52 个 mismatch 且不写文件。
- 使用全 voiced STA、26U+26V ART、26V+26U ART 重建的 DDB 为 1,304,518 字节，DDI 为 2,926 字节。公开解析器读出 `Sil a`/`a Sil`；DSE 原生读回 payload 939,556 与 1,273,798、各自 inner alignment，最终 `load.valid=True`。
- 本轮 PCM 仍是持续谐波，强制 unvoiced 区与波形内容不匹配。机制层缺口已经从“怎样注入边界”缩小为真实人声/静音录音、窗边界 QA 与宿主渲染。

## 2026-09-04：规格驱动的 WAV→DDI/DDB 一键闭环

- 新增 `build_minimal_sil_a_bank.py` 与 `minimal_sil_a_spec.example.json`。规格输入 singer stem、参考 F0、三段 WAV、两条秒边界和四个 inner 稳定区间。
- 构建器校验 44.1 kHz mono PCM16、固定 `Sil↔a` 图、时间范围与 F0；自动构建两个 Release harness，生成三份最终 SMS2，检测实际单次 voicing 变化，并要求检测 split 与秒标注换算一致。
- STA 要求全部 voiced；两条 ART 分别强制 unvoiced→voiced 与 voiced→unvoiced。随后自动生成单元 DDB、绝对偏移 manifest、双音素 DSE 树、带完整 alignment/DBSe 的最终 DDI，并用 DSE 原生加载器验收。
- 合成输入回归从空输出目录开始完整成功：split 26、inner `[3,23]/[29,49]`、DDB 1,304,518 字节、DDI 2,926 字节，最终 `root.authenticated=1` 与 `load.valid=True`。公开解析器另行读出 voiced `a`、unvoiced `Sil`、STA `a`、52 帧的 `Sil a`/`a Sil`。
- `build_report.json` 保存每个单元的 FRM2/SND 偏移、voicing boundary、frame alignment 和最终文件信息。下一次真人录音实验可直接替换规格中的三条 WAV，不再手工重放研究命令链。

## 2026-09-04：七库中文音素图与音高层基线

- 新增 `analyze_reference_graph.py`，通过公开 DDI reader 取得键，再独立计算集合、图、层数与 edge-trail 下界；不读取 DDB 音频/声学负载。
- 七库 PHDC 均为完全相同的 62 音素、42 voiced/20 unvoiced；STA 均为相同的 38 key。STA 在每个产品内统一为 2、3 或 4 层，对应总数 76/114/152。
- 七库 ART 交集为 2,556，且全部是二音素 key；并集为 2,559。仅 `s→ei`、`t_h→ei`、`z`→`ua` 三条额外边，三者都只存在于 Luo_Tianyi_Wan、Yan_He_Mu、Yuezheng_Ling_You。
- ART 的 60 个节点构成单一强连通分量；`?` 与 `Asp` 只存在于 PHDC、不参与 ART。公共图含 34 个 self edge，1,970 条边的反向边也存在。
- `x→Sil` 恰有 38 条，源集合与 STA 完全一致；`Sil→x` 有 55 条。由此确认起音/收音边界不对称，不能给全部 62 音素机械补 `Sil` 双向边。
- 不重复边时，覆盖 2,556 公共边至少需 449 条 trail、合计 3,005 个音素 token。它只是任意音素路径下界，后续还需允许重复连接边并加入中文音节与歌手录音长度约束。
- 每库绝大多数 ART key 具有完整 2/3/4 主层数，只有 7–17 个 key 是单层；全部单层例外均为 `Sil→unvoiced consonant`，没有其它类型的部分缺层。

## 2026-09-04：V5 元数据生成器

- 新增 `generate_v5_metadata.py`，把 CompID payload、native language、组件名/显示名、版本、DRP、Date、基础 Path、同 stem 文件名与 DBSe digest 收束为 schema version 1 的离线输入。
- 工具从用户自己的 VDM.dll 加载 codec 表，要求生成的 16 字符 CompID 能原样解回 payload，并拒绝语言位不一致、后六位非数字、保留 ID 冲突和不满足 VDM 长度门槛的元数据。
- 输出为 JSON manifest 和 `.reg.txt` 审阅稿；不读写注册表，不输出 `Key/IceProductName/IceValue`，并显式标记 `creates_license=false` 与 `license_status=unresolved`。
- 示例规格的正向 smoke test及语言位、DRP、Path、冲突四类负向测试通过。该工具只闭合组件身份与发现元数据，不改变 DSE 许可证结果。

## 2026-09-04：DSE 许可证对象与 Editor 判定链

- 新增只读 `license_harness`，直接调用 VDM/DSE 公共导出；不启动 Editor、不写注册表、不输出 key/serial 内容，默认只给汇总，逐项组件信息必须显式开启。
- VDM descriptor 与 DSE `License` 已明确分层：前者只有候选 key/serial；DSE 初始化验证后才产生 `Result`、`SpliceResult`、expiry 和 remaining-days。
- DSE 许可证构造对象的 type、CompID、显示名、版本与结果字段已通过 vtable 包装和构造路径闭合。`CompName` 来源是 `VDM_VoiceBank_name`，不是 `componentName`。
- 本机枚举 29 个传统库、57 个 descriptor，全部 key/serial 非空；DSE 中 29 个 CompID 全部能匹配对象，但结果为 `Expired=7`、`InvalidTrialKey=1`、`InvalidKey=21`，无一进入 Editor 接受集合。
- 6.13.0.1 Editor 只接受 Trial、ValidLeaseFile、PaidOffLeaseFile、ValidExpiryKey、NoError，并另行检查 synthesizable version。由此确认“注册发现、格式加载、许可证有效、版本兼容、最终渲染”必须分开验收。
- 许可证验证含结构检查与哈希/签名分支，不是非空字符串判断。DSE 虽导入网卡/卷信息 API，但尚未闭合到具体许可证分支，不能先断言机器绑定方式；精确载荷格式与合法签发路径仍未知，本研究不生成或绕过许可证。
- `SpliceResult` 已定位到独立的 `%LOCALAPPDATA%\SpliceSettings\license\<identifier>.lic` 只读支路；缺失、结构/身份和时间状态分别判定，但未读取本机文件，也未研究生成或绕过其载荷。

## 2026-09-04：中文 ART 录音 trail/clip 基线

- 新增 `plan_art_recording_trails.py`，直接消费 `analyze_reference_graph.py --include-keys` 的交集/并集，不在仓库保存完整商业库派生边清单。
- 规划器按度数差加入虚拟平衡边、运行 Hierholzer traversal 并在虚拟边处切开；交集 2,556 边得到严格最少的 449 条 edge-disjoint trails、3,005 个 token，逐边恰好一次断言通过。
- 再以有向 BFS 贪心连接 trail，所有 connector 仍必须是图中真实 ART 边；交集使用 497 个重复连接 transition，形成 3,053-transition 连续 route。
- 默认 12-token 上限并在相邻片段间重叠一个 token，得到 278 个 clips、3,331 个实际录音 token；2,556 条必需边全部反查到 clip 和 transition index，JSON round-trip 输出约 875 KB。
- 2,559 边并集也通过完整验证；另用六边不平衡小图验证虚拟切分、连接、分片和 trace。当前输出仍是音素链，不是自然中文句表，下一步加入音节模板、提示文本和秒数/呼吸代价。

## 2026-09-04：VOCALOID4 传统声库结构与 V3/V4/V5 对比

- 逆向分析了 `VDM2.dll` 中的多模式注册表路由器 `FUN_1800e04f0`：Mode 0/1 指向现代 64 位 `HKLM\SOFTWARE\VOCALOID5\Voice\Components`；Mode 2/3/4 分别指向 32 位 `HKLM\SOFTWARE\WOW6432Node` 下的 `VOCALOID4\DATABASE`（4.0）、`VOCALOID4\DATABASE41`（4.1）以及 `VOCALOID3\DATABASE\VOICE3`（3.0）。
- `FUN_1800dcc30`（入口 `0x1800dcc98`）在汇编层面直接对 Mode 2/3/4 执行条件跳转，转入旧格式读取器 `FUN_1800dabf0`。
- 旧读取器 `FUN_1800dabf0` 要求 DWORD `INSTALLED == 1`，通过 `(` 和 `)` 提取 `NAME` 括号内的短名，通过 `PATH` + `CompID` + `FindFirstFileW("*.ddb")` 寻找首个数据文件，并隐式为 Mode 2/3/4 赋值版本号 `4.0.0`、`4.1.0` 与 `3.0.0`。
- 破解了 `.vvd` 文件的混淆编码：非空白/换行字节与 `0x1A` 异或。V4 的 `.vvd` 包含 6 项语音参数（包括 V4 新增的 `"Growl" = "1"`）；V5 则退化为仅含 `VOICEIDSTR` 与 `VOICENAME` 的 `_v4compatible.vvd` 桩文件。DSE 加载流程（`FUN_18010d490`）完全不读取 `.vvd`。
- 对 V4 洛天依萌（`Luotianyi_CHN_Meng.ddb`，4.16 GB）进行全量物理块扫描：367,558 个 `FRM2` 与 7,749 个 `SND ` 块。其掩码分布同样完全由主有声帧（`0x2000e00207`）、无声帧（`0x200`）和 VQM 帧（`0xe22b7`）组成。
- 确立了 V3 与 V4 的物理分水岭：V3 物理上不存在 `0xe22b7` FRM2 帧与 `VQM ` 块，V4 正式引入 Growl（VQM）并在 DDB/DDI 中建立对应结构；V5 沿用了该体系。
- 用 `tree_harness` 调用 DSE 原生加载器（`0x18010d490`）验证 V4 初音未来 Sweet（`MIKU_V4X_Sweet`）：成功反序列化全部 12 个音素、4 个 stationary 层、51 个 articulation 单元、双 SND 指针与 alignment 四元组，`load.result = 0`。
- 定位了 `DSE.dll` 中 `DBSe` 摘要读取器 `0x18010d8e0` 的双分支认证：分支 1 为 `MD5("K2ho" + UPPER(stem) + "nF")`，分支 2 为 `MD5("1m5Pj" + UPPER(stem) + "qFE")`。
- 整理生成专门文档 `18_v4_voicebank_structure_and_comparison.md`，涵盖完整注册表逆向、汇编跳转、.vvd 解密、物理块统计与三代对比矩阵。
## 2026-09-04：原生中文 G2PA 与拼音 ART 覆盖

- 新增 `g2pa_harness`，在内存中建立 VSM sequence/track/part/note，再向 CHS G2PA 查询真实候选；不启动 Editor、不保存工程、不写注册表。实测 sequence 和 manager 均正常释放。
- 新增 `probe_chinese_g2pa.py`。项目 `Pinyin2Xsampa` 的 441 个写法全部 `CanConvert=true`、各有一个候选，原生首候选与映射 441/441 精确一致；清单 SHA-256 为 `1d79dd0db27d65eded7da82bc4fc3844a13d3bf381df18bcc1b90b2ec7a4f7ba`。
- 441 个拼音写法折叠成 407 个不同音素音节，且每个音节只含一或两个音素。
- 新增 `plan_chinese_g2pa_prompts.py`。七库公共 2,556 边被无歧义地分成 373 条音节内、2,090 条音节间、55 条静音起始、38 条静音收尾，零重叠、零未分类。
- 首尾显式 `Sil`、每片最多两个拼音音节的候选共有 166,056 个不同 coverage set；最终 2,090 条双音节提示覆盖全部 2,556 边，无非法边。每片最多只有一条音节间边，因此 2,090 同时是该模型下界，输出构造达到下界。
- 当前提示是可发音拼音而非自然句；声调、汉字、秒数、呼吸、音高层、forced alignment 和真实录音 QA 仍待加入。

## 2026-09-04：长录音到 ART/STA 单元的名义切分接口

- 新增 `plan_recording_segments.py` 与 `segmentation_spec.example.json`。工具只接受 capture validation 中状态为 `passed` 的 take，并交叉核对 manifest、长提示与七库 ART 图的 SHA-256/边集合。
- 对每条 ART 长提示重新展开全部 transition：`Sil→onset`、双音素音节内部、音节间和 `coda→Sil` 四类边界分别落到固定录音时序；190 条提示共 3,376 个 occurrence，角色与 2,556 条 `required_edge_trace` 完全一致。
- 每个候选保存原 WAV SHA-256、源采样点 outer 区间、切后单元内 `boundary/source_inner/target_inner` 秒值、两侧 PHDC voiced/unvoiced 分类和目标 F0，可直接转写到现有规格驱动构建器。DRS 帧下标使用 `ceil(unit_samples/256)` 与现有 `round(seconds/duration*frame_count)` 先给 provisional 值，实际分析后必须按真实 frame count 重算。
- STA 不再把带首尾静音和 carrier onset 的整条 WAV 送入全 voiced 分析，而只提出持续区 35%–85% 的稳定候选。所有 ART/STA 都保留 `needs_manual_*_review`，没有把节拍表称为声学强制对齐。
- 自有正弦 partial 回归中，一条 ART take 产生 14 个候选、一条 STA take 产生 1 个候选；首个 0.44 秒 ART 单元为 19,404 samples、估计 76 帧、split 38，inner 估计为 `[19,33]` 与 `[43,57]`。两次输出 JSON SHA-256 均为 `3121580a46f382e5c08b33ade34ccfebcf1d37e3970f71565ed8ff5fa71cbe06`，规范候选哈希为 `7dc564df618604551fe4ca256e10a09bb0c8f96b6066490ba5a6cec8c1b7c01e`。
- 另用不读取 WAV 的全 manifest 结构回归确认：684 个通过 take 会展开 10,128 个 ART occurrence、114 个 STA 候选，并为 `2,556*3=7,668` 个 ART layer-edge 和 `38*3=114` 个 STA layer-phoneme 各选择一个确定性占位候选；该测试只验证规划完整性，不冒充 684 条真实录音已经通过。

## 2026-09-04：身份字段与许可证对象逐项对齐

- 扩展只读 `license_harness`，在不输出 key/serial 内容的前提下，把 VDM `ComponentName/Name/Version/NativeLangID/LangIDs/IsSynthesizableVersion` 与 DSE License 的 `CompID/CompName/Version` 逐项对照。
- 41 个 Voice license 全部按 CompID 唯一匹配到 29 个传统 DSE 或 12 个 DNN voice bank；`DSE CompName == VDM VoiceBank.Name` 为 41/41，版本三元组相等为 41/41，名称/版本 mismatch 均为 0。由此确认 License 对象使用 `BankName→VoiceBank.Name`，不是注册 `Name→ComponentName`。
- 29 个传统库只有语言 0 与 4 两组，分别 11/18，全部 `NativeLangID == LangIDs[0]` 且当前版本可合成。七个中文 V5 库均为 5.0.0、语言 4、`LangIDs=[4]`；七个 CompID 解出的 payload 第 4 位也全部为 4。
- 明确四个常被混称“认证”的对象：CompID 的可逆编码/checksum 只做身份语法；DBSe MD5 只做 DDI stem 一致性；用户 `defaultVoiceCompID` 只做默认选择；DSE `License.Result` 才是 Editor 授权门槛。前三者都不能产生后者。
- 复核 2026 年官方页面：Authorizer 1.0.2 仍管理 V3–V6 Voicebanks，Voicebank 未授权宽限期仍为 14 天；第三方 Voice Bank 支持仍指向与 Yamaha 签有许可协议的 partner company。后续又补充确认企业咨询与审查制 `VOCALOID FAN-ding` 两类公开项目入口，但仍没有自助 CompID/签名 API。

## 2026-09-04：preferred 单元提取与批量 DRS 帧契约

- 新增 `extract_recording_units.py`。它在创建输出目录前核对 segmentation 规范哈希、完整性、源 WAV SHA/容器/frame count、采样范围和安全相对路径；输出 PCM 与源 slice 逐字节相同，全部单元保留 `unapproved_extracted_candidate`。
- 局部自有合成回归从 2 个通过 take 提取 14 ART + 1 STA；两份输出全部相对路径/文件 SHA 零差异。unit canonical hash 为 `61ace9a615a3043b46bb73056e6daf9c5a07bc520308bfe77c4f1d2a9ec57a18`，完整 manifest SHA 为 `6ffdde3ef5543401f0909778907305af77975496cfeb666133f3b5fb7a9af938`。
- `DrsHarness` 新增全清音 `unvoiced` 模式：关闭 auto F0 并把外部 F0 固定为零。19,404-sample 测试得到 76/76 个 unvoiced frame，补齐了 unvoiced→unvoiced ART；其余三类仍使用固定正 F0 或单次 0/正 F0 阶跃。
- 新增 `analyze_recording_units.py`。它重新验证 unit manifest/WAV，顺序生成最终 SMS2，要求实际帧数等于 `ceil(samples/256)`，逐帧核对四类 ART 清浊契约，并按实际帧数重算 split 与 inner ranges；STA 必须全 voiced。
- 15 个单元全部通过：11 个 76V ART、1 个 76U ART、1 个 38V+38U ART、1 个 38U+38V ART和 1 个 130V STA。上游只是 partial capture，因此 analysis complete 但 coverage complete 保持 false，所有结果标为 `structurally_valid_unapproved`。
- 两次独立运行发现全部原始 SMS2 hash 均不同，但所有 FRM2 块逐帧逐字节相同；首例 875,353 字节仅 96 个非帧 wrapper 字节变化。因此规范哈希改用带 `uint64_le(size)` 分隔的 FRM2 payload stream，两次均为 `cab00d108e8661518030560d793c63f77b9562d020acfb371dfe602195ce7b77`，原始 SMS2 hash 只作单次 provenance。
- 已有输出目录会被拒绝且原清单不变；篡改单元 WAV 后在创建输出目录前因 SHA 不符退出。下一工程接口是把验证后的 FRM2+PCM 批量变成单元 DDB，再合并完整 DDB/DDI。

## 2026-09-04：流式多单元 DDB

- 新增 `assemble_recording_units_ddb.py`，同时绑定 unit manifest 与 analysis manifest，不产生几千份临时单元文件，按 unit order 直接流式写 `FRM2...+SND` 到单一 DDB。
- 每个单元保留绝对 frame offsets、SND chunk/payload/core 三类位置、PCM 数量、补零差、F0 与 ART 两组 outer/inner alignment；STA core pointer 与 ART payload/core pointer 关系和已闭环 finalizer 完全一致。
- 装配时再次核对输入 SHA、FRM2 payload、frame count、四类清浊与 inner ranges；完成后从最终文件逐单元重算范围 SHA、逐帧 parse/serialize、重算 payload SHA 并检查 SND header，所有检查通过后才删除 incomplete marker。
- 两份 raw SMS2 全部不同但 FRM2 相同的 15-unit 输入，生成逐字节相同的 12,656,830-byte DDB，SHA 为 `72dfb6190191252499e866613e9449960df56afa32ee7d5410b06a8b7526a4cb`，规范偏移清单哈希为 `5aa0f398abdaa0acc1e693e94c9434b993e254b4aa3d7d2bc6b17f29feb2f603`。
- 已有输出目录会被拒绝且原 DDB 不变；篡改 SMS2 后在创建输出目录前失败。上游仍是 partial synthetic fixture，不能称为完整声库。下一步是通用多音素/多层 skeleton 与 DDI 注入。

## 2026-09-04：计划驱动的多音素、多层 DDI

- 新增 `plan_ddi_tree.py`，把七库公共图、完整 62-PHDC 和流式 DDB 的当前 STA/ART unit 顺序绑定成规范计划；partial 输入保留完整 PHDC，但不伪造缺失的 STA/ART。
- 扩展 `tree_harness` 的 `--plan` 模式：DSE 原生构造 62 个 PHDC、计划内 STAu/STAp 与 ARTu/ARTp，并为每个 ART layer 写唯一 SND/EpR source offset。
- 新增 `finalize_planned_ddi.py`。它验证 plan/DDB/skeleton 的哈希、语义、名称、数量、顺序和 source offset，修正序列化 STAu/ARTu index，再从后向前注入全部 EpR/SND/alignment，最后统一加入 DBSe digest 和 compact normalization。
- 新增 `verify_planned_ddi.py`，用固定外部版本的公开 `ddb-tools` 独立比较 PHDC、index、part key、frame offsets、SND pointers、duration 与 alignment，不复用 finalizer 的二进制解析。
- 15-unit partial 回归得到 62 PHDC、1 STA、14 ART；公开解析器恢复 14/14 edge，DSE 返回 `load.result=0`、`root.authenticated=1`、`load.valid=True`。最终 DDI 为 24,555 bytes，SHA-256 `5f601025d6aae5c5b5269bb12118500a6692e90792ca56fad32960c1008377c7`。
- 两次独立构建的计划文件和原生 skeleton 保留各自非语义 provenance 差异，但最终 DDB/DDI 分别逐字节相同。另一个两层同边 fixture 恢复 STA keys `["0","1"]` 和 ART keys `[108,916853]`，公开解析器与原生 loader 均通过。
- 单 STA/单 ART 的旧 finalizer 也重新跑过原生加载回归，确认拆分通用插入函数没有破坏旧路径。完整真人 7,782-unit 构建、宿主隔离渲染和正式授权仍未完成。

## 2026-09-04：官方许可证入口补充

- 复核 Yamaha 2026-02-20 许可说明与 2026-04-02 Authorizer 1.0.2：每个产品 serial 对应终端用户 license，authorization info 由 Yamaha 服务器发放并保存在单一 PC/OS 环境；Voicebank 宽限期仍为 14 天，Authorizer 继续覆盖 V3/V4/V5/V6 Voicebanks。
- 第三方支持政策仍指向与 Yamaha 签有许可协议的 partner company，并给考虑 VOCALOID business 的主体提供企业咨询入口。
- 补充发现审查制 `VOCALOID FAN-ding`：Yamaha/CAMPFIRE 公开承诺支持筹资、录音、voicebank 制作、发行与宣传，也允许无制作经验的创作者申请；但不保证获选，且没有公开自助 CompID/serial/signing API，也没有确认接收申请人自构建的传统 V5/DSE DDI/DDB。
- 因此合法发行路线不是“自己生成 CompID 和 Key”，而是进入 Yamaha/partner 的产品化与签发流程；格式构建器继续保留 `creates_license=false`，不实现或研究许可绕过。

## 2026-09-04：EVEC 完整逆向工程与 VOCALOID 内原生实现闭环

- 完成对初音未来 V4X 物理声库（`MIKU_V4X_Original_EVEC.ddi`）的全量逆向分析：
  - `PHDC` 块严格包含 133 个物理音素：44 个基础日语音素、24 个 Voice Color 元音/鼻音（Soft `#2` 与 Power `#6`）、58 个 Consonant Attack 辅音起音（Mild `#2` 与 Strong `#6`）、2 个 Voice Release 吐气释放音（`*#1` Breath-Short 与 `*#2` Breath-Long）、5 个独立呼吸音（`br1..br5`）。
  - `STA / STAu` 严格包含 36 个持续音单元（12 标准持续音 + 12 Soft 持续音 + 12 Power 持续音），证明所有音色均有真实声学主帧与采样支持。
  - `ART / ARTu` 包含 209 个源音素块与 1,611 条转接路径：包含 72 条直通 `*#1/*#2` 的吐气过渡、1,017 条辅音 Attack 转接、130 条标准到彩色元音的切入转接。
  - 普通非 EVEC 声库（如 `MIKU_V4X_Sweet`、`MIKU_V4X_Dark`）也包含 51 个音素，原生带有 `*#1` 与 `*#2` 的 Voice Release 释放音。
- 通过 Ghidra MCP 逆向 CFM Piapro Studio 核心动态库（`PPS.dll`，x86 32-bit）：
  - 反编译定位了 EVEC 核心类层次：`EVECDefinitionSet`、`EVECDefinition`、`UEVECDivideInfo`、`NoteEVECs`、`UEVECData`、`CVCLDNoteEVECPage` 与 `EVECRecomposer`。
  - 解析了 `CVCLDNoteEVECPage::CreatePanes`（`0x10168C70`）界面构建逻辑：三大属性槽对应 `Slot 0 (CVV)`、`Slot 1 (VSil)`、`Slot 2 (CTop)`，分别对应 Voice Color、Voice Release 和 Consonant Extension/Attack，单槽大小为 0x20（32 字节）。
  - 提取了官方时间切分规范：`Common` 规则为 `divide: [45.0, 45.0]`, `limit: [30.0, 60.0]`, `min-v: 45.0` ms；`VSil` 吐气规则为 `divide: [60.0, 60.0]`, `limit: [50.0, 70.0]`, `min-v: 45.0` ms。
  - 揭示了 Piapro Studio 在 VSQX 中的绑键切分与反向重组机制：使用 `$pps(=)` 标记 Voice Color 延音段，使用 `$pps(/)` 标记 Voice Release 吐气释放段；反编译字符串明确记录 `"reassemble note from vsqx : invalid EVEC structue - found unorderd evec note: "`。
- 确立了在 `VOCALOIDPatcher` 中原生实现 EVEC 的技术闭环：
  - Yamaha DSE 引擎天然能够直接根据 `PHDC` 匹配并渲染 `[a#6]`、`[k#6]` 和 `[*#1]`，无须任何外部 DSP 插件。
  - Patcher 拦截 `WIVSMNote.SetPhonemes` / `G2PAMultiLingualManager`，根据音符 EVEC 属性将音素重组为带后缀序列，并设置 `isValid = true` 绕过编辑器校验。
  - 形成专门技术文档 `29_evec_complete_reverse_engineering_and_implementation_plan.md`。

## 2026-09-04：EVEC 宿主反例与 V6 适配重新开题

- VOCALOID 6 宿主实测已推翻“PHDC 中存在后缀即可直接等价渲染”的完成结论：启用
  Consonant Extension 后出现整音符无声；Color、Release、Extension 的组合还曾因共享
  事务和时值失败形成互锁，无法独立清除。
- 已确认 `WIVSMNote.SetPhonemes` 的 `isValidPhonemes` 参数是写入音符的有效状态，不是
  “是否跳过 G2PA 字典校验”的开关。V6 对已确认音素的直接写入路径使用 `true`；调用
  G2PA 与直接设置 VSM 音素必须分开讨论。
- 已确认 Piapro UI 的槽位顺序是 `CVV / VSil / CTop`，但这不证明 V6 能把三个槽简单
  合并为一个带后缀的音素串。尤其官方 `RINLEN_V4X_EVEC.txt` 的 CTop 是 id 301 且没有
  `phn-suffix`，映射为 `[306,301]`、`[302,-1]`；把 Miku 的 `#2/#6` 规则机械应用到
  Rin/Len 没有配置依据。
- 新建 `31_evec_v6_adaptation_progress.md`，以后把每条 PPS、DDI/DDB、VSM 和宿主证据按
  “已证实/推测/待宿主验证”登记。旧文档 29 的“100%”“完全一致”结论已加状态警告。

## 2026-09-04：Miku/Rin/Len CTop 实体 ART 路径纠错

- 用固定版本公开 DDI 解析器完整统计本机实体声库：Miku Original EVEC 为
  133 PHDC / 36 STA / 1,611 ART，Rin Power EVEC 为 159 / 36 / 1,344，Len Power
  EVEC 为 159 / 48 / 1,339。Miku 含 284 条三音素 ART，Rin/Len 各含 142 条。
- Miku 的 CTop 三音素路径为 `C ^C#2 V`（Mild）和 `C ^C#6 V`（Accent），各 142 条；
  Rin/Len 的路径为 142 条 `C ^C V`，并不存在 `C#2`/`C#6` PHDC。这与官方 Miku
  302/306 后缀配置及 Rin/Len 301 无后缀配置相互印证。
- 旧重组器把 `C V` 直接替换成 `C#2 V`/`C#6 V`，但 Miku 实体图没有对应的
  `C#2 -> V`/`C#6 -> V` 后续边。这条 ART 断路已经足以解释宿主“一开辅音延长整音符
  无声”，不能再归因于用户选择的延长时间。
- Rin/Len PHDC 虽含九组彩色元音/鼻音，官方产品配置只暴露 Soft `#2` 与 Power `#6`；
  因此废弃“只看 PHDC token 就展示能力”的判据，转向官方产品配置 + 完整 ART 路径判定。

## 2026-09-04：CTop 外部表达纠正与独立发音延长计数

- 实体图先证明 DDI 内部 CTop ART 键为 Miku `C ^C#2/6 V`、Rin/Len `C ^C V`；进一步
  反编译 `FUN_10222cc0` 与 `FUN_10166b80` 后确认 caret 不能直接照抄到编辑器音素。
  PPS 的官方外部表达是 Miku `C C#2/6 V`、Rin/Len `C C V`，由 DSE 映射到内部 caret
  ART。VSM 离线接受 caret 只说明数据层不做强校验，不是可渲染证据。
- PPS 另有 `TopConsonantRepeatCountChangeCommand`。`FUN_101654b0` 搬运 note `+0xF8`
  字段，`FUN_102255b0` 将内部值限制为 0–3，`FUN_10222cc0` 按该值重复首音素再拼接
  CTop suffix 和余下音素。由此确认 CTop 录音性格与发音延长次数是两个正交参数：计数 N
  额外重复 N 份普通首辅音，启用 CTop 再增加一份带 suffix 的末副本。时值和 V6 UI 接入
  已按该模型实现。
- 新状态把 CTop 强度与 0–3 延长分成独立控件、独立更新和 sidecar 字段。Miku、Rin/Len、
  鼻音、四维组合、切换、清除与旧串迁移的逻辑矩阵全部通过；V6 数据层可原样保存官方
  外部串并保持 valid。完整 Debug 解决方案 0 警告；四份翻译 XML 的 1,280 个键完全一致。
  Release 合并 DLL 为 7,140,352 bytes，SHA-256
  `CF08C8E3215C3482763AA33165721335EA87130A965E3A1E8D57CE52E01BE2AC`；在确认 Editor
  关闭后部署，托管/原生 DLL 哈希均一致。Renderer/宿主可听输出等待用户复测。

## 2026-09-04：EVEC 实体声库离线 DSE 渲染被授权状态阻断

- 新增 `voicebank/tools/evec_render_harness`，不启动 Editor，直接按 6.13 官方调用链初始化
  VDM/DSE/VSM，创建 tempo/time signature/track/part/note，重置 HMM weight 与 vibrato，提交
  后调用 `WIVSMMidiPart.Render`，并解析 WAV PCM peak/RMS。
- Miku Original EVEC、Rin Power EVEC、Len Power EVEC 的 CompID 均能绑定；普通 `k a` 与
  官方外部 EVEC 串都能插入、提交，Render 返回 NoError 并生成 44.1 kHz 16-bit WAV，但所有
  PCM 完全为零，普通音和 EVEC 音结果相同。
- 交叉读取 DSE license：三库依次为 `InvalidKey`、`InvalidTrialKey`、`InvalidTrialKey`；本机
  35 个传统 DSE 声库的注册授权统计为 7 Expired、7 InvalidTrialKey、21 InvalidKey，没有
  `NoError` 阳性对照。唯一 NoError voice license 属于 DNN，不适用于传统 DSE renderer。
- 结论：离线链已到真实 WAV，但授权先于 EVEC 声学选择阻断；全零结果不能用来否定或确认
  重组串。后续只在合法授权的传统 DSE 环境做自动 A/B，或由用户在宿主已授权环境完成矩阵；
  不尝试授权绕过。离线 harness Release 与完整 Debug 解决方案均为 0 警告、0 错误，EVEC
  生产重组逻辑矩阵复跑全部通过。

## 2026-09-04：EVEC divide/min-v 汇编闭合与短音符降级

- PPS `FUN_10219630` 的 SSE2 汇编确认：divide 候选按
  `d0 + (d1-d0)/(limit1-limit0)*duration` 计算，裁到 `d1`；低于 `d0` 返回 0。若候选会让
  剩余元音短于 `min-v`，只在 `duration-min-v >= d0` 时缩短，否则同样返回 0。
- 官方 Common `[45,45]` 与 VSil `[60,60]` 因端点相等，分别恒为 45/60 ms；`limit`
  不是将结果按音符长度压到 30–60/50–70 ms 的可选范围。含 45 ms `min-v` 后，两个拆分
  的精确最低总时长分别为 90/105 ms。
- 新增纯逻辑 `EvecTimingMath` 并用于生产时值分配。短于门槛或 VSM 可接受范围不包含精确
  目标时，不再夹取到任意合法边界；保留已选 EVEC 录音和状态，仅沿用 VSM 原生边界。
  这让 Short/Long 与音符长度解耦，也避免时值失败参与 Color/Release/CTop/延长互锁。
- 逻辑 harness 新增 89.999/90 ms Common、104.999/105 ms VSil 和长音固定值回归，全部
  通过；主项目 Debug 构建 0 警告、0 错误。
- `CVCLDNoteEVECPage::CreatePanes` 没有创建 `SliderAccent`；标准 Accent 的 slider/command
  与 `EVECIDChangeCommand`、`EVECDataChangeCommand`、`TopConsonantRepeatCountChangeCommand`
  分离。当前实现继续不改 Note Accent、Velocity、Decay，CTop 的“强弱”由录音 ID 决定。
- 完整 Debug 解决方案和 Release 主项目均以 0 警告、0 错误构建。ILRepack 输出为
  7,140,864 bytes，SHA-256 `5BAFEACDBB18AD9D7A719B151D5F97CCEB72BF0D278168D16BCCD01DA2A7315D`；
  在 Editor 未运行时确认安装端符号链接解析到同一文件/哈希，native clock 两端哈希也一致。

## 2026-09-04：EVEC 全有向切换、sidecar 与 ConsonantOffset 探针

- 将产品支持集展开为全有向状态矩阵：Miku 108 状态/11,664 transition、Rin/Len 72 状态/
  5,184 transition、Luka 30 状态/900 transition。每条都验证源串→目标串、StripEvec 基础串
  不变及解析存在性；Miku/Luka 要求精确状态回读，Rin/Len 同形状态按 sidecar 契约验证。
  17,748 条全部通过，没有字符串层单向死角。
- 用临时 ZIP 对 `EvecProjectArchive` 做写入、读取、保留非 EVEC entry 和清空 EVEC entry
  回归；Rin/Len 301 + extension 2 + Power + Long Release 的定位字段和四维状态完全回读。
- 生产时值变更比较改用与物理音素严格匹配的逻辑 cache/sidecar 状态，避免 Rin/Len
  `C C V` 被重新解析为另一个同形组合后误触发无关维度。SetPhonemes 失败且发生部分写入时，
  现在尝试恢复原音素、valid 标志、语言、音素边界和 protect 状态，缩小批量部分提交风险。
- 新增 `--consonant-offset-probe`：VSM `ConsonantOffset` 初值 0，负值被拒绝且不 staged；
  0/1/10/100/1000 可写入。history commit 100 后 Undo 回 0、Redo 回 100，证明字段原生可撤销。
  由于未授权传统 DSE 环境仍不给音素位置/非零音频，尚不能确定单位与声学作用，未接入 EVEC。
- 完整 Debug、两个 harness Release 和主项目 Release 均以 0 警告、0 错误构建；ILRepack
  产物为 7,140,864 bytes，SHA-256
  `135A45E1C19265AD11FA02EE114FB1770D5A118C87986309579E5749E46FDF27`。确认 Editor 未运行，
  安装端符号链接读取到相同哈希，native clock 两端仍一致。

## 2026-09-04：VSM ConsonantOffset setter 与引用范围

- render harness 从真实 `WIVSMNote` vtable 读出：`+0x2A8` getter 指向 VSM RVA `0xED980`，
  `+0x2B0` setter 指向 RVA `0xED990`；导出包装分别位于 RVA `0x1D0B0`、`0x1D100`。
- getter 仅返回 note 对象 `+0x118` 的 `int`。setter 调用 RVA `0xEDFA0`：值小于 0 返回错误
  2；允许历史时把旧 `+0x118` 写入 `UpdateConsonantOffsetCommand`；随后写入新值并排入
  `DidUpdateConsonantOffset` 通知。setter/helper 内没有音素位置重算或渲染转换。
- `UpdateConsonantOffsetCommand` 的执行路径会保存当前 `+0x118`、写回命令值、发同类通知，
  与 probe 的 Undo/Redo 结果一致；note 复制构造也原样复制该字段。
- 6.13 托管 `MusicalEditorViewModel` 对 `DidUpdateConsonantOffset` 直接 `return`；全量托管源码
  除属性包装和该通知 case 外没有消费者。对 VSM 汇编的直接 `dword [note+0x118]` 引用筛查
  暂只定位到 getter、复制构造、setter/撤销命令；其它同偏移命中属于不同对象。
- 继续从 `VIS_VSM_WIVSMMidiPart_render`（RVA `0x1FD90`）向下闭合到 `0x68C20`、`0x59B40`、
  score builder `0x5A620`，最终到单音符 score event builder `0x575D0`。该函数解析 note
  `+0xA0` 的音素字符串，并消费时值、音高、Velocity/Expression、Vibrato 等实际合成输入；
  函数内没有读取 note `+0x118`，也没有调用 `ConsonantOffset` 的 vtable getter `+0x2A8`。
- 结合托管零消费者与 VSM 直接引用筛查，现可把 `ConsonantOffset` 判定为当前传统 DSE 同步
  渲染路径中的非声学载体，而不是继续等待它解决 EVEC 发音延长。只有另一条实际启用的渲染
  模式出现相反读取证据时才重开该结论；保存持久化语义本身仍未单独验证。
- render harness 增加了 `HoldingScoreList`/`RenderingScoreList` 摘要。当前机器全部传统 DSE
  license 无效或过期时，所有用例虽返回 `render_result=NoError`，但 `score_frames=0`；探针可
  在合法授权环境继续使用，本机结果不能作为音频或 score 阳性对照。

## 2026-09-04：PPS 重复计数/CTop 合成闭环与 Rin/Len 同形互锁修正

- `FUN_102216e0` 的唯一 `FUN_10222cc0` 调用点位于 `0x102227AA`。调用对象是宿主 note
  对象 `+0xE4`；因此 `FUN_10222cc0` 在 `0x10222E37` 读取的 `this+0x14`，等价于 note
  `+0xF8` 的 `TopConsonantRepeatCount`。无 CTop 时只有该值大于 0 才进入重组。
- 重组器先保留首音素，再循环 repeat count 次追加“空格 + 首音素”。当目标 voice mode
  有 Slot 2 CTop 且第三个 0x20-byte 槽的 `+0x04` 指针非空时，`0x10222E9E` 还会把循环数
  加一；CTop 指针为空则清零该附加量。`FUN_10166b80` 从 CTop 对象 `+0x14` 复制
  `phn-suffix`，在重复段后、其余音素前直接追加。
- 因而 PPS 的精确外部公式是：`base C + repeat×C + (CTop ? C+suffix : empty) + rest`。
  Miku suffix 为 `#2/#6`，可由字符串区分；Rin/Len 301 suffix 为空，导致 301+repeat0 与
  Normal+repeat1 都是 `C C V`。两个源字段仍独立，但 V6 音素串会丢失这部分身份信息。
- 旧 `Realizations[phonemeString] = state` 只能为同形串保留最后一个逻辑状态；撤销、回写或
  再次出现该字符串时会复活错误组合，是宿主“互相互锁、谁都解不开”的确定性风险。现已
  删除该历史映射，只保留当前物理串精确匹配的 live state/sidecar。元数据缺失时，Rin/Len
  第一份普通重复优先恢复为 301，剩余重复恢复为 0–3 延长；五份 C 的上限也不会被 clamp
  丢掉 301 身份。
- 若同形切换移除旧 CTop，生产时值层会重置它拥有的元音起点边界，再应用新状态；避免
  `C C V` 未改写时 45 ms CTop 编辑边界继续残留。纯逻辑新增 301+延长 0/1/3 回退测试，
- 对 EVEC 自己提交的原生事务新增 before/after 逻辑历史。Undo/Redo postfix 只在当前实际
  音素（同形时再核对边界/保护位）命中预期目标后移动历史并恢复 cache，普通原生编辑不会
  被拦截。由此可在 `Normal+延长1` 与 `301+延长0` 的往返撤销中保留原字段身份。
- 17,748 条跨产品全有向切换和 sidecar ZIP round-trip 全部通过；完整 Debug 解决方案为
  0 警告、0 错误。主项目 Release 与 render harness 同样通过；ILRepack 产物为 7,151,616
  bytes，SHA-256 `219A20433F3300556DFB554C9A07B5E9EA3BC5839B86282FD35A27F42DB6C67B`。
  Editor 未运行时安装端符号链接读取到同一哈希，宿主可听/交互结果待复测。

## 2026-09-04：Rin/Len 辅音自连接缺口与安全档位

- 在现有 `evec_render_harness` 增加 `--voicebank-paths` 只读诊断，直接通过 VDM
  `VoiceBank.Path` 取得 Miku Original、Rin Power、Len Power EVEC 实体数据库位置；不启动
  Editor、不渲染、不修改注册表或声库。
- 使用固定外部 `ddb-tools` 解析三份 DDI 的完整 ART inventory。按所有内部 CTop 三音素
  `C ^C[#suffix] V` 聚合得到 35 个源辅音 token；Miku 35/35 都有普通 `C C` self-edge，
  Rin 与 Len 均为 32/35，确定缺失项是 `Z Z`、`h\\ h\\`、`z z`。早先“29 个、仅缺
  `h\\`/`z`”是按部分覆盖口径形成的不完整统计，已在主进度文档纠正。
- 缺 self-edge 不会否定直接三音素 CTop：`C ^C V` 仍存在，故 Rin/Len Accent 301 必须
  保留。真正不可达的是再增加普通副本的 `C C C V` 及更长序列。能力归一化现规定这三类
  辅音无 CTop 时最多 extension=1，有 301 时 extension=0；选择 Accent 会自动把旧 extension
  收到安全范围，切回 Normal 后重新开放，Inspector 也只列当前多选共同可用档位。
- 继续按生产 UI 暴露集核对组合必要边：Miku/Rin/Len 的 12 个可着色元音/鼻音全部有
  `V -> V#2/#6`；普通、`#2`、`#6` 结尾全部有到 `*#1/#2` 的释放边；每条 CTop
  三音素也都有普通 `C -> V` 基础边。当前实体图没有发现需要联动禁用 Color/Release 的
  其它断路，安全限制保持精确到 Rin/Len 的三条辅音 self-edge。
- 纯重组/切换/sidecar harness 全部通过，并新增 `h\\`、大写 `Z` 的前核辅音识别断言；
  完整 Debug 解决方案、render harness Release 与主项目 Release 均为 0 警告、0 错误。
  ILRepack 合并 DLL 为 7,152,640 bytes，SHA-256
  `F7E0D87A7CEE3DBAAFF7E2F42BEC7EC33D3B39A71757255F86EB48F14DA09D18`。最终程序集保留
  `HarmonyLib.Harmony` 且无独立合并依赖引用；Editor 未运行，安装目录符号链接读取到同一
  哈希，native clock 源/安装哈希均为
  `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。
- 这一步消除了已由实体图证实的不可达组合；`C C V` 在 V6 宿主中的实际听感以及三类音的
  Accent/Normal 可听差异仍需合法授权宿主复测，不能仅凭图结构标记完成。

## 2026-09-04：Piapro 延长四段控件与 V6 多选手感

- 继续反编译 `CVCLDNoteEVECPage::CreatePanes`（`0x10168C70`）：其中通过
  `0x101679F0` 构造 `EVECConsonantExtension`，严格循环四次创建索引 0、1、2、3 的
  `ConsonantRepTimeBtn`（构造函数 `0x10167830`）。索引 0 取资源 ID `0x1DA0` 的本地化
  字符串，索引 1–3 使用 `x %d`。这证明官方 UI 是固定四段互斥选择，不是会缩短的下拉框。
- 数据控件提交函数 `0x10167E20` 把所选按钮索引写入 NoteParam `+0xB8` 的数值字段；既有
  `0x101654B0` 已证明该字段与 note `+0xF8` 的 TopConsonantRepeatCount 往返。该路径没有
  连续毫秒/力度 slider；标准 `SliderAccent` 仍属于普通音符参数页，不能并入 EVEC。
- V6 Inspector 将延长下拉替换为常驻关闭/×1/×2/×3 ToggleButton。能力不足的档位保留位置
  并灰显，关闭档可随时清除；多选状态不一致时 Color/CTop/Release 不再显示第一颗音符的
  值，延长四段也不误勾。右键菜单同样按所有选中音符的共同上限禁用档位并正确显示混合态。
- 完整 Debug 解决方案和主项目 Release 均为 0 警告、0 错误。ILRepack 产物为 7,155,200
  bytes，SHA-256 `C2E8AF0776B3C5E192685F2E85EAC63D6AD74AE2D6C34309D47D2B1CBDB72C82`；
  合并程序集包含 `HarmonyLib.Harmony` 与 `EvecInspectorView`。Editor 未运行，安装目录符号
  链接读取到同一哈希，等待宿主交互与可听复测。

## 2026-09-04：Miku EVEC 变体实体图与 Beta 名称反例

- `evec_render_harness --voicebank-paths` 改为通过 VDM 枚举全部 `_EVEC` 组件，而不是只打印
  渲染用例中的三个 CompID。本机另有 Miku Soft、Solid，以及注册名为
  `MIKU_V4X_Beta_EVEC`、实际文件为 `MIKU_V4_Chinese.ddi` 的组件。
- 固定解析器全图结果：Original/Soft/Solid 均为 133 PHDC、36 STA，ART 分别为 1,611、
  1,610、1,607；三者都有 284 条 `C ^C#2/6 V`、35 个 CTop 源辅音的完整 self-edge、
  `#2/#6` Color 和 `*#1/*#2` Release。现有统一 Miku V4X EVEC profile 有实体依据。
- 中文 Beta 名称组件只有 62 PHDC、38 STA、2,577 ART，CTop 三音素为 0、普通到彩色元音
  的边为 0、`*#1/*#2` token 为 0。仅凭 `VoiceBank.Name` 命中 Beta profile 是明确误判。
  探测器现优先解析实体 DDI 文件名；当前组件得到 `MIKU_V4_Chinese` 并返回 None，而真实
  文件名为 `MIKU_V4X_Beta_EVEC` 的安装仍可采用官方只含 Color 的 Beta profile。
- 同一修正为 `.ddb` 路径查找同目录 `.ddi`，并暴露了另一条 VDM 元数据错误：实际
  `LEN_V4X_Serious.ddi` 被报告为 `RIN_V4X_Serious`。文件名优先后可正确命中 release-only
  profile。Rin Warm/Sweet、Len Serious/Cold 四库分别为 51 PHDC、520–525 ART，全部含
  `*#1/*#2` 且各有 6 条入口边，官方 Voice Release 能力获得实体图确认。
- 完整 Debug 与主项目 Release 均为 0 警告、0 错误。最新 ILRepack DLL 为 7,155,200 bytes，
  SHA-256 `D2E6EEDE2B92F0C8B341FAA02D2C73013F324663DEC1D71E7CF1B5610D1AA1B3`；Editor 未运行，
  安装端符号链接读取到同一哈希。
- 为避免脚本模型与生产判定漂移，render harness 现直接链接生产
  `EvecVoicebankDetector`、`EvecPhonemeRecomposer`、状态和常量源码，用真实 VDM VoiceBank
  输出能力。实测中文 Beta 为 unsupported；Miku 三库、Rin/Len Power 和四个 release-only
  库的 ID 列表均与官方配置/实体图一致。限值探针也确认 Miku `k/h\\/z` 最大 3，双子 Power
  的 `k` 最大 3，`h\\/z` 在 Normal/Accent 下最大 1/0。由此生产路径、不是旁路复刻，已经
  覆盖 `.ddb→.ddi`、错误 VDM 名称、Beta 反例与自连接限制。

## 2026-09-04：第二次宿主互锁反馈后的交互优先级与状态快照修正

- 现行 UI 用当前逻辑 state 计算延长上限；对 Rin/Len 缺 `C C` self-edge 的 `Z/h\\/z`，301
  会把最大延长降成 0，因此虽然“关闭”与 CTop=Normal 理论上可退出，实际手感呈现为两个
  控件互相制约。PPS 的四段控件证据并不支持这种动态锁法；图约束应在提交层消解。
- 生产能力层新增最后操作优先：延长按钮的可选范围只由基础辅音图决定，不由当前 CTop
  决定；用户选择延长 1 时若与 301 冲突，则清除 301。反向选择 301 时继续沿用归一化把
  延长降为 0。这样两个方向都有确定出口，同时不放行实体图不可达的延长 2/3。
- 审计 `EvecService.UpdateNotes` 又发现 updater 会原地修改传入的 `EvecNoteState`，而同一
  引用随后被用于 `CaptureHistorySnapshot`。这使 before/after 在逻辑历史中可能同值，掩盖
  实际字段切换。现改为 `beforeState.Clone()` 后再交给 updater，历史与诊断保留真实 before。
- `EvecCachedState` 增加 `VoiceBankID`；只有句柄代次、实际音素文本和声库身份三者都一致
  才能复用 live state。普通 Editor 换声库还会经过 `ResetPhonemes`，本修正额外封死同形串
  跨声库复活旧 CTop/Color 的缓存路径。
- 增加 512 KiB 自动轮换的 `evec-diagnostic.log`。日志只包含 before/requested/applied 的
  `Color/Attack/Release/Extension` ID、成功位与修改前后 token 数，用于宿主内精确区分 UI
  请求、能力归一化与 VSM 写入失败；不记录歌词、音素正文、工程路径或声库路径。
- 当前完整 Debug、逻辑 harness、render harness 构建均为 0 警告、0 错误；17,748 条全有向
  字符串切换继续通过。真实 VDM 探针中 Miku 三库和 Rin/Len Power 的新交互策略断言全部
  通过。
- render harness 新增 `--mutation-probe`，在真实 Miku Original 与 Rin Power Part 上按生产
  顺序执行解锁、`SetPhonemes(..., true, LangID)`、恢复保护与原生 Transaction。Miku 的
  `k a → k k#2 a → k k#6 a → k a`、CTop+延长、四维组合、全部清除，以及 Rin 的
  `Normal → 301 → Normal → 延长1 → 301 → Normal` 每步都满足实际串、保护位和提交结果。
  由此再次排除 VSM 数据层拒绝 Mild/Accent 切换；剩余宿主故障若出现可直接由新诊断日志
  区分 UI 请求、能力归一化或生产写入。
- 主项目 Release 0 警告、0 错误；ILRepack DLL 为 7,158,272 bytes，SHA-256
  `C49E2DF97FEC930C66CD40AA6BE9917551A020B917FFC73BC1F97D8AA8081ACE`。ilspy/反射确认包含
  `HarmonyLib.Harmony`、`EvecDiagnosticLog`，且程序集引用没有 `0Harmony`。Editor 主进程
  未运行，安装目录符号链接哈希相同；native clock 源/安装哈希均为
  `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。

## 2026-09-04：EVEC 保护位与歌词 G2PA 重算的实测闭环

- 6.13 自己的手工音素编辑器会在写入前把 `IsProtected` 解除；现有 EVEC 歌词 patch 却只在
  prefix 保存 state，未解锁便调用 G2PA，再在 postfix 重贴 EVEC。由于 EVEC 借保护位保存
  特殊音素，这可能把“保护手工音素”的原生语义错误带进正常歌词编辑。
- render harness 新增 `--lyrics-probe`，并加载真实 `G2PAManager.dll`/JPN manager。在 Miku
  Original Part 上以固定“か”与 `k k#2 a` 建立受保护 EVEC 音符；保持保护时调用原生
  `SetLyrics("き")` 返回 true/length 1，但实际音素仍是旧 `k k#2 a`。这直接证明返回成功
  不等于 G2PA 已重算受保护音素。
- 同一音符解除保护后再次调用，G2PA 生成新基础音素 `k' i`；生产重组器应用 Mild 后写回
  `k' k'#2 i`，`isValid=true`、重新保护和最终字符串核对全部成功。该阳性对照同时证明
  EVEC 重组器能处理带撇号的日语辅音，而不是只适用于测试用 `k a`。
- 生产 `EvecSetLyricsPatch` 现仅在受保护且实际有 EVEC 时临时解锁；原调用成功后用
  `commit:false` 在编辑器已有歌词事务内重贴原四维状态，原调用失败或抛异常则恢复旧保护
  位。重贴失败时不强锁普通新音素，避免再次制造不可编辑状态。
- `SetNoteEvec` 的直接路径新增与批量更新相同的 before/requested/applied/success/token-count
  诊断，因此歌词重贴、复制和 sidecar 恢复不再是日志盲区。完整 Debug 与 render harness
  Release 构建均为 0 警告、0 错误，native lyrics probe 为 `valid=True`。确认 Editor 关闭后
  主 Release 也以 0 警告、0 错误完成；ILRepack DLL 为 7,159,808 bytes，SHA-256
  `5542714E88861981ED188CC622548E1A401BD9E2672B9425469AF4619CDAC530`。程序集含
  `EvecLyricsEditState`/`EvecDiagnosticLog`/`HarmonyLib.Harmony` 且无外部 `0Harmony` 引用；
  安装端符号链接哈希一致。

## 2026-09-04：EVEC 音符属性粘贴的同形状态缺口

- 6.13 的 `WIVSMClipboard.ProcessNoteProperty` 映射已核实：剪贴板只有一颗音符时，把同一
  `source` 应用到所有 `target`；多颗时按 `GetNotes.Zip(targets)` 一一配对。`Pair<T>` 构造
  顺序明确为 `(target, source)`。
- `LyricsAndPhonemes` 的实际复制不调用 `DuplicateNote` 或 `PushNote`，而是直接解除目标
  保护、复制歌词并调用 `SetPhonemes`，最后把来源保护位写给目标。因此现有两条 clone patch
  不能恢复双子 `C C V` 的来源逻辑态。
- 修复必须在公开的 `CopyNotePropertyTo` 批次边界捕获全部 before，再在原生复制成功后于
  调用者已有 `Transaction` 内统一重贴来源 state，最后只记录一条批量逻辑历史。若在私有
  `CopyNoteProperty` 的逐对 postfix 各记一次历史，多选粘贴的一次原生 Undo 只会消费一项，
  后续 Undo 会与剩余逻辑项错位，故不能采用该实现。
- `MusicalEditor.DivideNoteAt` 在原生 `DivideNote` 后会把新右音符重设为连音符 `-`；
  `SplitNote` 还会根据 Melisma/指定音素/元音策略继续改写两侧音素。是否保留 EVEC 不能只
  在底层 `DivideNote` 无条件 clone，需先按最终音素和编辑器意图分路径验证。`JoinNotes`
  仅返回 `bool`，结果对象也需用实际 note 列表/句柄变化判定，暂不做猜测性补丁。

### native probe 与生产修正结果

- `--clipboard-probe` 纠正了一条中途假设：clipboard note 的 `Parent` 并非 null，而是与来源
  MidiPart 相等，VoiceBankID 也保留为 Rin Power；`PushNote` 返回包装器与 `GetNotes` 枚举
  包装器使用同一 native handle。现有 `EvecClipboardNotePatch.CloneState` 因此能在入剪贴板
  时保留 sidecar。
- 随后让原生 `CopyNotePropertyTo(LyricsAndPhonemes)` 把 `k k a` 写入目标：音素和保护位都
  复制成功，但对结果单独运行生产解析与 plain-attack 消歧，稳定得到 `301/0`。这就是来源
  若实际为 `Normal/延长1` 时的可执行反例，probe 输出
  `naive_state=301/0, ambiguity_confirmed=True, valid=True`。
- 生产新增批次 patch：prefix 把 targets 物化一次并按原生规则建立 source→target 计划；
  postfix 在外层事务尚未提交时重贴来源 state。缓存只在整批成功后发布；多目标历史合并为
  一条；失败使原生返回 false，从而由 `MusicalEditorViewModel.PasteNoteProperty` 的现有
  Transaction 回滚整批。来源无 EVEC 时不调用 `TryApplyToNote`，以免把用户自己保护的手工
  音素误解锁，只移除目标旧 EVEC cache。
- `--structure-probe` 实测底层 `DivideNote`：左句柄不变，右侧为新句柄，二者都保留
  `k k a` 和保护位；故双子延长1若不 clone，右侧会退化解析为 301。新增 DivideNote postfix
  clone。按正常左到右列表执行 `JoinNotes` 后只剩原左句柄，音素/保护位/总时长保持，左侧
  live state 自然继续有效，无需 join patch。`DivideNoteAt`/高级 Split 后续若把右侧重设为
  `-` 或元音，cache 的 exact-phoneme guard 会使 clone 自动失效。
- Debug 全解决方案与 render harness Release 均 0 警告、0 错误；两项 probe 均
  `valid=True`。随后完整重跑逻辑、真实 VDM、mutation 与 lyrics probes，结果也全部通过。
- 确认 Editor 关闭后完成主 Release；ILRepack DLL 为 7,166,464 bytes，SHA-256
  `2DE8AE1A46432F96BB6C47117C97773CA2203B01E742EB922DEF44864DE602DD`。安装端符号链接哈希
  一致，最终程序集可见 `EvecClipboardPropertyPatch`、`EvecDivideNotePatch`、
  `ClipboardPropertyTransfer`、`EvecDiagnosticLog` 和 `HarmonyLib.Harmony`；native clock
  源/安装哈希仍为 `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。

## 2026-09-04：音符几何编辑后的 EVEC 毫秒边界重锚

- 当前 Release 只在 EVEC 选项本身变化时调用 `ApplyTiming`。6.13 右边缘缩放明确调用
  `WIVSMNote.SetDuration(VSMRelTick)`，左边缘缩放调用 `WIVSMNote.ResizeLeft(VSMRelTick)`；
  两条路径都不经过 `SetNoteEvec`。因此原本距 note end 60 ms 的 release start 会在后续
  拉长音符后保持旧相对 Tick，新增时长全部落到 release 区间，形成“尾音长度随音符长度
  变化”的直接机制，而不是 Short/Long 录音 ID 自动切换。
- 新增 `ReapplyTimingAfterGeometryChange`，只读取并归一化当前 EVEC state，在调用者已有
  VSM transaction 内重新执行时值分配；不重组音素、不新建事务、不改 Note Accent、Decay、
  Velocity 或任何表达字段。`SetDuration`/`ResizeLeft` 成功 postfix 均调用该入口。
- 原生 `DivideNote` 会在 native 内部同时改变左音符时长并新建右音符，无法由 SetDuration
  patch 捕获；现有 Divide postfix 在 clone 后对两侧都重锚。`JoinNotes` 同样在 native 内部
  拉长 survivor；新增 Join postfix 用 `HasNote` 识别 survivor 并重锚，同时清掉已消失句柄
  的 EVEC cache，避免后续句柄复用。
- 纯时值测试补充 240 ms、1000 ms 两个长音符，VSil divide 均保持 60 ms；既有 105 ms
  成功与 104.999 ms 返回 0 的临界不变。Debug 全解决方案与 17,748+900 切换/sidecar harness
  均 0 警告通过。本段记录时尚未重建/安装新的 Release。

### tempo 编辑的毫秒不变量

- 45/60 ms 经 `GetTickFromTime` 写入的是当前 tempo map 下的相对 Tick。若之后插入、删除、
  移动 tempo 点或修改其 Value，原 edited phoneme position 不会自动换算；即使音符长度不变，
  实际毫秒边界也会漂移。这与几何缩放是同一类派生数据生命周期问题。
- 6.13 tempo 编辑可能在一个 Transaction 中密集调用多次 `InsertTempo`，逐次全工程重锚会
  造成明显卡顿。因此各 tempo mutation postfix 只把 sequence handle 加入去重集合；
  `WIVSMSequence.Commit(bool)` prefix 在 native commit 前消费一次标记并重锚全部 EVEC。
  `WIVSMTempo.Value`、GlobalTempo、GlobalTempoEnabled、ARATempoEnabled 的 setter 也纳入。
- 重锚发生在原 tempo transaction 尚 staged 时，edited boundary 与 tempo 作为同一次历史
  提交；Rollback 清理 pending 标记，Commit 失败则重新标记。扫描先以 `IsProtected` 排除
  普通音符，再用 exact cache/实际音素判定 EVEC；每个 note 单独异常隔离。
- 当前只能由调用链、事务结构和纯 45/60 ms 数学回归证明实现契约；实体声库授权阻断仍使
  离线 note phoneme positions 为空，BPM 改动后的实际边界/听感须由宿主复测。
- 完整回归与 Release 构建均 0 警告、0 错误；最新 ILRepack DLL 为 7,173,632 bytes，
  SHA-256 `F4D370327DB7B9834BDCB5CDEAC82070EB1E4D50A4B95C37A03F6DF6D311B2AD`。Editor 未运行，
  安装符号链接哈希一致；最终程序集已核对包含 geometry/tempo/join/clipboard patches、
  `EvecDiagnosticLog` 与 `HarmonyLib.Harmony`。native clock 源/安装哈希仍一致为
  `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。

## 2026-09-04：用户换声库会跳过受保护 EVEC 音符

- 原生编辑器的单 Part 和多 Part 换声库最终都进入
  `WIVSMMidiPartExtension.SetVoiceBank(WIVSMMidiPart, VoiceBank)`；其顺序是先写新的
  VoiceBankID，再调用 `part.ResetPhonemes()`，且整个过程位于调用者已有 Transaction 内。
- `--voicebank-switch-probe` 在真实 VSM/JPN G2PA 上建立两个 Miku Original Part，均含
  `か / k k#2 a`。两者切为 Rin Power 后执行相同 part-wide Reset：
  - 受保护组：返回 true，但仍为 `k k#2 a` 且保护位为 true；
  - 解锁组：返回 true，正确重生为 `k a` 且保护位为 false。
- 这说明 G2PA part reset 明确把保护位当成“不改音素”，而 EVEC 又必须用同一保护位保住
  特殊 token；二者在换声库边界产生语义冲突。Miku 的 `#2/#6` 被原样带到 Rin/Len 或
  release-only 声库时，不属于目标 DDI 图，是比缓存误判更靠前的实体无声来源。
- 生产修复选择原生整次 SetVoiceBank 边界，而不是全局改 ResetPhonemes：普通移动、缩放、
  转换等 part-wide reset 仍需尊重 EVEC 保护，只有“目标声库实际改变”时才临时解锁实际
  EVEC 音符。成功后按原生换声库语义清空 EVEC，不猜测跨声库映射 Mild/Accent/Color；
  before/base 快照作为一条逻辑历史与 native voice-bank transaction 对齐，以保住 Undo 后
  Rin/Len `C C V` 的 301/延长1歧义。无 EVEC 的手工保护音素不会被触碰。
- 当前尚未把 `ReplaceVoiceHelper` 的“缺失声库自动替换”路径纳入同一补丁：该路径只在
  工程载入/导入时使用 raw SetVoiceBankID，随后由外层 ResetPhonemes。需继续结合 sidecar
  应用时序审计，避免为了修自动替换而在正常工程加载时误清 EVEC。
- render harness Release 和完整 Debug 解决方案均 0 警告、0 错误；新 probe 为
  `valid=True`。本阶段未生成/安装生产 Release。

## 2026-09-04：Part 属性粘贴绕过换声库入口，整 Part 复制丢失歧义 sidecar

- `WIVSMClipboard.CopyPartPropertyTo` 的原生映射与 Note 属性相同：剪贴板只有一个 Part 时
  单源应用到全部 targets；多个 Part 时 `GetParts.Zip(targets)`。其执行顺序固定为先 Note、
  后 VoiceBank；`CopyVoiceBank` 仅调用 raw `SetVoiceBankID`/`SetAiVoiceBankID`，不会进入
  `WIVSMMidiPartExtension.SetVoiceBank`，也不会 ResetPhonemes。
- native `--part-property-probe` 证实：Rin source 入剪贴板后仍保留声库、物理串和保护位；
  只贴 VoiceBank 到 Miku target 后，目标 ID 已为 Rin，但 `k k#2 a/true` 完全保留；贴
  `Note|VoiceBank` 后最终物理值为 Rin 的 `k k a/true`。前者直接留下跨库非法 suffix，
  后者物理可用却丢失 `301/0` 与 `Normal/延长1` 的来源语义。
- `PushMidiPart` 是 native 内部整 Part 克隆，不经过公开 `DuplicateNote`，所以原有
  `EvecClipboardNotePatch` 无法复制每音符 sidecar。DuplicatePart、DuplicateTrack、
  DuplicateSequence 也存在同型缺口；现全部按 native 顺序逐 Part/逐 Note clone state。
- `EvecClipboardPartPropertyPatch` 在公开批次边界物化 targets 并捕获来源状态、目标 before。
  VoiceBank-only 在原事务内将旧 EVEC 重组为空，避免跨声库 token；Note 分支在原生先复制
  Notes、再写最终 VoiceBank 后，才按来源状态和最终能力重贴。cache 只在整个批次成功后
  发布，失败通过 `__result=false` 触发调用者 Transaction 回滚。
- 原逻辑历史假设 before/after 使用同一 note handle，无法表达 `RemoveAllNotes + DuplicateNote`
  的整 Part 替换。现历史项存独立 before/after 快照集合；Undo 查 before handles，Redo 查
  after handles，再核对物理串/必要的时值和保护位后发布对应 cache。这样来源/目标音符数不等
  也能恢复，且未真正撤销到该 native 事务时不会误弹逻辑历史。
- 反射已核对 DuplicatePart、DuplicateTrack、DuplicateSequence、PushMidiPart、
  CopyPartPropertyTo 精确签名；完整 Debug 解决方案 0 警告、0 错误。当前仍待完整回归与
  Release 安装。

### 回归与部署结果

- 剪贴板 `ClearNote`/`ClearMidiPart` 现在会在 native 对象销毁前捕获 note handles，清理后
  释放对应 generation/cache，避免 clipboard handle 被复用且物理串恰好相同时复活旧歧义态。
- 完整 Debug、render harness Release、生产 Release 均 0 警告、0 错误。逻辑总计
  17,748+900 条切换与 sidecar archive 通过；7 个 native/VDM probes 均通过，其中
  Part property probe 的 Undo/Redo 分别恢复 old/new handle，验证集合式历史的方向匹配。
- Editor 已确认关闭。新合并 DLL 为 7,191,040 bytes，SHA-256
  `745B453503F229B3C152D08C80754BC8E3FF4A9489C0C50C1F0610DC95AD5F9E`；安装符号链接哈希
  相同。最终 metadata 中可见换声库、Part property、整 Part/Track/Sequence clone 及既有
  geometry/tempo 类型；`HarmonyLib.Harmony` 已合入且 assembly references 无 `0Harmony`。
  native clock 两端哈希继续为
  `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。

## 2026-09-04：跨 Part 分割点的音符不会被 native 拆成两颗

- 扩展 `--part-structure-probe`：Part 绝对位置 4800，音符绝对位置 6240、时长 1440，
  在绝对 6720（Part 相对 1920）分割，音符明确横跨分割点。
- 真实 VSM 结果：左 Part 仍有原 handle 音符，右 Part 为 0 颗；左音符仍为
  `@6240+1440:k k a:protected=True`。这说明 DividePart 不按边界裁切/复制跨界音符，EVEC
  PartStructureTransfer 的一对一稳定顺序映射在该边界情形下成立。
- 同轮生命周期审计补齐 EVEC 的 RemoveTrack/Sequence.Close。删除成功才释放捕获 handles；
  EVEC snapshots 以 before→empty 合入调用者 native Transaction，供 Undo/Redo 恢复双子
  `k k a` 的 301/延长1逻辑歧义；Close 成功按捕获的旧 sequence handle 清理 Histories、
  PendingHistoryTransitions、PendingTempoSequences 和所属自动换库上下文，不影响其它 sequence。
- 完整 Debug 和 render harness Release 均 0 警告、0 错误；扩展 native probe
  `crossing.valid=True`、整体 `valid=True`。尚待单独验证 RemovePart/RemoveTrack 的 native
  Undo 是否恢复同一 note handle，然后才生成下一版生产 Release。

### 删除/撤销实测与部署

- 新 `--removal-lifecycle-probe` 使用同一组两个原始 note handles。RemovePart→Undo 恢复的
  handles 与原数组逐项相同，Redo 后 Part 数为 0；再次 Undo 后执行 RemoveTrack→Undo，恢复
  轨道内 handles 同样逐项相同，Redo 后 sequence track 数为 0，整体 `valid=True`。
- 这使删除 transfer 可只按捕获 handle 记录 EVEC before→empty，并在成功 postfix 释放 cache；
  Undo 时 native 同 handle 对应历史 snapshot，能恢复 Rin/Len `k k a` 无法自解释的
  Normal+延长1，Redo 再清理。Remove 失败不会清状态，Close 失败也不会清 sequence 数据。
- 完整 logic harness 和 11 项 VDM/VSM probes 全部 exit code 0；Debug、render harness Release、
  生产 Release 均 0 警告、0 错误。Editor 本体关闭后生成 7,218,688-byte 合并 DLL，源码输出
  与安装符号链接 SHA-256 均为
  `5EF51D0D2C9036104C6979F27112ABFDF8619134641BAF6CA413D20FDFB34266`。最终程序集包含
  RemovePart/RemoveTrack/SequenceClose、Part structure、transition accumulator 和 Harmony；
  metadata 的 46 个程序集引用不含 `0Harmony`。native clock 两端继续一致为
  `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。
- Release 定稿前又封住一条失败分支：part-wide G2PA 成功会先触发 UI 刷新，之后
  ResetXSynthSecondaryVoiceBank 仍可能失败；若 UI 已清掉旧 cache，native rollback 后双子
  同形串会丢歧义。失败/异常路径现用原 VoiceBankID+before phonemes 预置 exact cache；实际
  回滚后恢复有效，不回滚则因 identity/text guard 自动失效。该改动已重新完成 Debug/Release
  0 警告构建，以上 SHA-256 为最终安装版本。

## 2026-09-04：缺失 Voice Bank 自动替换发生在 sidecar 应用之后

- `Sequence.LoadProjectSequenceFile` 只打开并返回 `WIVSMSequence`；当前 EVEC load postfix 在
  此处立即套用 sidecar。真正的 `Sequence.Load/InitializeForLoad` 随后才逐 Part 调用
  `Sequence.ComplementMidiPart`。
- `ComplementMidiPart` 的固定顺序为 `ReplaceVoiceHelper.ReplaceVoice`、补齐效果器参数、
  `ResetLyrics`、`ResetPhonemes`。Track import 的 `InitializeImportedPart` 使用同一顺序。
  `ReplaceVoice` 本身只 raw 写 VoiceBankID/AiVoiceBankID，不经过用户换声库入口。
- 因此旧库缺失时存在确定竞态：sidecar 先在 null VoiceBank 能力下被清空；自动替换后，旧
  EVEC note 仍为 protected，part-wide G2PA 返回成功却跳过它。旧 Miku `#2/#6` 可直接留在
  Rin/Len 或 release-only 声库下。
- 生产修复以同一 `WIVSMMidiPart` 包装对象在 `ReplaceVoice` 到 `ResetPhonemes` 之间作短期
  handoff。Replace prefix 捕获经物理串严格验证的逻辑态但不解锁，避免中间的 ResetLyrics
  改写受保护歌词；G2PA part prefix 消费 handoff 并解锁，postfix 再针对新 Voice Bank
  `Normalize` 后重贴兼容维度。用户显式换库继续清空旧 EVEC；自动缺失库替换才做兼容保留。
- Sidecar 暂存只接受 `Recompose(StripEvec(phonemes), requestedState) == phonemes`。这既保留
  Rin/Len `k k a` 对 CTop 301/延长1的两种合法 sidecar 解释，也拒绝与当前物理串不符的旧
  metadata。逻辑 harness 已加入四条正/反例，全部通过。
- ReplaceVoice/G2PA 失败分支优先保证可恢复：若 VoiceBankID 已变则移除旧 cache、解除保护，
  不把跨库 token 再锁住。Debug 全解决方案 0 警告、0 错误；全逻辑矩阵、archive、真实
  voice-bank switch/Part property probes 已通过。尚待完整 probe、Release 与宿主复测。

### 回归与部署结果

- 逻辑 harness：Miku 11,664、Rin/Len 5,184、Luka 900 条全有向切换，archive round-trip、
  45/60 ms 时值临界/长音符回归及新增 exact-sidecar 四例全部通过。
- render harness Release 0 警告、0 错误；`--voicebank-paths`、`--mutation-probe`、
  `--lyrics-probe`、`--clipboard-probe`、`--structure-probe`、`--voicebank-switch-probe`、
  `--part-property-probe` 七项均以 exit code 0 通过。实体授权仍报告 InvalidKey/InvalidTrialKey，
  因而这些结果证明对象/图/事务行为，不代替最终音频试听。
- 精确确认 Editor 本体未运行后构建生产 Release。ILRepack 输出 7,194,112 bytes，源输出与
  安装符号链接 SHA-256 均为
  `9EB8818C7F8086AE2459B0553433E3EFBAED5041A31CAF661485F1EA0005CF7E`。最终 type metadata
  包含自动替换、G2PA part reset、显式换库、Part property、EVEC service/diagnostic 与
  `HarmonyLib.Harmony`；assembly references 无 `0Harmony`。native clock 两端仍为
  `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。
- 当前机器安装了源/目标实体声库，无法让原生 `ReplaceVoiceHelper` 自然进入“旧库缺失”分支；
  所以该 Harmony 两阶段交接虽有反编译顺序、exact-sidecar 逻辑回归和真实 G2PA 解锁对照支撑，
  仍须用实际缺库 VPR/导入场景验证。`evec-diagnostic.log` 目前不存在，暂无宿主侧写入证据。

## 2026-09-04：歌词移动会复制受保护物理串，但不会复制 EVEC 歧义态

- `LyricMoveLeft`/`LyricMoveRight` 直接在现有 note handle 之间复制 Lyric、Phonemes 与
  IsProtected，最后调用范围 ResetPhonemes。它不经过 Clipboard、DuplicateNote 或现有 EVEC
  批量 setter。单选会把从选中点到行尾的歌词整体左/右移；多选只移动连续选区并清空一端。
- 对双子 Power 的 `Normal+延长1`，源物理串是 `k k a` 且 protected。native probe 按原生
  raw copy + part reset 执行后，SetPhonemes 与 Reset 均返回 true，目标仍为 `k k a/true`；
  若只从物理串恢复，确定性 fallback 得到 `Attack301+延长0`。同一串同时精确符合来源
  sidecar `Attack0+延长1`，因此这是不可从字符串恢复、必须随操作传递的逻辑数据。
- 新 `EvecLyricMovePlanner` 逐项复刻 6.13 的单选/多选、左右和首尾边界映射；逻辑 harness 六
  组映射均通过。高层 Prefix 建立 ThreadStatic 短生命周期目标映射，低层 raw SetPhonemes
  postfix 在原 Transaction 尚未结束时发布来源逻辑态和目标几何时值，高层 postfix 记录
  before/after 历史并清理上下文；异常路径按 Lyric+Phonemes exact guard 恢复旧 cache。
- History snapshot 增加 Lyric，防止物理串相同的歌词移动在 Undo/Redo 时被误识别成相邻 EVEC
  操作。全局 raw SetPhonemes 成功还会先使旧 cache 失效：生产 EVEC 写回会在返回后重新缓存，
  歌词移动由上下文重新缓存，其余手工/结构编辑则以新物理事实重新解析。
- 完整 Debug 0 警告、0 错误；逻辑矩阵、archive、6 组歌词移动规划及 8 个 native/VDM
  probes 全通过。新增 `--lyric-move-probe` 输出 `naive_changed_meaning=True`、
  `sidecar_keeps_meaning=True`、`valid=True`。尚待 Release 与宿主交互复测。

### 部署结果

- Editor 本体已确认关闭；Release 0 警告、0 错误。合并 DLL 为 7,202,816 bytes，源码输出和
  安装符号链接 SHA-256 均为
  `221F6CB47B08F87A2988888430778933C52A4874D8FDAADB9BCDDC40862177C6`。
- 最终程序集包含 raw phoneme、LyricMove 左/右、纯 planner、自动/显式换库补丁和
  `HarmonyLib.Harmony`，引用列表没有 `0Harmony`。native clock 两端哈希继续为
  `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。
- 下一次宿主矩阵新增：把物理同为 `k k a` 的 Accent301 与 Normal+延长1 相邻排列，分别做
  单选/多选歌词左移、右移及 Undo/Redo，确认 UI 逻辑选项随来源移动而非统一回退到 Accent。

## 2026-09-04：绝对位置变化也必须重算 EVEC 毫秒边界

- 现有 duration/left-resize/tempo-map 补丁仍漏掉“数据不变、音符绝对位置改变”。edited
  phoneme position 是相对 Tick，但 45/60 ms 换算依赖音符所在位置的 tempo。
- 真实 VSM 两段 tempo probe（120 BPM→60 BPM）测得 60 ms 分别需要 58/29 tick；若把旧
  58 tick 原样带到慢速区，实际为 120.833 ms，重算 29 tick 为 60.417 ms。该结果不依赖
  声库授权，直接证明位置生命周期缺口。
- `MoveNote` 是钢琴卷帘拖动、Quantize、InsertRest、Double/Half Tempo 等路径的共同底层；
  `WIVSMMidiTrack.MovePart` 是 Part 拖动、插入工程空间等共同底层。两者成功 postfix 现在于
  caller Transaction 内调用既有 timing allocator，MovePart 逐颗重锚且单颗失败隔离。

## 2026-09-04：DividePart/JoinParts 会更换部分 note handles

- native `--part-structure-probe` 对两颗 Rin `k k a/true` 实测：Divide 的右音符、Join 的第二
  音符都获得新 handle；物理串和保护位完整。由此证明按 handle 缓存的 301/延长1歧义态必须
  显式迁移，不能依靠 native 对象身份幸存。
- `PartStructureTransfer` 在调用前捕获所有 note 的绝对位置、音高、稳定次序、逻辑态和历史
  快照；调用后按最终绝对顺序重贴新声库兼容态、重算时值并清除消失 handles。RemovePart
  另在销毁前捕获并释放全部 note handles。
- InsertSilence 会在一次 native Transaction 中串联 DividePart、DuplicatePart、RemovePart、
  JoinParts。若每个低层 postfix 各记一条逻辑历史，一次 native Undo 会与多条 sidecar 历史
  错位。新增 transition accumulator：后续 before 若命中当前 after 则视为同一链，只保留
  首次 before/最终 after；不相交转换并入同一事务。Commit 成功发布一条，Rollback 丢弃。
- 纯回归已验证连续与并行组合；`--part-structure-probe` 和 `--position-timing-probe` 均
  `valid=True`。完整 Debug 0 警告、0 错误，尚待全 probe、Release 与宿主复测。

### 回归与部署结果

- 完整 Debug、logic harness、render harness Release 通过；10 项真实 VDM/VSM probes 均
  exit code 0。Part 结构 probe 明确要求 Divide/Join 均“不保留全部 handles”才算通过，位置
  probe 明确要求旧 Tick 在新 BPM 下产生大于 20 ms 漂移才算通过。
- Editor 本体关闭后生成 7,213,568-byte 合并 DLL，源输出与安装符号链接 SHA-256 同为
  `0B094C0B290D2C1F9D4C473D311E79A1114DFC1043CA3D9E9377E7F4FBFE78D6`。最终类型检查包含
  MoveNote/MoveMidiPart timing、Divide/Join/RemovePart、transition accumulator、LyricMove 和
  Harmony；assembly references 无 `0Harmony`。native clock 两端继续一致为
  `E2F1876BFDFC8BAD17F8F1238F2E828FA5E3FD102C863163C16C901555F5EBAD`。
