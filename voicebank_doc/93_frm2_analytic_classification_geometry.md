# 分类窗与频带边界的解析生成

接续[92](92_frm2_owned_classification_power.md)。再次核对同一DSE.dll元数据，
并查询分类构造器 `0xe2b90`：当前默认分支的窗与边界均由公式生成，无须读取系数表。

## 构造规则

- 窗长度：`floor(rate*1024/22050)+1`，构造代码先执行整数除法。
- 类型11，lookup=0、derivative=0，复用已验证的解析余弦窗公式。
- half长度：`int(2**ceil(f32(log(size-1))/log(2))*0.5)`。
  必须保留logf的float舍入；例如44100 Hz下size=2049、half=2048，
  不能用通常的整数“向上取二次幂”规则替换。
- 边界：`low=log(1.0571428571428572)*1127.01048`，
  `step=(log(8.142857142857142)*1127.01048-low)/25`，
  第i项为 `f32((exp((i*step+low)/1127.01048)-1)*700)`，i=0..41。
  不是把40 Hz到5000 Hz平均分成41段；5000 Hz位于第25项，后续仍继续外推。

这些标量来自构造器运算及常量交叉核对，未复制窗表或边界数组。
新模块 `frm2_classification_geometry.py` 不访问原生内存。
观察器使用预测窗和预测边界作后续计算；原生系数只作逐项比较。

## 构造验证

8000、16000、22050、32000、44100、48000、96000 Hz的窗口长度、half长度均一致，
合计12,660个窗/边界值全部零误差。该检查仅证明这些采样率的构造规则，
完整相位流程仍只在44100 Hz执行。

```powershell
python voicebank/tools/verify_frm2_classification_geometry.py --dll 'C:/Program Files/VOCALOID6/Editor/DSE.dll' --output '../VOCALOIDPatcher-research-work/frm2/classification_geometry_93.json'
```

当前默认分类路径不再依赖原生窗、边界或功率计算。仍保留原生调度、F0输入和自有谐波注入，
不宣称同批自动F0/谐波重放、其它分类配置或整个商业制作预设已经完成。


## 联合链验证

替换预测输入后，48组全量复验通过：3,466,908个功率缓冲值、67,680个频带值、
90,344个相位ENV横纵坐标全部零误差；原有控制量、状态与对齐检查仍通过。
证据：[93_analytic_classification_geometry.json](evidence/93_analytic_classification_geometry.json)。

```powershell
python voicebank/tools/verify_frm2_phase_pipeline.py --dll 'C:/Program Files/VOCALOID6/Editor/DSE.dll' --source-controls --output '../VOCALOIDPatcher-research-work/frm2/analytic_classification_93_full.json'
```

四个涉及工具通过Python语法检查。没有使用Windows Temp、修改安装DLL、复制商业帧或启动Editor。
