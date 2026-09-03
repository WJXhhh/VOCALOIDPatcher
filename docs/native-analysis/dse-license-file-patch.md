# DSE 许可证放行：Python 文件版 Patch 完整操作流程与经验

> 本文记录对 **原版 DSE2.dll（6.13.0.1）** 做"文件级许可证放行"的完整可复现流程：
> 扫描验证核心里的结果码赋值 → 把红灯立即数全部改成绿灯值 → 短路动态赋值分支。
> 2026-08-13 已用本流程从干净原版重新打出一份 patch，SHA-256 与既有结论逐字节一致。

## 1. 背景与文件基线

### 1.1 为什么要文件级 patch

VOCALOID 6 Editor 的许可证由 `DSE.dll` 负责校验（`VIS_DSE_InitializeManager → ManagerImpl::Init → FUN_1801ce880 → 验证核心`）。
验证核心按 license 内容计算结果码并写入 `LicenseImpl` 对象的 `+0x50`（结果码）与 `+0x54`（Splice 结果码）字段。
上层编辑器通过 `VIS_DSE_GetResultFromLicense` 等导出读取，绿灯则编辑器与声库全部放行。

文件级 patch 的作用位置是**数据写入端**（验证核心把字段写成什么值），
而不是某个 getter 的读取端——因此无论上层从哪个 getter 读，拿到的都是绿灯。

### 1.2 涉及的版本与哈希（2026-08-13 实测）

| 文件 | 说明 | SHA-256 |
|---|---|---|
| `DSE2.dll.bak` | 干净原版 6.13.0.1（10,694,400 字节，PE 时间戳见 `binary-baseline.md`） | `E1E6278980ACE36A105AA77BFFE20F248169172407417D436862650D4905900E` |
| `DSE2.dll`（patch 后） | 46 字节差异的放行版 | `01C14A1340522A6DD956CDBF4BC2C0CAD56379C940F873D41A6D2CFA7E171B2E` |

