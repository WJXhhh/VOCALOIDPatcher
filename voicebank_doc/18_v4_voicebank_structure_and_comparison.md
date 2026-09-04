# VOCALOID4 传统声库结构、注册表与 V3/V4/V5 对比

## 1. 概述与核心结论

通过 Ghidra 逆向 `VDM2.dll` / `DSE.dll` 以及对本机安装的 VOCALOID3、VOCALOID4（洛天依 V4 萌、星尘、初音未来 V4X / V4C、乐正龙牙淳/雅等）和 VOCALOID5 声库实测，关于 V4 传统声库得出以下确定性结论：

1. **引擎层（DDI/DDB）高度统一**：VOCALOID4 与 VOCALOID5 在物理容器、声学帧模型（FRM2）与音频聚合（SND）上完全同构。VOCALOID6 的 DSE 加载器（`DSE5::CDBSinger::Load`，`FUN_18010d490`）能直接反序列化并无缝加载 V4 的 `.ddi` 和 `.ddb`。
2. **Growl（VQM）是 V3 与 V4 的分水岭**：V3 声库物理上不存在 `VQM ` 树与 `0x00000000000e22b7` 掩码帧；V4 正式引入了 Growl 特征，并在 DDI 中加入 `VQM / VQMu / VQMp`，在 DDB 中加入 VQM 帧。V5 沿用了该结构。
3. **发现层分流（VDM 多模式路由）**：现代 V5/V6 声库位于 64 位 `HKLM\SOFTWARE\VOCALOID5\Voice\Components`；而 V4/V3 全部位于 32 位 `HKLM\SOFTWARE\WOW6432Node`。VDM 内部通过模式切换将 V4/V3 分流到旧格式读取器 `FUN_1800dabf0`。
4. **`.vvd` 加密算法破解**：确认 `.vvd` 采用 **非空白/换行字符逐字节异或 `0x1A`** 的混淆算法。V4 的 `.vvd` 与声库同 stem，保存 6 项语音参数（含 `"Growl" = "1"`）；V5 则退化为固定名称的 `_v4compatible.vvd` 兼容桩，实际参数行被填充为 `"GENERATOR"="MYXX"`。
5. **DSE 运行时完全不依赖 `.vvd`**：DSE 只以同 stem 方式打开 `.ddi` 和 `.ddb`，`.vvd` 仅供 VDM/编辑器 UI 呈现默认参数。

---

## 2. VDM 注册表发现与多模式路由器

在 `VDM2.dll`（6.13.0.1）中，`FUN_1800e04f0` 负责枚举已安装声库。其通过模式参数（`param_2`）控制根键和子键：

```c
// VDM2.dll: FUN_1800e1ec0 (选择根键)
if (mode == 0)      root = L"SOFTWARE\\VOCALOID6";
else if (mode == 1) root = L"SOFTWARE\\VOCALOID5";
else if (mode == 2 || mode == 3) root = L"SOFTWARE\\Wow6432Node\\VOCALOID4";
else if (mode == 4) root = L"SOFTWARE\\Wow6432Node\\VOCALOID3";

// VDM2.dll: FUN_1800e04f0 (选择子键)
if (mode == 2)      subKey = L"DATABASE";
else if (mode == 3) subKey = L"DATABASE41";
else if (mode == 4) subKey = L"DATABASE\\VOICE3";
else                subKey = L"Voice\\Components";
```

### 模式分流汇编

`FUN_1800dcc30` 入口处对模式进行显式判断：

```assembly
1800dcc98: MOV ECX, dword ptr [R9]   ; 读取 mode
1800dcc9b: SUB ECX, 0x2              ; mode == 2 ? (DATABASE, V4.0)
1800dcc9e: JZ  0x1800dfe5f           ; -> 跳转到旧读取器 FUN_1800dabf0
1800dcca4: SUB ECX, 0x1              ; mode == 3 ? (DATABASE41, V4.1)
1800dcca7: JZ  0x1800dfe5f           ; -> 跳转到旧读取器 FUN_1800dabf0
1800dccad: CMP ECX, 0x1              ; mode == 4 ? (VOICE3, V3)
1800dccb0: JZ  0x1800dfe5f           ; -> 跳转到旧读取器 FUN_1800dabf0
```

### 旧读取器 `FUN_1800dabf0` 的解析逻辑

1. **`INSTALLED` 校验**：
   - 必须读取 DWORD `INSTALLED`。
   - 若值不等于 `1`，立即返回错误码 `10` 并丢弃该组件。
2. **组件 ID 与语言提取**：
   - 调用 `FUN_1800daa40` 对 16 字符键名进行 base-28 解码与校验，提取 native language ID。
3. **`DRP` 校验**：
   - 读取字符串 `DRP`，长度必须严格等于 6。
