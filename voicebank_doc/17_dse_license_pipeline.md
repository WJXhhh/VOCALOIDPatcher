# DSE 许可证对象与编辑器可用性判定

## 结论

`CompID`、声库名称、语言和版本属于组件身份/发现元数据，不是许可证。VOCALOID 6.13.0.1 的实际链路至少分成三层：

1. VDM 从注册数据建立 `VoiceBank`，并暴露零个或多个只含 `Key`/`SerialNumber` 的 license descriptor；
2. DSE 初始化时读取组件身份和这些 descriptor，执行原生验证，为每个组件建立一个带明确结果码的 DSE `License` 对象；
3. Editor 再按 `CompID` 查找 voice license，只接受五种结果，并另行检查声库版本是否可合成。

所以，格式正确的注册项、可解码的 CompID、非空 `Key`/`SerialNumber`，乃至 DSE 列表中已经出现同 CompID 的对象，都不能单独证明授权有效。

本章只研究识别和判定链，不生成、修改或绕过许可证。

## 两种 license 对象不是一回事

### VDM license descriptor

`VDM.VoiceBank.NumLicenses` 和 `VoiceBank.GetLicense(index)` 暴露的是注册数据解析后的候选描述。公开 C API 只有 parent、ANSI `Key` 和 ANSI `SerialNumber`。它没有最终结果码。

Ghidra 交叉验证的 VDM vtable 槽位为：

| 数据 | 槽位 |
| --- | ---: |
| `VDM_License_serialNumber` | `+0x08` |
| `VDM_License_key` | `+0x10` |
| `VDM_VoiceBank_numLicenses` | `+0xa0` |
| `VDM_VoiceBank_license(index)` | `+0xa8` |

### DSE License

DSE 的 `VIS_DSE_GetLicense` 返回最终判定对象。公开读取接口包括：

- `CompType`、`CompID`、`CompName`、`CompVersion`；
- `Result`、`SpliceResult`；
- `ExpiryDate`、`RemainingTrialDays`。

`DSE.dll` 中这些导出只是 vtable 包装。当前对象布局的主要字段已经闭合：

| 对象偏移 | 含义 |
| ---: | --- |
| `+0x08` | component type |
| `+0x10` | UTF-16 CompID |
| `+0x30` | UTF-16 CompName |
| `+0x50` | Result |
| `+0x54` | SpliceResult |
| `+0x58` | expiry |
| `+0x60` | remaining trial days |
| `+0x68` | 三段版本对象 |

构造路径还确认了这些身份字段的来源：`CompID` 取 VDM voice-bank vtable `+0x10`，`CompName` 取 `+0x40`，版本取 `+0x20`。再与 VDM 公共导出交叉核对，`+0x40` 对应 `VDM_VoiceBank_name`，不是 `VDM_VoiceBank_componentName`（后者在 `+0x28`）。因此许可证对象显示的是声库 `Name/BankName` 一侧的名称。

## DSE 初始化如何形成结果

DSE 初始化后的许可证列表构建函数位于 `0x1801dc510`。这条函数很大，但高层数据流已经可以可靠描述：

1. 取得 VDM voice bank 的 `NumLicenses`，逐个读取 descriptor 的 serial/key；
2. 结合组件 ID、名称、版本和其它组件属性建立候选结果；
3. 走包含 BCrypt 哈希接口、结构化数据检查和签名校验的验证分支，因此它不是“某个注册表字符串是否非空”的判断；
4. 将候选按一个固定但非数值顺序的优先级比较器排序；
5. 选中候选写入 DSE `License` 的 `Result`、expiry 和 remaining-days，并把对象加入 manager 列表。

当前只把这段反编译用于确认输入、输出和控制流。精确的密钥载荷格式、签名方案、候选优先级语义和 lease 文件协议尚未闭合，也不属于自训声库格式生成器应该实现的内容。

DSE 还导入 `GetAdaptersInfo` 与 `GetVolumeInformationA`，但当前没有把它们与 `0x1801dc510` 的直接调用关系闭合；现阶段不能据此断言某一种许可证一定绑定网卡或卷信息。

