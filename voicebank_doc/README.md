# VOCALOID 传统声库研究

本目录记录从已安装、合法可用的 VOCALOID3/4/5 传统声库出发，对 `.ddi`、`.ddb`、`.vvd`、DSE 加载与声学分析流程所做的可复现研究。

最终目标不是只做到“替换几段采样能够发声”，而是形成一条从自有录音、标注和声学分析，到生成可由 VOCALOID6 的传统 DSE 轨道稳定加载、完整演唱的声库构建流程，并明确其版本、授权和分发边界。

## 当前状态

- 已盘点本机 7 个 VOCALOID5 注册的传统中文声库。
- 已验证 `.ddi` 是块式索引/模型树，`.ddb` 是 `FRM2` 与 `SND ` 等块的聚合数据文件。
- 已用公开解析器只读解析一个完整声库，并批量统计全部 7 个声库的音素、静态音和转接单元覆盖。
- 已确认公开 `ddb-tools` 能解析/提取并基于现成 `singer.tree` 重新打包，但不能从原始 WAV 生成声学分析帧或新的树。
- 已通过 Ghidra 确认当前 DSE 内含 `DSE5::CDBSinger`、`DSE5::CDBVArticulation`、`DSE5::CDBVStationary`、`DRS::CSMSAnalysis` 等类；`CDBSinger` 内部存在 `.tree` 序列化与加载路径。
- 已用独立探针对 7 个 DDB 的 30.73 GB 顶层块做全量验证，并逐项交叉检查 DDI 引用：2,670,252 个 EpR 与全部 FRM2 一一对应，56,894 个样本与全部 SND 一一对应。
- 已确定本批中文库只有三种 FRM2 字段掩码；其中 `0x0000002000e00207` 是普通有声/STA 主帧，`0x00000000000e22b7` 对应 VQM，`0x0000000000000200` 是 ART 中基频值为零的轻量帧。
- 已完整走通普通、无声与 VQM 三种 FRM2 的物理布局；7 库全部 2,670,252 帧通过内部边界扫描，全部 1,459 个 VQM 帧和代表性普通/无声帧可逐字节回环。
- 已区分 DDI 的两个音高字段：第一个是样本层选择坐标，第二个是合成使用的实际参考音高；两者均为相对 A4 的音分。
- 已恢复 DRS 的内存 PCM 批处理驱动：支持 `CSoundIO` 16-bit PCM 或 `{float*, count, sample_rate}` 描述符，按 128 samples 供给环形缓冲并逐帧提交 `CSMSFrame`；hop 构造公式与参考库的 256-sample 结果闭合。
- 已用独立 harness 从程序生成的 44.1 kHz PCM 得到含完整声学字段的 DRS SMS2；自动/外部 F0 两条分支均实测，writer 输出可由同一 DSE reader 完整载回，独立 probe 也能验证 87/345 帧闭环。
- 已恢复 DRS 原始帧到最终普通 DSE5 FRM2 的转换入口；harness 可直接接收 44.1 kHz PCM16 WAV，生成目标主掩码 `0x0000002000e00207`，并由独立 validator 对 52/52 帧逐字段验证。
- 已构建第一对完全自有的单 STA `.ddi/.ddb`：52 个最终 FRM2、1 个 SND、1 个 `a` 音素。公开解析器与 DSE 6 原生加载器均成功回读，帧偏移、SND 指针、PCM 数量和 DBSe 名称摘要全部闭合。
- 已进一步构造一个诊断用 `a→a` ART/ARTu/ARTp：第二个自有单元使用独立 DDB 偏移，两个 SND 指针与两组 frame-alignment 均由公开解析器和 DSE 原生加载器一致恢复。
- 已把最小图扩展为 voiced `a`、unvoiced `Sil`、一个 `a` STA 和 `Sil→a`/`a→Sil` 两条 ART；三单元 DDI/DDB 已由公开解析器和 DSE 原生加载器按名称、层级和独立指针双重验证。
- 已用归一化 external-F0 阶跃把 0.15 秒边界精确生成成 26/26 个 unvoiced/voiced 帧，并以方向相反的两份真实不同 ART 分析数据重建双向库；逐帧类型、公开解析和 DSE 原生加载均通过。
- 已新增规格驱动的一键构建器，从三段自有 WAV、F0 和秒标注开始，自动完成 DRS 分析、逐帧/边界检查、建树、DDI/DDB 打包与 DSE 原生加载验收；合成输入端到端回归通过。
- 已完成七个中文库的音素/STA/ART 图交并集：62 音素与 38 STA 全库一致，2,556 条二音素 ART 边全库共有，只有 3 条产品可选边；60 节点公共图强连通，音高层稀疏例外全部是 `Sil→无声辅音`。
- 已闭合 VDM→DSE 运行时发现链：V5 注册项只枚举目录内首个 `.ddb`，DSE 再按同目录同 stem 固定打开 `.ddi/.ddb`；独立 VDM harness 实际枚举 29 个传统库，组件 ID codec 也已双向恢复并由 VDM 自身交叉验证。
- 尚未达到“有把握自训”的完成条件；当前首要硬缺口已经转为真实转接录音/边界标注、`Sil↔V` 与目标语言覆盖，以及宿主内隔离加载/渲染。

## 文档索引

- [研究日志](00_research_log.md)
- [传统声库格式第一轮盘点](01_traditional_format_first_pass.md)
- [达到可自训所需的验证路线](02_path_to_self_trained_bank.md)
- [DSE 中的 FRM2 与分析路径](03_dse_frm2_and_analysis_path.md)
- [七个中文参考库的全量结构统计](04_reference_bank_statistics.md)
- [普通 FRM2 字段语义与样本音高元数据](05_main_frm2_semantics.md)
- [DRS 离线声学分析驱动与 PCM 输入路径](06_drs_offline_analysis_driver.md)
- [DRS SMS2 harness、外部 F0 与回读闭环](07_drs_sms2_harness.md)
- [最小 STA DDI/DDB 与 DSE 原生回读闭环](08_minimal_stationary_bank.md)
- [ART/ARTu/ARTp 结构、对齐组与诊断闭环](09_minimal_articulation_bank.md)
- [VDM 运行时发现、组件 ID 与 DSE 配对加载](10_vdm_runtime_discovery.md)
- [`Sil`/`a` 双向边界库闭环](11_minimal_sil_a_bank.md)
- [ART 边界标注注入与有声/无声帧生成](12_articulation_annotation_injection.md)
- [规格驱动的最小 `Sil`/`a` 构建器](13_spec_driven_minimal_builder.md)
- [七库中文音素表、ART 图与音高层覆盖](14_chinese_phoneme_graph.md)
- [VDM 架构、语言标志与运行时补丁分析](15_vdm_architecture_and_patch_analysis.md)（主要讨论 V6 AI 声库，其中未注明直接证据的模型架构推断仍需复核）

## 数据边界

- 不把已安装商业声库的 `.ddb/.ddi/.vvd`、解出的录音或其它专有内容复制进仓库。
- 文档只记录结构、统计值、偏移关系、调用链和可复现实验结果。
- 任何自有录音实验应放在 Git 忽略的外部工作目录，生成物也不作为普通源码提交。
