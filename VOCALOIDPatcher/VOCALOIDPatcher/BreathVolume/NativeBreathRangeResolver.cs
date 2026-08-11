namespace VOCALOIDPatcher.BreathVolume;

internal static class NativeBreathRangeResolver
{
    internal static bool TryResolve(
        long beginFrame,
        long endFrame,
        long scoreCount,
        long samplesPerFrame,
        out long beginSample,
        out long endSample)
    {
        beginSample = 0;
        endSample = 0;
        if (beginFrame < 0 || endFrame <= beginFrame || endFrame > scoreCount ||
            scoreCount <= 0 || samplesPerFrame <= 0 ||
            endFrame > long.MaxValue / samplesPerFrame)
            return false;

        beginSample = beginFrame * samplesPerFrame;
        endSample = endFrame * samplesPerFrame;
        return true;
    }
}