一个有用的行为结论是：验证失败的注册组件仍可得到 DSE `License` 对象，只是 `Result` 为失败值。因此“能按 CompID 找到对象”和“有效许可”必须分开记录。

## 结果码与 Editor 门槛

6.13.0.1 托管枚举为：

| 值 | `LicenseResult` | Editor 接受 |
| ---: | --- | :---: |
| 0 | `Undefined` | 否 |
| 1 | `MissingLeaseFile` | 否 |
| 2 | `Trial` | 是 |
| 3 | `Expired` | 否 |
| 4 | `InvalidTrialKey` | 否 |
| 5 | `ExpiredLeaseFile` | 否 |
| 6 | `InvalidLeaseFile` | 否 |
| 7 | `ExpiredKey` | 否 |
| 8 | `ValidLeaseFile` | 是 |
| 9 | `PaidOffLeaseFile` | 是 |
| 10 | `InvalidKey` | 否 |
| 11 | `InvalidSerialNumber` | 否 |
| 12 | `InvalidComponent` | 否 |
| 13 | `InvalidHash` | 否 |
| 14 | `ValidExpiryKey` | 是 |
| 15 | `NoError` | 是 |

`VoiceBankExtension.GetLicenseResult` 从 DSE manager 中寻找第一个 `CompType == Voice` 且 CompID 完全相等的对象。`IsValidLicense` 只接受表中的五个“是”。即使许可证结果有效，`GetNameWithUnavailableReason` 还会独立检查 `VoiceBank.IsSynthesizableVersion`；版本兼容和授权仍是两个门槛。

`SpliceResult` 与 `Result` 是两个独立字段。Editor 对普通 voice-bank 可用性的上述扩展方法读取 `Result`；尚不能把 `SpliceResult` 当作同义字段。

继续检查 6.13.0.1 全部托管引用后，`SpliceResult` 的消费者范围可以再收窄：`DSEManager.GetSpliceResultForApplicationCompID` 明确只匹配 `CompType.Application`，唯一上层调用是 `AnalyticsController`，把 Editor 主组件的结果映射成 `missing/expired/invalid/valid/paid_off_lease_file` 统计字符串。启动授权、传统声库与 AI 声库可用性均读取普通 `Result`，不读取 voice license 的 `SpliceResult`。这只是当前托管 Editor 的消费者结论，不排除其它原生宿主或未来版本另有用途。

### `SpliceResult` 的本地文件支路

继续追踪 `FUN_1801edee0` 后，`SpliceResult` 的独立来源也更清楚了：

- `FUN_1801ed6c0` 传给 `SHGetKnownFolderPath` 的 GUID 是 `FOLDERID_LocalAppData`；
- 它在该目录下构造 `SpliceSettings\license\<identifier>.lic`；
- `FUN_1801ee9e0` 只读打开并载入整个 `.lic`；
- 解析器区分文件缺失、结构/身份不匹配和时间状态，并从许可证列表构建函数 `0x1801dc510` 的三处直接调用进入。

这解释了为什么对象同时保留普通 `Result` 与 `SpliceResult`。这里没有读取或保存本机 `.lic` 内容，也不把其内部混淆/签名载荷作为可生成格式；尚未确认文件名参数在所有组件类别中的精确身份映射。

## 本机只读实测

新增的 `voicebank/tools/license_harness` 只调用 VDM/DSE 公共导出，不启动 Editor、不写注册表，也不输出 key 或 serial 内容。2026-09-04 在当前 6.13.0.1 安装上的汇总为：

| 项目 | 数量 |
| --- | ---: |
| VDM 枚举的传统 DSE voice banks | 29 |
| VDM license descriptors | 57 |
| 非空 key / 非空 serial | 57 / 57 |
| 只有 1 个 descriptor 的传统库 | 1 |
| 有 2 个 descriptor 的传统库 | 28 |
| VDM 枚举的 AI/DNN voice banks | 12 |
| DNN license descriptors | 25 |
| DNN 非空 key / 非空 serial | 25 / 25 |
| 有 2 / 3 个 descriptor 的 DNN 库 | 11 / 1 |
| DSE License 总数 | 43 |
| Application / Voice | 2 / 41 |
| 与传统 DSE / AI DNN 库按 CompID 匹配 | 29 / 12 |
| 未匹配到 VDM voice bank 的 voice license | 0 |

