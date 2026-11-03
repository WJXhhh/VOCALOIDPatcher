# ACE 力度删点影响未选区域及 Sequence 终结器崩溃

2026-10-24，用户对比截图显示：删除 ACE 力度控制点后，左侧仍显示 50 的未选区域音量也变化。09:22:51 的 Windows 事件和本机崩溃转储确认 `WIVSMSequence.Finalize()` 抛出“非托管对象未释放”，进程退出。

## 力度曲线语义

核对当前只读反编译参考的 `UIControlParameter.Render`：原生控制器保持前一点的值，到下一点才垂直跳变。原 ACE PCM 后端却对相邻点线性插值。删掉中间点后，界面上保持 50 的长区间会在音频中提前向下一低值渐变。

`AceAcousticCurve.At` 现按原生规则保持前值，首点之前仍为中性。`AceAcousticHost.CaptureControls` 仅将真实事件 tick 转成时间；不再在速度变化处制造插值点。0～100 到 −1～1 的归一化保留小数。

在保存工程的临时副本中删除绝对 tick 16804～17093 的 20 个点；下一幸存点为 17107。因此比较的实际影响区间为 [16804,17107)，并非只到最后一个删除点。旧实现使此前 66,774 个 PCM16 采样变化，最大差 438 个量化单位。修复后前后区间逐样本不变，区间内部确有变化；Undo/Redo 恢复对应音频。每一步的上游音频相同，宿主处理入口输出与用同一快照新算的 DSP 输出一致。

## 私有 Sequence 所有权

已有清理逻辑能处理“管理器已移除对象，但包装仍留旧指针”，却不能处理该地址已被另一个私有对象复用的情况。`HasSequence(pointer)` 此时仍为 true，旧包装的 Close 会误关新对象。待关闭字典以地址为键，也会将同址的不同 owning 包装合并，漏掉需要保活的包装。

现在待关闭队列按托管引用身份保活；清理前核对已登记的新 owner。确认替代 owner 后，仅清空旧包装指针，保留新对象及其内部来源登记。反登记也按包装身份执行。关闭和重试仍在 owner 线程，原生失败仍保留强引用；没有全局吞掉终结器异常或对存活对象禁止终结。

隔离故障夹具重建已知的旧包装状态：旧实现会关闭替代对象，修复后替代对象及登记均存活，旧包装脱离。还验证了同址两包装从后台请求清理时分别保活、owner 线程重试归零，以及 7 次注入 Close 失败后的强制 GC 与恢复。

**现场崩溃与此漏洞的关联尚未证实。** 本次转储不含完整托管堆，无法追溯发生终结的具体包装。工程副本 12 轮 Undo/Redo、每轮两次强制 GC 未崩溃，最终待关闭数为 0；这不能替代用户重开 Editor 后的实际删点验收。

## 验证与复现

- 完整解决方案 Debug/Release：零警告、零错误；独立 artifacts 目录，Release ILRepack 成功。
- `AceAcousticOfflineTests` 51 项、`AceParameterOfflineTests` 62 项和 MCP 500 项通过。
- 四个既有载体在 MIKU V4X、V5 中文和 V6 天依共 24 个场景与各自基线逐样本相同，基线非静音。
- 工程源文件与临时副本哈希一致；没有修改、保存、重载真实工程，没有启动或关闭 Editor，没有部署。

隔离入口沿用 `--voice-comp-probe --patcher-dse-initialization`，设置 `VOCALOID_CONTROLLER_DELETE_PROBE=1`、`VOCALOID_CONTROLLER_DELETE_SOURCE` 指向临时工程副本；可用 `VOCALOID_CONTROLLER_DELETE_LOOPS` 设置 0～30 轮压力验证。`VOCALOID_SEQUENCE_REUSED_OWNER_PROBE=1` 单独执行地址复用故障夹具。程序集必须按 `PatcherIsolatedValidation=true`、Debug 和独立 artifacts 路径构建，不能部署。

本轮 SDK 为 6.13.3.1。隔离环境没有 AudioPlayer，其他 Part 的后台准备可记录被捕获异常；验证结论限于上述明确断言，不代表完整 UI 验收。汇总与原始结果位置见 [证据](evidence/ace-controller-delete-2026-10-24.json)。
