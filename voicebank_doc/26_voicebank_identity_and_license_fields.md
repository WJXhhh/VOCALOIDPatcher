# 声库身份、默认选择与许可证字段矩阵

## 结论

传统 V5 声库没有一段同时承载“识别码、显示名称、语言、版本、默认选择和授权”的总认证字符串。VOCALOID 6.13.0.1 把它们分在不同层：

| 概念 | 实际字段/位置 | 作用 | 能否产生授权 |
| --- | --- | --- | :---: |
| 组件身份 | 16 字符 CompID，注册表组件子键名 | 枚举、工程引用、默认选择、license 匹配 | 否 |
| 原生语言 | CompID 解出的 14 位 payload 第 4 位 | VDM `NativeLangID/LangIDs`、G2PA 语言 | 否 |
| 组件全名 | 注册 `Name` | VDM `ComponentName` | 否 |
| 声库显示名 | 注册 `BankName` | VDM `VoiceBank.Name`、DSE License `CompName`、UI | 否 |
| 版本 | `Version\Major/Minor/Revision` | VDM/DSE License 版本与兼容性门槛 | 否 |
| 用户默认声库 | 用户设置 `defaultVoiceCompID` | 按 CompID 选择默认 VoiceBank | 否 |
| DDI stem 校验 | DBSe 中的 32 字符 MD5 文本 | 检查 `.ddi/.ddb` stem 对应 | 否 |
| 候选 license 描述 | VDM descriptor 的 key/serial | DSE 验证输入 | 否，非空也不够 |
| 最终授权判定 | DSE `License.Result` | Editor 是否接受该 VoiceBank | 是，且只接受五类结果 |

所以回答“自制声库需要哪些认证信息”时，必须先问是哪一层。CompID 和 DBSe digest 都能由已恢复的格式规则合法生成，但它们只是结构身份；stock Editor 所需的最终产品授权不能由这两者推出。

## CompID 的确切语义

现代传统库的注册子键名必须是 16 个字符并通过 VDM 解码。当前 6.13.0.1 codec 把它还原为 14 位 base-28 payload：

```text
encoded CompID  = 14 个扰动后的字符 + 2 个 checksum 选择字符
decoded payload = 14 位；第 9–14 位只允许十进制数字
native language = payload[3]
```

两位末尾字符参与的是可逆表变换和字节和校验，不是密码学许可证签名。`compid_codec.py` 能从用户自己的 `VDM.dll` 读取表并双向回环，说明格式有效 CompID 可以离线构造；这不等于 Yamaha 已为该 ID 分配产品或签发许可证。

七个中文 V5 库的实测为：

| CompID | 解码 payload | 语言位 |
| --- | --- | ---: |
| `BD79E492NWWK3DDF` | `00L415D0050000` | 4 |
| `BL8CEAM5N4XN3LFK` | `00L415E0050000` | 4 |
| `BMA8DBBZM5ZH2MDE` | `00L415G0050000` | 4 |
| `BMBNDB8EM5222MK3` | `00L415B0050000` | 4 |
| `BN69LCH2W6TK8NEF` | `00L415F0050000` | 4 |
| `BP8CDDH5M7XN2PED` | `00L415C0050000` | 4 |
| `BY98KLLZTDYH7YEE` | `00L415A0050000` | 4 |

它们只在 payload 的一个产品位上不同，不能据此推导 Yamaha 的正式分配规则。新研究库可生成不与本机冲突的结构测试 ID，但正式发行前仍需获得合法的产品身份/签发安排。

## 名称和“默认声库”

两个容易混淆的名称来自不同注册值：

```text
Name     -> VDM VoiceBank.ComponentName
BankName -> VDM VoiceBank.Name
```

七个中文库的典型例子：

```text
ComponentName = VOCALOID Luo_Tianyi_Ning
VoiceBank.Name = Luo_Tianyi_Ning
```

