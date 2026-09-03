# 研究日志

## 2026-09-03：建立传统声库基线

### 工作区与样本

- 用户指定记录目录 `voicebank/_doc` 当时不存在；仓库只有未跟踪目录 `voicebank_doc/`，内有一份 VDM/AI 声库分析。
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
