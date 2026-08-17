using System;
using System.Collections.Generic;
using VOCALOIDPatcher.Formats.LibreSvip.Plugins.Vsqx;

namespace VOCALOIDPatcher.Formats.LibreSvip;

/// <summary>
/// 单个拼音 token 的可渲染状态（白名单矩阵条目）。
/// </summary>
internal enum RenderablePhonemeStatus
{
    /// <summary>标准音节：原生 G2PA 候选/写入路径已覆盖，补丁不干预。</summary>
    Native,

    /// <summary>非标准输入，但音素串经编辑器实测可发声（直接写入即可）。</summary>
    Verified,

    /// <summary>非标准输入，直接写入在传统轨静音，必须走分段载波渲染（临时实现）才可发声。</summary>
    Split,

    /// <summary>未实测或实测静音。</summary>
    Reject,
}

/// <summary>
/// 可渲染音素白名单 / 实测矩阵。
///
/// 传统声库的可发音性由声库自身的 Voice Table / phoneme 模板决定，不能只凭
/// “声母、韵母各自可发音”推断组合可发音。例：fong 的声母 f（在 fa 里）与韵母 UN
/// （在 dong 里）都可发声，但直接写入 f UN 在传统轨导出全零 PCM——Voice Table 里
/// 没有 f→UN 这个单元的可用数据。
///
/// 当前矩阵只约束「分段载波渲染」这一个临时手段（见
/// <see cref="ChinesePinyinPhonemeConverter.IsSplitRenderEligible"/> 与
/// SegmentedPhonemeRenderCoordinator.TryBuildPlans）：只有标记为
/// <see cref="RenderablePhonemeStatus.Split"/> 的组合才触发昂贵的双次原生渲染。
/// 一般组合（任意可转换的声母+韵母）的候选注入与音素写入保持通用，不做白名单裁剪。
///
/// 矩阵条目以编辑器实测（PCM 峰值/RMS）为准：m/n 在传统轨导出非零 PCM；
/// fong 直接写入 f UN 导出全零 PCM（Voice Table 无 f→UN 单元）。实测过程与
/// 调用链边界曾记录于 docs/native-analysis（g2pa-n5、chs-input-acceptance 等），
/// 具体文件可能随文档整理而移动，此处只固化实测结论本身。
/// </summary>
internal static class RenderablePhonemeMatrix
{
    private static readonly Dictionary<string, RenderablePhonemeStatus> StatusByPinyin =
        new(StringComparer.Ordinal)
        {
            // E5 实测（传统轨非零 PCM；仅候选层缺失，写入层已生成 m / n）：
            ["m"] = RenderablePhonemeStatus.Verified,
            ["n"] = RenderablePhonemeStatus.Verified,

            // E5 实测：直接写入 f UN 在传统轨全零 PCM；临时实现按 f + UN 分段载波
            // （首载波如 fa、尾载波如 dong）分别原生渲染后交叉淡化合并。
            // 合并输出仍须过 SegmentedPhonemeRenderCoordinator 的 MinimumAudiblePeak
            // 门限，未通过时丢弃覆盖并回退原生（静音）结果。
            ["fong"] = RenderablePhonemeStatus.Split,
        };

    /// <summary>
    /// 查询归一化拼音的可渲染状态。标准音节返回 <see cref="RenderablePhonemeStatus.Native"/>；
    /// 非标准且未列入白名单的组合返回 <see cref="RenderablePhonemeStatus.Reject"/>。
    /// 当前只有 <see cref="RenderablePhonemeStatus.Split"/> 参与行为门控（分段渲染）。
    /// </summary>
    public static RenderablePhonemeStatus GetStatus(string normalizedPinyin)
    {
        if (string.IsNullOrEmpty(normalizedPinyin))
            return RenderablePhonemeStatus.Reject;

        if (StatusByPinyin.TryGetValue(normalizedPinyin, out RenderablePhonemeStatus status))
            return status;

        return VsqxPhonemeMaps.Pinyin2Xsampa.ContainsKey(normalizedPinyin)
            ? RenderablePhonemeStatus.Native
            : RenderablePhonemeStatus.Reject;
    }
}