增强后的只读 `license_harness` 对 41 个 Voice license 验证：

```text
DSE License.CompName == VDM VoiceBank.Name   41 / 41
DSE License.CompName == ComponentName        不是该映射
```

因此自建元数据需要分别填写组件全名和声库显示名；不能只留一个“默认名称”字符串。

Editor 的默认传统声库属于用户设置：

```text
UserSettings.defaultVoiceCompID = <16-char CompID>
```

启动时先按这个 CompID 查找；找不到时，从可用语言列表选择第一个 VoiceBank，并把实际 CompID 保存回用户设置，再调用 VDM `SetDefault()`。新 Part 还可能优先继承前一个 Part 的 voice-bank ID。也就是说：

- 默认选择键是 CompID，不是 `BankName`；
- 组件注册项不应写一个“默认声库名称”；
- 改显示名不会自动改变用户已保存的默认 CompID；
- 元数据生成器不应修改真实用户设置。

## 支持语言

6.13.0.1 的语言 ID 为：

| ID | VSM | G2PA |
| ---: | --- | --- |
| 0 | Japanese | JPN |
| 1 | English | ENG |
| 2 | Korean | KOR |
| 3 | Spanish | ESP |
| 4 | Chinese | CHS |

传统 V5 路径不是从一个可自由填写的 `Languages` 字符串列表读取语言。VDM 先解码 CompID，产生 `NativeLangID` 和 `LangIDs`。本机 29 个传统库的只读结果只有：

```text
11 个: NativeLangID=0, LangIDs=[0]
18 个: NativeLangID=4, LangIDs=[4]
```

七个目标中文库的 payload 语言位、`NativeLangID` 与 `LangIDs[0]` 三者全部为 4。DSE License 公共对象本身没有 language 字段；许可证通过 CompID 关联到 VDM voice bank，Editor 再从 VDM 对象取得语言供音符与 G2PA 使用。

因此生成中文传统库元数据时，至少要满足：

```text
component_payload[3] == native_language == 4
```

但“语言字段正确”只保证路由到 CHS G2PA/音符语义，不保证 PHDC/STA/ART 真有完整中文覆盖；后者仍由 62 音素、38 STA 和 2,556 ART 图验证。

## 版本号

V5 注册表必须提供三个非负 DWORD：

```text
Version\Major
Version\Minor
Version\Revision
```

VDM 将三元组暴露给托管层，同时给出：

- `IsSynthesizableVersion`；
- `IsVersionTooOld`；
- `IsVersionTooNew`。

DSE License 对象也保存一份版本。当前 41 个 Voice license 的 DSE/VDM 版本 41/41 完全相等。七个中文 V5 库全部为 `5.0.0`，在 6.13.0.1 上 `IsSynthesizableVersion=true`。

这只证明当前已安装产品的对象传递和兼容结果。还没有证据证明：

- 任意三元组都能通过 VDM；
- 修改注册版本不会影响 license validation；
- 新产品应自行选择某个未分配版本；
- `5.0.0` 本身能够为新 CompID 产生授权。

实际构建时可以把 `5.0.0` 作为已知可合成的 V5 格式候选，但必须把“字段可解析”“版本可合成”“许可证有效”作为三个独立检查结果。

## DBSe `authenticated=1` 不是产品授权

DSE 的 `.ddi` loader 会检查 PHDC 后的 DBSe 名称摘要。V5 分支已闭合为：

```text
MD5("K2ho" + UPPER(stem) + "nF")
```

最小自有 DDI 正确写入该块后，原生 loader 报 `root.authenticated=1`。这个字段只证明 DDI 内摘要与文件 stem 满足 loader 的结构规则：

- 它不包含 CompID；
- 它不读取 VDM license descriptor；
- 它不产生 DSE `License.Result`；
- 它不能让 stock Editor 接受无授权组件。

因此文档和构建报告应把它命名为“DBSe/stem authentication”或“DDI loader authentication”，不要简称成“声库许可证认证成功”。

