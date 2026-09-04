# VDM 运行时发现、组件 ID 与 DSE 配对加载

## 结论

VOCALOID6 的传统声库运行时入口已经闭合到一条可复现调用链：

1. VDM 从注册表枚举组件并验证元数据。
2. 对现代 `VOCALOID5\Voice\Components` 项，VDM 将注册值 `Path`、16 字符组件 ID 和目录中的第一个 `*.ddb` 合成完整路径。
3. `VDM_VoiceBank_path` 向 DSE 返回该 `.ddb` 路径。
4. DSE 丢弃扩展名，只保留目录与文件名 stem，再固定打开同 stem 的 `.ddi` 和 `.ddb`。

因此，一对文件的运行时配对条件是“同目录、同 stem、扩展名分别为 `.ddi/.ddb`”。注册表不直接保存 DDI 路径，`.vvd` 也不参与这条 V5 风格的 VDM→DSE 加载链。

这不等于编辑器已经允许无许可证的自建组件。格式发现、DSE 反序列化、许可证可用性是三个独立关卡。

## VDM 枚举路径

6.13.0.1 的 `FUN_1800e04f0` 按数据库模式枚举注册表：现代传统声库使用 `Voice\Components`，旧版本还存在 `DATABASE`、`DATABASE41` 和 `DATABASE\VOICE3` 分支。组件读取由 `FUN_1800dcc30` 完成；模式 2–4 会转入旧格式读取器 `FUN_1800dabf0`。

现代传统声库分支的关键步骤为：

- 构造 0x210 字节的 `VDM5::VoiceBank` 对象；
- 验证组件 ID 并从中恢复语言编号；
- 读取 `Path`，再拼接组件 ID 子目录；
- `FindFirstFileW(...\*.ddb)`，把第一个匹配文件的完整路径写入对象偏移 `+0x80`；
- `VDM_VoiceBank_path` 最终通过 vtable `+0x30` 返回这个字符串。

同一目录若存在多个 `.ddb`，选择顺序依赖 `FindFirstFileW`，不应作为稳定协议使用。自建组件目录应只放一对目标 `.ddi/.ddb`。

## V5 风格注册元数据

下面是 `FUN_1800dcc30` 的实际验收条件。表中的“必需”指 VDM 枚举成功所需，不代表许可证一定有效。

| 项 | 验收语义 | 状态 |
| --- | --- | --- |
| 组件子键名 | 必须恰好 16 字符，并通过组件 ID 解码与校验 | 必需 |
| `IsInstalled` | 读取前默认值为 1；缺失仍视为安装，显式 0 会拒绝 | 可省略 |
| `Path` | 传统 voice 模式下与组件 ID 拼接，目录内必须能找到 `*.ddb` | 必需 |
| `Version\Major` | DWORD，必须非负 | 必需 |
| `Version\Minor` | DWORD，必须非负 | 必需 |
| `Version\Revision` | DWORD，必须非负 | 必需 |
| `DRP` | UTF-16 字符串，长度必须严格为 6 | 必需 |
| `Name` | 非空组件全名；对应 `ComponentName` | 必需 |
| `Date` | UTF-16 字符串，长度必须严格为 16 | 必需 |
| `Key` | ANSI 字符串，建立名为 `default` 的 VDM license descriptor | 枚举可省略，授权另论 |
| `BankName` | 非空显示名；对应 `VoiceBank.Name` | 必需 |
| `DefaultStyleID` | 空或缺失时回退为 `0c29827a-4289-495d-94d2-e23602d346c6` | 可省略 |
| `GroupName` | 缺失时回退到 `BankName` | 可省略 |
| `IceProductName` / `IceValue` | 可建立额外授权描述；缺失时走固定 bundle 描述 | 可省略 |
| `Product`、模型名、模型索引、`Languages` | 属于另一数据库/AI 分支，传统 DSE voice 分支不依赖 | 非本路径必需 |

本机 7 个 V5 组件没有 `IsInstalled`，仍全部被 VDM 正常枚举；它们的 `DRP` 都是 6 字符，`Date` 都是 16 字符，`Version` 子键完整。空的 `DefaultStyleID` 被实际 API 统一返回为上述固定 UUID，缺失的 `GroupName` 被返回为 `BankName`。