29 个匹配传统库的结果分布：

| 结果 | 数量 |
| --- | ---: |
| `Expired` | 7 |
| `InvalidTrialKey` | 1 |
| `InvalidKey` | 21 |

全部 43 个 DSE License 的结果分布为 `Expired=19`、`InvalidTrialKey=2`、`InvalidKey=21`、`NoError=1`。这些数字只是当前机器、当前时间和当前安装状态的快照；它们不能用于推断其它机器或产品的授权状态。

此前“额外 12 个 Voice license”的来源现在已经闭合：它们一一对应 VDM 的 12 个 DNN/AI voice banks。DNN 结果分布为 `Expired=10`、`InvalidTrialKey=1`、`NoError=1`；加上 29 个传统 DSE 库与 2 个 Application 对象后，43 项全部有来源，没有未匹配对象。DSE manager 的单一 license 列表同时服务传统和 AI 声库，Editor 再通过同一 `CompID` 与 VDM 对象关联。

最直接的证伪是：57 个 VDM descriptor 的 key/serial 都非空，但 29 个传统库没有一个落入 Editor 接受的五类结果。因此注册表 `Key`、descriptor 数量或字段非空绝不能作为许可成功判据。

### 身份字段逐项对齐

`license_harness` 现同时读取匹配 VDM voice bank 的 `ComponentName`、`Name`、版本、`NativeLangID/LangIDs` 和 `IsSynthesizableVersion`，再与 DSE License 对象逐项比较。当前 41 个 Voice license（29 DSE + 12 DNN）得到：

```text
CompID 匹配到 VDM voice bank       41 / 41
DSE CompName == VDM VoiceBank.Name 41 / 41
DSE version == VDM version         41 / 41
name/version mismatch               0 / 0
```

这进一步确认 DSE License 的身份三元组是 `CompID + VoiceBank.Name + Version`；注册值 `Name` 对应的 `ComponentName` 不是许可证对象的 `CompName`。这里的“一致”只证明对象复制/关联关系，不证明名称或版本本身经过签名，也不能把任意相同字符串变成有效许可证。

29 个传统库中，11 个运行时语言为 `0`、18 个为 `4`，全部满足 `NativeLangID == LangIDs[0]` 且 `IsSynthesizableVersion=true`。七个目标中文 V5 库均为：

```text
Version             5.0.0
NativeLangID        4 (Chinese/CHS)
LangIDs             [4]
IsSynthesizable     true
DSE CompName        BankName，不是 ComponentName
```

语言没有出现在 DSE License 公共对象中；传统 V5 的语言由 CompID 解码后进入 VDM voice bank，再供 Editor/G2PA 使用。详细字段边界与七库 payload 见 [声库身份、默认选择与许可证字段矩阵](26_voicebank_identity_and_license_fields.md)。

## 只读 harness

```powershell
dotnet run --project voicebank\tools\license_harness\LicenseHarness.csproj -c Release
```

默认只打印总数和结果分布。需要现场诊断组件匹配时，可显式启用逐项输出：

```powershell
$env:LICENSE_HARNESS_INCLUDE_ENTRIES = '1'
dotnet run --project voicebank\tools\license_harness\LicenseHarness.csproj -c Release
Remove-Item Env:LICENSE_HARNESS_INCLUDE_ENTRIES
```

逐项模式会输出 CompID、名称、版本和结果，但仍只统计 key/serial 是否非空，不输出其值。可用 `LICENSE_HARNESS_EDITOR`、`LICENSE_HARNESS_VDM`、`LICENSE_HARNESS_DSE` 和 `LICENSE_HARNESS_EXPLIB` 指向其它只读测试安装。

