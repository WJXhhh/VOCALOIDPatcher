# 自生成声库的大规模构建与失败恢复验收

2026-10-15。对应原大表“大规模构建压力与失败恢复”。范围限定为现有离线构建链、自生成PCM和结构化STA/ART；不涉及真人录音、Editor、制作来源或产品发行。

**原表本项按离线合成工程边界验收完成。** 7782个单元、996096帧及约11.91 GB DDB通过现有完整构建链；两次全规模DDI/DDB哈希一致，14项故障/恢复检查通过。正式机器证据为[476](evidence/476_synthetic_bank_stress_and_recovery.json)。

## 验收对象

复用已有 `assemble_recording_units_ddb.py → plan_ddi_tree.py → TreeHarness → finalize_planned_ddi.py → verify_planned_ddi.py → TreeHarness LOAD_EXISTING`，不重写声学算法。新增 [build_planned_bank.py](../voicebank/tools/build_planned_bank.py) 将整条链放入私有目录，在全部验证成功后发布一个完整目录。

自生成的三个PCM16单声道44.1 kHz信号分别为220/330/440 Hz，各32768样本，含基频与二次谐波。现有DRS harness每个信号实际分析一次，检查128帧及STA/双音素ART的清浊和alignment契约，然后按三个层复用已验模板。每个逻辑单元均有独立ID、路径和manifest绑定。

因此本轮测的是**大规模装配、布局、偏移、序列化、验证与恢复**，不是7782份独立录音的分析吞吐，也不是声学多样性或音质。`coverage_complete=true`只表示声明的合成图已完整构建，`approval_complete`始终为false。

| 规模 | STA part | ART edge / part | 层数 | 总单元 | 最终帧 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 小 | 6 | 5 / 10 | 2 | 16 | 2048 |
| 中 | 114 | 300 / 900 | 3 | 1014 | 129792 |
| 全规模 | 114 | 2556 / 7668 | 3 | 7782 | 996096 |

均包含62个PHDC名称；合成名称不是中文或日语录音覆盖声明。全规模采用文档30规划的38个STA、2556条ART、三个层的数量。输入通过硬链接复用自有模板；遇NTFS单文件链接上限1142时改为复制，文件字节保持相同。首次准备失败记录保留在外部目录，不算构建器失败。

## 发布与恢复契约

1. 每次构建建立输出目录同级的 `.目标名.build-UUID`，保留请求、各阶段日志和状态。所有中间DDI/DDB均在该私有目录内。
2. 每个阶段使用Windows Job Object监督，支持阶段超时和进程提交内存限额；Job关闭时终止已加入的工作进程及其后代。
3. 装配器逐单元复查WAV/SMS2哈希，并检查写出DDB的完整哈希、每单元哈希及每帧解析/重写；finalizer读取实际最终帧F0计算pitch2；公开解析器检查全部计划part、引用与alignment；原生loader检查加载结果及全部计划树对象。
4. 新入口在公开验证前核对DDI与finalizer清单的哈希，并在所有阶段结束后再次核对DDI/DDB与最终构建清单的绑定。读回成功不替代字节完整性检查。源脚本哈希在开始时固定，发布前复查。
5. 仅成功后将整个 `bank` 目录重命名到目标。已有目标拒绝覆盖，并发发布也不覆盖先成功者。提交后的私有状态写失败不把已发布的有效结果误报成失败。
6. 失败保留诊断，不发布目标。**恢复方式为修正故障后用同一输入、同一目标重新完整构建**；不信任半成品，不提供逐单元断点续跑。强制杀死监督进程可能留下`building`状态文件，不能据此判断进程仍活着；发布结果中的`build_manifest.json`及实际进程状态优先。

finalizer自身的`ddi_manifest.json`仍保留`native_loader_valid=false`，表示它没有自行执行外部原生验收；新入口在实际执行成功后，另以`build_manifest.json`记录true，两者不混淆。

## 规模、确定性与资源结果

