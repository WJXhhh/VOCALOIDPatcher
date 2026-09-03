# 传统声库格式第一轮盘点

## 1. 安装布局

本机 VOCALOID6 通过 `HKLM\\SOFTWARE\\VOCALOID5\\Voice\\Components` 发现传统声库。注册项以 16 字符组件 ID 为子键，至少提供 `Name` 和公共 `Path`；实际文件位于 `Path\\<CompID>`。

| 组件 ID | 名称 | DDI 大小 | DDB 大小 |
| --- | --- | ---: | ---: |
| `BD79E492NWWK3DDF` | Luo_Tianyi_Ning | 3,412,593 | 3,370,145,906 |
| `BL8CEAM5N4XN3LFK` | Luo_Tianyi_Wan | 5,678,491 | 5,232,406,982 |
| `BMA8DBBZM5ZH2MDE` | Yan_He_Mu | 4,110,177 | 3,679,089,898 |
| `BMBNDB8EM5222MK3` | Yuezheng_Ling_You | 4,360,337 | 4,030,786,256 |
| `BN69LCH2W6TK8NEF` | Yan_He_Qing | 4,626,848 | 4,452,490,220 |
| `BP8CDDH5M7XN2PED` | Luo_Tianyi_Meng | 4,501,235 | 4,144,357,036 |
| `BY98KLLZTDYH7YEE` | Yuezheng_Ling_Chi | 6,068,143 | 5,822,649,968 |

这些是同一套中文音素系统、不同音色和音域层数的良好对照组。

## 2. DDI：索引与对象树

当前可识别块：

- `DBSe` / `DBS `：声库根对象及版本化序列化标志。
- `PHDC`：音素清单、有声/无声分类、音素组与 EpR guide。
- `PHG2`：音素类别到内部索引/名称的映射。
- `TDB `：参与 timbre 模型的音素，当前中文样本为 42 个有声音素。
- `DBV `：voice 子树入口。
- `STA ` / `STAu` / `STAp`：stationary 单元、音素与各音高样本。
- `ART ` / `ARTu` / `ARTp`：articulation 转接树与各音高样本。
- `VQM ` / `VQMu` / `VQMp`：growl 等 voice quality modification 样本。
- `EMPT`：外部块或占位引用，常跟随 `SND`、`EpR` 名称。
- `ARR `：数组包装标记。

`STAp/ARTp/VQMp` 中目前已观察到：

- duration（Float64）
- pitch1、pitch2、unknown2、dynamics、tempo（Float32）
- EpR 数量及每个 EpR 在 DDB 中的 64 位偏移
- 采样率
- SND 标识符与 DDB 64 位偏移
- ART 的第二个 SND 起点/浊音起点偏移
- ART 的 frame alignment 分组
- `default` 等样本标签

字段的物理类型已由解析和 DSE 读写代码交叉验证。`pitch1` 已确认是样本层选择坐标，`pitch2` 是合成使用的实际参考音高；两者都以 A4=440 Hz 为零点、单位为 cent。`unknown2` 的业务语义仍按未知处理。

## 3. DDB：声学帧与 PCM 聚合

### SND 块

公开的 Python 与 Rust 实现独立给出同一布局，本机 DDI 中的偏移也可定位到该头：

| 偏移 | 大小 | 含义 |
| ---: | ---: | --- |
| `0x00` | 4 | ASCII `SND ` |
| `0x04` | 4 | 整个块长度，little-endian |
| `0x08` | 4 | 采样率 |
| `0x0c` | 2 | 声道数 |
| `0x0e` | 4 | 后续 PCM 值数量 |
| `0x12` | 变长 | 16-bit PCM 负载 |

DSE 自身的读写循环证明 `0x0e` 是 PCM 值数量，而不是标识符，并满足 `chunk_size == 18 + sample_count * 2`。7 个样本库的 DDI 均报告采样率 44,100 Hz；全量 DDB 扫描进一步确认全部 56,894 个 SND 都是 44,100 Hz、单声道，长度公式无一例外。

