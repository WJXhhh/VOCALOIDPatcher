# 实时四十频带接入相位生产

接续[90](90_frm2_classification_bands.md)。目标元数据再次核对为相同 DSE.dll，
SHA-256 `7a4965bd377dbc2b08710e807ae1345b67d9fff6579ca0c01a769f744366f5b5`。

主流程 `0xd33c7..0xd33f0` 从 analysis+ca8 取得特征分析器，把最新原始帧交给
`0xe3640`。运行实例确认 mode(+3b0)=0、配置+5b4=0、+5a0=0，
因此使用第90轮的 `0x7b4b0` 四十频带路径。频谱为2048 bin，采样率44100。
这些条件均在新观察器中断言，变化时拒绝沿用当前模型。

## 衔接方式

`ClassificationObserver` 在step前记录最新原始帧身份，step后从该特征分析器读取
功率谱和42个边界，独立计算全部40个频带，并逐点比较原生帧 feature+2c。
这一步显式依赖原生频谱和边界资产，不宣称从PCM独立产生频谱。

预测频带按帧身份存储。相位模型按有效频带数量，选择首个大于-100的预测值，
找不到时传入None。阈值选择不再读取原生分类字段作为输入。
level与derivative仍由自有PCM的原始能量及历史计算；观察阶段规则沿用第89轮，
未把后续覆盖的 feature12 当作原始能量。

补正频带函数默认下限为-100；第90轮原生比较显式传入该下限，所以当时结果有效，
但函数原先未传参时的-200默认值不正确。本轮32组比较改用默认参数，1,280值仍零误差。

## 全量验证

启用及关闭特征各24组，覆盖6种情形、256/512 hop、80/220 Hz：

| 比较 | 数量 | 最大误差 |
| --- | ---: | ---: |
| 原始帧能量控制量 | 3,384帧 | 0 |
| 启用特征的频带值 | 67,680 | 0 |
| 相位状态推进 | 3,144帧 | 原点及状态断言全部通过 |
| 相位ENV横、纵坐标 | 各90,344点 | 0 |

对齐相位、修正及已定义差分的比较也全部零误差。
证据：[91_live_classification_phase.json](evidence/91_live_classification_phase.json)。

```powershell
python voicebank/tools/verify_frm2_phase_pipeline.py --dll 'C:\Program Files\VOCALOID6\Editor\DSE.dll' --source-controls --output '../VOCALOIDPatcher-research-work/frm2/live_classification_91_full.json'
```

`--source-controls` 现在支持这两种特征配置。报告明确启用特征时的原生频谱依赖。
谐波仍是自有注入序列，F0仍由原生提供；同批自动F0/谐波重放、分类频谱准备及其它
特征分支尚未完成。余弦变换不参与这里的阈值选择，本轮未验证它。
五个涉及的Python工具通过语法检查，未执行无关C#构建。无Windows Temp、商业负载复制、
安装DLL修改或Editor启动。
