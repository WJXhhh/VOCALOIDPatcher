# 二分音符 yuan 的左侧过渡右边界专项复现

2026-10-22，按用户指定的场景单独测试。原生桥与安装目录的 DLL SHA256 相同，使用本机 6.13.3.1 SDK/DSE，未启动或修改 Editor。

- 中文 `yuan` 按仓库拼音映射使用原生音素 `y{_n`。
- 一个音符，960 tick，即 120 BPM 下的二分音符、1 秒；Velocity 64，未加过渡音量。
- V5 中文、V6 中文各测试 MIDI 36、48、60、72、84，包含常用低/中/高音及两端极端音区。
- 目标明确为最左侧 `Sil→y{_n` 的 `End`，对应 UI 左侧过渡带的右边界。Begin、Split 和音符起止位置保持原意。
- 从最小可达 End 扫描至最大可达 End，包括 25%、50%、75%、90%、99%、100% 位置；分别比较只拖边界与完整来源模式。另测两个端点处的进度 ±1，包含热身、基准与恢复。

共 200 次渲染，30 个无法保留完整来源端点的组合被明确拒绝；所有已消费 ART 来源映射正确，原生 timing/gain 失败数为零。只拖右边界的测试没有添加进度或音量参数。

| 声库 | MIDI | 原 End | 最大 End | 向右拖动量 |
| --- | ---: | ---: | ---: | ---: |
| V5 | 36 / 48 / 60 | 3375 | 3497 | 708.2 ms |
| V5 | 72 / 84 | 3377 | 3498 | 702.4 ms |
| V6 | 36 / 48 / 60 | 3375 | 3496 | 702.4 ms |
| V6 | 72 / 84 | 3379 | 3496 | 679.2 ms |

最大扩张后，左侧过渡占据音符约前 0.88 秒，足以覆盖“狠拉”场景。原始 PCM 及局部包络图可见起音显著拉长、不同音区的包络形状和幅度变化。40 ms 局部波形中未观察到持续平顶削波，全部原始输出最高峰 8099/32768，满幅样本数为零；最长非静音同值平台为 7 样本，不能据此判定方波听感。

使用当前自动平滑文件处理器重新处理全部 200 份原始 WAV，实际修改 11 份；没有新增满幅样本或增加任一文件的全局最大相邻样本差，窗口外 PCM 完全一致。这仅验证离线文件处理，不代表宿主播放、显示和主观听感已验收。本轮仍不能确认用户报告的方波已经复现。

原始 WAV、平滑输出、完整 10 组包络/局部波形/来源进度图和逐样例指标保留在仓库外。摘要证据见 [yuan-left-transition-2026-10-22.json](evidence/yuan-left-transition-2026-10-22.json)。生产处理代码本轮没有进一步修改。

复跑入口：

```powershell
dotnet build voicebank/tools/evec_render_harness/EvecRenderHarness.csproj -c Debug --artifacts-path <独立构建目录>
$env:VOCALOID_TIMING_PROBE_NATIVE = '<待测原生桥 DLL>'
dotnet <harness.dll> --yuan-transition-probe --patcher-dse-initialization <输出目录>
dotnet <harness.dll> --transition-artifact-reprocess <输出目录>
python -B voicebank/tools/analyze_transition_repair.py <输出目录> <摘要.json>
python -B voicebank/tools/analyze_yuan_transition.py <输出目录>
```

分析分别需要 NumPy，以及 NumPy/Matplotlib。所有 WAV、PNG 和构建产物保存在仓库外。
