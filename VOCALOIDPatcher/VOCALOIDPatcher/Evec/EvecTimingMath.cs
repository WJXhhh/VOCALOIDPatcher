using System;

namespace VOCALOIDPatcher.Evec;

/// <summary>
/// Pure implementation of PPS's UEVECDivideInfo duration calculation.
/// Values are expressed in milliseconds by the managed adapter.
/// </summary>
internal static class EvecTimingMath
{
    public const double CommonDivideStartMs = 45.0;
    public const double CommonDivideEndMs = 45.0;
    public const double CommonLimitStartMs = 30.0;
    public const double CommonLimitEndMs = 60.0;

    public const double VoiceReleaseDivideStartMs = 60.0;
    public const double VoiceReleaseDivideEndMs = 60.0;
    public const double VoiceReleaseLimitStartMs = 50.0;
    public const double VoiceReleaseLimitEndMs = 70.0;

    public const double MinVowelDurationMs = 45.0;

    /// <summary>
    /// Calculates the leading articulation duration using the operation order
    /// observed in PPS FUN_10219630. A false result means PPS would omit the
    /// split because the interval cannot retain the configured minimum vowel.
    /// </summary>
    public static bool TryCalculateDivide(
        double availableDurationMs,
        double divideStartMs,
        double divideEndMs,
        double limitStartMs,
        double limitEndMs,
        double minVowelDurationMs,
        out double divideMs)
    {
        divideMs = 0.0;
        if (!double.IsFinite(availableDurationMs) ||
            !double.IsFinite(divideStartMs) ||
            !double.IsFinite(divideEndMs) ||
            !double.IsFinite(limitStartMs) ||
            !double.IsFinite(limitEndMs) ||
            !double.IsFinite(minVowelDurationMs) ||
            availableDurationMs <= 0.0 ||
            divideStartMs <= 0.0 ||
            divideEndMs < divideStartMs ||
            limitEndMs <= limitStartMs ||
            minVowelDurationMs < 0.0)
        {
            return false;
        }

        double candidate =
            ((divideEndMs - divideStartMs) / (limitEndMs - limitStartMs)) * availableDurationMs +
            divideStartMs;

        if (candidate > divideEndMs)
            candidate = divideEndMs;
        else if (candidate < divideStartMs)
            return false;

        double maximumWithVowel = availableDurationMs - minVowelDurationMs;
        if (candidate <= maximumWithVowel)
        {
            divideMs = candidate;
            return true;
        }

        if (maximumWithVowel < divideStartMs)
            return false;

        divideMs = maximumWithVowel;
        return true;
    }
}