| 规模 | DDB字节 | DDI字节 | 六阶段耗时合计（秒） | 最高观察worker working set（MiB） | 公开解析 / 原生回读 |
| --- | ---: | ---: | ---: | ---: | --- |
| 小 | 24674848 | 34052 | 2.25 | 40.31 | 通过 / 通过 |
| 中 | 1552341388 | 1251704 | 93.79 | 47.57 | 通过 / 通过 |
| 全规模正式复测 | 11913531244 | 9531224 | 783.98 | 207.39 | 通过 / 通过 |

正式全规模监督进程从启动到退出的Windows进程时间为**791.64秒，约13分12秒**；六阶段合计不含监督器自身的清单、末尾完整性哈希及发布工作。监督器另测最高观察working set为39.18 MiB，不与worker峰值相加冒称同时峰值。

| 正式全规模阶段 | 秒 | 最高观察working set（MiB） | Job峰值提交（MiB） |
| --- | ---: | ---: | ---: |
| DDB装配及逐帧回读 | 631.81 | 182.09 | 173.67 |
| tree plan | 0.41 | 115.74 | 107.69 |
| 原生skeleton | 0.21 | 71.44 | 60.09 |
| 最终帧F0/pitch2及DDI生成 | 133.51 | 125.99 | 119.93 |
| 公开解析与引用核对 | 17.78 | 207.39 | 199.70 |
| 原生loader | 0.26 | 68.47 | 57.40 |

全规模公开解析逐项确认62个PHDC、114个STA part、2556条ART边/7668个ART part。DSE实际回读返回`load.result=0`、`root.authenticated=1`、`load.valid=True`，计划part数量及树对象检查全部通过。装配后校验解析并重写比较全部996096帧；finalizer再次读取最终帧F0。636989个帧起点超过4 GiB、277888个超过8 GiB，最大帧起点11913450354，最大SND payload指针11913461612；不是只测了32位以内的小偏移。

两轮全规模产物一致：

| 文件 | SHA-256 |
| --- | --- |
| DDI | `315aa0f5ac2d5887c1b88818ee47b262d3bec014acbb6fc7e31de078f19aa85c` |
| DDB | `be19ab5e8ffc06b75278e71db1826f2a4367694e5984ec8ea0c396221086056b` |

首轮在完整性保护补强前已启动，旧监督器快照及混合版本限制单列于476；其末尾采集的源哈希不作为执行版本证明。正式验收以第二轮的启动/结束一致源哈希为准。两轮底层装配/最终化源未改变，均实际从相同输入重建DDI/DDB，输出字节一致。小/中规模还通过多次故障后重建与基线比较。

资源指标区分：working set是每50 ms观察的操作系统历史高水位，最后未采样尾部可能遗漏；Job peak commit是操作系统维护的阶段进程/Job提交内存高水位。阶段指标不含监督器、整台机器总内存或磁盘缓存；不同阶段不重叠，不把峰值相加。并行执行的规模复测会竞争资源，耗时只作为本机本轮实测，不作为纯单任务基准。

## 故障注入与恢复

测试器：[verify_bank_build_recovery.py](../voicebank/tools/verify_bank_build_recovery.py)。故障只作用于测试专用副本或新构建私有目录，不改安装DLL、既有成功产物、共享模板或旧证据。

最终套件14项全部通过，耗时约153.9秒。各可恢复失败在同一目标解除故障后重建，DDI/DDB均与无故障基线的SHA-256一致。

