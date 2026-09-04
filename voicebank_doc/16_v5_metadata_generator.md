# V5 传统声库元数据生成器

## 目的与边界

`generate_v5_metadata.py` 把已经由 VDM 6.13.0.1 验证的 V5 组件规则收束为可重复工具。它只读取 JSON 和用户自己的 `VDM.dll`，输出 JSON manifest 与扩展名为 `.reg.txt` 的注册表审阅稿：

- 不访问或修改 Windows 注册表；
- 不复制商业声库数据；
- 不生成、伪造或安装许可证；
- 不保证候选 CompID 在其它机器或未来产品中全局唯一。

## 输入

示例见 `v5_metadata_spec.example.json`。主要字段：

| 字段 | 约束 |
| --- | --- |
| `component_payload` | 14 位 base-28 payload；第 9–14 位只能是数字 |
| `native_language` | `0..4`，必须等于 payload 第 4 位 |
| `component_name` | 非空组件全名，对应注册值 `Name` |
| `bank_name` | 非空显示名称，对应 `VoiceBank.Name` |
| `group_name` | 可为 `null`；VDM 运行时回退到 `BankName` |
| `singer_stem` | ASCII 字母、数字、`_`、`-`；同时决定 DDI/DDB 文件名及 DBSe 摘要 |
| `version` | `major/minor/revision` 三个非负 31-bit 整数 |
| `drp` | 严格 6 个 UTF-16 code unit；当前只确认长度门槛，示例值不是已证明的产品语义 |
| `date` | 严格 16 个 UTF-16 code unit；当前只确认长度门槛 |
| `path` | 绝对 Windows 基础目录；VDM 会再拼接 CompID |
| `default_style_id` | UUID 或 `null`；空值的有效回退值由 manifest 明示 |
| `reserved_component_ids` | 可选冲突集合；每项也必须能由同一 VDM codec 解码 |

示例 payload 和输出 ID 只用于结构验证，不是预留或正式分发身份。正式选择 payload 前必须检查已安装组件、团队分配记录和拟分发范围。

这里的 `component_name`、`bank_name` 和 `group_name` 都是组件显示元数据，不决定 Editor 的“默认声库”。6.13.0.1 把默认传统声库作为用户设置 `defaultVoiceCompID` 保存，运行时按 CompID 解析；组件 manifest 因而不会也不应生成“默认声库名称”。CompID、名称、支持语言和版本的运行时关系见 [VDM 运行时发现、组件 ID 与 DSE 配对加载](10_vdm_runtime_discovery.md#名称身份和默认声库是三个概念)。

## 使用

```powershell
python voicebank\tools\generate_v5_metadata.py `
  voicebank_doc\v5_metadata_spec.example.json `
  E:\VoicebankResearch\metadata
```

工具从本机 `C:\Program Files\VOCALOID6\Editor\VDM.dll` 读取 6.13.0.1 CompID 表；其它版本可用 `--vdm` 显式指定。版本布局不匹配时工具应失败，不会静默生成猜测 ID。

输出包括：

- `v5_metadata_manifest.json`：规范化字段、CompID/payload 回环结果、实际目录、同 stem 文件路径、DBSe digest、回退字段和安全声明；
- `v5_registry_review.reg.txt`：只供人工核对的最小 V5 注册视图，故意不使用 `.reg` 扩展名，且不包含 `Key`、`IceProductName` 或 `IceValue`。

DBSe 摘要使用已由 DSE loader 闭合的规则：

```text
MD5("K2ho" + UPPER(singer_stem) + "nF")
```

## 已验证与未解决项

端到端 smoke test 已验证：示例 payload 由脚本编码成 16 字符 CompID，再由同一 VDM 表解回原 payload；语言位、DBSe digest、注册路径和同 stem DDI/DDB 路径均一致。负向测试覆盖语言位冲突、DRP 长度错误、相对 Path 和保留 ID 冲突。

这解决的是 M3/M5 中“生成安装元数据”的离线部分。许可证仍是独立关卡：合法 CompID 和格式正确的注册项不会自动产生 DSE 可接受的 license result。已确认的对象、结果码和 Editor 门槛见 [DSE 许可证对象与编辑器可用性判定](17_dse_license_pipeline.md)，字段间的完整关系见 [声库身份、默认选择与许可证字段矩阵](26_voicebank_identity_and_license_fields.md)。