### FRM2 块

`FRM2` 头至少包含 magic 与 little-endian 块长。一个 ART/STA 样本通常引用多个 FRM2 块，公开解析器把它们称为 EpR；代表性库每个 ART 样本的引用数量从 4 到 112 不等，中位数 59，明显与持续时间/分析帧数相关。

因此，只替换 `SND ` PCM 而保留原 FRM2 最多只能验证容器和调用链，不能视为完成新声库训练：合成器仍会按旧录音的谱包络、谐波、相位/共振等分析特征工作。

DDI 引用还有一个不能被“统一叫作 SND 块偏移”掩盖的差异：ART/VQM 样本指向 SND 块首，而本批全部 836 个 STA 样本的指针都严格等于 `SND 块首 + 2066`，即跳过 18 字节头和 1024 个单声道 PCM samples。将这些指针归一到所属块后，7 个 DDI 的样本集合与 56,894 个 SND 块严格一一对应。

## 4. 中文对照组统计

| 声库 | 音素 | STA 单元×层 | ART 双音素单元 | ART 样本 | VQM |
| --- | ---: | ---: | ---: | ---: | ---: |
| Luo_Tianyi_Ning | 62 | 38×2 | 2556 | 5099 | 1 |
| Luo_Tianyi_Wan | 62 | 38×4 | 2559 | 10191 | 1 |
| Yan_He_Mu | 62 | 38×3 | 2559 | 7663 | 1 |
| Yuezheng_Ling_You | 62 | 38×3 | 2559 | 7655 | 1 |
| Yan_He_Qing | 62 | 38×3 | 2556 | 7636 | 1 |
| Luo_Tianyi_Meng | 62 | 38×3 | 2556 | 7634 | 1 |
| Yuezheng_Ling_Chi | 62 | 38×4 | 2556 | 10173 | 1 |

大部分 ART 单元在每个目标音高层各有一个样本，少数单元缺层。STA 的层数严格一致。pitch1 是相对 A4=440 Hz 的样本层选择坐标；各库有 2–4 个相距约 3–5 个半音的录音层。它不是 MIDI 音高编号，而是带有实际录音音准偏差的 cent 值。

## 5. 代表性中文音素清单

`Luo_Tianyi_Ning` 的 PHDC 共 62 个：

- 有声：`a o 7 aI ei @\` AU @U a_n @_n AN @N i\\ i\` i ia iE_r iAU i@U iE_n i_n iAN iN iUN u ua uo uaI uei ua_n u@_n uAN UN u@N y yE_r y{_n y_n n m z\` l`
- 无声：`t p k k_h p_h t_h s\\ f x s s\` ts\\_h ts\\ ts\`_h ts_h ts\` ts ? Sil Asp`

其中只有 38 个可持续有声音素拥有 STA；`n/m/z\`/l` 等虽列为 voiced 并进入 TDB，但没有 STA。转接覆盖不是简单的 62×62 全笛卡尔积，需要从 ART 键集合反推出语言相关的合法前后接规则。

## 6. 尚未解决

1. 普通 FRM2 的逐字段结构已经恢复；各分析量从 PCM 的精确生成算法、量纲和标度仍未知。
2. EpR guide、TDB timbre 与 FRM2 数据的确切关系。
3. ART frame alignment 四元组的准确时间语义。
4. pitch1/pitch2 的单位和运行时用途已恢复；ART 的 pitch2 有声选区规则、unknown2 及 dynamics/tempo 的生成方法仍未知。
5. `singer.inf`、`epr_templates.txt` 如何驱动 DSE 内部建库对象。
6. 从标注语料生成 `STA/ART/VQM` 树的内部入口。
7. `.vvd` 元数据、注册、组件 ID 与宿主加载/授权的最小要求。
