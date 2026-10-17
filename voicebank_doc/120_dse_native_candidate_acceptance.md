# 普通授权计算的原生候选分支验收

**后续自制资源链已补齐：** [121章](121_dse_product_payload_and_owned_license_chain.md)及[484](evidence/484_dse_owned_resource_license.json)不再手填产品表P，改为独立写入自制DDI槽位，经过原生文件加载/提取/建表，再取得NoError。20份资源、38次判定通过，另对齐15传统库槽位。以下是483阶段的候选分支验收范围；其P生产待办在标准DBSe路径已由后续实验解决，宿主安装/渲染仍分开验收。

2026-10-16，接续[119章计算交叉验证](119_dse_ordinary_and_splice_calculations.md)。用户授权用自建标识或现成声库进行验收；本轮运行 DSE 的完整原生候选处理函数，结果通过公开 getter 读取。

**普通32字符 Key 的计算关系已通过原生候选分支验收，返回 `NoError (15)`。62字符有效/到期日期也已进入完整分支验证。** 这比482的摘要 helper 对照多覆盖了候选读取、字段校验、硬件遍历、产品查找、候选排序及 `License` 对象结果生成。

证据：[483](evidence/483_dse_candidate_acceptance.json)。工具：[verify_dse_candidate_acceptance.py](../voicebank/tools/verify_dse_candidate_acceptance.py)。DSE/VDM版本与精确SHA-256检查沿用482，Ghidra当前目标已再次核对为同一 DSE 6.13.1.1。

## 验收方法

原安装没有普通 SerialNumber descriptor。为避免改安装或授权配置，脚本在独立进程构造最小 VDM 接口适配对象，向**未修改的** `0x1801dc510` 提供测试输入：

1. 自建的16字符 CompID、SerialNumber、日期 ID；它们通过既有 codec 编码及校验，不涉及给 CompID 做公私钥签名。
2. descriptor 的字符串 getter、声库名称/版本/Date/CompID getter、数据库枚举接口。
3. 产品数据表与候选列表。自有测试用自有 P；现成声库测试用本机原生资源加载后得到的 P 快照。
4. 用已恢复公式在内存计算 Key，不向注册表、现有 VDM 对象或安装文件写回。

原生函数自己完成分支判断与结果写入；适配对象没有提供“授权成功”字段。最终通过 `VIS_DSE_GetResultFromLicense` 取得公开枚举，同时核对生成对象的 CompID、名称。候选函数前后 `0x3b60` 字节范围的内存SHA-256一致，适配回调无错误；具体哈希和回调计数随483保存。

这项验收是**原生候选判定验收**。适配对象替代了输入枚举层，未验证 stock Editor 从注册表发现自制库、建立 Part 或最终渲染。

## 95项结果

| 输入/范围 | 数量 | 实际结果 |
| --- | ---: | --- |
| 自有数据，传统DSE与DNN两条路径 | 58 | 全部符合预期，详见下表 |
| 本机18库资源快照 + 合成普通候选 | 18 | 全部 `NoError (15)` |
| 同一18库，仅改 Key 一个字符 | 18 | 全部 `InvalidKey (10)` |
| asa原有Ice字段与资源快照 + 内存Splice候选 | 1 | 摘要匹配后 `MissingLeaseFile (1)` |
| 合计 | 95 | 全部通过断言 |

18库为15个传统库、3个DNN库，每库名称和CompID已列在483。这里的18个成功结果来自**资源快照与合成内存候选**；不是声称原安装的 descriptor 已有效或发生了真实激活。没有保存计算出的 Key、硬件标识、P 或 Ice 字段内容。

58项自有测试在传统/DNN两种枚举路径中各跑29项，公开结果分布为：

