# 最小 STA DDI/DDB 与 DSE 原生回读闭环

## 结论

截至 2026-09-04，已经从完全自有的 220 Hz PCM 与 DRS 最终帧构造出一对单静态音 `.ddi/.ddb`，并完成两条独立回读：

1. `ddb-tools` 的公开 `DDIModel` 能解析 PHDC/TDB/DBV/STA/ART，得到一个 `a` 音素、一个 STAp、52 个 EpR 偏移和正确的 SND 引用。
2. 当前 VOCALOID6 随附 `DSE.dll` 的 `CDBSinger` 加载入口能打开同一 `.ddi/.ddb`，重建 STA→STAu→STAp 层级，并得到完全一致的缓存字段。

这证明“自有 PCM → 最终 FRM2/SND DDB → 最小索引树 DDI”已经形成原生闭环。它仍不是可演唱声库：当前没有 ART 转接覆盖，也尚未在编辑器宿主内注册或渲染。

## 工具

- `voicebank/tools/build_unit_ddb.py`：把一段最终 DRS SMS2 与匹配 WAV 组装成一个 STA/ART DDB 单元。
- `voicebank/tools/tree_harness`：调用 DSE 构造 PHDC/TDB/DBV/STA/ART 中间树；也可用 `TREE_HARNESS_LOAD_EXISTING=1` 原生回读 `.ddi/.ddb`。
- `voicebank/tools/finalize_stationary_ddi.py`：把含一个 STAp 和两个 `EMPT` 外部引用的 DSE `.tree` 骨架编译成最终紧凑 DDI。
- `voicebank/tools/probe_ddb.py`、`probe_frm2.py`、`validate_main_sms2.py`：独立检查 DDB、FRM2 与最终主帧字段。

这些工具固定针对本机 DSE 6.13.1.1 的内部 ABI；RVA 和对象大小不能假定跨版本稳定。

## STAp 的两级表示

DSE 的训练中间态和最终 DDI 使用同一对象层级，但负载不同：

```text
STA "normal"
  └─ STAu "a" (phoneme index 0)
       └─ STAp "0"
            ├─ EMPT "SND" -> 源单元偏移
            └─ EMPT "EpR" -> 源单元偏移
```

模式 0 的源单元会实际内联 `SND ` 与 EpR；紧凑 DDI 只留下 `EMPT` 引用，再内联运行时缓存：

```text
i32 marker
u32 epr_count
u64 ddb_frm2_offsets[epr_count]
u32 sample_rate
u16 channels
u32 pcm_sample_count
u64 ddb_snd_core_pointer
i32 integrity_payload[4]
length-prefixed STAp name
```

DSE 的紧凑读取器 `0x18010a950` 逐项把这些字段装入 STAp：帧表位于 `+0x1a8/+0x1b0`，采样率、声道、PCM 数量和 SND 指针位于 `+0x1c8/+0x1cc/+0x1d0/+0x1c0`。

## 两个源单元偏移

STAp 源文件中，首个 SND chunk 的 canonical 偏移严格是 `0x3d`：

```text
STAp 固定头与标量       0x36 bytes
length + "SND"           0x07 bytes
SND magic 起点           0x3d
```

EpR magic 的偏移为：

```text
epr_source_offset = 0x3d + snd_chunk_size + 4 + len("EpR")
                  = 0x3d + snd_chunk_size + 7
```

这不是根据单个样本猜测。商业参考样本的 PCM 数量为 69,888，因此：

```text
snd_chunk_size = 18 + 69888 * 2 = 139794
0x3d + 139794 + 7 = 139862
```

其 DDI 中第二个 qword 正是 139,862。生成器对自有单元使用同一公式。

## STA 的帧、PCM 与时长不变量

对最终普通单元：

```text
core_pcm_samples = epr_count * 256
stored_pcm_samples = core_pcm_samples + 1024 + 1024
duration = stored_pcm_samples / 44100
ddi_snd_pointer = snd_chunk_offset + 18 + 1024 * 2
```