4. **`NAME` 与短名截取**：
   - 读取 `NAME`（如 `VOCALOID4 Library (LuoTianyi_V4_Meng)`）。
   - 扫描 `L'('` 与 `L')'`，截取括号中间的字符串写入对象的 `Name` 字段；若未找到括号，则回退为全名。
5. **版本号派生**：
   - Mode 2 (`DATABASE`)：硬编码 `Version = 4.0.0`。
   - Mode 3 (`DATABASE41`)：硬编码 `Version = 4.1.0`（如初音未来 V4X EVEC）。
   - Mode 4 (`VOICE3`)：硬编码 `Version = 3.0.0`。
6. **DDB 发现**：
   - 读取 `PATH`（如 `E:\Program Files (x86)\VoiceDB\LuoTianyiV4_CHN\`）。
   - 拼接组件 ID 子目录后，执行 `FindFirstFileW("...\\*.ddb")`，选取第一个匹配的 `.ddb` 文件，将其完整路径保存为 `VoiceBank.Path`。
7. **`TIME` 与授权子键**：
   - 读取 16 字符字符串 `TIME`。
   - 打开 `KEYS` 子键，枚举键值对以填充 VDM license descriptors。
8. **默认样式与语音参数**：
   - `defaultStyleID` 固定赋值为 `0c29827a-4289-495d-94d2-e23602d346c6`。
   - 依次初始化 5 项标准参数：`bre`、`bri`、`cle`、`gen`、`ope`。

---

## 3. `.vvd`（VOCALOID Virtual Voice）格式逆向

`.vvd` 是随声库发布的小型文本配置，文件大小约 200–250 字节。

### 混淆算法

`.vvd` 并非明文，其编码规则为：
- 控制与空白字符保持原样：`0x20`（空格）、`0x0D`（`\r`）、`0x0A`（`\n`）不作变换；
- 其余所有字节与 `0x1A` 进行异或操作（`byte ^ 0x1A`）。

### 各代实际解密内容对比

#### VOCALOID3（`Tianyi_CHN.vvd`）
```ini
"ID" = "VOCALOID VIRTUAL VOICE"
"FORMAT" = "3.0.0.0"
"VOICEIDSTR" = "BETDB8W6KWZPYEB9"
"VOICENAME" = "Tianyi_CHN"
"Breathiness" = "0"
"Brightness" = "0"
"Clearness" = "0"
"Opening" = "0"
"Gender Factor" = "0"
```

#### VOCALOID4（`Luotianyi_CHN_Meng.vvd`）
```ini
"ID" = "VOCALOID VIRTUAL VOICE"
"FORMAT" = "4.0.0.0"
"VOICEIDSTR" = "BK8H76TAEHXWSKDB"
"VOICENAME" = "Luotianyi_CHN_Meng"
"Breathiness" = "0"
"Brightness" = "0"
"Clearness" = "0"
"Opening" = "0"
"Gender Factor" = "0"
"Growl" = "1"
```
*注：V4 在 V3 基础上增加了第 6 个参数 `"Growl"`，默认值为 `"1"`。*

#### VOCALOID5（`_v4compatible.vvd`）
```ini
"GENERATOR"="MYXX"
"GENERATOR"="MYXX"
"VOICEIDSTR" ="BD79E492NWWK3DDF"
"VOICENAME" = "Luo_Tianyi_Ning"
"GENERATOR"="MYXX"
"GENERATOR"="MYXX"
"GENERATOR"="MYXX"
"GENERATOR"="MYXX"
"GENERATOR"="MYXX"
"GENERATOR"="MYXX"
```
*注：V5 中仅保留了 `VOICEIDSTR` 与 `VOICENAME`，其余参数行均被填充为 `GENERATOR=MYXX` 桩行，证实其仅作为向后兼容旧工具的占位符。*

---

## 4. DDB 声学数据与采样块

使用只读探针对 V4 洛天依萌（`Luotianyi_CHN_Meng.ddb`，4,165,429,908 字节）进行全量扫描，结果如下：

- **顶层块总数**：375,307 个；其中 `FRM2` 367,558 个，`SND ` 7,749 个。
- **SND 规范**：全部 7,749 个样本均为 44,100 Hz、单声道、16-bit PCM；总采样点数 109,962,752。
- **SND 边缘余量不变量**：
  $$\text{pcm\_count} = \text{epr\_count} \times 256 + 2048$$
  在 V4 中同样 100% 严格成立，STA 样本有效指针同样严格位于 `SND块首 + 2066`（跳过 18 字节头和 1024 个采样点）。
- **FRM2 掩码分布**：
  - `0x0000002000e00207`：348,230 帧（普通有声主帧）；
  - `0x0000000000000200`：19,223 帧（轻量无声帧，基频为 0.0）；
  - `0x00000000000e22b7`：105 帧（**VQM Growl 帧**）。

与 V5 相同，V4 的声学帧完全由这三种掩码组成。而对 V3 天依 DDB 的实测表明，V3 仅包含前两种掩码，不存在 `0xe22b7`。

---

## 5. DDI 索引树与 DSE 原生加载验证

通过 `tree_harness` 调用 DSE 原生加载器（`0x18010d490`）对 V4 初音未来 Sweet（`MIKU_V4X_Sweet.ddi/.ddb`）进行加载回读验证：

```text
stage=construct
stage=constructed result=0x1d9009c2340
stage=initialize_for_load
stage=initialized_for_load result=0
stage=load_existing
load.result=0
stationary.count=1
phoneme.count=12
part.count=4
part.snd_pointer=558800146
part.integrity_payload=195,227,70,203
articulation.count=51
articulation_target.count=44
articulation_part.count=3
articulation_part.snd_payload_pointer=285218
articulation_part.snd_core_pointer=287266
articulation_part.alignment_count=2
articulation_part.alignment[0]=0,12,4,12
articulation_part.alignment[1]=12,25,12,20
```

实测表明：
1. **DSE 原生加载完全成功**（`load.result = 0`），DSE 能完整反序列化 V4 的 `DBSe`、`PHDC`、`PHG2`、`TDB `、`STA `、`ART ` 与 `VQM `。
2. **指针几何关系与 V5 完全一致**：
   - `articulation_part.snd_core_pointer - articulation_part.snd_payload_pointer == 2048`（恰好 1024 个 PCM 点）。
   - Outer alignment `[0, 12]` 与 `[12, 25]` 无缝覆盖全帧 `25`，Inner alignment 分别为 `[4, 12]` 与 `[12, 20]`。
3. **DBSe 摘要校验的双分支机制**：
   在 DSE 的 `0x18010d8e0` 中，校验逻辑存在两个加盐分支：
   - **分支 1**：`MD5("K2ho" + UPPER(stem) + "nF")`
   - **分支 2**：`MD5("1m5Pj" + UPPER(stem) + "qFE")`
   两个分支的选择取决于对象内部的浮点标量 `dVar13`。商业库与自建库只要在此块写入对应的 32 字节十六进制文本并后补 228 个零（共 0x104 字节），即可通过 DSE 的 `authenticated` 校验。

---

## 6. V3 / V4 / V5 传统声库全方位对比矩阵

| 特性 / 维度 | VOCALOID 3 | VOCALOID 4 | VOCALOID 5 |
| :--- | :--- | :--- | :--- |
| **注册表架构** | 32 位（`WOW6432Node`） | 32 位（`WOW6432Node`） | 64 位（原生 `SOFTWARE`） |
| **注册表路径** | `...\VOCALOID3\DATABASE\VOICE3` | `...\VOCALOID4\DATABASE` (4.0)<br>`...\VOCALOID4\DATABASE41` (4.1) | `...\VOCALOID5\Voice\Components` |
| **短名提取** | 从 `NAME` 括号 `(...)` 中截取 | 从 `NAME` 括号 `(...)` 中截取 | 独立注册表值 `BankName` / `Name` |
| **版本号指定** | 注册表模式隐式派生（3.0.0） | 注册表模式隐式派生（4.0.0 / 4.1.0） | 显式子键 `Version\Major, Minor, Revision` |
| **安装文件命名** | `<stem>.ddb`<br>`<stem>.ddi`<br>`<stem>.vvd` | `<stem>.ddb`<br>`<stem>.ddi`<br>`<stem>.vvd` | `<stem>.ddb`<br>`<stem>.ddi`<br>`_v4compatible.vvd` |
| **`.vvd` 语义** | 混淆文本（XOR 0x1A）<br>含 5 项基础语音参数 | 混淆文本（XOR 0x1A）<br>含 6 项参数（**新增 Growl**） | 混淆文本（XOR 0x1A）<br>参数被 `MYXX` 占位，仅保留 ID/名称 |
| **交叉合成 (XSY)** | 不支持 | **原生支持**（`XSYLink` 矩阵与 `Presets`） | 宿主直接管理，无独立声库级预设 |
| **Growl (VQM)** | **无**（无 VQM 块和帧） | **支持**（包含 VQM 树与 `0xe22b7` 掩码） | **支持**（沿用 V4 VQM 结构） |
| **DDB SND 格式** | 44.1 kHz, mono, 16-bit PCM | 44.1 kHz, mono, 16-bit PCM | 44.1 kHz, mono, 16-bit PCM |
| **DDB FRM2 掩码**| 普通有声帧 + 无声帧 | 普通有声帧 + 无声帧 + **VQM 帧** | 普通有声帧 + 无声帧 + **VQM 帧** |
| **DSE 加载兼容**| DSE5 兼容 | DSE5 兼容 | DSE5 原生 |
