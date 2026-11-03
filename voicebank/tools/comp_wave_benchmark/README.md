# Comp WAV 拼接基准

独立 .NET 8 控制台，链接生产 `CompWave` 与模型，不加载 Editor 或专有 SDK。创建固定 44.1 kHz 合成 PCM，比较完整拼接与预览峰值生成，另外输出八组格式验证摘要。

```powershell
dotnet build voicebank/tools/comp_wave_benchmark/CompWaveBenchmark.csproj -c Release --artifacts-path <临时构建目录>
& <临时构建目录>/bin/CompWaveBenchmark/release/CompWaveBenchmark.exe <临时结果目录>
```

通过 `-p:CompWaveSource=<修改前 CompWave.cs 的绝对路径>` 可用相同驱动编译基线。旧代码没有 `MixWithPeaks` 时，驱动自动调用 `Mix` 后调用 `Peaks`；新代码使用组合入口。更换源码必须使用独立 artifacts 目录，以免旧增量输出影响对照。

每个性能场景预热至少一秒、至少三轮，然后计时 30 次，包含 WAV 写出/替换，不包含夹具创建和摘要计算。建议 A/B、B/A、A/B 交错运行，并在所有格式中比较 WAV/峰值 SHA-256 和削波计数。计时是热文件缓存下的局部处理开销，不能代替 Editor 端到端测量。

结果为 `comp-wave-benchmark.json` 与 `comp-wave-formats.json`，音频与报告均留在指定结果目录，不应提交生成物。
