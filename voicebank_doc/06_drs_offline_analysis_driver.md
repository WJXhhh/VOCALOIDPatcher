# DRS 离线声学分析驱动与 PCM 输入路径

## 结论摘要

DSE 6.13.1.1 中不仅存在供 `wbhsm_getAudioChunk` 使用的运行时 `DRS::CSMSAnalysis`，还保留了一条完整的内存 PCM 批处理驱动 `0x1800d9b10`。这条函数没有出现在 DSE 导出表中，当前也没有直接代码调用者，只有数据表引用；因此它能证明 DRS 分析器的输入/输出契约，但还不能当作稳定、受支持的声库制作 API。

已经确认：

- 输入可以是 `DRS::CSoundIO`，也可以是一个内存 float PCM 描述符；不要求分析器自己打开 WAV。
- 源音频按 128 samples 分块送入环形缓冲。
- 分析主函数不接收 PCM 参数，而是从对象内的环形缓冲取窗；每次成功产生一帧后，批处理驱动将该 `CSMSFrame` 交给派生类的插帧虚方法。
- hop 的构造公式已经恢复。默认配置 `86.1328125` 会得到 512 samples；参考传统库使用的有效值 `172.265625` 会得到 256 samples，现已由独立 harness 的输出时间步再次验证。
- 普通参考库的 2048-sample 窗口及边缘扩展仍由全库数据不变量确认；它不是 `0x1800d9b10` 的 128-sample 输入块大小。

## 对象和虚方法表

`DRS::CSMSAnalysis`：

- 构造函数：`0x1800cdde0`
- vtable：`0x1805bef20`
- 析构包装：`0x1800cf6e0`
- 分析一帧：`0x1800d2cc0`
- 初始化输出：`0x1800da0d0`
- 插入输出帧：`0x1800da130`
- 收尾：`0x1800da150`

`DRS::CSMSRegionAnalysis`：

- 构造包装：`0x1800956b0`
- vtable：`0x1805bebd8`
- 析构包装：`0x1800956e0`
- 分析一帧包装：`0x180095730`
- 初始化 region：`0x180095780`
- 将 frame 加入 region：`0x1800957f0`
- 收尾 region：`0x1800959b0`

两个表的前五个业务槽一致：

| vtable 偏移 | 基类 | region 派生类 | 契约 |
| ---: | --- | --- | --- |
| `+0x08` | `0x1800d2cc0` | `0x180095730` | 尝试产生下一帧 |
| `+0x10` | `0x1800da0d0` | `0x180095780` | 初始化输出容器/region |
| `+0x18` | `0x1800da130` | `0x1800957f0` | 提交一帧 |
| `+0x20` | `0x1800da150` | `0x1800959b0` | 完成分析 |

派生类的 `+0x08` 先调用同一个 `0x1800d2cc0`；成功产帧后再按内部状态执行一个额外 hook。`+0x18` 会调整帧时间并把它加入当前 `CSMSRegion`；到达分区条件时关闭当前 region 并建立下一段。

## 批处理驱动 `0x1800d9b10`

反编译签名可保守表示为：

```text
AnalyzeBatch(
    CSMSAnalysis *analysis,
    CSoundIO *source_or_null,
    CSMSCollection *output,
    unknown,
    FloatPcmDescriptor *fallback_or_null)
```

当 `source_or_null != null` 时，驱动从 `CSoundIO` 取得：

| `CSoundIO` 偏移 | 类型 | 用途 |
| ---: | --- | --- |
| `+0x13c` | `i32` | PCM sample 数 |
| `+0x140` | `i32` | sample rate |
| `+0x160` | `i16 *` | 16-bit PCM 缓冲 |
| `+0x170` | `float *` | 可选的同源 float 缓冲 |

`0x180048250` 按给定起点从 `+0x160` 读 `i16`，越过有效范围时补零。批处理驱动每次请求 128 samples，再逐样本转换为 float；数值保持 16-bit PCM 的原幅值尺度，没有归一化到 `[-1, 1]`。

当 `source_or_null == null` 时，fallback 的已观察布局是：

```text
+0x00  float *pcm
+0x08  u32 sample_count
+0x0c  u32 sample_rate
```

这条路径直接把 float 数组的 128-sample 切片交给分析器。因此 WAV/AIFF 解析并不是 `CSMSAnalysis` 的职责；一个独立构建器可以先自行解码为 44.1 kHz 单声道 PCM，再按该内存契约供给分析阶段。

### 驱动状态机

`0x1800d9b10` 的主流程已经可以还原为：

```text
reset analysis and output state
create/get CSMSGenericTrack in output collection
call vtable + 0x10                       // initialize

for each 128-sample input block:
    convert i16 to float, or use fallback float slice
    push block into analysis ring

    while vtable + 0x08 does not return 1:
        if analysis produced current_frame:
            call vtable + 0x18           // insert frame/region

when configured analysis range is exhausted:
    call vtable + 0x20                   // finalize
```

