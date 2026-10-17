# EVEC 调试交接提示词

请接手本仓库的 VOCALOID 6 EVEC 调试及渲染链收尾。先阅读仓库 AGENTS.md 和
docs/mcp.md，保留当前未提交改动，不重置工作区。下面的机器路径是本机交接信息，
不是跨设备约定。所有结论都要区分源码检查、独立测试、真实宿主测试和人工试听。

## 本机与接入

- 仓库：`C:\Users\WJC\Documents\development\VOCALOIDPatcher`。
- Editor：`C:\Program Files\VOCALOID6\Editor`，本机 V6 参考反编译代码按
  AGENTS.md 的 VOCALOID_V6SRC / 同级 v6src 规则定位，只读。
- 2026-09-11 已复制部署合并 Release 主 DLL、原生时钟、翻译和 MCP Companion。
  主 DLL SHA-256：`E3D07535B97FE1F2BADFE5BCE5F5E86DC9C3E388CDA5B1E654FAF23423100ADC`。
  部署清单：`C:\Users\WJC\.codex\tools\v6-evec-deployment-result.json`。
  旧文件备份：`C:\Users\WJC\.codex\backups\v6-evec-debug-20260911`。
- Codex 已配置 `vocaloid6` stdio MCP。其它客户端也可使用：

```json
{
  "mcpServers": {
    "vocaloid6": {
      "command": "C:\\Users\\WJC\\.codex\\tools\\vocaloid6-mcp-schema\\VOCALOIDPatcher.McpServer.exe",
      "args": ["--transport", "stdio"]
    }
  }
}
```

Companion 使用本机已有 .NET 10 运行时，客户端按需启动。不要把它当 HTTP 服务启动。
上述 schema 目录包含 DSH 的 tools/list 兼容修复，将 schema 位置的 true 转换为 {}，
同时接受 operations 数组及一层 JSON 数组字符串。Codex 既有 debug 配置仍可使用；
DSH 的本机覆盖层和启动脚本已指向 schema 目录，实际注册 20 个工具通过。
安装目录的 mcp 副本是此前部署版本；本轮正在运行的 HTTP Companion
也可能仍是此前版本。调试请优先用上面的最终 stdio 配置，不需要退出或重启 V6。
V6 设置的 MCP 和 HTTP 已启用；若使用 HTTP，从 V6 设置复制实际端点与 Token，
不把 Token 写入仓库或调试报告。stdio 不需要手工填写 Token。
Ghidra 与 V6 MCP 是两个服务；先发现当前客户端实际可用工具，不凭服务名猜实现。

2026-09-11 已完成真实宿主预检：V6 6.13.1.1 已运行；state、catalog、空 notes 查询
成功，catalog 确实包含 notes.set_evec 和 Miku/Rin 的 EVEC 选项。
修复前 Codex 的 operations 字符串导致宿主报“operations must be a JSON array”；
最终 Companion 已兼容该传法，真实宿主 add-note dry_run 成功，工程 revision 仍为 3，
没有创建音符、取得写租约或修改工程。当前空工程没有音符，所以实际 EVEC 设置、
撤销、回滚及试听留待下面的调试矩阵，不把 dry_run 当成这些项目已通过。

## 第一阶段：确认确实可调试

### DS 宿主报告与后续修复（2026-09-11）

证据来源：用户转交的 DS 实际 V6 MCP 操作报告，本代理未重复这些写操作。
已通过：Soft/Mild/Short Release/辅音延长的单项和部分更新、单项清除和全清、
跨音符批量、dry_run revision 不变、非法字段/组合/ID/索引零副作用、批量回滚、
撤销重做。Rin/Len 的 301/延长0 与普通/延长1 虽同为 `k k a`，两次撤销及重做
仍保持各自语义，未互相污染。notes.evec 因此标记 host_verified；渲染时机仍未接入。

DS 同时发现 temp_id 创建后错绑旧实体，以及未设置 Part 语言时 notes.add 失败。
源码已核对并修正：创建前后用 native handle 比较，必须唯一识别新实体，否则回滚；
省略 language_id 时依次采用 Part、对应传统/AI 声库 native language、日语。
显式非法语言仍拒绝。新增回归测试后共 101 项通过，完整 Release 构建通过。

这两项宿主修复已于 2026-09-11 21:50 在 V6 退出后部署，使用合并 Release out 主 DLL。
安装文件 SHA-256：`1A943CD9B129BEBBCF89A822E5A046DDF5FD198866C8AE7133AF8A99B652649F`。
旧 DLL 备份：`C:\Users\WJC\.codex\backups\v6-mcp-identity-admin-20260911-215026`。
部署后未启动 V6、未修改工程；由用户重新打开 V6 后做宿主回归。
回归必须在独立测试工程覆盖：已有实体前提下，混合批次创建轨道→Part→音符，
检查所有 temp_id 与返回索引确实指向新增实体；再检查未设置语言的 Part 添加音符，
包括非日语声库及显式 language_id。不要用旧部署的结果验收新修复。

1. 列出 V6 实例，再调用 `v6_get_state`、`v6_get_catalog`。
   多实例显式指定 instance_id。检查返回中的 ok/error，工具调用完成不等于宿主成功。
2. catalog 必须出现 `notes.set_evec`、`notes.evec` 能力和声库的 `evec` 选项。
   如果只有工具描述有 set_evec，而 catalog 没有，检查宿主是否仍加载旧 DLL。