最后一项按字节计数，所以相对 SND magic 的增量为 2066。DDI 中此前被公开工具叫作 `snd_identifier` 的 `u32` 实际是 `stored_pcm_samples`。

本次 52 帧自有单元的固定结果为：

| 字段 | 值 |
| --- | ---: |
| EpR/FRM2 数量 | 52 |
| core PCM | 13,312 samples |
| 两侧边缘 | 2,048 samples |
| SND PCM 总数 | 15,360 samples |
| duration | `0.348299319727891` s |
| SND chunk offset | 605,320 |
| DDI SND pointer | 607,386 |

## 音高字段

STAp 的两个音高标量均为相对 A4=440 Hz 的音分：

```text
pitch_cents = 1200 * log2(f0_hz / 440)
```

220 Hz 因而写为 `-1200.0`。最小构建器先令层选择音高和实际参考音高相同；未来多层样本允许第一字段作为选择坐标做小幅修正。

## 四个尾部整数不是声学对齐点

STAp `+0x1f0/+0x1f4/+0x1f8/+0x1fc` 的四个 `i32` 一度看似循环或拼接边界。运行时消费点排除了该解释：

- `0x18010cfb0` 只取每个整数的低字节，按顺序旋转并拼接成库级载荷；第一个 STAp 的第三个整数充当总长度。
- `0x18010d1f0` 检查这些字段是否为 `-1`，据此选择内部常量生成回退载荷。
- 它们不参与 FRM2、SND、时长、音高或波形边界计算。

因此自训最小库可以把四项都写成 `-1`，表示该可选库级载荷不存在。DSE 原生回读确认四项保持为 `-1`。

## `.tree` 到 `DBSe` 的关键差异

DSE 中间树的根 magic 是 `DBS `；最终 DDI 是 `DBSe`。不能只改四个字符。`DBSe` 的根读取器 `0x18010d8e0` 会在 PHDC 后额外读取 0x104 字节：

```text
32-byte lowercase MD5 hex
228 zero bytes
```

当前 DBSe 分支的摘要输入为：

```text
MD5("K2ho" + upper(singer_base_name) + "nF")
```

遗漏整个 0x104 块会使后续 TDB/DBV/STA 全部错位，DSE 递归读取进入高 CPU 循环。仅填零虽然能保持对齐，但认证标志为假。`finalize_stationary_ddi.py` 根据输出文件 stem（或显式 `--singer-name`）生成摘要，DSE 回读的根认证标志为 1。

最终编译还会把普通结构块的临时 dirty/size 字段归零，并把 DBV、STA、STAu、STAp、ART 及承载它们的 ARR 的 materialized source position 从 `-1` 归一为 0；`EMPT` 的真实源单元偏移保持不变。

## 固定回读结果

使用名称 `one` 构建时，DBSe 摘要为：

```text
05c1567e1d1199adc6de0bf994654ffc
```

DSE 原生回读输出：

```text
load.result=0
root.authenticated=1
stationary.count=1
phoneme.count=1
part.count=1
part.frame_count=52
part.sample_rate=44100
part.channels=1
part.pcm_count=15360
part.snd_pointer=607386
part.integrity_payload=-1,-1,-1,-1
load.valid=True
```

公开解析器对同一文件得到 `phoneme=a`、52 个 EpR、duration `0.348299319727891`、pitch `-1200.0`、fs 44100 与同一 DDB SND 指针。

## 当前边界与下一步

已经证明最小静态单元可从自有 PCM 完整生成和原生加载。剩余工作不是继续猜 STA 字段，而是：

1. 构造 ART→ARTu→ARTp，并恢复 ART 的 SND 起点、两个音高和有声/无声帧组合规则。
2. 用真实人声及人工标注重复最终帧、DDB、DDI 闭环。
3. 加入至少 `Sil→V`、`V→Sil` 与一个 `V→V/CV` 过渡，形成最小可连唱图。
4. 在隔离且可撤销的测试组件中做宿主识别和渲染；当前研究没有部署或启动 VOCALOID Editor。