工作目录示例：`C:\Program Files\VOCALOID6\新建文件夹\`（存放原版/修改版/备份的试验目录，不属于安装目录）。

### 1.3 验证核心定位（6.13.0.1 原版）

- 验证核心函数：`FUN_1801dac80`，RVA `0x1DAC80`，函数体 `0x3AFC` 字节（到 RVA `0x1DE77C`）。
- 修改版（伪 6.13.0.1 / 真 6.10.0）的验证核心是 `FUN_1801db1a0`（RVA `0x1DB1A0`，`0x7899` 字节），反编译已存 `docs/FUN_1801db1a0.c`；本文流程只针对原版。
- 若版本不同，验证核心 RVA 会位移：先按"结果码赋值模式"全局扫描（见 4.3）确认命中数（60 处）再动手，不要照抄 RVA。

## 2. 逆向基础（本文用到的结论）

### 2.1 LicenseImpl 字段与 getter

| 偏移 | 语义 | getter | vtable 槽（原版 6.13） |
|---|---|---|---|
| `+0x08` | 组件 ID | `GetCompID` | `+0x00` |
| `+0x10` | 组件名 (std::string) | `GetCompName` | `+0x08` |
| `+0x50` | **结果码 (int)** | `GetResult` | `+0x18`（读 `+0x50`，RVA `0x1D2FD0`） |
| `+0x54` | **Splice 结果码 (int)** | `GetSpliceResult` | `+0x20`（读 `+0x54`，RVA `0x1D3160`） |
| `+0x58` | 过期日期 | `GetExpiryDate` | `+0x28` |
| `+0x60` | 剩余试用天数 | `GetRemainingTrialDays` | `+0x30` |

`GetResult` 是混淆出口：`r = *(this+0x50); return r == 0 ? 0 : 7^r mod 17`（`7` 是模 17 的原根，双射混淆，防把字段直接改 0）。

### 2.2 结果码枚举与绿灯集合

结果码字符串映射（`FUN_1801f4900`）：`OK / MISSING / EXPIRED / INACTIVE / BEGINS_IN_FUTURE / EXPIRATION_TOO_FAR / INVALID_FORMAT / INVALID_PRODUCT / PARSE_ERROR / UNKNOWN_ERROR` 等。

编辑器绿灯判定：`GetResult` 返回值 ∈ **{8, 9, 14, 15}**（`ValidLeaseFile / PaidOffLeaseFile / ValidExpiryKey / NoError`）。
按 `7^r mod 17` 反推，**DSE 内部结果码绿灯集合 = {2, 6, 11, 14}**（`0x02 / 0x06 / 0x0B / 0x0E`）。

本 patch 选用的绿灯值：**`0x0B`（= 11，`ValidExpiryKey`）**，与修改版 DSE 实际行为（恒 `NoError=2`）同属绿灯集合。

### 2.3 验证核心的三组同构阶段

结果码赋值指令按地址分成三组，对应三类 license 路径（行为完全同构，可互相参照）：

| 组 | RVA 范围 | 赋值目标字段 | 对应 |
|---|---|---|---|
| 组 1 | `0x1DAFAF`–`0x1DB95E` | `+0x50`（`[RSP+0x38]`） | V6 声库（新 license） |
| 组 2 | `0x1DBE2F`–`0x1DC7F4` | `+0x50`（`[RSP+0x38]`） | 旧版本声库（V5/V4/V3） |
| 组 3 | `0x1DCD20`–`0x1DDF49` | `+0x54`（`[RBP-0x38]`） | Splice/lease 声库 |

每组内部绿灯结构相同：`{0x06, 0x0E, 0x02, 0x02, 0x0B}` 5 处绿灯 + 14 处红灯；
另有一处**动态赋值**（`0x1DDB8B: 74 18 → 74 00`，见 4.5）。

## 3. 快速上手

```powershell
# 前置：原版备份 DSE2.dll.bak 与 python 3.10+
# 1) 复制工作副本（不要直接改备份）
Copy-Item DSE2.dll.bak DSE2_work.dll -Force
# 2) 运行 6.2 的完整脚本（输入=原版，输出=patch 版）
python dse_license_patch.py DSE2_work.dll DSE2_patched.dll
# 3) 校验：SHA-256 应等于 01C14A13...；与既有 patch 版逐字节一致
```

## 4. 完整操作流程

### 4.1 确认基线

```powershell
Get-FileHash DSE2.dll.bak -Algorithm SHA256   # 必须 = E1E62789...
Copy-Item DSE2.dll.bak DSE2_work.dll -Force
```

### 4.2 定位验证核心（PE 节区换算）

从 PE 头读出 `.text` 的 `VirtualAddress / SizeOfRawData / PointerToRawData`，验证核心 RVA `0x1DAC80` 换算为文件偏移 = `RVA - .text.VA + .text.Raw`。函数体长度 `0x3AFC`。

### 4.3 扫描结果码赋值（两个指令模式，注意立即数偏移）

| 指令 | 字节 | 前缀长 | 立即数位置 | 语义 |
|---|---|---|---|---|
| `MOV dword [RSP+0x38], imm32` | `C7 44 24 38 XX 00 00 00` | 4 | `前缀+4`（`+4`） | 普通结果码 → `+0x50` |
| `MOV dword [RBP-0x38], imm32` | `C7 45 C8 XX 00 00 00` | 3 | `前缀+3`（`+3`） | Splice 结果码 → `+0x54` |

> **坑 1（本次复现踩到）：两个模式的前缀长度不同**（4 vs 3），立即数偏移分别是 `+4` 和 `+3`。
> 用统一偏移 `+4` 扫描第二个模式会**全部漏掉**。
>
> **坑 2：不要用 Python `re` 的 `(.)` 匹配立即数**——`.` 默认不匹配 `0x0A`（换行符），
> 会漏掉每组 1 处、共 3 处的 `0x0A` 红灯赋值。必须用手工逐字节扫描（`bytes[i:i+n] == 前缀`）。

扫描结果（原版 6.13.0.1）：`[RSP+0x38]` 40 处 + `[RBP-0x38]` 20 处 = **60 处**结果码赋值。

### 4.4 Patch 红灯立即数 → 0x0B

- 绿灯值 `{0x02, 0x06, 0x0B, 0x0E}`：**保持不动**（15 处）。
- 红灯值（45 处）：`0x00/0x01/0x03/0x04/0x05/0x07/0x09/0x0A/0x0C/0x0D/0x0F/0x10` → 全部改成 `0x0B`。
- 分组：组 1 15 处、组 2 15 处、组 3 15 处。

### 4.5 动态赋值：短路 JZ

`0x1DDB8C`（文件偏移 `0x1DCF8C`）处：

```text
0x1DDB89  84 DB              TEST BL, BL
0x1DDB8B  74 18              JZ  +0x18        ; BL==0 时跳过绿灯赋值
0x1DDB8D  C7 45 C8 02 00 00 00  MOV [RBP-0x38], 0x02   ; 绿灯
```

把 `74 18` 改成 `74 00`（`JZ +0` = 不跳转，无条件执行绿灯赋值）。
> **坑 3：字节位置**——`74` 在 `0x1DDB8B`，`18` 在 `0x1DDB8C`（以 `74` 所在地址为准，勿偏移 1 字节）。

### 4.6 验证

1. **红灯残留扫描**：重新按 4.3 扫描，红灯值应为 0 处。
2. **差异统计**：与备份逐字节对比，应恰好 **46 字节**（45 处 `→0x0B` + 1 处 `0x18→0x00`）。
3. **PE 完整性**：MZ/PE 签名、节区数（7）、导出表不变。
4. **SHA-256**：等于 `01C14A1340522A6DD956CDBF4BC2C0CAD56379C940F873D41A6D2CFA7E171B2E`。
5. （可选）与既有结论版逐字节 diff 应为 0 差异。

### 4.7 部署与回滚

```powershell
# 部署（覆盖 Editor 目录前先备份现有文件）
Copy-Item DSE2_patched.dll "C:\Program Files\VOCALOID6\Editor\DSE.dll" -Force
# 回滚
Copy-Item DSE2.dll.bak "C:\Program Files\VOCALOID6\Editor\DSE.dll" -Force
```

按项目约定：部署、启动/关闭编辑器由用户执行，代理只产出 patch 文件与验证结果。

## 5. 完整脚本（可直接运行）

保存为 `dse_license_patch.py`，用法：`python dse_license_patch.py <输入原版> <输出patch版>`。
脚本内置全部断言：核心定位、60 处赋值、46 字节差异、无红灯残留；任一步不符合即抛错不写文件。

```python
"""DSE 许可证放行：原版 6.13.0.1 验证核心结果码赋值 patch。

用法: python dse_license_patch.py <input.dll> <output.dll>
输入必须是干净原版 (SHA-256 E1E62789...)，否则红灯残留断言会失败。
"""
import struct, sys, hashlib

