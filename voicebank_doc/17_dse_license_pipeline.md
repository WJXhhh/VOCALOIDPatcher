# DSE 许可证对象与编辑器可用性判定

## 结论

`CompID`、声库名称、语言和版本属于组件身份/发现元数据，不是许可证。VOCALOID 6.13.0.1 的实际链路至少分成三层：

1. VDM 从注册数据建立 `VoiceBank`，并暴露零个或多个只含 `Key`/`SerialNumber` 的 license descriptor；
2. DSE 初始化时读取组件身份和这些 descriptor，执行原生验证，为每个组件建立一个带明确结果码的 DSE `License` 对象；
3. Editor 再按 `CompID` 查找 voice license，只接受五种结果，并另行检查声库版本是否可合成。

所以，格式正确的注册项、可解码的 CompID、非空 `Key`/`SerialNumber`，乃至 DSE 列表中已经出现同 CompID 的对象，都不能单独证明授权有效。

本章只研究识别和判定链，不生成、修改或绕过许可证。

**2026-10-16 本地验证补充：** 本轮按用户指定范围只追本地 descriptor 的类型、来源和匹配用途，不把服务器流程作为研究前提。当前 DSE 为 6.13.1.1，下面新增的 [SerialNumber 分支与实际用途](#serialnumber-分支与实际用途2026-10-16) 修正了旧记录中硬件调用尚未闭合、把哈希笼统称作签名、直接按内存整数解释结果码的不足。旧机器统计仍是历史快照。

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
| `+0x50` | 内部编码的 Result，须经 getter 转换为公开枚举 |
| `+0x54` | SpliceResult 内部存储，须通过对应 getter 读取 |
| `+0x58` | expiry |
| `+0x60` | remaining trial days |
| `+0x68` | 三段版本对象 |

构造路径还确认了这些身份字段的来源：`CompID` 取 VDM voice-bank vtable `+0x10`，`CompName` 取 `+0x40`，版本取 `+0x20`。再与 VDM 公共导出交叉核对，`+0x40` 对应 `VDM_VoiceBank_name`，不是 `VDM_VoiceBank_componentName`（后者在 `+0x28`）。因此许可证对象显示的是声库 `Name/BankName` 一侧的名称。

## DSE 初始化如何形成结果

DSE 初始化后的许可证列表构建函数位于 `0x1801dc510`。这条函数很大，但高层数据流已经可以可靠描述：

1. 取得 VDM voice bank 的 `NumLicenses`，逐个读取 descriptor 的 serial/key；
2. 结合组件 ID、名称、版本和其它组件属性建立候选结果；
3. 按 descriptor 标记进入不同的结构、哈希、硬件匹配或本地 lease 检查分支，因此它不是“某个注册表字符串是否非空”的判断；不能由哈希调用推断公私钥签名；
4. 将候选按一个固定但非数值顺序的优先级比较器排序；
5. 选中候选写入 DSE `License` 的 `Result`、expiry 和 remaining-days，并把对象加入 manager 列表。

精确的完整载荷计算、候选优先级语义和 lease 文件协议尚未全部闭合。本轮补充确认了分支标记、普通分支的硬件候选来源与部分摘要路径，仍没有独立重现完整授权计算。未定位并证明普通分支使用一对非对称公私钥。

2026-10-16 已把 `0x1801dc510 → 0x1801d8030 → GetAdaptersInfo / 0x1801d7920 → GetVolumeInformationA` 闭合到普通候选分支。卷序列号和网卡地址被组织成多个可尝试的候选，并非直接把全部硬件字段拼成一个固定机器码；不能据此断言每类许可证都绑定同一组硬件。

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

## SerialNumber 分支与实际用途（2026-10-16）

### 先分清字段和业务概念

`VDM_License_serialNumber` 返回一个字符串，不是一个单独的“授权类型枚举”。DSE 先识别保留字符串，再处理普通标识。**字段名叫 SerialNumber，不代表每项都是购买时输入的序列号。** 本轮不假设它与购买序列号之间的转换规则，也不研究服务器。

可以把每个 descriptor 理解为一张候选卡：上面有“标记或标识 S”和“校验数据 K”。声库对象另外提供产品 CompID、Date 等上下文；DSE 根据 S 选择检查方式，再为产品选出结果。一个声库可以有多张卡，所以“两项 descriptor”不等于“两次激活成功”。

本轮目标为 DSE.dll **6.13.1.1 / x86-64 / image base 0x180000000**，SHA-256 为 `7a4965bd377dbc2b08710e807ae1345b67d9fff6579ca0c01a769f744366f5b5`，与当前 Ghidra Program 一致；安装文件 Authenticode 为 Valid，签署者 Yamaha Corporation。VDM.dll 仍为 6.13.0.1。具体观察与验证边界保存在 [证据480](evidence/480_dse_serialnumber_types.json)，不保存真实 Key、普通 serial 或硬件标识。

| S 的内容 | 代码实际检查的东西 | 对自制声库研究的用途 | 目前不能据此得到什么 |
| --- | --- | --- | --- |
| `default` | 32 字符 Key、可解码产品身份、产品派生摘要和日期窗口 | 定位试用候选是否建立、区分试用校验失败与到期、核对安装 Date 的影响 | 改成 default 并不会自动得到无限期可用声库 |
| `bundle` | 特定内置组件身份的固定匹配 | 解释为何某个随附组件可用，也解释很多其它库的 bundle 候选为什么不起作用 | 它不是任意新组件都能使用的通用捆绑授权模式 |
| `splice` | 组件附加信息的 SHA-256 一致性、本地 `.lic` 的结构、身份与时间状态 | 分开诊断附加信息不匹配、lease 缺失、lease 无效和到期 | 仅添加字符串不产生相应 `.lic` 或有效结果 |
| 其它非保留字符串 | 普通标识的 codec、32/62 字符 Key、产品派生值与本机硬件候选的组合校验 | 研究离线产品/机器匹配，建立只输出类型、长度和检查阶段的诊断器 | 标识格式通过不等于 Key 匹配；完整授权计算仍未独立复现 |

### default：安装默认候选，走试用检查

既有 VDM 读取研究确认，安装组件项的 `Key` 可以被包装为 S=`default` 的 descriptor。它与用户设置 `defaultVoiceCompID` 没有关系，后者只控制默认选哪个歌手。

传统声库循环的分支识别位于 `0x1801de595`。后续要求 K 长度为 32，产品 CompID 可解码；`0x1801d7d90` 与 `0x1801d82e0` 建立产品侧和 Key 侧的摘要比较值，`0x1801d89f0` 计算时间边界。校验失败可以返回 `InvalidTrialKey`；时间窗口内返回 `Trial`，窗口外返回 `Expired`。产品身份本身不可解码也会先失败。

这条路径可用来诊断“声库已经被发现，为什么仍显示试用过期”。它不等于普通硬件绑定路径：本分支没有走下面普通分支的硬件候选枚举，不能把两条公式混用。本轮也不能把不同产品/上下文的试用窗口一律写成固定 14 天；日期辅助函数有多个条件分支。

### bundle：一个具体内置组件的例外

识别位于 `0x1801df360`。继续检查后发现，它不是一个可自由指定捆绑关系的通用机制：候选进入后先验证产品身份，再与内置固定身份比较。

将该比较常量按已有字节变换还原，得到固定 CompID `BLKEA452HDSKXLD7`。相等时候选内部值为 2，经 getter 转为公开结果 `NoError`；不相等时该候选为 `Undefined`。随后通过 VDM 公共 API 确认，此 ID 对应本机 DNN/AI 声库 **asa**。该 ID 的 codec 回环也通过，payload 为 `000018I0060000`。这里的固定 ID 是用于辨认例外的证据，不是新声库应复用的身份。

**实际用途是排除误解：** 安装项出现 bundle descriptor，不代表库拥有有效捆绑许可；其它 CompID 进入此分支也不会自动变为有效。自制库继续使用自己的独立产品身份，不能把这一例外当作通用发行配置。

### splice：附加信息与本地 lease 的另一条路径

识别后的关键段为 `0x1801deb03–0x1801def70`。其中调用 BCrypt SHA-256，将组合信息计算成摘要，再与另一项 64 字符十六进制值转换得到的 32 字节比较；后续进入 `0x1801edee0` 读取本地 `.lic` 并处理缺失、结构/身份和时间状态。

因此，上一轮发现 SHA-256 只能证明这个分支的摘要检查，不能据此把普通激活称为 SHA-256 验签。后续本轮 Splice 专项已确认独立的 Ed25519 文件验签，以及 `+0xf8/+0x100` 与 Ice 注册字段的对应；详见下节。这不等于普通激活使用同一方案，也不证明本机有对应签发私钥。

保留标记 `splice` 的候选分支与公共对象的 `SpliceResult` 字段也不能视为同义词。当前 Editor 的声库可用性读取普通 `Result`；独立 `SpliceResult` 的消费者及文件路径见上节。自制传统库研究中，这条路径可暂时放在普通候选之后。

#### Splice 专项：三道本地检查与实际公钥

2026-10-16 继续只追这条分支，记录见 [证据481](evidence/481_splice_local_validation.json)。可将声库 `splice` 候选的检查理解为三道关：

```text
组件附加信息与 IceValue 的 SHA-256 一致性
    → 按 IceProductName 找本地 .lic
    → 文件解析、产品身份/状态/时间检查
    → 用 DSE 内置公钥验证 Ed25519 签名
    → ValidLeaseFile / PaidOffLeaseFile，或对应失败结果
```

**字段已经实测对齐。** 通过 VDM 的只读 getter 与 asa 安装项比较，vtable `+0xf8` 返回值等于 `HKLM\SOFTWARE\VOCALOID6\Voice\Components\<asa CompID>` 的 `IceProductName`，长度15；`+0x100` 等于 `IceValue`，长度64且为十六进制文本。只保存相等关系与长度，没有输出两项值。前一道 SHA-256 使用前者、产品 CompID 的派生信息等组合输入，与后者转换出的32字节比较；所有组合辅助项的独立回环仍未完成。

**文件按附加产品标识寻找。** 声库循环在 `0x1801deee3` 把 `IceProductName` 传给解析器，`0x1801ed6c0` 将其作为文件名标识。因此此调用点的路径是 `%LOCALAPPDATA%\SpliceSettings\license\<IceProductName>.lic`，不能直接拿声库 CompID 当文件名。本机当前没有这个 license 目录，所以没有真实 lease 的解析/有效性实测。

**解析与验签分开。** `0x1801edee0` 处理文件结构、格式标识、传入产品标识与文件内身份的一致性、状态及起止时间；通过后，调用者才在 `0x1801ef7b0` 验证附加签名。该 wrapper 对签名文本和内置公钥文本作 Base64 解码，调用 `0x1801ec1d0(signature, message, length, publicKey)`。没有证据表明 `.lic` 内容解开就等于授权成功。

**这次找到的是实际验签公钥。** DSE 静态初始化 `0x180001fc0` 建立一个44字符 Base64 文本，解码为32字节；`0x1801ef7b0` 将它用于验签。`0x1801ec1d0` 的点运算、SHA-512 与签名长度检查符合 Ed25519；另以 [RFC 8032 §7.1](https://www.rfc-editor.org/rfc/rfc8032#section-7.1) 的空消息公开测试向量直接调用这份 DLL 的同一函数，结果如下：

| 原生验证输入 | 实测结果 |
| --- | --- |
| 原始公开签名、消息、公钥 | 接受 |
| 签名首字节翻转1位 | 拒绝 |
| 消息增加一个字节 | 拒绝 |
| 公钥首字节翻转1位 | 拒绝 |

此实验只验证密码学原语，使用公开向量，没有使用许可证载荷或尝试签发 `.lic`；未调用 DSE manager 初始化、未操作 Editor。**组件 descriptor 的32字符 `Key`、64字符 `IceValue`、DLL内32字节验签公钥是三种不同数据。** 本轮定位了公钥的来源，没有定位或证明本机存在对应的签发私钥。

声库候选调用点的结果映射现已明确：

| 检查结果 | 普通 `License.Result` |
| --- | --- |
| IceValue 长度或摘要不匹配 | `InvalidHash` |
| loader 未读到非空文件内容，解析状态1 | `MissingLeaseFile` |
| 解析状态2，即到期 | `ExpiredLeaseFile` |
| 其它非零解析状态，或签名失败 | `InvalidLeaseFile` |
| 解析状态0、签名通过、文件内类别字节为2 | `PaidOffLeaseFile` |
| 解析状态0、签名通过、其它被接受类别 | `ValidLeaseFile` |

解析器还区分未启用状态、开始时间超前、格式不符、身份不符、尺寸不足，以及到期时间超过当前时间90天等情况；它们在这个声库候选调用点大多收束成 `InvalidLeaseFile`。这些解析状态不是公共 `LicenseResult`，也不能把文件类别字节2误读为公开枚举 Trial。

**对自制声库的实际价值：** 这条路线现已能明确“缺哪一层”，并证实最终有独立的非对称签名门槛。可用于复核自有安装元数据、文件寻找路径、失效原因和原生验签行为；仅拥有 Key、IceValue 或知道公钥，不等于掌握可独立签发这种 lease 的条件。481实验未完成的摘要组合已在后续[计算交叉验证](119_dse_ordinary_and_splice_calculations.md)补齐，并与本机 asa 的 IceValue 完全一致；产品数据仍由原生资源 helper 读取。真实 lease 端到端及完整外层载荷仍未覆盖，没有扩大到服务器。

### 普通标识：最接近“本机授权数据怎样匹配”的问题

未命中三个保留字符串后，`0x1801df44b` 开始要求 K 长度为 **32 或 62**。先检查产品 CompID，再把 S 恢复为字符串，调用与组件 ID 相同的解码校验辅助函数：普通 S 必须通过 **16 字符 ID → 14 位 payload** 的 codec。无效 S 会形成 `InvalidSerialNumber`。

这里需要同时保留两个独立身份：C 是当前声库的 CompID，S 是候选授权标识。**共用编码格式不证明 S=C，也不证明两者各位具有相同业务语义。** 目前不能把 S 直接命名为购买序列号、机器码或另一产品 ID。

匹配阶段已确认：

1. `0x1801d83b0` 解码产品 C，把 payload 的索引 7 改成字符 `0`，再编码并变换。这个归一化位的业务意义尚未知，不能自行命名为版本位或变体位。
2. `0x1801d8030` 建立硬件候选列表：卷信息路径由 `0x1801d76b0 → 0x1801d7920` 得出，网卡地址由 `GetAdaptersInfo` 枚举。主循环逐个尝试候选；不能表述成必须同时精确匹配磁盘和全部网卡。
3. `0x1801df557–0x1801df6bb` 按 `T(S) || T(C0) || T(H) || P` 组合，再经逆变换 MD5 与附加公共上下文的外层 MD5，与 Key 侧比较。[119章](119_dse_ordinary_and_splice_calculations.md)验证摘要，[121章](121_dse_product_payload_and_owned_license_chain.md)后续闭合标准DBSe的P槽位生产与自制文件链：首part的`+0x1f8`为长度，按`i3/i1/i0`及后续`i2/i3/i1/i0`取低字节右转1位。默认四个`-1`会在已存在表项中形成空P；非空自有P也已从文件经原生产品表进入NoError。AI资源及其它根状态未由此实验泛化。
4. 匹配成功的 32 字符路径返回 `NoError`；62 字符路径先去掉索引3开始的30字符参与基础摘要，再处理日期：起始取声库 `Date`，到期取 Key 内嵌日期 ID，可能返回 `ValidExpiryKey` 或 `ExpiredKey`。日期 getter来源18/18对齐、日期 helper8项对照通过；后续483已在完整候选中验证有效、过期、未来开始及当天边界，并核对10次公开expiry值。**62 字符的不同路径属于 Key 的形式差异，不是第四个保留字符串。**

对手头工作的帮助是：可以把“组件身份无效”“授权标识无效”“硬件/摘要不匹配”“期限已过”分成不同诊断阶段，避免一看到不可用就重新改 DDI/DDB。也能指导后续只读检查器：输出标记、长度、codec 是否通过、失败阶段和最终结果即可，不必打印真实授权内容。

### 内部结果码不能直接当公开枚举

`LicenseImpl` vtable `+0x18` 指向 `0x1801d4860`，该 getter 会调用 `0x1801d4480` 转换内部值。本轮所见小整数范围内，非零值的映射可表示为 `7^n mod 17`，0 单独返回 0。这里只描述所见范围，不推广到任意整数或另一版本。

| 内部候选值 | getter 返回的公开结果 |
| ---: | --- |
| 2 | 15，`NoError` |
| 5 | 11，`InvalidSerialNumber` |
| 7 | 12，`InvalidComponent` |
| 9 | 10，`InvalidKey` |
| 10 | 2，`Trial` |
| 11 | 14，`ValidExpiryKey` |
| 12 | 13，`InvalidHash` |

这修正了“内存里看到 2 就是 Trial”的错误读法。分支判断必须先转换结果，再与托管 `LicenseResult` 对照。

### 当前资料能交付到哪一步

- 已能解释分支标签、局部来源、产品/硬件匹配入口及关键失败阶段；这些是制作、安装和诊断时可用的知识。
- 482/483完成摘要及95项完整候选验收；后续[121章](121_dse_product_payload_and_owned_license_chain.md)与[484](evidence/484_dse_owned_resource_license.json)将标准DBSe自制资源的P独立写入/解码，再通过原生文件加载、产品表生成及候选函数：20份资源、38次判定，NoError20/InvalidKey18。还只读对齐15传统库的70字节槽位来源。资源枚举与descriptor仍由进程内适配器提供，未修改原安装或验收Editor发现/渲染；Splice没有真实lease端到端。
- 480实验只初始化 VDM、调用公开 API 枚举类型/长度，没有初始化 DSE 或测试激活；后续482另在独立进程初始化 VDM/DSE并重建只读产品表快照。第一次给 native API 传正斜杠表达库路径返回13（`ExpDBPathNotFound`）；改为解析后的 Windows 反斜杠路径后返回0并成功枚举。目录存在本身不保证 native 路径解析成功。
- 没有启动、关闭或修改已运行的 Editor，没有写注册表、读取 `.lic` 内容、修改 Ghidra 项目或导出真实 Key/serial。文中的旧统计保留原日期。
- 自有完整候选及标准DBSe资源P链分别由483/484补齐；剩余问题是普通 S 的业务语义、归一化位的业务范围、其它根状态/AI资源、自制声库安装与宿主渲染。Splice正确摘要已进入MissingLeaseFile，仍缺真实lease端到端；服务器不作为本地研究前置条件。

本轮新的 **2026-10-16 VDM 枚举快照**如下；它只证明候选来源与形状，不证明激活结果。与下面2026-09-04的历史安装快照分开保存，不覆盖旧数字。

| 当前类型 | 声库数 | default 项 | bundle 项 | splice 项 | 普通项 |
| --- | ---: | ---: | ---: | ---: | ---: |
| DSE 传统声库 | 15 | 15 | 7 | 0 | 0 |
| DNN/AI 声库 | 3 | 3 | 3 | 1 | 0 |
| 合计 | 18 | 18 | 10 | 1 | 0 |

29项候选的 Key 都是32字符；default 的 S 长7，bundle/splice 的 S 长6。没有普通 S 或62字符 Key 的当前样本，因而它们的说明仍是静态代码结论。本机10项 bundle 候选只有一项的父组件等于固定目标 asa，这直接说明“有 bundle 标签”并不等于当前产品能通过该分支。没有在此实验读取真实 `.lic` 或输出 Key/普通 serial 内容。

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