## 最终许可证门槛

VDM descriptor 只提供候选 key/serial。DSE 初始化后生成的 `License.Result` 才是 Editor 使用的结果。Editor 接受：

```text
Trial
ValidLeaseFile
PaidOffLeaseFile
ValidExpiryKey
NoError
```

当前机器有 57 个传统库 descriptor，key/serial 均非空；29 个传统库的结果却是：

```text
Expired            7
InvalidTrialKey    1
InvalidKey        21
有效                0
```

这直接否定了“只要复制/填一个长 Key 或 CompID 就能认证”的假设。研究工具不会输出 key/serial 值，也不会实现签名、lease 文件生成或 Editor 绕过。

## 官方合法路径

Yamaha 2026-02-20 的公开说明把每个产品 serial code 视为终端用户 license，用户通过 Authorizer 向 Yamaha 服务器取得保存在该 PC/OS 环境的 authorization info；Voicebank 的未授权宽限期为 14 天。2026-04-02 的 Authorizer 1.0.2 仍明确管理 V3/V4/V5/V6 Voicebanks：

- [VOCALOID Product License Management](https://www.vocaloid.com/en/learn/ln6110/)
- [VOCALOID Authorizer](https://www.vocaloid.com/en/support/download/vocaloid_authorizer/)

对于新第三方产品，官方支持政策仍要求联系与 Yamaha 签有许可协议的 partner company，并把考虑 VOCALOID business 的主体导向企业咨询：

- [VOCALOID support policy](https://www.vocaloid.com/en/support/inquiry/support_policy_products/)
- [Yamaha corporate VOCALOID inquiry](https://inquiry.yamaha.com/contact/?act=39&lcl=en_WW)

此外，Yamaha 与 CAMPFIRE 自 2025-11-25 起提供审查制 `VOCALOID FAN-ding`。公开说明覆盖筹资、录音、voicebank 制作、发行和宣传，并明确申请不保证获选：

- [Yamaha 的 FAN-ding 新闻稿](https://www.yamaha.com/ja/news_release/2025/25112501/)
- [VOCALOID FAN-ding 说明与申请入口](https://camp-fire.jp/highlights/vocaloid-fan-ding)

这是目前个人/创作者也可申请的官方项目化入口，但不是技术自助签发服务。页面没有说明会接收申请人自行构建的 `.ddi/.ddb`，也没有确认会为新的传统 V5/DSE 产品分配身份和签发许可证；公开描述反而是由 Yamaha 专业团队参与录音和 voicebank 制作。

公开页面没有个人自助分配 CompID、生成 product serial、签名 key 或签发 voicebank 的 API。可执行结论是：

1. 本地格式/声学研究可以继续使用独立研究 CompID、自有录音与只读 harness；
2. stock Editor 中的合法授权与对外发行必须另行向 Yamaha/合法 partner 确认；
3. 不应把许可证绕过写入自训构建器；
4. 在获得正式答复前，manifest 必须保留 `creates_license=false` 和 `license_status=unresolved`。

## 仍待确认

1. Yamaha 当前是否接受新的第三方传统 DSE 声库，而不只接受 V6 AI 产品。
2. 正式 CompID、产品 serial、签名 key 和 QA/发行包分别由哪一方生成。
3. 合作主体、SDK、录音/质量验收、费用、地区和售后责任要求。
   - FAN-ding 说明称申请/获选时没有一律前置费用，成功众筹后从筹资中支付制作等费用；具体目标额和日程逐案协商。这不能替代传统库项目的书面报价和许可条款。
4. 新研究 CompID 在隔离注册后，stock Editor 具体停在列表显示、Part 建立还是渲染入口；该实验必须保持可撤销且不能覆盖商业组件。
5. 版本三元组是否参与许可证签名/产品身份约束；现有证据只确认它被复制进 DSE License 并独立接受兼容性检查。
