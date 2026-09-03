# V6 MCP 阶段 0～2 验证记录

日期：2026-08-12<br>
宿主：VOCALOID 6 Editor 6.13.0.1<br>
Transport：当前用户命名管道，独立宿主验证客户端<br>
工程：未保存的新工程；写确认在本机测试配置中关闭

## 自动化门禁

- `dotnet test VOCALOIDPatcher.McpTests/VOCALOIDPatcher.McpTests.csproj -c Debug`：29/29 通过。
- `dotnet build VOCALOIDPatcher.sln -c Release`：0 警告、0 错误，ILRepack 输出成功。
- Companion stdio、Streamable HTTP、认证失败、Schema、请求保护、写租约、幂等缓存、事件缓冲、查询预算和四领域最小契约均有自动化覆盖。

## 宿主矩阵

| 用例 | 结果 |
|---|---|
| capability/catalog | 返回 12 个细粒度 capability、7 个基线 operation contract 及错误码清单。 |
| 混合 dry-run | Track → Part → Note → Dynamics → G2PA 全链校验成功；revision 保持 2。 |
| 混合提交 | 五项状态为 `created,created,created,created,updated`；只产生一次 commit，revision 2→3。 |
| 写后读回 | 歌词 `ら`、原生音素 `4 a`、Dynamics 点数 1。 |
| 幂等 | 相同 `client_request_id` 重试返回缓存结果，revision 4→4。 |
| 稳定 ID | Track 实际移动 0→1、undo 1→0、redo 0→1，三次 `entity_id` 相同。 |
| 中途失败 | 第一项改名、第二项非法音高；返回 `operation_index=1`、`rolled_back=true`，名称和 revision 均未改变。 |
| 等待 | `project_revision_changed` 可按 event ID 读取；`wait_for_revision` 和停止态 `wait_for_playback` 均满足。 |
| 工程替换 | 先 undo 回干净新工程，再执行原生 new；project ID 改变，旧 cursor 与旧 entity 请求均返回 `stale_project`，并收到 `document_replaced`。 |
| 原生回退 | 缺省音素原先被原生 `InsertNote` 拒绝；修正为事务内合法占位后由 V6 G2PA 重算，完整链通过。 |

## 性能基线

数据为同一 AI Part、`projection=["note_number"]`、命名管道端到端测量：

| 数据量 | 返回 | 扫描 | Dispatcher | 响应 | 端到端 |
|---:|---:|---:|---:|---:|---:|
| 1,000 音符 | 1,000 | 1,000 | 101 ms | 203,891 B | 117 ms |
| 10,001 音符，首个 1,000 项页（最终实现） | 1,000 | 1,000 | 7 ms | 203,891 B | 32 ms |

10,000 音符初测为 1,668 ms，定位到循环中反复读取 `WIVSMMidiPart.Notes`、每次重建托管列表的二次增长。最终实现按 Part 只取一次列表，并按 projection 延迟读取 expression/AI expression/direct pitch。末段 tick 范围查询在 250 ms Dispatcher 预算下用 2 次调用到达 1,000 个命中项，两个 cursor 不同，证明无命中前缀也会推进扫描位置而不会循环。

## 尚未声称的范围

这里只验证路线图阶段 0～2 和已有四领域最小切片。阶段 3 以后的 BVL、Register Shift、Mixer、Audio Part、原生语义命令、完整 UI/Transport、导入和录音不因本记录而标记为已完成。
