# ART 边界标注注入与有声/无声帧生成

## 结论

已经找到并实际验证一种可复现的单边界约束输入：把 DRS 动态参数 `0x0d` 的外部 F0 envelope 在标注位置从 0 阶跃到目标 F0，或从目标 F0 阶跃到 0。配合参数 `0x14=0` 的 external F0 模式，DSE 会在 F0 为 0 的区间生成最终 unvoiced `0x200` FRM2，在 F0 非零的区间生成普通 voiced `0x2000e00207` FRM2。

对 0.30 秒、52 帧的自有合成输入，在 0.15 秒边界实验得到：

```text
Sil -> a:  frames  0..25 = unvoiced, 26..51 = voiced
a -> Sil:  frames  0..25 = voiced,   26..51 = unvoiced
```

两份 SMS2 均由 DSE writer 写出、DSE reader 回读，并通过独立 `probe_sms2.py` 的 52 帧完整布局检查。这第一次把 ART 的 outer 分界标注落实到了声学帧类型，而不是只把同一份 STA 帧挂到两个 ART 名称下。

输入 PCM 仍是持续谐波信号，F0=0 区间与实际波形并不匹配；本轮只能证明约束机制和文件链。真实训练必须让 `Sil` 区间的 PCM 本身也是干净静音/底噪，让 `a` 区间来自真实元音。

## 动态参数坐标是归一化位置

配置 getter 在分析时会结合整段录音时长，把分析时间映射到 envelope 的 0..1 坐标。因此用户标注是秒时，写入动态参数前必须换算：

```text
boundary_position = boundary_seconds / recording_duration_seconds
```

早期把 0.15 秒直接写成 envelope 位置 0.15，52 帧结果为 8/44，而不是预期的 26/26；`0.15 * 52 ≈ 8` 直接暴露了坐标误用。改为 `0.15 / 0.30 = 0.5` 后，两种方向都精确得到 26/26。

为避免默认线性插值产生长 F0 滑坡，harness 在边界前极小位置和边界本身写入两点：

```text
Sil -> a:  (0, 0), (b-eps, 0), (b, F0), (1, F0)
a -> Sil:  (0, F0), (b-eps, F0), (b, 0), (1, 0)
```

对应环境变量：

```text
DRS_HARNESS_F0_BOUNDARY_SECONDS
DRS_HARNESS_F0_BOUNDARY_DIRECTION=sil-to-voiced|voiced-to-sil
```

仅 external pitch mode 接受该约束；harness 会拒绝越界时间、未知方向以及 auto F0 与显式边界的错误组合。

## `CSMSRegionAnalysis` 不是可直接采用的音素边界

Ghidra 中已确认：

```text
constructor       0x1800956b0
vtable            0x1805bebd8
initialize region 0x180095780
commit frame      0x1800957f0
finalize          0x1800959b0
```

当动态参数 `0x2f` 非零时，commit 会在当前 region 已超过 4 帧后，根据 frame `+0xf0` 的正负在 type 7 与 type 1 间切换。参数 `0x2f` 默认值为 0。

但 DRS 原始有声帧的 bit 31 置位时，`+0xf0` 保存相对 A4 的音分，而不是线性 Hz。220 Hz 为 -1200 cents，110 Hz 为 -2400 cents；所以“`+0xf0 > 0`”不是 voiced 判定。实测启用 `CSMSRegionAnalysis` 和 `0x2f=1` 后，当前 220 Hz/自动约 110 Hz 数据仍只有一个 type 7 region，尽管 48/52 帧实际有 F0。

因此不能把该类的 type 1/7 直接写进训练规格作为 voiced/unvoiced 音素标注。本阶段真正有效的约束是 external F0 envelope 的 0/目标值，而 ART outer/inner 仍由 DDI alignment 单独表达。

## 使用不同分析帧重建双向库

两份边界 SMS2 分别经 `build_unit_ddb.py` 生成：

```text
Sil -> a DDB unit = 334,218 bytes
a -> Sil DDB unit = 334,242 bytes
```

大小不同来自主帧与 32 字节 unvoiced frame 的排列及个别主帧长度差异。`build_unit_ddb.py` 新增可选的强约束：

```text
--split-frame
--source-voicing voiced|unvoiced
--target-voicing voiced|unvoiced
```

三项必须一起用于 ART。正确方向通过；把 `Sil→a` 的期望反写成 voiced→unvoiced 会报告全部 52 帧不符，并且不产生输出文件。

最终库使用：

```text
unit 0 = 原有全 voiced STA a
unit 1 = 26 unvoiced + 26 voiced, Sil -> a
unit 2 = 26 voiced + 26 unvoiced, a -> Sil
```

结果：

```text
DDB bytes = 1,304,518
DDI bytes = 2,926

Sil -> a payload/core = 939,556 / 941,604
a -> Sil payload/core = 1,273,798 / 1,275,846
```

公开解析器读出 voiced `a`、unvoiced `Sil`、STA `a`、ART `Sil a` 与 `a Sil`，两条 ART 各 52 个 EpR。DSE 原生加载器按名称恢复两条转接、裁剪过的 inner alignment 和上述不同指针，最终 `load.valid=True`。

## 对真实录音的含义

当前最小真实输入合同已经比较明确：

1. 44.1 kHz PCM16 单声道 WAV；
2. 录音参考 F0；
3. 秒单位 outer 边界，构建时换算为 envelope 归一化位置和最近 frame；
4. 两侧 inner 稳定 frame 区间；
5. 源/目标各自是 voiced 还是 unvoiced，用于构建后逐帧强校验。

下一步不再需要猜测怎样让 DRS 生成一半无声、一半有声。真正未验证的是：真实人声/静音 PCM 在该约束下的分析质量、边界附近窗函数泄漏、inner 稳定区间的听感，以及宿主 VSM 是否能用这三个单元渲染自然的起音和收音。