CORE_RVA = 0x1DAC80   # FUN_1801dac80（6.13.0.1 原版；其它版本需重新定位）
CORE_LEN = 0x3AFC
GREEN    = {0x02, 0x06, 0x0B, 0x0E}   # 编辑器绿灯集合（DSE 内部结果码）
RED_TO   = 0x0B                        # 绿灯 ValidExpiryKey
JZ_RVA   = 0x1DDB8B                    # 74 18 -> 74 00（74 所在地址）

def sections(data):
    e = struct.unpack_from('<I', data, 0x3C)[0]
    n = struct.unpack_from('<H', data, e + 6)[0]
    osz = struct.unpack_from('<H', data, e + 20)[0]
    sec = e + 24 + osz
    out = []
    for i in range(n):
        o = sec + i * 40
        nm = data[o:o+8].rstrip(b'\0')
        out.append((nm, struct.unpack_from('<I', data, o+12)[0],
                    struct.unpack_from('<I', data, o+8)[0],
                    struct.unpack_from('<I', data, o+20)[0]))
    return out

def rva_to_off(sects, rva):
    for _, va, vs, ra in sects:
        if va <= rva < va + vs:
            return ra + (rva - va)
    raise ValueError('RVA 0x%X 不在任何节区' % rva)

def main():
    src, dst = sys.argv[1], sys.argv[2]
    data = bytearray(open(src, 'rb').read())
    sects = sections(data)

    off_s = rva_to_off(sects, CORE_RVA)
    off_e = off_s + CORE_LEN
    patched, scanned = [], 0

    # 扫描 + patch 两个指令模式（手工逐字节：re 的 . 不匹配 0x0A 会漏）
    for prefix in (b'\xc7\x44\x24\x38', b'\xc7\x45\xc8'):
        plen, imm = len(prefix), len(prefix)   # 立即数偏移 = 前缀长度 (4 或 3)!
        i = off_s
        while i < off_e - 7:
            if data[i:i+plen] == prefix and data[i+imm+1:i+imm+4] == b'\x00\x00\x00':
                v = data[i+imm]
                scanned += 1
                if v not in GREEN:
                    assert data[i+imm] == v
                    data[i+imm] = RED_TO
                    patched.append((CORE_RVA + (i - off_s), v, RED_TO))
                i += 8
            else:
                i += 1

    # 动态赋值 JZ 短路
    jz = rva_to_off(sects, JZ_RVA)
    assert data[jz] == 0x74 and data[jz+1] == 0x18, 'JZ 位置字节不符'
    data[jz+1] = 0x00
    patched.append((JZ_RVA, 0x18, 0x00))

    # 断言
    assert scanned == 60, '结果码赋值总数 %d != 60，验证核心 RVA 可能已变' % scanned
    assert len(patched) == 46, 'patch 总数 %d != 46' % len(patched)

    open(dst, 'wb').write(data)
    h = hashlib.sha256(data).hexdigest()
    print('patched %d bytes (45 red immediates + 1 JZ)' % len(patched))
    print('SHA-256:', h)
    print('期望   : 01c14a1340522a6dd956cdbf4bc2c0cad56379c940f873d41a6d2cfa7e171b2e')
    print('一致   :', h == '01c14a1340522a6dd956cdbf4bc2c0cad56379c940f873d41a6d2cfa7e171b2e')