旧 V3/V4 路径的字段名和目录组合规则不同。例如旧读取器检查大写 `INSTALLED/NAME/PATH/TIME`，并从 `NAME` 的圆括号内提取短名。自建库应优先采用已经闭合的 V5 风格组件路径，不混用旧字段集合。

### 名称、身份和“默认声库”是三个概念

6.13.0.1 的托管包装和启动逻辑把这些字段分得很清楚：

| 概念 | 存储/来源 | 运行时用途 |
| --- | --- | --- |
| 组件身份 | 16 字符 `CompID`（注册表组件子键名） | 组件查找、工程引用、许可证匹配、默认选择持久化 |
| 组件全名 | 注册值 `Name` → `VoiceBank.ComponentName` | 产品/组件描述；不是默认选择键 |
| 声库显示名 | 注册值 `BankName` → `VoiceBank.Name` | 编辑器声库名称和 `GetVoiceBankName` 返回值 |
| 分组显示名 | `GroupName`，缺失时回退到 `BankName` | UI 分组；不是授权身份 |
| 默认传统声库 | 用户设置 `defaultVoiceCompID` | 由 CompID 找库，找不到时按可用语言选择首个候选 |
| 默认 AI 声库 | 用户设置 `defaultAiVoiceCompID` | 同样按 CompID 保存，但走 DNN 类型和多语言可用性判断 |

`MainViewModel.Initialize()` 会把解析出的对象传给 `VoiceBank.SetDefault()`；VDM 的 `DefaultVoiceBank`/`DefaultAiVoiceBank` 随后供新 Part 继承。如果前一个 Part 存在，新 Part 优先继承前一个 Part 的 voice-bank ID。因此不存在一个需要写进 V5 组件元数据、名为“默认声库名称”的认证字段；需要生成的是 `Name`/`BankName` 等显示元数据，而默认选择属于每个 Editor 用户的设置。

传统 V5 路径的支持语言也不是自由填写的字符串列表：`NativeLangID` 由 CompID payload 第 4 位恢复，`LangIDs` 由 VDM 对象暴露；本路径不读取 AI 分支的 `Languages` 注册值。版本号则来自 `Version\Major/Minor/Revision`，VDM 还会独立给出 `IsSynthesizableVersion`、`IsVersionTooOld` 和 `IsVersionTooNew`，所以“字段能被解析”不等于“当前引擎接受该版本”。

## 组件 ID 不是任意 16 字符串

VDM 的 `FUN_1800daa40` 调用 `FUN_1800d9de0`，把 16 字符组件 ID 解成 14 位 base-28 payload。其规则包括：

- 两轮按校验半字节选择的 base-28 位置置换；
- 14 列替换表；
- 对替换后 14 字节求和所得的 8-bit 校验；
- payload 第 4 位（索引 3）由 base-28 转为 native language ID。

例如，本机一个中文 V5 组件：

```text
component_id = BD79E492NWWK3DDF
payload      = 00L415D0050000
language     = 4
```

新增的 `compid_codec.py` 不内置或复制 VDM 的置换表，而是在运行时从用户自己的 `VDM.dll` 中定位并验证表结构。它可以做双向闭环：

```powershell
python voicebank\tools\compid_codec.py --decode BD79E492NWWK3DDF
python voicebank\tools\compid_codec.py --encode 00A40000000000
```

对 payload `00A40000000000`，脚本生成 `BCB8AXEZKKTHYCAF`。随后用 VDM 6.13.0.1 自身内部解码函数交叉验证，得到 `native.valid=True`，原样恢复 payload。这个 ID 只是算法测试值；正式自建库仍应检查它没有与任何已安装或计划分发的组件冲突。

当前表布局针对 6.13.0.1 验证。脚本会检查 alphabet 唯一性、表值范围和每列有效 digit 集合；版本布局不匹配时应失败，而不是静默产生错误 ID。

## DSE 如何从 `.ddb` 找到 `.ddi`

DSE 初始化链为：