这里返回值 `1` 表示当前输入下没有可继续取出的完整帧；外层会先补下一块 PCM，再继续 drain。输出 frame 的暂存字段是对象 `+0xcc8`（以 `u64` 视图看是索引 `0x199`）。

## PCM 环形缓冲与时间推进

真正的 PCM push 函数是 `0x1800d1140`。对象字段用途如下：

| 偏移 | 类型 | 已确认用途 |
| ---: | --- | --- |
| `+0xad0` | pointer | 大型分析配置对象 |
| `+0xad8` | `i32` | sample rate |
| `+0xae0` | `float *` | 原始 PCM 环形缓冲 |
| `+0xae8` | `double *` | 可选预处理/滤波环形缓冲 |
| `+0xaf0` | `i32` | 环形缓冲容量 |
| `+0xaf4` | `i32` | 写位置 |
| `+0xaf8` | `f64` | 已供给输入的累计秒数 |
| `+0xb00` | `i32` | 当前分析读位置 |
| `+0xba8` | `f64` | 每帧时间步长（秒） |
| `+0xbb0` | `i32` | hop samples |
| `+0xc10` | `f64` | 当前输出帧时间 |

`0x1800d1140` 会处理环形末尾回绕，然后严格执行：

```text
write_pos  = (write_pos + input_count) % ring_capacity
input_time = input_time + input_count / sample_rate
```

`0x1800d2cc0` 完成一次帧推进后执行：

```text
frame_time = frame_time + frame_step_seconds
read_pos   = (read_pos + hop_samples) % ring_capacity
```

构造函数 `0x1800cdde0` 从配置 `+0x18` 计算：

```text
hop_samples       = round(sample_rate / config_value_0x18)
frame_step_seconds = hop_samples / sample_rate
```

配置构造函数给 `+0x18` 的默认值是 `86.1328125`，在 44,100 Hz 下对应 512-sample hop；它不是参考商业库的实际生成设置。本批 44,100 Hz 传统库的相邻 EpR 时间差严格为 `256 / 44100`，所以其有效配置对应 `hop_samples = 256`，也就是 `config_value_0x18 = 172.265625`。独立 harness 覆盖该值后，生成 SMS2 的相邻帧时间也严格为 `256 / 44100`。这不是推测的窗口大小：普通 STA/ART 的 2048-sample 分析窗另由全部 56,887 个样本的 `pcm_count = frame_count * 256 + 2048` 和 STA 的 1024-sample 对齐点共同确认。

环形容量构造时先设为一个 sample rate 的样本数，再根据启用的分析模块、历史帧数和 look-ahead 增长。因此实现时不应把容量写死为 44,100；真正的不变量是它必须容纳最大分析窗、历史和前视需求。

## 分析范围配置

批处理驱动把配置 `+0x1c`、`+0x20` 作为源长度的归一化比例：

```text
start = clamp(floor(sample_count * config[0x1c]), 0, sample_count - 2)
end   = clamp(max(start + 1, floor(sample_count * config[0x20])), ..., sample_count - 1)
```

分析器在该区间外不继续提交有效帧。这个范围与 DDI 中 ART/STA 的音素/region 标注还没有完成对应；目前只能确认它控制本次 PCM 的分析裁剪边界。

配置 `+0x5a0` 会切换两套明显不同的构造与分析分支。`0` 分支创建完整的 F0、谐波、共振和多帧工作对象；非零分支更紧凑。它很可能与普通/VQM 或简化分析模式有关，但在找到配置名称和值来源前不作最终命名。

## 与自训目标的距离

本轮关闭了以下未知：

- PCM 可以怎样进入 `CSMSAnalysis`；
- 输入块、环形缓冲、hop 和输出 frame 的调度关系；
- collection/track/region 的初始化、逐帧提交与 finalize 顺序；
- 分析器并不依赖 WAV 路径，可用自有内存 PCM 驱动。
- 大型配置对象的构造、默认值读取以及 77 个动态参数槽的基本布局；
- 自动 F0 与外部 F0 的分支：动态参数 `0x14 == 0` 时，参数 `0x0d` 直接作为每帧 F0；
- DRS collection 可以由自身 writer 写为 SMS2，并由同一 DSE 的 reader 无损载回。

仍未关闭：

1. 大多数配置 ID 的正式名称、普通/VQM 模式选择与版本依赖；
2. 音素/region 标注怎样由外部切分结果注入；
3. DRS `CSMSCollection/CSMSFrame` 到最终 DSE5 `.tree/.ddi/.ddb` 的全部转换与字段筛选；
4. 位 0 幅度标度、位 22 第二共振组以及两个 ENV 的精确生成算法；
5. 对一段真正自有的人声 PCM 执行同样闭环，而不只是程序生成的谐波测试信号。

因此当前判断是：离线分析数据流和 DRS 自身序列化闭环已经恢复，但“有把握自训”仍不能结项。下一步优先定位 DRS SMS2 到 DSE5 最终帧的转换/字段筛选路径，并用自有短元音验证外部 F0 与 region 切分。可运行实验与原始输出摘要见 `07_drs_sms2_harness.md`。
