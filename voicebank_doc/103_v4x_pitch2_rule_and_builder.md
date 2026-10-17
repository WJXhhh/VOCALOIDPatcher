# V4X `pitch2` 取帧规则与构建接线

本页补齐[此前的全库候选](39_v4x_regions_layers_and_pitch2.md)及计划式 DDI 构建器的实际写入。对象是当前已安装、哈希已核对的八套 V4X 传统声库；旧中文 V5 库本机没有重新读取。`pitch1` 仍是层选择坐标，`pitch2` 是从最终帧 F0 得到的参考音高，二者不可直接复制。

## 可复现的数值规则

先对每个纳入的正 F0 帧计算 `1200 × log2(F0_Hz / 440)`，在音分域取算术平均，最后写为 float32。没有可纳入的正 F0 时写 `-FLT_MAX`。取帧范围为：

| 素材 | 取帧范围 |
| --- | --- |
| STA | 全部最终帧中的正 F0 |
| 双音素 ART | 两组 outer 区间中，PHDC 标记为有声的音素所对应区间内的正 F0；不按 inner 裁剪 |
| 三音素 ART | 全部最终帧中的正 F0；四组 alignment 不强行与三个音素一一对应 |

[八库完整核对284](evidence/284_v4x_pitch2_full_audit.json)逐项读取 DDI 所列的最终 DDB 帧，按上式独立计算。688 个 STA、20,615 个双音素 ART 和 3,266 个三音素 ART part 全部吻合：23,130 个有效 `pitch2` 的 float32 位型与保存值完全一致，双音素 ART 另有 1,439 个无音高哨兵也全部一致。已核对帧数分别为 51,681、756,666、151,262；没有遗漏或超容差项。只取所有正 F0 或仅取 inner 的反例及误差分布见[前次记录39](39_v4x_regions_layers_and_pitch2.md)。

同版本 DSE 的集合统计入口 RVA `0xc86a0` 在参数 `0x5d` 下，对给定的闭区间逐帧计算参考音高均值。汇编显示：未编码的正 Hz 值先换算音分；带音分编码标记的帧直接取音分；无效值不进入求和。[自生成帧核对283](evidence/283_pitch2_native_mean.json)覆盖限定区间、两种编码、混合编码及无有效值五种输入，原生结果与独立计算最大相差 `0.00002241 cent`。其上层 `0xc84f0` 在分析集合收尾时调用这个统计入口。该入口本身不读取 PHDC，也不直接写 DDI `pitch2`：它验证平均方式，PHDC 选区及最终哨兵规则则由284的全库保存值和自生成反例共同支持，不能把它说成已找到原制作软件的最终写入调用点。

## 当前构建入口

[独立计算模块](../voicebank/tools/pitch2_reference.py)按最终 FRM2 的 F0、PHDC 有声类别与 ART outer alignment 计算 `pitch2`；[计划式 DDI 构建器](../voicebank/tools/finalize_planned_ddi.py)读取将要写入 DDI 的同一组 DDB 帧，再把计算结果单独交给 STA/ART part 写入函数。录音标注的 `f0_hz` 仍只用于 `pitch1` 层坐标。无有效 F0 的 ART 写入 `-FLT_MAX`，不把标注音高伪装成实测参考音高。

现有单单元示例构建入口保留原来的 `--pitch-hz` 行为；本轮接线针对有 PHDC 与多单元规划信息的计划式入口。它不会修改已安装声库，也不执行 Editor 宿主演唱。构建器依据的是当前八库可复现的规则；原制作软件中 PHDC 选区到 DDI 字段的确切调用点仍未知，因此不把这一实现宣称为厂商代码的逐指令复刻。

验证：`verify_pitch2_native_mean.py` 的五组原生数值对照、`audit_v4x_pitch2.py` 的八库逐 part 核对，以及 `test_pitch2_reference.py` 的选区、哨兵、最终帧读取和 `pitch1/pitch2` 分写测试。