3. 分页查询 `kind=notes`、`projection=["phonemes","evec"]`，确认返回四项逻辑状态、
   声库选项、可用辅音扩展上限和 timing 预览。初次查询不要遍历整个工程。
4. 在用户同意的测试工程/测试 Part 上做 dry_run。实际写入先 acquire_write，
   使用最新 project_id/revision 和唯一 client_request_id；其他客户端持有租约时协调释放。
   首次写入、覆盖文件及替换当前工程的确认仍由 V6 控制，不修改权限来绕过。
5. 依据实际工具 schema 传参。有的桥接把 operations 暴露为 JSON 字符串，
   有的接受数组；不要把文档中的示例形状强套到所有客户端。

单个 operation 的内容是：

```json
{
  "op": "set_evec",
  "track_index": 0,
  "part_index": 0,
  "note_index": 0,
  "evec": {
    "voice_color_id": 101,
    "attack_id": 302,
    "release_id": 201,
    "consonant_extension": 0
  }
}
```

上例只适用于支持这些选项的 Miku。缺省字段保留；单项 0 关闭；
`evec={"clear":true}` 清除四项。辅音扩展 0/1/2/3 是 none/×2/×3/×4。
CTop 的额外辅音独立计算，不能把截图中的 `t t#2` 解释成用户打开了 ×2。
`v6_apply_operations` 中添加 `domain=notes`，沿用其稳定引用/临时引用机制。
临时新音符的 EVEC dry_run 尚不能完整校验其未来声库与音素，应如实报告限制。

## 已有证据与尚未完成的功能

先读 voicebank_doc/31、32、33、34；旧文档有已撤回结论，以最新更正为准。

- 用户截图 `t t#2 e|e#2|*#1` 是起音、Soft 延音、Breath-Short 收音的事件摘要。
  PPS 在标签生成路径插入真实显示字符 `|`，它不是合成音素。
- PPS 规划：Color 从逻辑起点约 45 ms 切入；Release 占末尾约 60 ms；
  90/105/150 ms 门槛及共享预算见实现；同语言后继间隔 ≤100 ms 抑制 Release。
  Short/Long 选择不同录音单元，不能简单按长短倍增时长。CTop 留在起音串。
- 传统 DSE 轨道拒绝 SetEditedPhonemePosition，不能沿 AI 接口假装已写入时值。
- EvecRenderCopy 已能只在副本里 DivideNote 并保留源工程；续接段 velocity=64、
  Accent/Decay=50、关闭 portamento，其他表达保留。不要在用户工程中永久拆音符。
- **EvecRenderCopy 尚未接入编辑器正常 WAV/score 发布、取消、缓存与播放链。**
  `renderer_integrated=false` 是真实状态，不要仅修改能力标记。
- 已完成辅音倍数显示、Luka 独立扩展能力、规范竖线解析、自定义音素保护和门槛容差。
- Miku/Rin 各 90 次独立异步原生渲染通过，有非零 score；渲染副本另有独立 probe。
  这些都不等于宿主已使用分段方案，更不等于人工听感验收。
- 旧 -50 dB RMS 活动阈值漏掉低音量气声，不能据此宣称提前截断，更不能盲目延长收音。
  异步 WAV 含 presend，音素边界应结合 timing.json 的时间映射。

## 需要完成的调试

先验证新 MCP 的单项更新、全部清除、混合批量、dry_run 无修改、非法组合拒绝、
批量后项失败回滚、撤销重做、保存重开。重点测试 Rin/Len plain CTop 301 与一次
普通辅音重复：二者音素可能完全相同，但逻辑选项不同，历史不能混淆。

再完成 EVEC 渲染副本的宿主接入：调度、去抖、原生会话互斥、WAV/score 配对发布、
播放 placement 刷新、缓存失效，以及删除/关闭/撤销/变速/换声库时的取消和安全释放。
参考 SegmentedPhonemeRenderingPatch.cs 的生命周期；它的中文两次 carrier 算法
与 EVEC 不同，不能直接套用。修改前梳理共享资源，防止重复 JVM、重复渲染和 UI 卡顿。

按隔离长音、短音门槛、连唱、间隙 100 ms 前后、跳音、力度、变速、鼻音、
Short/Long 做实际播放与导出对照，再检查连续编辑、取消、空 Part、保存重开。
只有拿到新的时间或试听证据才微调；不要为通过测试改声库登记、激活或目录发现链。

源码重点：Evec/EvecService.cs、EvecRenderCopy.cs、EvecRenderTimingPlan.cs、
EvecMcpEdit.cs；Mcp/VocaloidMcpFacade.Evec.cs、.Edit.cs、.Read.cs；
McpBridge/EvecEditRequest.cs；Patch/Patches/EvecPatches.cs。

验证命令：

```powershell
dotnet test VOCALOIDPatcher.McpTests/VOCALOIDPatcher.McpTests.csproj -c Debug
dotnet run --project voicebank/tools/evec_logic_harness/EvecLogicHarness.csproj -c Release
dotnet run --project voicebank/tools/evec_render_harness/EvecRenderHarness.csproj -c Debug -- --evec-contract-probe
dotnet build VOCALOIDPatcher.sln -c Release
```

交接时上述 MCP 测试为 90 项通过，Release 0 警告、0 错误；其中 stdio 使用假 Bridge
验证参数传输，不能替代宿主事务/撤销测试。每次改动后记录复现、修改、验证和剩余限制。
不要用未合并 DLL 部署；不要擅自覆盖用户工程；不要擅自退出仍有未保存内容的 V6。
