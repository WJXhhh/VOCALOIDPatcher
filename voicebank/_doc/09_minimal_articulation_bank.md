# ART/ARTu/ARTp 结构、对齐组与诊断闭环

## 结论

已经在最小 STA 库中加入一个完全自有、结构诊断用途的 `a→a` 转接单元，并由公开解析器与 DSE 6 原生加载器双重回读。该单元复用了第二份自有 220 Hz 分析结果，因此只能证明 ART 的树、偏移和缓存格式，不能代表真实的音素过渡音质。

新增工具：

- `build_bank_ddb.py`：按顺序合并多个经过验证的单元 DDB，并输出所有绝对 FRM2/SND 指针的 JSON manifest。
- `finalize_minimal_articulation_ddi.py`：把一个 STA、一个 ARTp 的 DSE 中间树与 manifest 编译成最终 DDI。
- `tree_harness` 的 `TREE_HARNESS_ADD_EMPTY_ARTP=1`：建立 ART→ARTu→ARTp 骨架。
- `tree_harness` 的 `TREE_HARNESS_EXPECT_ARTP=1`：在原生 DDI 回读中检查 ART 层级、两个 SND 指针和 alignment vector。

## 构造器与对象大小

当前 DSE 6.13.1.1 中：

| 对象 | magic | 构造函数 RVA | 大小 |
| --- | --- | ---: | ---: |
| ART | `ART ` | `0x110b00` | `0x168` |
| ARTu | `ARTu` | `0x110b30` | `0x178` |
| ARTp | `ARTp` | `0x110b70` | `0x268` |

初始化 singer 的 articulation ARR 已为 PHDC 中每个音素建立一个源 ART。新增转接只需找到源 ART，加入以目标音素命名的 ARTu，再在 ARTu 下加入名为 `default` 的 ARTp。

ARTp 与 STAp 共用 `CDBDataPhUPart` 的大部分标量和 EpR/SND 缓存，但其版本 3 紧凑尾部不同。

## ART 源单元偏移

单转接源单元的首个 ARTp magic 位于 `0x33`；ARTp 内部 SND magic 相对 ARTp 为 `0x39`，所以 DDI 中两个首要位置为：

```text
ARTp source position = 0x33
SND source position  = 0x33 + 0x39 = 0x6c
EpR source position  = 0x6c + snd_chunk_size + 7
```

商业参考库第一个 ARTp 的三个值正好是：

```text
ARTp = 0x33
SND  = 0x6c (108)
EpR  = 0xae85 (44677)
```

该样本有 79 帧，因此 PCM 数量为 `79*256+2048=22272`，SND 总长为 `18+22272*2=44562`：

```text
0x6c + 44562 + 7 = 44677 = 0xae85
```

诊断单元有 52 帧，生成值为 SND `0x6c`、EpR `30853 (0x7885)`，公开解析器按 ARTp key 108 读回。

## ARTp 版本 3 紧凑缓存

两个 `EMPT` 之后的负载为：

```text
u32 epr_count
u64 ddb_frm2_offsets[epr_count]
u32 sample_rate
u16 channels
u32 pcm_sample_count
u64 ddb_snd_payload_pointer
u64 ddb_snd_core_pointer
u32 alignment_group_count
i32 alignment_groups[count][4]
length-prefixed ARTp name
```

读取函数为 `0x180110d50`。版本小于 3 时是另一套兼容布局；当前构建器固定生成根版本 3。

## 两个 SND 指针

ARTp 的第一个指针是 SND 的 PCM payload 起点：

```text
snd_payload_pointer = snd_chunk_offset + 18
```

第二个指针是跳过前侧 1024 个 PCM samples 的核心起点：

```text
snd_core_pointer = snd_payload_pointer + 1024*2
                 = snd_payload_pointer + 2048 bytes
```

对 `Luo_Tianyi_Ning` 全部 5,099 个 ARTp 聚合检查，第二指针减第一指针全部严格等于 2048，零例外。诊断库的两个绝对指针为 1,241,396 和 1,243,444，也满足同一关系。

STA 只在紧凑缓存中保存核心指针；ART 同时保存 payload 与核心指针。这解释了公开工具为何把 ART 的第一个指针归一为 SND chunk 首，而 STA 直接落在 chunk 首加 2066。

## frame-alignment 四元组

参考库 5,099 个 ARTp 全部恰有两组四元组：

```text
(outer_start, outer_end, inner_start, inner_end)
```

聚合不变量：

- 第一组 outer start 全部为 0。
- 第二组 outer end 全部等于 EpR 帧数。
- 第一组 outer end 全部等于第二组 outer start，即两个 outer 区间完整分割整条 EpR。
- 每组 inner 区间全部位于自身 outer 区间内。
- 5,024/5,099 个样本的 inner 与 outer 完全相同；75 个样本至少裁掉一侧的若干帧。
- 5,098/5,099 个样本的两段 inner 在分界处相接；唯一例外存在 10 帧空隙。

因此 outer 两段可以确定为源/目标音素在转接 EpR 中的帧区间；inner 两段是各自的可用/稳定子区间，而不是 voiced/unvoiced 标志。`o→uei` 的一个 96 帧样本全是普通有声主帧，但 inner 仍由 `[0,42]`、`[42,96]` 裁成 `[16,42]`、`[42,74]`，直接排除了“inner 只表示有声帧”的解释。

诊断 `a→a` 没有真实标注，暂按中点 26 分割，并令 inner 等于 outer：

```text
(0, 26, 0, 26)
(26, 52, 26, 52)
```

真实构建器必须把 outer 边界来自人工/自动音素切分，把 inner 边界来自稳定区标注或明确算法；不能长期使用中点猜测。

## 原生闭环结果

最终诊断 DDB 含两个各自独立偏移的自有单元，总长 1,272,116 字节；DDI 为 2,204 字节。DSE 原生回读结果：

```text
root.authenticated=1
stationary.count=1
phoneme.count=1
part.count=1
articulation.count=1
articulation_target.count=1
articulation_part.count=1
articulation_part.frame_count=52
articulation_part.sample_rate=44100
articulation_part.channels=1
articulation_part.pcm_count=15360
articulation_part.snd_payload_pointer=1241396
articulation_part.snd_core_pointer=1243444
articulation_part.alignment_count=2
articulation_part.alignment[0]=0,26,0,26
articulation_part.alignment[1]=26,52,26,52
load.valid=True
```

公开解析器同时读回 ART source `0x33`、SND source `0x6c`、EpR source `0x7885`、ARTp key 108、52 个独立的第二单元 FRM2 偏移和相同 alignment。

## 生命周期修正

DSE 的 DBSe 根读取器会销毁并替换构造 singer 时传入的初始 phonetic dictionary。早期回读 harness 在进程结束时再次释放原始 dictionary/group，导致 `0xc0000374` 堆损坏。加载模式现不再释放这些已转交 DSE 的原始指针；修正后同一 ART 回读正常以 exit code 0 结束。

## 下一步

1. 定义可复现的转接标注输入：源音素区间、目标音素区间及各自稳定子区间。
2. 用真实 `Sil→a` 与 `a→Sil` 录音替换诊断用的重复 220 Hz 单元。
3. ~~让 PHDC 同时包含 voiced `a` 和 unvoiced `Sil`，批量生成两个 ARTu/ARTp。~~ 已完成，见 `11_minimal_sil_a_bank.md`。
4. 把 outer/inner 稳定区间变成显式标注，再研究多音高层 ARTp 的选择坐标和 source file 中多个 part 的累积偏移。