| 故障/条件 | 观察结果与验收 |
| --- | --- |
| 已有成功目标 | 拒绝覆盖，目录中全部文件哈希不变 |
| 损坏manifest / WAV / 截断SMS2（三项） | 在装配预检拒绝，尚未创建私有bank；恢复输入后同目标重建一致 |
| 阶段超时 | 实际终止阶段Job，无目标发布；解除超时后重建一致 |
| 12 MiB进程提交限额 | 工作进程退出1，Job峰值提交12578816字节；监督器保留失败状态，解除限额后重建一致。提交限额不等于working set限额 |
| 独占DDB清单临时文件 | 实际Windows I/O拒绝，装配失败且目标不存在；解除文件锁后重建一致 |
| 独占DDI报告临时文件 | 私有目录内DDI已写出，但报告失败，不发布半套DDI/DDB；解除文件锁后重建一致 |
| 公开验证前改DDI头 | 新的DDI清单哈希检查拒绝，verify阶段退出2；重建一致 |
| 原生加载前改DDI头 | 原生loader仍返回0，随后独立哈希保护拒绝发布；重建一致。**不是原生loader拒绝** |
| 原生加载前截断DDI至16字节 | 原生阶段退出6，拒绝发布；重建一致 |
| 提交后私有状态写失败 | 已验证目标保持发布成功；不误报为构建失败 |
| 两个真实构建并发发布 | 返回码0/2，只有一个发布者；再次重试拒绝覆盖，胜出目录全部文件不变 |
| DDB写入后强杀监督器 | 观察到1559594字节写出后强杀；持有的工作进程句柄确认已退出，目标不存在；同一中规模输入重建后的1.55 GB DDB及DDI与基线一致 |

早期试验发现只改DDI开头四字节时，两种既有读取器都可继续接受，初版监督器错误地发布了该副本；该试验保留，不能算故障拒绝通过。由此增加独立清单哈希保护。另将“原生读取器拒绝截断”和“读取器仍接受但发布哈希保护拒绝”分开测试，避免把后者冒称原生loader拒绝。

物理磁盘写满、整机断电、文件系统故障修复、所有中断时间点及并发输入写入不在本轮证明内。独占写入失败是实际I/O失败，不冒称测过物理ENOSPC。Job终止证据针对工作进程已建立并实际写出DDB后的强制终止条件。

## 复现

所有生成物使用显式外部工作目录。本轮目录为仓库同级 `VOCALOIDPatcher-research-work/build-stress-20261015`，不使用Windows Temp。工具拒绝覆盖既有测试目录；复测应指定新的目录或套件名。

```powershell
dotnet build voicebank/tools/tree_harness/TreeHarness.csproj -c Release

# 生成并验证小/中规模基线；每个profile只运行一次。
python -B voicebank/tools/verify_bank_build_stress.py --work-root <work> --dll <DSE.dll> --parser-root <ddb-tools> --profile small
python -B voicebank/tools/verify_bank_build_stress.py --work-root <work> --dll <DSE.dll> --parser-root <ddb-tools> --profile medium

# 完整事务式构建：输入目录中应已有两个manifest及graph。
python -B voicebank/tools/build_planned_bank.py --unit-manifest <profile>/unit_manifest.json --analysis-manifest <profile>/analysis/analysis_manifest.json --unit-root <profile>/units --analysis-root <profile>/analysis --graph <profile>/graph.json --dll <DSE.dll> --parser-root <ddb-tools> --stem StressBank --output <new-output>

python -B voicebank/tools/verify_bank_build_recovery.py --work-root <work> --dll <DSE.dll> --parser-root <ddb-tools> --suite-name recovery
```

全规模模板可由`verify_bank_build_stress.prepare`以`(38,2556,3)`生成，随后交给事务式入口；脚本的`--profile full`则保留直接阶段基线入口。最终报告中的源文件/二进制哈希用于区分早期基线监督器与补强后的正式入口。

## 完成边界

原有30项Python测试通过，整个Debug解决方案构建0警告、0错误。既有464份JSON证据哈希全部保持不变。现有DrsHarness二进制直接复用并记录SHA-256，不冒称本轮已从当前工作树重建它。

本项只关闭原表的工程压力与安全重建验收。真人全量分析、标注QA、演唱音质、Editor自然调度、许可证与发行条件保持独立，不因本项通过而宣称整套制库工程完成。
