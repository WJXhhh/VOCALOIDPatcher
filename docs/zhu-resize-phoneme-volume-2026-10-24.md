# zhu a ang 拉长后音素音量失效

2026-10-24，只读检查当前工程 revision 59、track 0 / part 0 / note 12，歌词 `zhu a ang`，音素为 ``ts` u a AN``，音量为 `[0, -8.3, -6.1, -8.9]` dB。用户确认从 420 tick 拉长至 480 tick 后失效，已拖回 420。当前工程未由探针编辑、保存或重新加载。

## 原因

在保存工程的临时副本中复现：420 tick 正常；480 tick 时 Comp 私有源准备失败，后续 que 起始边界要求 3343 帧，实际出现于 3342 帧，随后其他边界也偏移。音量数据仍然存在，但新的合成结果无法发布。

原生调度器按“下一个输入位置减去已经变换过的调度终点”计算静音间隔。补丁却把这个间隔当作原始源长度再次变换，重复计入上一段的位移。比如原始终点 3331 被映射到 3332，下一段原始开始于 3338，原生请求的间隔为 6；旧实现错误地把下一个源起点算成 3337。

`native/playback-clock/src/phoneme_timing.rs` 现在为静音补齐恢复绝对输入终点，再应用 warp。音素样本左右半段继续采用源长度语义，播放时钟的一帧输出填充继续走原有排除逻辑。没有修改 ABI、保存的控制值或放宽音素边界校验。

## 验证

相同的最终托管探针分别加载旧原生 DLL 和修复后的 Release 原生 DLL。旧版再次在 480 tick 以相同边界错误失败；420 tick 的 AN 内部 PCM 与修复版逐样本一致。

修复版在临时副本中使用当前观测到的音量，验证 420 → 480、音量 Undo/Redo、长度 Undo/Redo、直接拖回 420。所有结果 Ready，保存的时值控制指纹不变。480 tick 下 AN 从 -8.9 调至 -14.9 dB，PCM RMS 从 0.0095946249 降至 0.0048087021，实测 -6.0000026 dB。对应操作的 Undo/Redo 恢复各自检查区间的逐样本 PCM；该比较不代表长度变化前后整段音频相同。

56 项 Rust 测试、500 项托管测试通过；独立目录中的完整解决方案 Debug/Release 构建均零警告、零错误，ILRepack 成功。Release 原生 DLL 另通过延续音渲染、私有源显式拒绝、忙锁恢复、146→73→146 BPM 及失败不发布旧音频回归。

验证进程未提供 AudioPlayer，记录的 placement TargetException 已被边界捕获；声学验证直接读取 Comp 输出。首次原生库路径配置错误的运行不计入验收。所有探针都在独立 SDK 进程运行，未启动或关闭 Editor。原始工程文件与临时副本的 SHA-256 仍一致。

13:44 已按用户要求部署验证过的 Release 托管程序集及原生 `v6patch_clock.dll`。部署时 Editor 已关闭，未由代理启动或关闭；两个安装文件、主程序集链接、旧版目标和两份备份的哈希均独立校验通过。真实宿主拖动与听感待用户打开后验收。原始记录与产物摘要见 [验证证据](evidence/zhu-resize-phoneme-volume-2026-10-24.json)，操作和回退位置见 [部署证据](evidence/zhu-resize-phoneme-volume-deployment-2026-10-24.json)。

复现探针沿用 `--voice-comp-probe --patcher-dse-initialization`，设置 `VOCALOID_COMP_GENG_REPRO=1`、`VOCALOID_COMP_PROJECT_REPRO=<临时 VPR 副本>`、`VOCALOID_COMP_RESIZE_GAIN_PROBE=1`，并分别指定隔离程序集和原生库路径；该探针需要含上述 note 12 的夹具，不能对任意工程套用。