| 结果 | 数量 | 对应场景 |
| --- | ---: | --- |
| `NoError` | 18 | 本机6个硬件候选分别命中；非零归一化位；错误/正确候选的两种排列 |
| `InvalidKey` | 14 | 有效但不同S、错误Key长度、Key改单字、错误硬件、P变化、P表项缺失、expiry基础摘要错误 |
| `InvalidSerialNumber` | 2 | S不通过codec |
| `InvalidComponent` | 2 | C不通过codec |
| `Undefined` | 2 | 零候选 |
| `ValidExpiryKey` | 6 | 将来到期、今天到期、今天开始且今天到期 |
| `ExpiredKey` | 4 | 已过期、起始日期在将来 |
| `MissingLeaseFile` | 2 | 自有Splice附加摘要正确，独立测试文件名没有lease |
| `InvalidHash` | 8 | Splice摘要改单字、产品附加名改变、P改变、摘要长度不符 |

本机硬件列表有1个卷候选及5个网卡候选；每个都单独用于计算 Key，并在原生循环中取得 `NoError`，支持“任一候选匹配即可”的结论。它们的实际值未输出。

正确候选放在错误候选之前或之后都能得到 `NoError`，说明这次经过了候选选择流程，不能把结果解释为“直接取输入列表第一项”。

## 62字符期限的补充结论

两条路径均验证了：

- 摘要正确、到期在将来 → `ValidExpiryKey (14)`；
- 摘要正确、到期在过去 → `ExpiredKey (7)`；
- 摘要正确、声库 `Date` 在将来 → `ExpiredKey (7)`；
- 今天到期，以及今天开始且今天到期 → `ValidExpiryKey (14)`；
- 基础摘要错误，即使日期有效 → `InvalidKey (10)`。

10个进入日期判定的测试还核对了公开 `VIS_DSE_GetExpiryDateFromLicense` 返回值，与独立计算的本地 `_mktime64` 日期一致。结合 `0x1801dc070` 的本地午夜归零逻辑，当前比较采用本地当天的日期基准；不能把“到期日午夜”误解为到期当天一开始就无效。

这里只替换传给适配对象的声库 `Date` 字符串，没有修改真实声库日期或系统时间。

## Splice 验收到哪一关

自有数据在传统/DNN两条路径均表现为：

```text
正确Ice摘要 + 缺少lease → MissingLeaseFile
错误Ice摘要            → InvalidHash
```

现成 asa 的原有 `IceProductName/IceValue` 与产品资源也完成了同样的完整候选调用：独立摘要相等，原生返回 `MissingLeaseFile`。这是“附加摘要检查通过、继续进入文件支路”的正向证据，不只是看见 SHA-256 调用。

本机没有相应 `.lic`，所以本轮不宣称 `ValidLeaseFile` 或 `PaidOffLeaseFile`。CompID 的可生成编码、普通Key的MD5关系，与Splice文件的Ed25519签名是不同机制；自建一个公私钥对不能使原本固定验签公钥接受新签名。本轮没有替换公钥或生成lease。

## 复现

```powershell
# 自有数据：完整原生候选函数，覆盖DSE/DNN两条路径
python voicebank/tools/verify_dse_candidate_acceptance.py

# 另加入现成声库资源快照，以及本机asa的Splice摘要支路
python voicebank/tools/verify_dse_candidate_acceptance.py --installed --report voicebank_doc/evidence/483_dse_candidate_acceptance.json
```

脚本继承计算工具的64位Windows、精确DLL哈希和禁止 `-O` 的保护。`--installed` 初始化的是本进程VDM/DSE，不会启动、停止或注入Editor。所有适配对象、合成候选、计算Key、临时产品表只在进程内存在；每次调用后由原生析构清理生成许可证对象。

当前已验收：普通摘要到公开许可结果的原生完整链路，以及Splice摘要通过后的缺文件结果。仍单独留待验证：P的全部独立生产、自制声库实际安装发现及Editor渲染、真实Splice lease签发/验签端到端。服务器不在本轮范围内。

交付检查：候选验收脚本退出0，95项全部通过；Python语法、UTF-8、相对链接、证据JSON和定向diff检查通过。整个解决方案 `dotnet build VOCALOIDPatcher.sln -c Debug --no-restore` 成功，0警告、0错误。没有部署补丁或进行Editor宿主操作。
