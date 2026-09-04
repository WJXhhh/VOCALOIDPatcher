using System;
using System.Collections.Generic;
using VOCALOIDPatcher.Utils;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Evec;

/// <summary>
/// Maps Piapro Studio's millisecond articulation constraints onto V6 phoneme
/// boundaries without replacing user-authored note expression or velocity.
/// </summary>
internal static class EvecTimingAllocator
{
    /// <summary>
    /// Applies EVEC timing boundaries to the specified note.
    /// This method is designed to be called inside a VSM Transaction.
    /// </summary>
    public static bool ApplyTiming(WIVSMNote note, EvecNoteState state)
    {
        if (note == null || !state.HasAnyEvec)
            return true;

        try
        {
            return ApplyBoundaries(note, state);
        }
        catch (Exception ex)
        {
            Debug.Print($"[EVEC] ApplyTiming error: {ex.Message}");
            return false;
        }
    }

    internal static void ResetRemovedTiming(
        WIVSMNote note,
        EvecNoteState previousState,
        EvecNoteState newState)
    {
        if (note == null)
            return;

        try
        {
            string[] tokens = note.Phonemes.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);
            int vowelIdx = FindNucleusIndex(tokens);
            if (vowelIdx < 0)
                return;

            if (previousState.HasConsonantAttack && !newState.HasConsonantAttack)
                note.ResetEditedPhonemePosition(vowelIdx);

            if (previousState.HasVoiceColor && !newState.HasVoiceColor &&
                vowelIdx + 1 < tokens.Length)
                note.ResetEditedPhonemePosition(vowelIdx + 1);

            if (previousState.HasVoiceRelease && !newState.HasVoiceRelease &&
                tokens.Length > 0)
                note.ResetEditedPhonemePosition(tokens.Length - 1);
        }
        catch (Exception ex)
        {
            Debug.Print($"[EVEC] ResetRemovedTiming error: {ex.Message}");
        }
    }

    private static bool ApplyBoundaries(WIVSMNote note, EvecNoteState state)
    {
        string phonemes = note.Phonemes;
        if (string.IsNullOrWhiteSpace(phonemes))
            return true;

        string[] tokens = phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
            return true;

        List<int> positions = note.GetPhonemePositions();
        if (positions == null || positions.Count < tokens.Length + 1)
            return true;

        // Find token indices
        int vowelIdx = FindNucleusIndex(tokens);

        if (vowelIdx < 0)
            vowelIdx = 0;

        int vowelStartTick = positions[vowelIdx];

        // Piapro's VSil definitions use the same fixed 60 ms divide for both
        // *#1 and *#2. Short/Long selects a different recorded release unit;
        // it is not a percentage of the note. Anchor the boundary to the
        // logical note end instead of VSM's newly generated phoneme end.
        int phonemeEndTick = checked((int)note.DurationTick.Value);

        // 1. Voice Release Timing (*#1 or *#2)
        int releaseStartTick = phonemeEndTick;
        if (state.HasVoiceRelease)
        {
            int releaseIdx = tokens.Length - 1;
            if (tokens[releaseIdx].StartsWith("*#", StringComparison.Ordinal))
            {
                TrySetPiaproBoundary(
                    note,
                    releaseIdx,
                    positions[releaseIdx],
                    vowelStartTick,
                    phonemeEndTick,
                    reverse: true,
                    EvecTimingMath.VoiceReleaseDivideStartMs,
                    EvecTimingMath.VoiceReleaseDivideEndMs,
                    EvecTimingMath.VoiceReleaseLimitStartMs,
                    EvecTimingMath.VoiceReleaseLimitEndMs,
                    out releaseStartTick);
            }
        }

        // 2. CTop timing. PPS writes an additional consonant copy immediately
        // before the nucleus (C C[#] V); the caret spelling exists only in the
        // internal DDI ART key. Map the Common 45 ms rule to the final CTop
        // copy. Earlier plain copies belong to the independent 0-3 extension
        // count and retain VSM's natural allocation.
        if (state.HasConsonantAttack && vowelIdx > 0)
        {
            int attackIdx = vowelIdx - 1;
            int attackStartTick = positions[attackIdx];
            TrySetPiaproBoundary(
                note,
                vowelIdx,
                positions[vowelIdx],
                attackStartTick,
                releaseStartTick,
                reverse: false,
                EvecTimingMath.CommonDivideStartMs,
                EvecTimingMath.CommonDivideEndMs,
                EvecTimingMath.CommonLimitStartMs,
                EvecTimingMath.CommonLimitEndMs,
                out vowelStartTick);
        }

        // 3. Voice Color Timing (CVV: Consonant + Base Vowel + Colored Vowel)
        if (state.HasVoiceColor && vowelIdx >= 0 && vowelIdx + 1 < tokens.Length)
        {
            int colorVowelIdx = vowelIdx + 1;
            string colorToken = tokens[colorVowelIdx];
            if (colorToken.Contains('#'))
            {
                TrySetPiaproBoundary(
                    note,
                    colorVowelIdx,
                    positions[colorVowelIdx],
                    vowelStartTick,
                    releaseStartTick,
                    reverse: false,
                    EvecTimingMath.CommonDivideStartMs,
                    EvecTimingMath.CommonDivideEndMs,
                    EvecTimingMath.CommonLimitStartMs,
                    EvecTimingMath.CommonLimitEndMs,
                    out _);
            }
        }

        return true;
    }

    private static int FindNucleusIndex(IReadOnlyList<string> tokens)
    {
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            if (EvecPhonemeRecomposer.IsColorablePhoneme(tokens[i]))
                return i;
        }

        return -1;
    }

    private static bool TrySetPiaproBoundary(
        WIVSMNote note,
        int index,
        int current,
        int intervalBegin,
        int intervalEnd,
        bool reverse,
        double divideStartMs,
        double divideEndMs,
        double limitStartMs,
        double limitEndMs,
        out int applied)
    {
        applied = current;
        double availableDurationMs = MillisecondsBetween(note, intervalBegin, intervalEnd);
        if (!EvecTimingMath.TryCalculateDivide(
                availableDurationMs,
                divideStartMs,
                divideEndMs,
                limitStartMs,
                limitEndMs,
                EvecTimingMath.MinVowelDurationMs,
                out double divideMs))
        {
            // PPS returns zero and omits the split when the interval is too
            // short. Keep the selected EVEC recording/state, but retain V6's
            // native boundary instead of squeezing it into the note.
            return false;
        }

        int desired = reverse
            ? TickAtMilliseconds(note, intervalEnd, -divideMs)
            : TickAtMilliseconds(note, intervalBegin, divideMs);
        var range = note.GetAcceptablePhonemePositionRange(index);
        range.Normalize();
        if (range.DurationTick.Value <= 0)
            return false;

        if (desired < range.Begin || desired > range.End)
            return false;

        applied = desired;
        return note.SetEditedPhonemePosition(index, new VSMRelTick(applied));
    }

    private static double MillisecondsBetween(WIVSMNote note, int beginRelativeTick, int endRelativeTick)
    {
        if (endRelativeTick <= beginRelativeTick)
            return 0.0;

        WIVSMSequence? sequence = note.Parent?.Sequence;
        if (sequence != null)
        {
            long notePosition = note.AbsPosTick.Value;
            var begin = new VSMAbsTick(notePosition + beginRelativeTick);
            var end = new VSMAbsTick(notePosition + endRelativeTick);
            return sequence.GetTimeFromTick(begin, end) * 1000.0;
        }

        const double ticksPerMillisecond = 0.96; // 120 BPM fallback
        return (endRelativeTick - beginRelativeTick) / ticksPerMillisecond;
    }

    private static int TickAtMilliseconds(WIVSMNote note, int originRelativeTick, double milliseconds)
    {
        WIVSMSequence? sequence = note.Parent?.Sequence;
        if (sequence != null)
        {
            long notePosition = note.AbsPosTick.Value;
            var origin = new VSMAbsTick(notePosition + originRelativeTick);
            long result = sequence.GetTickFromTime(origin, milliseconds / 1000.0).Value - notePosition;
            return checked((int)result);
        }

        const double ticksPerMillisecond = 0.96; // 120 BPM fallback
        return checked(originRelativeTick + (int)Math.Round(milliseconds * ticksPerMillisecond));
    }
}