```text
VIS_DSE_InitializeManager
  -> FUN_180184830
    -> FUN_18017fd50              枚举 VDM voice banks
      -> VoiceBank vtable + 0x30  取得完整 .ddb 路径
      -> FUN_180180180             _splitpath，得到目录和 stem
      -> FUN_18010c5b0             构造 CDBSinger(base=目录/stem)
      -> vtable + 0x18
         = FUN_18010d490           打开 base.ddi 与 base.ddb
```

`FUN_180180180` 明确丢弃输入扩展名。`FUN_18010d490` 第一次追加 `.ddi` 并反序列化对象树，第二次追加 `.ddb` 并保留数据流。先前最小 STA 与 STA+ART 银行已经通过同一 `FUN_18010d490` 成功回读，因此“发现路径”和“文件内容可加载”两端现在已经接上。

加载器本身对 DDI 打开失败没有提供清晰的错误返回；它的函数返回值不能单独作为有效性判据。仍应检查根认证、对象数量、帧表、SND 指针及实际 materialized 状态。

## 独立 VDM harness 实测

`voicebank/tools/vdm_harness` 直接调用公开 VDM C API，不启动 VOCALOID Editor，也不写注册表：

```powershell
dotnet run --project voicebank\tools\vdm_harness\VdmHarness.csproj -c Release
```

本机 6.13.0.1 实测结果：

- `VDM_createDatabaseManager("VOCALOID6", "C:\Program Files\Common Files\VOCALOID6\Explib", ...)` 返回成功；
- 共枚举 29 个传统 DSE voice bank，涵盖 V3、V4 和 V5 注册路径；
- 7 个 V5 中文库的完整 DDB 路径、版本、DRP、名称、语言 4、5 个 voice parameters 与 synthesizable-version 标志均正常；
- 每个 V5 样本对象暴露 2 个 VDM license descriptors，但这不代表 DSE 授权结果有效。

组件 ID 的原生交叉验证模式：

```powershell
$env:VDM_HARNESS_DECODE_COMPONENT_ID = 'BCB8AXEZKKTHYCAF'
dotnet run --project voicebank\tools\vdm_harness\VdmHarness.csproj -c Release
```

该模式直接调用 6.13.0.1 的内部 RVA，只用于验证 codec 研究结论，不应当作跨版本公共 API。

## 许可证边界

VDM 能枚举、DSE 能打开 DDI/DDB，仍不足以让 stock Editor 把组件视为可用：

- 托管层 `VoiceBankExtension.GetLicenseResult` 会在 DSE license 列表中按相同 `CompID` 查找 voice license；
- `IsValidLicense` 只接受 Trial、ValidLeaseFile、PaidOffLeaseFile、ValidExpiryKey 或 NoError；
- 查不到 license 或结果无效时，UI 会显示不可用/过期；
- 注册表 `Key` 被 VDM包装为授权描述，但任意字符串不会产生合法授权。

后续只读 harness 已进一步证明：本机 29 个传统库共有 57 个非空 key/serial descriptor，但最终结果仍全部不在 Editor 接受集合中。完整对象链、结果分布和复现方法见 [DSE 许可证对象与编辑器可用性判定](17_dse_license_pipeline.md)；CompID、名称、语言、版本、默认选择与 DBSe 摘要的边界汇总见 [声库身份、默认选择与许可证字段矩阵](26_voicebank_identity_and_license_fields.md)。

因此不能用现成商业组件 ID 冒充自建库：这会造成组件冲突、授权错配，并可能让已安装库失效。下一步宿主验证必须使用独立组件 ID 和可撤销的隔离注册；授权条件必须通过合法渠道另行满足。格式研究可继续通过不依赖 stock 授权 UI 的原生 DSE/VSM harness 分层验证。

## 尚未完成

- 尚未实际写入最小 V5 测试注册项；本轮保持注册表只读。
- 尚未让 stock Editor 枚举自建组件；也没有启动 Editor。
- 尚未证明无许可证自建组件能通过完整 VSM 渲染链。
- `_v4compatible.vvd` 不在本次确认的 V5 VDM→DSE 路径中，但不排除旧兼容模块或其它工具使用它。

当前最安全的下一步不是立即安装，而是先完成真实 `Sil↔a` 声学单元，再设计一个进程级或独立测试账户中的可撤销注册实验，并把许可证判断与格式/渲染判断分开记录。
