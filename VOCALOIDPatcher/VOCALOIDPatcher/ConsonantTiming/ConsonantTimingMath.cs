using System;

namespace VOCALOIDPatcher.ConsonantTiming;

internal static partial class ConsonantTimingMath
{
    // These reproduce native Emotion control sensitivity, not acoustic duration.
    internal static double DragWindowSeconds(int velocity, double noteSeconds, double downSeconds)
    {
        const double window = .320;
        if (velocity is < 0 or > 127 || !double.IsFinite(noteSeconds) || !double.IsFinite(downSeconds) || velocity >= 64)
            return window;
        double ratio = (noteSeconds - downSeconds) / (window * (64 - velocity) / 64.0);
        return ratio is > 0 and < 1 ? window * ratio : window;
    }

    internal static int VelocityFromDrag(int initial, long deltaTicks, long rangeTicks)
    {
        if (rangeTicks <= 0) return 64;
        if (deltaTicks >= rangeTicks) return 127;
        if (deltaTicks <= -rangeTicks) return 0;
        return (int)Math.Clamp(initial + decimal.Truncate(127m * deltaTicks / rangeTicks), 0m, 127m);
    }

}