## 官方授权与第三方发行边界

官方公开资料补上了逆向结果之外的制度边界：

- Yamaha 将每个最终产品的 serial code 视为终端用户 license；用户通过 VOCALOID Authorizer 把 serial 提交给 Yamaha 授权服务器，取得保存在当前 PC/OS 环境中的 authorization info。2026-02-20 更新的官方说明仍给 Voicebank 14 天未授权宽限期；2026-04-02 发布的 Authorizer 1.0.2 继续覆盖 V3/V4/V5/V6 Voicebanks：[VOCALOID Product License Management](https://www.vocaloid.com/en/learn/ln6110/)、[VOCALOID Authorizer](https://www.vocaloid.com/en/support/download/vocaloid_authorizer/)。
- 官方支持政策明确把非 Yamaha Voice Bank 的支持交给“与 Yamaha Corporation 签有许可协议的 partner company”；面向考虑 VOCALOID business 的法人，公开入口是 Yamaha 的企业咨询表：[support policy](https://www.vocaloid.com/en/support/inquiry/support_policy_products/)、[corporate inquiry](https://inquiry.yamaha.com/contact/?act=39&lcl=en_WW)。
- 另有 2025-11-25 启动的审查制 `VOCALOID FAN-ding`。官方说明由 Yamaha 与 CAMPFIRE 支持资金筹集、录音、voicebank 制作、发行与宣传；申请不保证获选，页面没有承诺申请人自行提交已构建 DDI/DDB，也没有说明传统 V5/DSE 产品是否在受理范围：[Yamaha 新闻稿](https://www.yamaha.com/ja/news_release/2025/25112501/)、[FAN-ding 申请说明](https://camp-fire.jp/highlights/vocaloid-fan-ding)。

因此需要区分两种完全不同的“许可证”：

1. 最终用户购买产品后持有的 serial/license，以及由 Authorizer 从服务器取得的本机 authorization info；
2. 声库制作者要把一个新 CompID 变成可销售、可授权产品所需的 Yamaha/partner 商业许可、产品登记和签发链。

截至 2026-09-04，官方页面没有公开个人可自助调用的 CompID 分配、license 签名或 voicebank 签发 API。公开可见的合法新产品路线至少有：既有 Yamaha partner、面向 VOCALOID business 的企业咨询，以及审查制 FAN-ding。三者都需要外部合作/审查，不能由本仓库的元数据生成器自行产生授权；FAN-ding 公开页所说的“Yamaha 制作”也不能推导为接受申请人自己的传统库构建链。这不等于 Yamaha 一定接受某个项目，也不能从公开页面推断正式 CompID、serial、签名、SDK、QA 或费用条款。

## 对自训路线的影响

- 元数据生成器已经能建立独立 CompID 和 VDM 可发现身份，但其 manifest 必须继续标记 `creates_license=false`。
- 原生 DDI/DDB loader 能验证格式，不等于 stock Editor 会把该组件作为可用歌手；宿主验收需要把“发现、格式加载、许可证结果、版本兼容、最终渲染”分别记账。
- 自训工具不应复制商业组件 ID，也不应生成或猜测 key/serial。面向实际产品的授权只能来自合法发行/授权渠道。
- 在授权路径未明确前，格式和声学研究可继续使用完全自有数据及只读/独立原生 harness；不能把绕过 Editor 判定列为构建器功能。

## 尚待确认

1. Yamaha 对新的第三方传统声库是否仍接受签约、所需主体/费用/SDK/QA/发行条件；partner/企业咨询和 FAN-ding 都是可联系入口，但公开资料没有确认传统 V5/DSE 自构建产品的受理方式；
2. Splice `.lic`、其它 lease 文件和 expiry-key 的合法生命周期，以及它们与本地 descriptor 的优先级；
3. `SpliceResult` 文件名参数在不同组件类别中的精确映射，以及是否有当前 Editor 之外的原生消费者；
4. 新的独立 CompID 在无授权状态下，stock Editor 分别在哪些 UI、VSM 建立和渲染入口停止。
