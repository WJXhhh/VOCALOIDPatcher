# 规格驱动的最小 `Sil`/`a` 构建器

## 结论

新增 `build_minimal_sil_a_bank.py`，已经把此前分散的研究步骤收束为从三段 WAV 到最终 `.ddi/.ddb` 的单命令流程。它不是只串几个文件复制命令，而是在每一层都保留失败检查：

```text
训练规格 + 3 WAV
  -> DRS external-F0 分析与最终 FRM2 派生
  -> STA 全 voiced 检查
  -> Sil->a / a->Sil 单次 voicing 边界检测
  -> outer 方向与逐帧类型强校验
  -> 单元 DDB
  -> 三单元合并与绝对指针 manifest
  -> DSE 原生双音素树骨架
  -> inner alignment 与 DBSe 收尾
  -> DSE 原生加载验收
  -> build_report.json
```

使用现有自有 0.30 秒合成 WAV 作为三份输入的端到端回归已经成功。最终输出为 1,304,518 字节 DDB 和 2,926 字节 DDI，原生回读 `load.valid=True`；公开解析器另行读出 voiced `a`、unvoiced `Sil`、STA `a`、ART `Sil a`/`a Sil`，每条 ART 52 帧。

这意味着以后拿到真人 `a`、`Sil→a`、`a→Sil` 录音时，格式研究阶段不需要手工重放几十条命令。仍需人工负责的是录音内容、F0、outer 秒边界和两侧稳定 inner 区间的质量。

## 训练规格

示例见 `minimal_sil_a_spec.example.json`。顶层字段：

```json
{
  "schema_version": 1,
  "singer_name": "my_voice",
  "pitch_hz": 220.0,
  "stationary": { ... },
  "articulations": [ ... ]
}
```

约束：

- `singer_name` 只接受 ASCII 字母、数字、下划线和连字符，同时作为 DDI/DDB stem 与 DBSe 摘要名称。
- 所有 WAV 必须是 44.1 kHz、单声道、未压缩 PCM16，时长大于 0.25 秒且不超过 30 秒。
- `articulations` 必须恰好包含 `Sil→a` 和 `a→Sil`，不能缺少、重复或加入第三条。
- 每条 ART 需要 `boundary_seconds`、`source_inner_seconds`、`target_inner_seconds`；inner 必须位于各自 outer 一侧。
- `f0_hz` 可在单元内覆盖顶层值，但必须有限且为正。

秒标注按分析得到的实际 frame count 换算：

```text
frame = round(seconds / WAV_duration * frame_count)
```

同时，DRS F0 envelope 的阶跃点用 `seconds / duration` 写入。分析后脚本不盲信这个计算值，而是从 FRM2 类型序列检测真实 split；两者不一致就停止构建。

## 运行

```powershell
python voicebank/tools/build_minimal_sil_a_bank.py `
  voicebank_doc/minimal_sil_a_spec.example.json `
  E:/voicebank-build/my_voice
```

可用 `--dse` 指定其它兼容 DSE；默认是当前安装的：

```text
C:\Program Files\VOCALOID6\Editor\DSE.dll
```

脚本只调用 DSE DLL 内的离线构造/分析/加载路径，不启动 VOCALOID Editor，不写注册表，也不部署文件。

输出目录：

```text
my_voice.ddi
my_voice.ddb
my_voice.manifest.json
build_report.json
work/
  stationary.sms2 / stationary.ddb
  sil_to_a.sms2 / sil_to_a.ddb
  a_to_sil.sms2 / a_to_sil.ddb
  skeleton/...
```

`work/` 用于诊断，不应作为普通源码提交。`build_report.json` 保存每个单元的帧偏移、SND 指针、voicing boundary、inner frame 区间和最终原生加载结果。

## 端到端回归

回归规格使用同一份完全自有合成 WAV 三次输入，边界为 0.15 秒，inner 为 0.02–0.13 与 0.17–0.28 秒。自动换算结果：

```text
split frame       = 26
source inner      = [3,23)
target inner      = [29,49)
Sil -> a voicing  = 26 unvoiced + 26 voiced
a -> Sil voicing  = 26 voiced + 26 unvoiced
```

构建器内部先完成两个 Release harness 构建，然后三次分析、三次单元打包、树生成、最终收尾和原生回读全部成功。DSE 读回：

```text
stationary SND core        = 607,386
Sil -> a payload/core      = 939,556 / 941,604
a -> Sil payload/core      = 1,273,798 / 1,275,846
both alignment groups      = [0,26,3,23] / [26,52,29,49]
root.authenticated         = 1
load.valid                 = True
```

## 当前边界

这是“最小训练流水线成立”，不是“完整声库训练完成”：

- 当前只支持 `Sil` 与 `a` 两个音素、一个音高层和两条边界 ART。
- F0 是整段 voiced 侧的常量；真人录音还需评估逐帧 F0 曲线或更稳健的外部 F0。
- 尚未对真实静音/人声边界的 2048-sample 窗泄漏做听感和频谱 QA。
- 尚未进入宿主 VSM 渲染，许可证/UI 发现门槛也仍与格式加载分开。
- 多音素、多 ART、多音高层、录音句表压缩和中文覆盖仍属于 M4/M5。

不过，这条流水线已经把“是否能从自有标注生成一对结构正确、帧类型正确、原生可加载的 `Sil↔a` 单元”从推测变成了可重复的实测答案。
