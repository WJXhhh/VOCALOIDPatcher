# 独立探针不能复用编辑器的 VSM 应用 ID

2026-10-20，排查 `geng @N→k` / `zhuang uAN→Sil` 随机渲染失败时，确认独立探针存在临时目录隔离缺陷。

`evec_render_harness` 原先默认使用 `WVSMModuleIF.CreateManager("VOCALOID6", ...)`。VSM 的管理器在创建和销毁时都会递归清理 `LOCALAPPDATA/VOCALOID6/VSM/<applicationId>`。这个 ID 没有跨进程所有权保护；独立进程并不代表独立临时资源。编辑器仍打开时运行旧探针，会删除编辑器及其局部规划序列正在使用的目录。

## 修正

探针统一通过 `ProbeManagerIdentity.Create` 生成 `VOCALOIDPatcher.Probe.<PID>[.<label>]`。`VOCALOID_PROBE_MANAGER_ID` 只作为后缀标签，不能替换整个 ID；限制字符与长度，拒绝路径分隔符。不同探针进程使用不同的临时根目录。VDM 的数据库应用 ID 没有因此改动。

新旧 SDK 的五组真实引擎对照均通过，并在探针退出后确认运行中的编辑器目录仍存在。269 项托管测试通过，其中新增隔离、覆盖标签和路径穿越回归。所有构建使用独立 artifacts 目录，没有替换已部署 DLL。

## 本次现场

当前原生任务保留的取消事件为 `FailedToGetTempDirPath`（8），任务代次与对应事件一致。新的控制快照未进入 cache/DSE 路径，因此“新快照失败计数为零”不能解释成渲染成功。

检查确认编辑器的主序列和两个内部规划序列的临时目录均已丢失。仅重建空目录仍未恢复本次实例。将当前修改另存到维护目录中的诊断副本、核对工程与自定义控制归档后，在同一编辑器进程重载副本，实际渲染分数和波形均恢复有效。之后通过 Computer Use 拖动 `zhuang uAN→Sil` 菱形、再撤销，两次实际渲染均有效。编辑器没有重启，原工程文件没有覆盖，部署 DLL 哈希未改变。

这证明了探针隔离缺陷及本次后续现场的目录错误。它仍不足以证明用户最初的随机过渡错误与探针具有同一个起因；该区别保留在证据记录中，不能把重新加载成功当作过渡 DSP 根因已修复。

原生函数定位、版本、验证结果及限制见 [证据记录](evidence/geng-transition-render-stall.json)。PCM 不一致问题继续暂缓。
