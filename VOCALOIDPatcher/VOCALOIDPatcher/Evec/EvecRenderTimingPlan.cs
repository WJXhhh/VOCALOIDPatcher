using System;
using System.Collections.Generic;

namespace VOCALOIDPatcher.Evec;

internal enum EvecRenderSegmentKind
{
    Base,
    Color,
    Release,
}

internal readonly record struct EvecRenderSegment(
    EvecRenderSegmentKind Kind,
    double BeginMs,
    double EndMs,
    string Phonemes);

/// <summary>
/// Experimental render-event plan, exercised by the standalone render harness.
/// Not yet wired to the editor renderer. Times are relative to the logical note
/// start, not to the automatically allocated first vowel. The caller converts
/// every boundary through the sequence tempo map, sharing adjacent tick edges.
/// </summary>
internal static class EvecRenderTimingPlan
{
    internal const double ConnectedNoteGapMs = 100.0;

    internal static bool TryCreate(
        string phonemes,
        EvecNoteState state,
        double durationMs,
        double? nextNoteGapMs,
        bool nextNoteSameLanguage,
        out IReadOnlyList<EvecRenderSegment> segments)
    {
        segments = Array.Empty<EvecRenderSegment>();
        if (!double.IsFinite(durationMs) || durationMs <= 0 ||
            (nextNoteGapMs.HasValue && !double.IsFinite(nextNoteGapMs.Value)) ||
            !EvecPhonemeRecomposer.CanRepresent(phonemes, state))
            return false;

        string basePhonemes = EvecPhonemeRecomposer.StripEvec(phonemes);
        if (string.IsNullOrWhiteSpace(basePhonemes))
            return false;
        string[] tokens = basePhonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // The observed Japanese CVV/VSil path uses a terminal nucleus. Do not
        // silently discard a custom coda or apply this model to other languages.
        if ((state.HasVoiceColor || state.HasVoiceRelease) &&
            !EvecPhonemeRecomposer.IsColorablePhoneme(tokens[^1]))
            return false;

        var attackState = state.Clone();
        attackState.VoiceColorId = EvecConstants.VoiceColorNone;
        attackState.ReleaseId = EvecConstants.ReleaseNone;
        string attackPhonemes = EvecPhonemeRecomposer.Recompose(basePhonemes, attackState);

        double colorBegin = 0;
        bool color = state.HasVoiceColor && EvecTimingMath.TryCalculateDivide(
            durationMs,
            EvecTimingMath.CommonDivideStartMs, EvecTimingMath.CommonDivideEndMs,
            EvecTimingMath.CommonLimitStartMs, EvecTimingMath.CommonLimitEndMs,
            EvecTimingMath.MinVowelDurationMs, out colorBegin);

        // Tempo-map subtraction can express exactly 100 ms as
        // 100.00000000000009. Ignore floating-point noise, not a musical gap.
        bool connected = nextNoteSameLanguage && nextNoteGapMs.HasValue &&
                         nextNoteGapMs.Value <= ConnectedNoteGapMs + 1e-7;
        double releaseDuration = 0;
        bool release = state.HasVoiceRelease && !connected && EvecTimingMath.TryCalculateDivide(
            durationMs - (color ? colorBegin : 0),
            EvecTimingMath.VoiceReleaseDivideStartMs, EvecTimingMath.VoiceReleaseDivideEndMs,
            EvecTimingMath.VoiceReleaseLimitStartMs, EvecTimingMath.VoiceReleaseLimitEndMs,
            EvecTimingMath.MinVowelDurationMs, out releaseDuration);
        double releaseBegin = durationMs - (release ? releaseDuration : 0);

        var result = new List<EvecRenderSegment>(3)
        {
            new(EvecRenderSegmentKind.Base, 0, color ? colorBegin : releaseBegin, attackPhonemes),
        };
        if (color)
            result.Add(new(EvecRenderSegmentKind.Color, colorBegin, releaseBegin,
                tokens[^1] + state.ColorSuffix));
        if (release)
            result.Add(new(EvecRenderSegmentKind.Release, releaseBegin, durationMs, state.ReleasePhoneme));
        segments = result.AsReadOnly();
        return true;
    }
}