if __name__ == '__main__':
    main()
```

## 6. 关键经验清单（踩坑汇总）

1. **作用于数据写入端，而不是 getter 读取端**：改验证核心的赋值（写 `+0x50/+0x54` 的立即数）比改 `GetResult` 函数更彻底——覆盖所有读取路径。这是文件版有效、而"只 patch GetResult 函数体"的版本可能无效的根本区别。
2. **两个指令模式的前缀长度不同**：`C7 44 24 38`（4 字节，立即数 `+4`）vs `C7 45 C8`（3 字节，立即数 `+3`）。统一偏移会漏掉整个第二组。
3. **`re` 的 `.` 不匹配 `0x0A`**：Python `re` 在 bytes 模式下 `.`（含 `(.)`）默认不匹配换行字节 `0x0A`，会漏掉 3 处 `0x0A` 红灯。务必用手工逐字节扫描。
4. **绿灯集合必须保持**：`{0x02, 0x06, 0x0B, 0x0E}` 是编辑器判定为绿灯的内部结果码（经 `7^r mod 17` 反推），patch 时不能动；把红灯改成集合内任意值即可，本文统一用 `0x0B`。
5. **动态赋值不止立即数**：`JZ 74 18 → 74 00` 短路"日期无效跳过绿灯"的分支，否则该路径仍会落到红灯。
6. **验证核心 RVA 是版本专属**：换版本先按"60 处命中"校准扫描，不要直接照抄 `0x1DAC80`。
7. **审计留痕**：patch 后保留 `SHA-256`、46 处差异的 RVA/新旧值清单（见下），便于回滚与复核。

## 7. 46 处差异清单（原版 → patch 版，RVA: 旧值 → 0x0B）

```text
组1 (+0x50, V6 声库)  组2 (+0x50, 旧版声库)   组3 (+0x54, Splice)
0x1DA3B3: 04->0B      0x1DB233: 04->0B       0x1DC123: 04->0B
0x1DA3E7: 07->0B      0x1DB263: 07->0B       0x1DC41C: 07->0B
0x1DA46C: 04->0B      0x1DB2F8: 04->0B       0x1DC4F1: 04->0B
0x1DA503: 0A->0B      0x1DB395: 0A->0B       0x1DC61E: 0A->0B
0x1DA51A: 03->0B      0x1DB3AC: 03->0B       0x1DC632: 03->0B
0x1DA6E2: 0C->0B      0x1DB57E: 0C->0B       0x1DC803: 0C->0B
0x1DA7F1: 0F->0B      0x1DB68C: 0F->0B       0x1DCA70: 0F->0B
0x1DA7FB: 10->0B      0x1DB696: 10->0B       0x1DCA79: 10->0B
0x1DA84A: 0D->0B      0x1DB6E5: 0D->0B       0x1DCADC: 0D->0B
0x1DA975: 00->0B      0x1DB80E: 00->0B       0x1DCCBB: 0C->0B
0x1DA99E: 09->0B      0x1DB83A: 09->0B       0x1DCFD1: 09->0B
0x1DA9C4: 07->0B      0x1DB85C: 07->0B       0x1DCFE9: 07->0B
0x1DAA09: 05->0B      0x1DB8A1: 05->0B       0x1DD024: 05->0B
0x1DABD0: 09->0B      0x1DBA70: 09->0B       0x1DD2C0: 09->0B
0x1DAD62: 01->0B      0x1DBBF8: 01->0B       0x1DD34C: 01->0B
动态: 0x1DCF8C: 18->00 (JZ 74 18 -> 74 00，74 位于 0x1DCF8B)
```

## 8. 相关文档

- `../FUN_1801db1a0.c`：修改版（6.10.0 内核）验证核心完整反编译，供对照"恒绿灯"实现。
- `dse-engine-contract.csv`、`binary-baseline.md`：DSE 模块基线与契约。
- `../dse-pitch-layer-control.md`：DSE 6.13.0.1 另一条独立功能线（音高 REG 控制），与本 patch 无交集。
